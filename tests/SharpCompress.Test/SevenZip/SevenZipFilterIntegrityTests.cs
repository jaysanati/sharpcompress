using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Compressors.Filters;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipFilterIntegrityTests
{
    private static Filter CreateFilter(int kind, bool encode, Stream stream) =>
        kind switch
        {
            0 => new BCJFilterARM(encode, stream),
            1 => new BCJFilterARM64(encode, stream),
            _ => new BCJFilterRISCV(encode, stream),
        };

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task SevenZip_Bcj_Tail_And_Offsets(int kind, bool asynchronous)
    {
        foreach (var tail in new[] { 0, 1, 2, 3 })
        {
            var raw = new byte[68 + tail];
            for (var i = 0; i < 68; i += 4)
            {
                if (kind == 0)
                {
                    raw[i + 3] = 0xEB;
                }
                else if (kind == 1)
                {
                    raw[i + 3] = 0x94;
                }
                else
                {
                    {
                        raw[i] = 0xEF;
                        raw[i + 2] = 0x40;
                    }
                }
            }
            if (kind == 2)
            {
                // AUIPC ra, 0 followed by JALR ra, 0(ra), alongside the JALs above.
                Array.Copy(new byte[] { 0x97, 0, 0, 0, 0xE7, 0x80, 0, 0 }, 0, raw, 8, 8);
            }
            var encoded = (byte[])raw.Clone();
            using (var encoder = CreateFilter(kind, true, Stream.Null))
            {
                encoder.TransformBlock(encoded, encoded.Length);
            }
            Assert.NotEqual(raw, encoded);
            foreach (var size in new[] { 1, 3, 4, 5, 7, 8, 9, 13, 64, 128 })
            {
                foreach (var encode in new[] { false, true })
                {
                    // Exercise all PC calculations, including encoder calls at a nonzero offset.
                    using var input = new MemoryStream(encode ? raw : encoded);
                    using var filter = CreateFilter(kind, encode, input);
                    using var output = new MemoryStream();
                    var buffer = new byte[size + 7];
                    int read;
                    while (
                        (
                            read = asynchronous
                                ? await filter.ReadAsync(buffer, 3, size, CancellationToken.None)
                                : filter.Read(buffer, 3, size)
                        ) > 0
                    )
                    {
                        output.Write(buffer, 3, read);
                    }
                    Assert.Equal(encode ? encoded : raw, output.ToArray());
                }
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SevenZip_Arm64_Writer_Final_Instruction(bool parallel, bool asynchronous)
    {
        foreach (var tail in new[] { 0, 1, 2, 3 })
        {
            var raw = new byte[65540 + tail];
            raw[65539] = 0x94;
            var expected = (byte[])raw.Clone();
            using (var filter = new BCJFilterARM64(true, Stream.Null))
            {
                Assert.Equal(65540, filter.TransformBlock(expected, expected.Length));
            }
            Assert.Equal(0x40, expected[65537]); // BL's PC-relative immediate at byte 65536.
            using var output = new MemoryStream();
            var writer = new SevenZipWriter(
                output,
                new SevenZipWriterOptions(parallel ? CompressionType.LZMA2 : CompressionType.None)
                {
                    LeaveStreamOpen = true,
                    EnableParallelism = parallel,
                    MaxThreads = 2,
                }
            );
            writer.BeginSolidBlock(new SevenZipSolidBlockOptions { Filter = SevenZipFilter.Arm64 });
            using var source = new MemoryStream(raw);
            if (asynchronous)
            {
                await writer.WriteAsync("arm64", source, null, CancellationToken.None);
                await writer.DisposeAsync();
            }
            else
            {
                writer.Write("arm64", source, null);
                writer.Dispose();
            }
            var archiveBytes = output.ToArray();
            if (!parallel)
            {
                Assert.Equal(expected, archiveBytes.Skip(32).Take(raw.Length).ToArray());
            }
            output.Position = 0;
            using var archive = SevenZipArchive.OpenArchive(output);
            using var entry = archive.Entries.Single().OpenEntryStream();
            using var restored = new MemoryStream();
            entry.CopyTo(restored);
            Assert.Equal(raw, restored.ToArray());
        }
    }
}
