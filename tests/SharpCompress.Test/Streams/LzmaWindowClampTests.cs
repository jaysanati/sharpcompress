using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Crypto;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.Streams;

[Collection(DictionarySizeLimitCollection.Name)]
public class LzmaWindowClampTests : TestBase
{
    [Theory]
    [InlineData(0, 4096)]
    [InlineData(4095, 4096)]
    [InlineData(4096, 4096)]
    [InlineData(8192, 8192)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Lzma2_Effective_Window_Boundaries(long outputSize, int expected)
    {
        using var limits = new DictionarySizeLimitScope();
        DictionarySizeLimits.Lzma2 = long.MaxValue;
        Assert.Equal(expected, LzmaStream.GetLzma2DictionarySize(40, outputSize));
        Assert.Throws<NotSupportedException>(() =>
            LzmaStream.GetLzma2DictionarySize(40, (long)int.MaxValue + 1)
        );
    }

    [Theory]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(0xF0000000L)]
    [InlineData(0xFFFFFFFFL)]
    public async Task Lzma_Known_Size_Clamps_Sync_And_Async(long dictionary)
    {
        using var limits = new DictionarySizeLimitScope();
        DictionarySizeLimits.Lzma = DictionarySizeLimits.Lzma2 = 4096;
        var expected = Enumerable.Range(0, 3000).Select(i => (byte)(i % 17)).ToArray();
        var lzma2 = dictionary <= 40;
        using var packed = new MemoryStream();
        byte[] properties;
        using (
            var encoder = LzmaStream.Create(new LzmaEncoderProperties(false, 4096), lzma2, packed)
        )
        {
            properties = (byte[])encoder.Properties.Clone();
            encoder.Write(expected, 0, expected.Length);
        }
        if (lzma2)
        {
            properties[0] = (byte)dictionary;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(1), (uint)dictionary);
        }

