using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipWriterStoreMetadataTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var useAsync in new[] { false, true })
        {
            foreach (var method in new[] { CompressionType.None, CompressionType.LZMA2 })
            {
                foreach (var encryption in new[] { 0, 1, 2 })
                {
                    yield return new object[] { useAsync, method, encryption };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SevenZipWriter_StoreAndMetadata_RoundTrip(
        bool useAsync,
        CompressionType method,
        int encryption
    )
    {
        var random = new byte[100_003];
        new Random(1234).NextBytes(random);
        byte[][] contents = [random, "text content"u8.ToArray(), [], [], "last file"u8.ToArray()];
        var time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567);
        SevenZipWriterEntryOptions[] metadata =
        [
            new()
            {
                CreationTime = time,
                LastAccessTime = time.AddDays(1),
                ModificationTime = time.AddDays(2),
                Attributes = 0x23,
            },
            new() { ModificationTime = time, Attributes = 0x81A48000 },
            new() { LastAccessTime = time },
            new() { CreationTime = time, Attributes = 0x41ED8000 },
            new(),
        ];
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            useAsync ? new AsyncOnlyStream(output) : output,
            new SevenZipWriterOptions(method)
            {
                CompressHeader = false,
                Password = encryption == 0 ? null : "secret",
                EncryptHeader = encryption == 2,
                LeaveStreamOpen = true,
            }
        );
        try
        {
            for (var i = 0; i < contents.Length; i++)
            {
                if (i == 3)
                {
                    if (useAsync)
                    {
                        await writer.WriteDirectoryAsync(
                            "dir",
                            metadata[i],
                            CancellationToken.None
                        );
                    }
                    else
                    {
                        writer.WriteDirectory("dir", metadata[i]);
                    }
                    continue;
                }
                using var memory = new MemoryStream(contents[i]);
                // Include non-seekable empty input, and require async I/O on the async path.
                using Stream source = useAsync
                    ? new AsyncOnlyStream(new ForwardOnlyStream(memory))
                    : new ForwardOnlyStream(memory);
                if (useAsync)
                {
                    await writer.WriteAsync(
                        $"file{i}",
                        source,
                        metadata[i],
                        CancellationToken.None
                    );
                }
                else
                {
                    writer.Write($"file{i}", source, metadata[i]);
                }
            }
        }
        finally
        {
            if (useAsync)
            {
                await writer.DisposeAsync();
            }
            else
            {
                writer.Dispose();
            }
        }
        Assert.True(output.CanRead);
        if (method == CompressionType.None && encryption == 0)
        {
            var dataSize = contents.Sum(x => x.Length);
            Assert.Equal((ulong)dataSize, BitConverter.ToUInt64(output.ToArray(), 12));
            Assert.InRange(output.Length - dataSize, 32, 1024);
        }
        output.Position = 0;
        var readerOptions = new ReaderOptions
        {
            Password = encryption == 0 ? null : "secret",
            LeaveStreamOpen = true,
        };
        if (useAsync)
        {
            await using var archive = await ArchiveFactory.OpenAsyncArchive(
                output,
                readerOptions,
                CancellationToken.None
            );
            var index = 0;
            await foreach (
                var entry in archive.EntriesAsync.WithCancellation(CancellationToken.None)
            )
            {
                CheckMetadata(entry, metadata[index], index == 3);
                if (!entry.IsDirectory)
                {
                    using var source = await entry.OpenEntryStreamAsync(CancellationToken.None);
                    using var extracted = new MemoryStream();
                    await source.CopyToAsync(extracted, 81920, CancellationToken.None);
                    Assert.Equal(contents[index], extracted.ToArray());
                }
                index++;
            }
            Assert.Equal(contents.Length, index);
        }
        else
        {
            using var archive = SevenZipArchive.OpenArchive(output, readerOptions);
            var entries = archive.Entries.ToArray();
            Assert.Equal(contents.Length, entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                CheckMetadata(entry, metadata[i], i == 3);
                if (entry.IsDirectory)
                {
                    continue;
                }
                if (contents[i].Length > 0 && method == CompressionType.None)
                {
                    Assert.Equal(CompressionType.None, entry.CompressionType);
                    var coders = ((SevenZipEntry)entry).FilePart.Folder!._coders;
                    Assert.Equal(CMethodId.K_COPY, coders.Last()._methodId);
                    Assert.Null(coders.Last()._props);
                    Assert.Equal(encryption == 0 ? 1 : 2, coders.Count);
                }
                using var source = entry.OpenEntryStream();
                using var extracted = new MemoryStream();
                source.CopyTo(extracted);
                Assert.Equal(contents[i], extracted.ToArray());
            }
        }
        if (encryption != 0)
        {
            output.Position = 0;
            if (useAsync)
            {
                await Assert.ThrowsAnyAsync<SharpCompressException>(async () =>
                {
                    await using var archive = await ArchiveFactory.OpenAsyncArchive(
                        output,
                        new ReaderOptions { Password = "wrong", LeaveStreamOpen = true },
                        CancellationToken.None
                    );
                    await foreach (
                        var entry in archive.EntriesAsync.WithCancellation(CancellationToken.None)
                    )
                    {
                        if (entry.IsDirectory)
                        {
                            continue;
                        }
                        await entry.WriteToAsync(
                            Stream.Null,
                            new ExtractionOptions { CheckCrc = true },
                            cancellationToken: CancellationToken.None
                        );
                    }
                });
            }
            else
            {
                Assert.ThrowsAny<SharpCompressException>(() =>
                {
                    using var archive = SevenZipArchive.OpenArchive(
                        output,
                        new ReaderOptions { Password = "wrong", LeaveStreamOpen = true }
                    );
                    foreach (var entry in archive.Entries.Where(x => !x.IsDirectory))
                    {
                        entry.WriteTo(Stream.Null, new ExtractionOptions { CheckCrc = true });
                    }
                });
            }
        }
    }

    private static void CheckMetadata(
        IArchiveEntry entry,
        SevenZipWriterEntryOptions expected,
        bool directory
    )
    {
        Assert.Equal(directory, entry.IsDirectory);
        Assert.Equal(expected.CreationTime, entry.CreatedTime?.ToUniversalTime());
        Assert.Equal(expected.LastAccessTime, entry.LastAccessedTime?.ToUniversalTime());
        Assert.Equal(expected.ModificationTime, entry.LastModifiedTime?.ToUniversalTime());
        // Files without attributes get FILE_ATTRIBUTE_ARCHIVE, so the vector stays fully defined.
        var attributes = directory
            ? (expected.Attributes ?? 0) | 0x10
            : expected.Attributes ?? 0x20;
        // ExtendedAttrib is the raw 7z value; Attrib stays the Windows-only projection.
        Assert.Equal(unchecked((int)attributes), ((SevenZipEntry)entry).ExtendedAttrib);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Store_CopyFolderLayout(bool useAsync)
    {
        using var source = new MemoryStream("abc"u8.ToArray());
        using var output = new MemoryStream();
        var compressor = new SevenZipStreamsCompressor(output);
        var packed = useAsync
            ? await compressor.CompressAsync(
                source,
                CompressionType.None,
                cancellationToken: CancellationToken.None
            )
            : compressor.Compress(source, CompressionType.None);
        Assert.Equal("abc"u8.ToArray(), output.ToArray());
        Assert.Equal(3UL, Assert.Single(packed.Sizes));
        Assert.Equal(0x352441C2u, packed.Folder._unpackCrc);
        using var header = new MemoryStream();
        new SevenZipUnPackInfoWriter { Folders = [packed.Folder] }.Write(header);
        // Folder marker, count, external flag, coder count, one-byte method ID, Copy ID.
        Assert.Equal(new byte[] { 0x0B, 1, 0, 1, 1, 0 }, header.ToArray().Take(6).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void SevenZipFilesInfo_TimesAndAttributes_DefinedVectors(int count)
    {
        var time = DateTime.FromFileTimeUtc(1);
        var entries = Enumerable
            .Range(0, count)
            .Select(i => new SevenZipWriteEntry
            {
                Name = "",
                CreationTime = i % 2 == 0 ? time : null,
                LastAccessTime = i % 2 == 0 ? time : null,
                ModificationTime = i % 2 == 0 ? time : null,
                Attributes = i % 2 == 0 ? 0x81A48000u : null,
            })
            .ToArray();
        using var stream = new MemoryStream();
        new SevenZipFilesInfoWriter { Entries = entries }.Write(stream);
        using var expected = new MemoryStream();
        expected.WriteByte((byte)count);
        expected.WriteByte(17);
        expected.WriteByte((byte)(1 + 2 * count));
        expected.Write(new byte[1 + 2 * count], 0, 1 + 2 * count);
        for (byte property = 18; property <= 21; property++)
        {
            var width = property == 21 ? 4 : 8;
            expected.WriteByte(property);
            expected.WriteByte((byte)(2 + (count == 1 ? 0 : 2) + width * ((count + 1) / 2)));
            expected.WriteByte((byte)(count == 1 ? 1 : 0));
            if (count != 1)
            {
                expected.WriteByte(0xAA);
                expected.WriteByte(0xA0);
            }
            expected.WriteByte(0);
            for (var i = 0; i < count; i += 2)
            {
                var bytes =
                    property == 21 ? BitConverter.GetBytes(0x81A48000u) : BitConverter.GetBytes(1L);
                expected.Write(bytes, 0, bytes.Length);
            }
        }
        expected.WriteByte(0);
        Assert.Equal(expected.ToArray(), stream.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_LegacyNullMetadata_StableBytes(bool useAsync)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions { CompressHeader = false }
        );
        using var source = new MemoryStream();
        if (useAsync)
        {
            await writer.WriteAsync("a", source, null, CancellationToken.None);
            await writer.DisposeAsync();
        }
        else
        {
            writer.Write("a", source, null);
            writer.Dispose();
        }
        // Legacy raw header: an empty file named "a", default archive attribute, no times.
        byte[] header =
        [
            1,
            5,
            1,
            14,
            1,
            128,
            15,
            1,
            128,
            17,
            5,
            0,
            97,
            0,
            0,
            0,
            21,
            6,
            1,
            0,
            32,
            0,
            0,
            0,
            0,
            0,
        ];
        Assert.Equal(header, output.ToArray().Skip(32).ToArray());
    }

    [Fact]
    public async Task SevenZipWriter_Metadata_Cancellation()
    {
        using var output = new MemoryStream();
        await using var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions(CompressionType.None)
        );
        using var source = new MemoryStream("data"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.WriteAsync(
                "file",
                source,
                new SevenZipWriterEntryOptions(),
                cancellation.Token
            )
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.WriteDirectoryAsync(
                "dir",
                new SevenZipWriterEntryOptions(),
                cancellation.Token
            )
        );
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void SevenZipFilesInfo_NoMetadata_OmitsOptionalProperties()
    {
        using var stream = new MemoryStream();
        new SevenZipFilesInfoWriter
        {
            Entries = [new SevenZipWriteEntry { Name = "a", Attributes = null }],
        }.Write(stream);
        Assert.Equal(new byte[] { 1, 17, 5, 0, 97, 0, 0, 0, 0 }, stream.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SevenZipWriter_Store_StreamOwnership(bool useAsync, bool leaveOpen)
    {
        using var output = new MemoryStream();
        using var source = new MemoryStream("content"u8.ToArray());
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions(CompressionType.None) { LeaveStreamOpen = leaveOpen }
        );
        if (useAsync)
        {
            await writer.WriteAsync(
                "file",
                source,
                new SevenZipWriterEntryOptions(),
                CancellationToken.None
            );
            await writer.DisposeAsync();
        }
        else
        {
            writer.Write("file", source, new SevenZipWriterEntryOptions());
            writer.Dispose();
        }
        Assert.Equal(leaveOpen, output.CanWrite);
        Assert.True(source.CanRead);
    }
}
