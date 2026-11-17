using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.GZip;
using SharpCompress.Common.Zip.Headers;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.Xz.Filters;
using SharpCompress.Providers;
using Xunit;

namespace SharpCompress.Test.Streams;

public class CompressionBatch3Tests
{
    private static readonly byte[] Content = Encoding.ASCII.GetBytes("batch three payload");

    private static async Task<byte[]> Drain(Stream stream, bool asynchronous, int bufferSize = 7)
    {
        using var output = new MemoryStream();
        var buffer = new byte[bufferSize];
        int read;
        while (
            (
                read = asynchronous
                    ? await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
                    : stream.Read(buffer, 0, buffer.Length)
            ) != 0
        )
        {
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] Bzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var writer = BZip2Stream.Create(output, CompressionMode.Compress, false, true))
        {
            writer.Write(content, 0, content.Length);
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BZip2_TrailingPaddingAndConcatenation(bool asynchronous, bool concatenate)
    {
        var compressed = Bzip(Content);
        var data = compressed
            .Concat(concatenate ? compressed : Array.Empty<byte>())
            .Concat(new byte[512])
            .ToArray();
        using var input = new MemoryStream(data);
        using var decoder = asynchronous
            ? await BZip2Stream.CreateAsync(
                input,
                CompressionMode.Decompress,
                true,
                true,
                cancellationToken: CancellationToken.None
            )
            : BZip2Stream.Create(input, CompressionMode.Decompress, true, true);
        Assert.Equal(
            concatenate ? Content.Concat(Content).ToArray() : Content,
            await Drain(decoder, asynchronous)
        );
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 1, true)]
    [InlineData(true, 1, true)]
    public async Task BZip2_ThousandsOfEmptyBlocksFailNormally(
        bool asynchronous,
        int selectors,
        bool randomized
    )
    {
        var bits = new StringBuilder();
        void Put(long value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                bits.Append((value >> i) & 1);
            }
        }
        foreach (var value in Encoding.ASCII.GetBytes("BZh9"))
        {
            Put(value, 8);
        }
        for (var block = 0; block < 5000; block++)
        {
            Put(0x314159265359, 48);
            Put(0, 32);
            Put(randomized ? 1 : 0, 1);
            Put(0, 24);
            Put(0x8000, 16);
            Put(0x8000, 16); // One symbol in the map.
            Put(2, 3);
            Put(selectors, 15);
            if (selectors != 0)
            {
                Put(0, 1);
            }
            for (var tree = 0; tree < 2; tree++)
            {
                Put(2, 5);
                Put(0, 3);
            }
            Put(2, 2); // EOB, with no decoded symbols.
        }
        Put(0x177245385090, 48);
        Put(0, 32);
        while (bits.Length % 8 != 0)
        {
            bits.Append('0');
        }
        var data = new byte[bits.Length / 8];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = Convert.ToByte(bits.ToString(i * 8, 8), 2);
        }
        using var input = new MemoryStream(data);
        await Assert.ThrowsAsync<InvalidFormatException>(async () =>
        {
            using var decoder = asynchronous
                ? await BZip2Stream.CreateAsync(
                    input,
                    CompressionMode.Decompress,
                    true,
                    true,
                    cancellationToken: CancellationToken.None
                )
                : BZip2Stream.Create(input, CompressionMode.Decompress, true, true);
            await Drain(decoder, asynchronous);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BZip2_ThousandsOfEmptyStreams(bool asynchronous)
    {
        var empty = Bzip(Array.Empty<byte>());
        using var input = new MemoryStream(
            Enumerable.Range(0, 5000).SelectMany(_ => empty).Concat(Bzip(Content)).ToArray()
        );
        using var decoder = asynchronous
            ? await BZip2Stream.CreateAsync(
                input,
                CompressionMode.Decompress,
                true,
                true,
                cancellationToken: CancellationToken.None
            )
            : BZip2Stream.Create(input, CompressionMode.Decompress, true, true);
        Assert.Equal(Content, await Drain(decoder, asynchronous));
    }

    // A small XZ stream using an uncompressed LZMA2 chunk; no external encoder is needed.
    private static byte[] Xz(byte[] content)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(new byte[] { 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0 });
        var flags = new byte[] { 0, 1 };
        writer.Write(flags);
        writer.Write(Crc32.Compute(flags));
        var header = new byte[] { 2, 0, 0x21, 1, 0, 0, 0, 0 };
        writer.Write(header);
        writer.Write(Crc32.Compute(header));
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((byte)(content.Length - 1));
        writer.Write(content);
        writer.Write((byte)0);
        while (output.Position % 4 != 0)
        {
            writer.Write((byte)0);
        }
        writer.Write(Crc32.Compute(content));
        var index = new byte[] { 0, 1, (byte)(20 + content.Length), (byte)content.Length };
        writer.Write(index);
        writer.Write(Crc32.Compute(index));
        var footer = new byte[] { 1, 0, 0, 0, 0, 1 };
        writer.Write(Crc32.Compute(footer));
        writer.Write(footer);
        writer.Write(new byte[] { 0x59, 0x5a });
        return output.ToArray();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public async Task Xz_ConcatenatedStreams(bool asynchronous, int padding)
    {
        using var input = new MemoryStream(
            Xz(Content).Concat(new byte[padding]).Concat(Xz(Content)).Concat(new byte[8]).ToArray()
        );
        using var decoder = new XZStream(input);
        Assert.Equal(Content.Concat(Content).ToArray(), await Drain(decoder, asynchronous));
        Assert.True(input.CanRead);
    }

#if !LEGACY_DOTNET
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Xz_ConcatenationMemoryReads(bool corrupt)
    {
        var second = Xz(Content);
        if (corrupt)
        {
            second[second.Length - 13] ^= 1;
        }
        using var input = new MemoryStream(
            Xz(Content).Concat(new byte[4]).Concat(second).ToArray()
        );
        using var decoder = new XZStream(input);
        var buffer = new byte[128];
        if (corrupt)
        {
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                await decoder.ReadAsync(buffer.AsMemory(), CancellationToken.None)
            );
        }
        else
        {
            Assert.Equal(
                Content.Length * 2,
                await decoder.ReadAsync(buffer.AsMemory(), CancellationToken.None)
            );
            Assert.Equal(
                Content.Concat(Content).ToArray(),
                buffer.Take(Content.Length * 2).ToArray()
            );
            Assert.Equal(0, await decoder.ReadAsync(buffer.AsMemory(), CancellationToken.None));
        }
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Xz_ConcatenationForwardOnly(bool asynchronous)
    {
        using var input = new MemoryStream(
            Xz(Content).Concat(new byte[4]).Concat(Xz(Content)).ToArray()
        );
        using var forward = new SharpCompress.Test.Mocks.ForwardOnlyStream(input);
        using var tracked = SharpCompress.IO.SharpCompressStream.Create(forward);
        using var decoder = new XZStream(tracked);
        Assert.Equal(Content.Concat(Content).ToArray(), await Drain(decoder, asynchronous));
    }

    [Fact]
    public async Task Xz_CanceledRead()
    {
        using var input = new MemoryStream(Xz(Content));
        using var decoder = new XZStream(input);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            decoder.ReadAsync(new byte[7], 0, 7, cancellation.Token)
        );
        Assert.Equal(0, input.Position);
    }

    [Theory]
    [InlineData(false, "padding")]
    [InlineData(true, "padding")]
    [InlineData(false, "garbage")]
    [InlineData(true, "garbage")]
    [InlineData(false, "crc")]
    [InlineData(true, "crc")]
    [InlineData(false, "count")]
    [InlineData(true, "count")]
    [InlineData(false, "unpadded")]
    [InlineData(true, "unpadded")]
    [InlineData(false, "uncompressed")]
    [InlineData(true, "uncompressed")]
    [InlineData(false, "flags")]
    [InlineData(true, "flags")]
    [InlineData(false, "backward")]
    [InlineData(true, "backward")]
    public async Task Xz_InvalidIntegrity(bool asynchronous, string corruption)
    {
        var data = Xz(Content);
        if (corruption == "padding")
        {
            data = data.Concat(new byte[1]).ToArray();
        }
        else if (corruption == "garbage")
        {
            data = data.Concat(new byte[] { 42 }).ToArray();
        }
        else if (corruption == "crc")
        {
            data[data.Length - 13] ^= 1;
        }
        else if (corruption == "count")
        {
            Array.Clear(data, data.Length - 20, 4);
            Array.Copy(
                BitConverter.GetBytes(Crc32.Compute(new byte[4])),
                0,
                data,
                data.Length - 16,
                4
            );
        }
        else if (corruption == "unpadded" || corruption == "uncompressed")
        {
            data[data.Length - (corruption == "unpadded" ? 18 : 17)]++;
            Array.Copy(
                BitConverter.GetBytes(Crc32.Compute(data.Skip(data.Length - 20).Take(4).ToArray())),
                0,
                data,
                data.Length - 16,
                4
            );
        }
        else
        {
            data[data.Length - (corruption == "flags" ? 3 : 8)]++;
            Array.Copy(
                BitConverter.GetBytes(Crc32.Compute(data.Skip(data.Length - 8).Take(6).ToArray())),
                0,
                data,
                data.Length - 12,
                4
            );
        }
        using var input = new MemoryStream(data);
        using var decoder = new XZStream(input);
        await Assert.ThrowsAsync<InvalidFormatException>(() => Drain(decoder, asynchronous));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(11)]
    public void Xz_UnknownFilter(int id)
    {
        using var input = new MemoryStream(new byte[] { (byte)id });
        using var reader = new BinaryReader(input);
        Assert.Contains(
            id.ToString(),
            Assert.Throws<NotSupportedException>(() => BlockFilter.Read(reader)).Message
        );
    }

    [Fact]
    public void Xz_PropertiesCannotExceedHeader()
    {
        using var input = new MemoryStream(new byte[] { 0x21, 0xff, 0xff, 0xff, 0xff, 7 });
        using var reader = new BinaryReader(input);
        Assert.Throws<InvalidFormatException>(() => BlockFilter.Read(reader));
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 16)]
    [InlineData(true, 16)]
    public async Task GZip_HeaderStringLimit(bool asynchronous, int flag)
    {
        var data = new byte[10 + 65537];
        data[0] = 0x1f;
        data[1] = 0x8b;
        data[2] = 8;
        data[3] = (byte)flag;
        for (var i = 10; i < data.Length; i++)
        {
            data[i] = 65;
        }
        using var input = new MemoryStream(data);
        await Assert.ThrowsAsync<InvalidFormatException>(async () =>
        {
            if (asynchronous)
            {
                await GZipFilePart.CreateAsync(
                    input,
                    new ArchiveEncoding(),
                    CompressionProviderRegistry.Default,
                    CancellationToken.None
                );
            }
            else
            {
                GZipFilePart.Create(
                    input,
                    new ArchiveEncoding(),
                    CompressionProviderRegistry.Default
                );
            }
        });
        Assert.Equal(10 + 65537, input.Position);
    }

    [Fact]
    public void Zip_InvalidExtraFields()
    {
        for (ushort length = 0; length < 5; length++)
        {
            Assert.Throws<InvalidFormatException>(() =>
                new ExtraUnicodePathExtraField(
                    ExtraDataType.UnicodePathExtraField,
                    length,
                    new byte[length]
                )
            );
        }
        var field = new Zip64ExtendedInformationExtraField(
            ExtraDataType.Zip64ExtendedInformationExtraField,
            8,
            BitConverter.GetBytes(-1L)
        );
        Assert.Throws<InvalidFormatException>(() => field.Process(0, uint.MaxValue, 0, 0));
        Assert.Throws<InvalidFormatException>(() => field.Process(uint.MaxValue, 0, 0, 0));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Deflate_TruncationAndTrailingBytes(bool asynchronous, bool truncate)
    {
        using var compressed = new MemoryStream();
        using (
            var writer = new System.IO.Compression.DeflateStream(
                compressed,
                System.IO.Compression.CompressionMode.Compress,
                true
            )
        )
        {
            writer.Write(Content, 0, Content.Length);
        }
        var data = compressed.ToArray();
        data = truncate
            ? data.Take(data.Length - 1).ToArray()
            : data.Concat(new byte[512]).ToArray();
        using var input = new MemoryStream(data);
        using var decoder = new DeflateStream(
            input,
            CompressionMode.Decompress,
            CompressionLevel.Default,
            true
        );
        if (truncate)
        {
            await Assert.ThrowsAsync<ZlibException>(() => Drain(decoder, asynchronous));
        }
        else
        {
            Assert.Equal(Content, await Drain(decoder, asynchronous));
        }
    }
}