        foreach (var useAsync in new[] { false, true })
        {
            packed.Position = 0;
#if !LEGACY_DOTNET
            var before = GC.GetAllocatedBytesForCurrentThread();
#endif
            using var decoder = useAsync
                ? await LzmaStream.CreateAsync(
                    properties,
                    packed,
                    packed.Length,
                    expected.Length,
                    leaveOpen: true
                )
                : LzmaStream.Create(
                    properties,
                    packed,
                    packed.Length,
                    expected.Length,
                    leaveOpen: true
                );
            using var output = new MemoryStream();
            if (useAsync)
            {
                await decoder.CopyToAsync(output, 8192, CancellationToken.None);
            }
            else
            {
                decoder.CopyTo(output);
            }
            Assert.Equal(expected, output.ToArray());
#if !LEGACY_DOTNET
            // MemoryStream completes synchronously, so this includes the async decode allocations.
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 << 20);
#endif
        }
        if (!lzma2)
        {
            foreach (var useAsync in new[] { false, true })
            {
                packed.Position = 0;
                using var decoder = new Decoder();
                decoder.SetDecoderProperties(properties);
                using var output = new MemoryStream();
                if (useAsync)
                {
                    await decoder.CodeAsync(
                        packed,
                        output,
                        packed.Length,
                        expected.Length,
                        null!,
                        CancellationToken.None
                    );
                }
                else
                {
                    decoder.Code(packed, output, packed.Length, expected.Length, null!);
                }
                Assert.Equal(expected, output.ToArray());
            }
        }
        if (lzma2)
        {
            // A valid LZMA2 property whose window exceeds the decoder when the size is unknown.
            Assert.Throws<NotSupportedException>(() => LzmaStream.Create(properties, Stream.Null));
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await LzmaStream.CreateAsync(properties, Stream.Null, -1, -1)
            );
        }
        else
        {
            Assert.ThrowsAny<SharpCompressException>(() =>
                LzmaStream.Create(properties, Stream.Null)
            );
            await Assert.ThrowsAnyAsync<SharpCompressException>(async () =>
                await LzmaStream.CreateAsync(properties, Stream.Null, -1, -1)
            );
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lzma_Limit_Reports_Effective_Window(bool lzma2)
    {
        using var limits = new DictionarySizeLimitScope();
        DictionarySizeLimits.Lzma = DictionarySizeLimits.Lzma2 = 4096;
        var properties = lzma2 ? new byte[] { 40 } : new byte[] { 0x5D, 255, 255, 255, 255 };
        var error = Assert.Throws<DictionarySizeLimitExceededException>(() =>
            LzmaStream.Create(properties, Stream.Null, -1, 8192)
        );
        Assert.Equal(8192, error.RequestedSize);
    }

    [Theory]
    [InlineData(41)]
    [InlineData(255)]
    public void Lzma2_Invalid_Properties_Are_Not_Clamped(int property) =>
        Assert.Throws<InvalidParamException>(() =>
            LzmaStream.Create(new[] { (byte)property }, Stream.Null, -1, 1)
        );

    [Theory]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(40)]
    public async Task SevenZip_Lzma2_Large_Properties_Archive_Reader_And_Restart(int property)
    {
        using var limits = new DictionarySizeLimitScope();
        DictionarySizeLimits.Lzma2 = 4096;
        var expected = Enumerable.Range(0, 1000).Select(i => (byte)(i % 13)).ToArray();
        using var packed = new MemoryStream();
        using (
            var writer = new SevenZipWriter(
                packed,
                new SevenZipWriterOptions(CompressionType.LZMA2)
                {
                    CompressHeader = false,
                    LeaveStreamOpen = true,
                    LzmaProperties = new LzmaEncoderProperties(false, 4096),
                }
            )
        )
        {
            writer.BeginSolidBlock();
            using var source = new MemoryStream(expected);
            writer.Write("payload", source, null);
            source.Position = 0;
            writer.Write("second", source, null);
        }
        var bytes = packed.ToArray();
        var header = checked(32 + (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12)));
        // This writer emits one simple LZMA2 coder: attributes + ID + property length + property.
        var found = false;
        for (var i = header; i < bytes.Length - 3; i++)
        {
            if (bytes[i] == 0x21 && bytes[i + 1] == 0x21 && bytes[i + 2] == 1)
            {
                bytes[i + 3] = (byte)property;
                found = true;
                break;
            }
        }
        Assert.True(found);
        WriteCrc(bytes, 28, header, bytes.Length - header);
        WriteCrc(bytes, 8, 12, 20);
        using var input = new MemoryStream(bytes);
        using (
            var archive = SevenZipArchive.OpenArchive(
                input,
                new SharpCompress.Readers.ReaderOptions { LeaveStreamOpen = true }
            )
        )
        {
            Assert.True(archive.IsSolid);
            var entry = archive.Entries.First();
            using (var partial = entry.OpenEntryStream())
            {
                Assert.Equal(expected[0], partial.ReadByte());
            }
            using var restarted = entry.OpenEntryStream();
            using var output = new MemoryStream();
            restarted.CopyTo(output);
            Assert.Equal(expected, output.ToArray());
            using var reader = archive.ExtractAllEntries();
            Assert.True(reader.MoveToNextEntry());
            using var entryStream = reader.OpenEntryStream();
            using var readerOutput = new MemoryStream();
            entryStream.CopyTo(readerOutput);
            Assert.Equal(expected, readerOutput.ToArray());
        }
        input.Position = 0;
        await using var asyncArchive = await SevenZipArchive.OpenAsyncArchive(
            input,
            cancellationToken: CancellationToken.None
        );
        await foreach (
            var entry in asyncArchive.EntriesAsync.WithCancellation(CancellationToken.None)
        )
        {
            using var entryStream = await entry.OpenEntryStreamAsync(CancellationToken.None);
            using var output = new MemoryStream();
            await entryStream.CopyToAsync(output, 8192, CancellationToken.None);
            Assert.Equal(expected, output.ToArray());
        }
        await using var asyncReader = await asyncArchive.ExtractAllEntriesAsync();
        var count = 0;
        while (await asyncReader.MoveToNextEntryAsync(CancellationToken.None))
        {
            using var entryStream = await asyncReader.OpenEntryStreamAsync(CancellationToken.None);
            using var output = new MemoryStream();
            await entryStream.CopyToAsync(output, 8192, CancellationToken.None);
            Assert.Equal(expected, output.ToArray());
            count++;
        }
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zip_Lzma_Eos_Uses_Available_Entry_Size(bool useAsync)
    {
        using var limits = new DictionarySizeLimitScope();
        DictionarySizeLimits.Lzma = 4096;
        var expected = new byte[1000];
        using var packed = new MemoryStream();
        using (
            var writer = new SharpCompress.Writers.Zip.ZipWriter(
                packed,
                new SharpCompress.Writers.Zip.ZipWriterOptions(CompressionType.LZMA)
                {
                    LeaveStreamOpen = true,
                }
            )
        )
        {
            using var source = new MemoryStream(expected);
            writer.Write("payload", source, new SharpCompress.Writers.Zip.ZipWriterEntryOptions());
        }
        var bytes = packed.ToArray();
        var payload =
            30
            + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26))
            + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(payload + 5), uint.MaxValue);
        // The decoder previously ignored even the central directory's size with this flag.
        bytes[6] |= 2;
        for (var i = payload; i < bytes.Length - 4; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014B50)
            {
                bytes[i + 8] |= 2;
                break;
            }
        }
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        if (useAsync)
        {
            await using var archive = await SharpCompress.Archives.Zip.ZipArchive.OpenAsyncArchive(
                input,
                cancellationToken: CancellationToken.None
            );
            await foreach (
                var entry in archive.EntriesAsync.WithCancellation(CancellationToken.None)
            )
            {
                using var stream = await entry.OpenEntryStreamAsync(CancellationToken.None);
                await stream.CopyToAsync(output, 8192, CancellationToken.None);
            }
        }
        else
        {
            using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(input);
            using var stream = Assert.Single(archive.Entries).OpenEntryStream();
            stream.CopyTo(output);
        }
        Assert.Equal(expected, output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Xz_Block_Header_Size_Clamps_Lzma2(bool useAsync)
    {
        using var limits = new DictionarySizeLimitScope();
        DictionarySizeLimits.Lzma2 = 4096;
        // Twelve-byte header with uncompressed size 1 and LZMA2 property 40,
        // followed by an uncompressed LZMA2 chunk, end marker, and block padding.
        var bytes = new byte[]
        {
            2,
            0x80,
            1,
            0x21,
            1,
            40,
            0,
            0,
            0,
            0,
            0,
            0,
            1,
            0,
            0,
            65,
            0,
            0,
            0,
            0,
        };
        WriteCrc(bytes, 8, 0, 8);
        using var input = new MemoryStream(bytes);
        using var block = new SharpCompress.Compressors.Xz.XZBlock(
            input,
            SharpCompress.Compressors.Xz.CheckType.NONE,
            0
        );
        using var output = new MemoryStream();
        if (useAsync)
        {
            await block.CopyToAsync(output, 8192, CancellationToken.None);
        }
        else
        {
            block.CopyTo(output);
        }
        Assert.Equal(new byte[] { 65 }, output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lzma2_Clamped_Window_Rejects_Match_Before_History(bool useAsync)
    {
        // Range data selects a match before any literal has established history.
        var corrupt = new byte[32];
        new byte[] { 0xE0, 0, 0, 0, 24, 0x5D, 0, 0x80 }.CopyTo(corrupt, 0);
        using var input = new MemoryStream(corrupt);
        using var decoder = useAsync
            ? await LzmaStream.CreateAsync(new byte[] { 40 }, input, -1, 1)
            : LzmaStream.Create(new byte[] { 40 }, input, -1, 1);
        if (useAsync)
        {
            await Assert.ThrowsAsync<DataErrorException>(() =>
                decoder.CopyToAsync(Stream.Null, 8192, CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<DataErrorException>(() => decoder.CopyTo(Stream.Null));
        }
    }

    private static void WriteCrc(byte[] bytes, int destination, int offset, int count) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(destination),
            Crc32Stream.Compute(
                Crc32Stream.DEFAULT_POLYNOMIAL,
                Crc32Stream.DEFAULT_SEED,
                bytes.AsSpan(offset, count)
            )
        );

#if !LEGACY_DOTNET
    [Fact]
    public void Lzma2_Parallel_Workers_Clamp_Per_Block()
    {
        using var limits = new DictionarySizeLimitScope();
        DictionarySizeLimits.Lzma2 = 4096;
        var path = Path.Combine(SCRATCH_FILES_PATH, "clamped-blocks.bin");
        using var input = File.Create(path);
        input.Write(new byte[] { 1, 0, 0, 65, 0, 1, 0, 0, 66, 0 });
        input.Flush();
        using var output = File.Create(path + ".out");
        var blocks = new[] { new Lzma2Block(0, 5, 0, 1), new Lzma2Block(5, 5, 1, 1) };
        Lzma2ParallelDecoder.DecodeBlocksParallel(
            input.SafeFileHandle,
            new byte[] { 39 },
            0,
            blocks,
            output.SafeFileHandle,
            2
        );
        output.Position = 0;
        Assert.Equal(65, output.ReadByte());
        Assert.Equal(66, output.ReadByte());
    }
#endif
}
