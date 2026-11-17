using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipReadIntegrityTests
{
    private static readonly byte[] Data = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

    private static byte[] CreateArchive(bool encrypted = false, bool solid = false)
    {
        using var output = new MemoryStream();
        using (
            var writer = new SevenZipWriter(
                output,
                new SevenZipWriterOptions(CompressionType.None)
                {
                    LeaveStreamOpen = true,
                    Password = encrypted ? "correct" : null,
                }
            )
        )
        {
            if (solid)
            {
                writer.BeginSolidBlock();
            }
            using var source = new MemoryStream(Data);
            writer.Write("a", source, null);
            if (solid)
            {
                using var second = new MemoryStream(Data.Reverse().ToArray());
                writer.Write("b", second, null);
            }
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task SevenZip_Copy_Crc(bool asynchronous, bool readerApi, bool encrypted)
    {
        var bytes = CreateArchive(encrypted);
        if (!encrypted)
        {
            bytes[32] ^= 1;
        }
        using var input = new MemoryStream(bytes);
        var options = new ReaderOptions
        {
            LeaveStreamOpen = true,
            Password = encrypted ? "wrong" : null,
        };
        using var archive = asynchronous
            ? (SevenZipArchive)
                await SevenZipArchive.OpenAsyncArchive(input, options, CancellationToken.None)
            : (SevenZipArchive)SevenZipArchive.OpenArchive(input, options);
        if (readerApi)
        {
            if (asynchronous)
            {
                await using var reader = await archive.ExtractAllEntriesAsync();
                Assert.True(await reader.MoveToNextEntryAsync(CancellationToken.None));
                await using var stream = await reader.OpenEntryStreamAsync(CancellationToken.None);
                var error = await Assert.ThrowsAsync<ChecksumMismatchException>(async () =>
                    await stream.CopyToAsync(Stream.Null, 32, CancellationToken.None)
                );
                if (encrypted)
                {
                    Assert.Contains("password", error.Message);
                }
            }
            else
            {
                using var reader = archive.ExtractAllEntries();
                Assert.True(reader.MoveToNextEntry());
                using var stream = reader.OpenEntryStream();
                var error = Assert.Throws<ChecksumMismatchException>(() =>
                    stream.CopyTo(Stream.Null)
                );
                if (encrypted)
                {
                    Assert.Contains("password", error.Message);
                }
            }
        }
        else
        {
            var entry = asynchronous
                ? (await archive.EntriesAsync.ToListAsync()).Single()
                : archive.Entries.Single();
            using var stream = asynchronous
                ? await entry.OpenEntryStreamAsync(CancellationToken.None)
                : entry.OpenEntryStream();
            var buffer = new byte[Data.Length];
            // A read ending exactly at the declared size must validate without an extra EOF read.
            if (asynchronous)
            {
                await Assert.ThrowsAsync<ChecksumMismatchException>(async () =>
                    await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
                );
            }
            else
            {
                Assert.Throws<ChecksumMismatchException>(() =>
                    stream.Read(buffer, 0, buffer.Length)
                );
            }
        }
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SevenZip_Partial_Dispose_Does_Not_Check_Crc(bool asynchronous, bool readerApi)
    {
        var bytes = CreateArchive();
        bytes[32] ^= 1;
        using var input = new MemoryStream(bytes);
        using var archive = asynchronous
            ? (SevenZipArchive)
                await SevenZipArchive.OpenAsyncArchive(
                    input,
                    cancellationToken: CancellationToken.None
                )
            : (SevenZipArchive)SevenZipArchive.OpenArchive(input);
        if (readerApi)
        {
            if (asynchronous)
            {
                await using var reader = await archive.ExtractAllEntriesAsync();
                await reader.MoveToNextEntryAsync(CancellationToken.None);
                await using var stream = await reader.OpenEntryStreamAsync(CancellationToken.None);
                Assert.Equal(1, await stream.ReadAsync(new byte[1], 0, 1, CancellationToken.None));
            }
            else
            {
                using var reader = archive.ExtractAllEntries();
                reader.MoveToNextEntry();
                using var stream = reader.OpenEntryStream();
                Assert.NotEqual(-1, stream.ReadByte());
            }
        }
        else
        {
            var entry = asynchronous
                ? (await archive.EntriesAsync.ToListAsync()).Single()
                : archive.Entries.Single();
            using var stream = asynchronous
                ? await entry.OpenEntryStreamAsync(CancellationToken.None)
                : entry.OpenEntryStream();
            Assert.Equal(
                1,
                asynchronous
                    ? await stream.ReadAsync(new byte[1], 0, 1, CancellationToken.None)
                    : stream.Read(new byte[1], 0, 1)
            );
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SevenZip_Solid_Partial_Then_Next(bool asynchronous, bool disposeFirst)
    {
        using var input = new MemoryStream(CreateArchive(solid: true));
        using var archive = asynchronous
            ? (SevenZipArchive)
                await SevenZipArchive.OpenAsyncArchive(
                    input,
                    cancellationToken: CancellationToken.None
                )
            : (SevenZipArchive)SevenZipArchive.OpenArchive(input);
        var entries = asynchronous
            ? (await archive.EntriesAsync.ToListAsync()).ToArray()
            : archive.Entries.ToArray();
        using var first = asynchronous
            ? await entries[0].OpenEntryStreamAsync(CancellationToken.None)
            : entries[0].OpenEntryStream();
        var buffer = new byte[Data.Length / 2];
        Assert.Equal(
            buffer.Length,
            asynchronous
                ? await first.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
                : first.Read(buffer, 0, buffer.Length)
        );
        if (disposeFirst)
        {
            first.Dispose();
        }
        using var second = asynchronous
            ? await entries[1].OpenEntryStreamAsync(CancellationToken.None)
            : entries[1].OpenEntryStream();
        using var output = new MemoryStream();
        if (asynchronous)
        {
            await second.CopyToAsync(output, 32, CancellationToken.None);
        }
        else
        {
            second.CopyTo(output);
        }
        Assert.Equal(Data.Reverse(), output.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SevenZip_Truncated_Folder_Throws(bool asynchronous, bool readerApi)
    {
        using var input = new MemoryStream(CreateArchive());
        using var archive = asynchronous
            ? (SevenZipArchive)
                await SevenZipArchive.OpenAsyncArchive(
                    input,
                    cancellationToken: CancellationToken.None
                )
            : (SevenZipArchive)SevenZipArchive.OpenArchive(input);
        var entry = asynchronous
            ? (await archive.EntriesAsync.ToListAsync()).Single()
            : archive.Entries.Single();
        // Keep the parsed header and declared size but remove the last packed byte.
        input.SetLength(32 + Data.Length - 1);
        if (readerApi)
        {
            if (asynchronous)
            {
                await using var reader = await archive.ExtractAllEntriesAsync();
                await reader.MoveToNextEntryAsync(CancellationToken.None);
                await using var readerStream = await reader.OpenEntryStreamAsync(
                    CancellationToken.None
                );
                await Assert.ThrowsAsync<EndOfStreamException>(async () =>
                    await readerStream.CopyToAsync(Stream.Null, 32, CancellationToken.None)
                );
            }
            else
            {
                using var reader = archive.ExtractAllEntries();
                reader.MoveToNextEntry();
                using var readerStream = reader.OpenEntryStream();
                Assert.Throws<EndOfStreamException>(() => readerStream.CopyTo(Stream.Null));
            }
            return;
        }
        using var stream = asynchronous
            ? await entry.OpenEntryStreamAsync(CancellationToken.None)
            : entry.OpenEntryStream();
        if (asynchronous)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(async () =>
                await stream.CopyToAsync(Stream.Null, 32, CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<EndOfStreamException>(() => stream.CopyTo(Stream.Null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZip_Undefined_Crc_Still_Enforces_Size(bool asynchronous)
    {
        using var input = new MemoryStream(CreateArchive());
        using var archive = asynchronous
            ? (SevenZipArchive)
                await SevenZipArchive.OpenAsyncArchive(
                    input,
                    cancellationToken: CancellationToken.None
                )
            : (SevenZipArchive)SevenZipArchive.OpenArchive(input);
        var entry = asynchronous
            ? (await archive.EntriesAsync.ToListAsync()).Single()
            : archive.Entries.Single();
        var header = ((SevenZipArchiveEntry)entry).FilePart.Header;
        header.Crc = null;
        header.Size++;
        using var stream = asynchronous
            ? await entry.OpenEntryStreamAsync(CancellationToken.None)
            : entry.OpenEntryStream();
        var buffer = new byte[Data.Length];
        if (asynchronous)
        {
            Assert.Equal(0, await stream.ReadAsync(buffer, 0, 0, CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await stream.ReadAsync(buffer, 0, buffer.Length, cancellation.Token)
            );
            await Assert.ThrowsAsync<EndOfStreamException>(async () =>
                await stream.CopyToAsync(Stream.Null, 32, CancellationToken.None)
            );
        }
        else
        {
            Assert.Equal(0, stream.Read(buffer, 0, 0));
            Assert.Throws<EndOfStreamException>(() => stream.CopyTo(Stream.Null));
        }
    }
}
