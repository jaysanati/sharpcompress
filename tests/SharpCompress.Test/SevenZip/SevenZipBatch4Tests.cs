using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.LZMA.Utilities;
using SharpCompress.Compressors.PPMd;
using SharpCompress.Crypto;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipBatch4Tests : TestBase
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public async Task HeaderCountsRejectBeforeAllocation(int kind, bool asynchronous)
    {
        using var header = new MemoryStream();
        if (kind == 0)
        {
            header.Write(new byte[] { 1, 5 }, 0, 2);
            Number(header, 0x10000000);
        }
        else if (kind == 1)
        {
            header.Write(new byte[] { 1, 4, 7, 11 }, 0, 4);
            Number(header, 0x10000000);
            header.WriteByte(0); // inline folders
        }
        else if (kind == 2)
        {
            header.Write(new byte[] { 1, 4, 6, 0 }, 0, 4);
            Number(header, 0x10000000);
            header.WriteByte(9);
        }
        else if (kind == 3)
        {
            byte[] prefix = { 1, 4, 7, 11, 1, 0, 1, 1, 0, 12, 1, 0, 8, 13 };
            header.Write(prefix, 0, prefix.Length);
            Number(header, int.MaxValue);
        }
        else if (kind == 5)
        {
            byte[] prefix = { 1, 4, 7, 11, 1, 0, 1, 0x21, 0 };
            header.Write(prefix, 0, prefix.Length);
            Number(header, 0x10000000);
        }
        else
        {
            // Copy-coded encoded header: two real bytes, a huge claimed unpack size.
            byte[] prefix = { 23, 6, 0, 1, 9, 2, 0, 7, 11, 1, 0, 1, 1, 0, 12 };
            header.Write(prefix, 0, prefix.Length);
            Number(header, 0x7FFFFFFF);
        }
        header.WriteByte(0);
        header.WriteByte(0);
        using var input = Archive(
            header.ToArray(),
            kind == 4 ? new byte[] { 1, 0 } : Array.Empty<byte>()
        );
        var reader = new ArchiveReader();
        if (asynchronous)
        {
            await reader.OpenAsync(input, false, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                await reader.ReadDatabaseAsync(new NoPassword(), CancellationToken.None)
            );
        }
        else
        {
            reader.Open(input, false);
            Assert.Throws<InvalidFormatException>(() => reader.ReadDatabase(new NoPassword()));
        }
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamelessEmptyFilesUseBitPackedMetadata(bool asynchronous)
    {
        // Sixteen empty directories need two empty-stream bitmap bytes, no names.
        using var input = Archive(
            new byte[] { 1, 5, 16, 14, 2, 255, 255, 0, 0 },
            Array.Empty<byte>()
        );
        var reader = new ArchiveReader();
        ArchiveDatabase database;
        if (asynchronous)
        {
            await reader.OpenAsync(input, false, CancellationToken.None);
            database = await reader.ReadDatabaseAsync(new NoPassword(), CancellationToken.None);
        }
        else
        {
            reader.Open(input, false);
            database = reader.ReadDatabase(new NoPassword());
        }
        Assert.Equal(16, database._files.Count);
        Assert.All(database._files, file => Assert.True(file.IsDir));
    }

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(1, 2048)]
    [InlineData(65, 2048)]
    [InlineData(6, 0)]
    [InlineData(6, -1)]
    [InlineData(6, 100)]
    public void InvalidPpmdProperties(int order, int memory)
    {
        var properties = new byte[5];
        properties[0] = (byte)order;
        Array.Copy(BitConverter.GetBytes(memory), 0, properties, 1, 4);
        Assert.Throws<InvalidFormatException>(() => new PpmdProperties(properties));
    }

    [Fact]
    public void PpmdValidUnsignedMemoryIsUnsupportedRatherThanMalformed()
    {
        Assert.Throws<NotSupportedException>(() =>
            new PpmdProperties(new byte[] { 6, 0, 0, 0, 128 })
        );
        Assert.Throws<NotSupportedException>(() =>
            new SharpCompress.Compressors.PPMd.H.SubAllocator().StartSubAllocator(int.MaxValue)
        );
    }

    [Theory]
    [InlineData("7Zip.PPMd.7z", false)]
    [InlineData("7Zip.PPMd.7z", true)]
    [InlineData("7Zip.LZMA.7z", false)]
    [InlineData("7Zip.LZMA.7z", true)]
    public async Task DecoderMemoryLimitBeforeExtraction(string filename, bool asynchronous)
    {
        using var input = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, filename));
        var options = new ReaderOptions { LeaveStreamOpen = true, MaxDecoderMemory = 1 };
        using var archive = asynchronous
            ? (SevenZipArchive)
                await SevenZipArchive.OpenAsyncArchive(input, options, CancellationToken.None)
            : (SevenZipArchive)SevenZipArchive.OpenArchive(input, options);
        if (asynchronous)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await archive.EntriesAsync.ToListAsync()
            );
        }
        else
        {
            Assert.Throws<NotSupportedException>(() => archive.Entries.ToList());
        }
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(0x030101)]
    [InlineData(0x21)]
    [InlineData(3)]
    [InlineData(0x030401)]
    public async Task EmptyCoderProperties(int method)
    {
        using var input = new MemoryStream();
        Assert.Throws<InvalidFormatException>(() =>
            DecoderRegistry.CreateDecoderStream(
                new CMethodId((ulong)method),
                new Stream[] { input },
                Array.Empty<byte>(),
                new NoPassword(),
                1
            )
        );
        await Assert.ThrowsAsync<InvalidFormatException>(async () =>
            await DecoderRegistry.CreateDecoderStreamAsync(
                new CMethodId((ulong)method),
                new Stream[] { input },
                Array.Empty<byte>(),
                new NoPassword(),
                1,
                CancellationToken.None
            )
        );
    }

    private static MemoryStream Archive(byte[] header, byte[] packed)
    {
        var stream = new MemoryStream();
        SevenZipSignatureHeaderWriter.WriteFinal(
            stream,
            (ulong)packed.Length,
            (ulong)header.Length,
            Crc32Stream.Compute(header)
        );
        stream.Write(packed, 0, packed.Length);
        stream.Write(header, 0, header.Length);
        stream.Position = 0;
        return stream;
    }

    private static void Number(Stream stream, ulong value)
    {
        stream.WriteByte(255);
        for (var i = 0; i < 8; i++)
        {
            stream.WriteByte((byte)value);
            value >>= 8;
        }
    }

    private sealed class NoPassword : IPasswordProvider
    {
        public string? CryptoGetTextPassword() => null;
    }
}
