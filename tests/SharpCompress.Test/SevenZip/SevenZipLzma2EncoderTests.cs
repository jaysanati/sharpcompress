using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipLzma2EncoderTests
{
    private const int MiB = 1 << 20;

    [Fact]
    public async Task Random_Data_Uses_Bounded_Stored_Chunks()
    {
        var data = RandomBytes(5 * MiB);
        var archive = await CheckBothPaths(data);
        Assert.True(archive.Length <= data.Length * 1.002);
    }

    [Fact]
    public async Task Dictionary_Matches_Across_Chunk_Boundaries()
    {
        var data = new byte[6 * MiB];
        var block = RandomBytes(3 * MiB);
        Buffer.BlockCopy(block, 0, data, 0, block.Length);
        Buffer.BlockCopy(block, 0, data, block.Length, block.Length);
        var archive = await CheckBothPaths(data);
        Assert.True(archive.Length < 3.2 * MiB, $"Archive size: {archive.Length}");
        Assert.Contains(ParseFraming(archive, 32, data.Length).Controls, c => (c & 0xE0) == 0xC0);
    }

    [Fact]
    public async Task Text_Compression_Is_Comparable_To_Lzma()
    {
        var random = new Random(123);
        var words = Enumerable
            .Range(0, 2048)
            .Select(_ => new string(
                Enumerable
                    .Range(0, random.Next(3, 14))
                    .Select(_ => (char)random.Next('a', 'z' + 1))
                    .ToArray()
            ))
            .ToArray();
        var text = new StringBuilder(8 * MiB + 32);
        while (text.Length < 8 * MiB)
        {
            text.Append(words[random.Next(words.Length)]).Append(' ');
        }
        var data = Encoding.ASCII.GetBytes(text.ToString(0, 8 * MiB));
        var lzma2 = await CheckBothPaths(data);
        foreach (var useAsync in new[] { false, true })
        {
            var lzma = await ArchiveRoundTrip(data, useAsync, CompressionType.LZMA);
            Assert.True(
                Math.Abs((double)lzma2.Length / lzma.Length - 1) <= 0.02,
                $"LZMA2: {lzma2.Length}, LZMA: {lzma.Length}"
            );
        }
        Assert.Contains(ParseFraming(lzma2, 32, data.Length).Controls, c => (c & 0xE0) == 0x80);
    }

    [Fact]
    public async Task Stored_To_Compressed_Resets_State_Only()
    {
        var data = new byte[6 * MiB];
        for (var offset = MiB; offset < data.Length; offset += 2 * MiB)
        {
            Buffer.BlockCopy(RandomBytes(MiB), 0, data, offset, MiB);
        }
        var archive = await CheckBothPaths(data);
        var controls = ParseFraming(archive, 32, data.Length).Controls;
        Assert.True(
            controls.Zip(controls.Skip(1), (a, b) => a == 2 && (b & 0xE0) == 0xA0).Any(x => x)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(2 * MiB - 1)]
    [InlineData(2 * MiB)]
    [InlineData(2 * MiB + 1)]
    public async Task Boundary_Sizes_RoundTrip(int size)
    {
        await CheckBothPaths(RandomBytes(size));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Encrypted_Chunks_RoundTrip(bool useAsync, bool encryptHeader)
    {
        var data = new byte[3 * MiB];
        Buffer.BlockCopy(RandomBytes(MiB), 0, data, MiB, MiB);
        await ArchiveRoundTrip(
            data,
            useAsync,
            password: "lzma2 test",
            encryptHeader: encryptHeader
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_Raw_Stream_Contains_Only_End_Marker(bool useAsync)
    {
        using var output = new MemoryStream();
        var encoder = new Lzma2EncoderStream(output, 8 * MiB, 32);
        if (useAsync)
        {
            await encoder.DisposeAsync();
        }
        else
        {
            encoder.Dispose();
        }
        Assert.Equal(new byte[] { 0 }, output.ToArray());
        Assert.Equal(1, ParseFraming(output.ToArray(), 0, 0).End);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonSeekable_Short_Reads_RoundTrip(bool useAsync)
    {
        await ArchiveRoundTrip(RandomBytes(2 * MiB + 1), useAsync, nonSeekable: true);
    }

    private static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private static async Task<byte[]> CheckBothPaths(byte[] data)
    {
        var sync = await ArchiveRoundTrip(data, false);
        var asyncBytes = await ArchiveRoundTrip(data, true);
        Assert.Equal(sync, asyncBytes);
        return sync;
    }

    private static async Task<byte[]> ArchiveRoundTrip(
        byte[] data,
        bool useAsync,
        CompressionType compression = CompressionType.LZMA2,
        string? password = null,
        bool encryptHeader = false,
        bool nonSeekable = false
    )
    {
        using var output = new MemoryStream();
        using var source = new MemoryStream(data);
        var options = new SevenZipWriterOptions(compression)
        {
            LzmaProperties = new LzmaEncoderProperties(
                compression == CompressionType.LZMA,
                8 * MiB,
                32
            ),
            CompressHeader = false,
            Password = password,
            EncryptHeader = encryptHeader,
            LeaveStreamOpen = true,
        };
        using var shortReads = new ShortReadStream(source, 997, seekable: false);
        Stream input = nonSeekable ? shortReads : source;
        using var asyncInput = new AsyncOnlyStream(input, disposeStream: false);
        using var asyncOutput = new AsyncOnlyStream(output, disposeStream: false);
        var writer = new SevenZipWriter(useAsync ? asyncOutput : output, options);
        if (useAsync)
        {
            await using (writer)
            {
                await writer.WriteAsync("data.bin", asyncInput, null, CancellationToken.None);
            }
        }
        else
        {
            using (writer)
            {
                writer.Write("data.bin", input, null);
            }
        }
        Assert.True(source.CanRead);
        Assert.True(output.CanRead);
        var bytes = output.ToArray();
        if (compression == CompressionType.LZMA2 && password is null && data.Length > 0)
        {
            var end = ParseFraming(bytes, 32, data.Length).End;
            Assert.Equal(
                (ulong)(end - 32),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12, 8))
            );
        }
        output.Position = 0;
        var readerOptions = new ReaderOptions { Password = password, LeaveStreamOpen = true };
        using var extracted = new MemoryStream();
        if (useAsync)
        {
            await using var archive = await SevenZipArchive.OpenAsyncArchive(
                output,
                readerOptions,
                CancellationToken.None
            );
            var entry = Assert.Single(await archive.EntriesAsync.ToListAsync());
            using var stream = await entry.OpenEntryStreamAsync(CancellationToken.None);
            await stream.CopyToAsync(extracted, 65536, CancellationToken.None);
        }
        else
        {
            using var archive = SevenZipArchive.OpenArchive(output, readerOptions);
            using var stream = Assert.Single(archive.Entries).OpenEntryStream();
            stream.CopyTo(extracted);
        }
        Assert.Equal(data, extracted.ToArray());
        Assert.True(output.CanRead);
        return bytes;
    }

    // Validate framing independently of the library decoder, including the state
    // transition needed when a stream begins with stored bytes and has no properties.
    private static (List<byte> Controls, int End) ParseFraming(
        byte[] bytes,
        int offset,
        int expectedSize
    )
    {
        var controls = new List<byte>();
        var first = true;
        var propertiesNeeded = true;
        var resetNeeded = true;
        long unpacked = 0;
        int ReadByte()
        {
            Assert.InRange(offset, 0, bytes.Length - 1);
            return bytes[offset++];
        }
        while (true)
        {
            var control = ReadByte();
            if (control == 0)
            {
                Assert.Equal(expectedSize, unpacked);
                return (controls, offset);
            }
            controls.Add((byte)control);
            var size = (ReadByte() << 8) + ReadByte() + 1;
            int packed;
            if (control < 0x80)
            {
                Assert.Equal(first ? 1 : 2, control);
                Assert.InRange(size, 1, 65536);
                packed = size;
                resetNeeded = true;
            }
            else
            {
                var kind = control & 0xE0;
                Assert.Equal(
                    first ? 0xE0
                        : propertiesNeeded ? 0xC0
                        : resetNeeded ? 0xA0
                        : 0x80,
                    kind
                );
                size += (control & 31) << 16;
                packed = (ReadByte() << 8) + ReadByte() + 1;
                Assert.InRange(size, 1, 2 * MiB);
                Assert.InRange(packed, 1, 65536);
                if (kind >= 0xC0)
                {
                    Assert.Equal(0x5D, ReadByte());
                    propertiesNeeded = false;
                }
                resetNeeded = false;
            }
            unpacked += size;
            Assert.True(offset + packed < bytes.Length);
            offset += packed;
            first = false;
        }
    }
}
