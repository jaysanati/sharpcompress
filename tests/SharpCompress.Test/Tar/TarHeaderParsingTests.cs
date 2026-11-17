using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Common.Tar;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Readers.Tar;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Tar;

public class TarHeaderParsingTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (
            var scenario in new[]
            {
                "gnu",
                "prefix",
                "prefix155",
                "space",
                "nul",
                "link",
                "longname",
                "longlink",
                "binary",
                "badmode",
                "baduid",
                "badgid",
                "badtime",
                "overflowtime",
                "futuretime",
                "paddednumbers",
            }
        )
        {
            foreach (var useAsync in new[] { false, true })
            {
                foreach (var reader in new[] { false, true })
                {
                    yield return new object[] { scenario, useAsync, reader };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Tar_Header_Metadata(string scenario, bool useAsync, bool useReader)
    {
        var header = Header();
        var expectedName = "dir/report";
        string? expectedLink = null;
        long mode = 420,
            uid = 0,
            gid = 0;
        var time = TarHeader.EPOCH.ToLocalTime();
        byte[]? extension = null;
        switch (scenario)
        {
            case "gnu":
                Put(header, 257, "ustar  \0");
                Put(header, 345, "14707041774 14707041774");
                break;
            case "prefix":
                Put(header, 345, " parent ");
                expectedName = " parent /" + expectedName;
                break;
            case "prefix155":
                Put(header, 345, new string('p', 155) + "XY");
                expectedName = new string('p', 155) + "/" + expectedName;
                break;
            case "space":
                expectedName = " dir/report ";
                Put(header, 0, expectedName);
                break;
            case "nul":
                Put(header, 0, "dir/report\0ignored");
                break;
            case "link":
                header[156] = (byte)'2';
                expectedLink = " target ";
                Put(header, 157, expectedLink + "\0ignored");
                break;
            case "longname":
            case "longlink":
                var name = " " + new string('n', 110) + " ";
                var payload = Encoding.ASCII.GetBytes(name + "\0ignored");
                var extraHeader = Header();
                extraHeader[156] = (byte)(scenario == "longname" ? 'L' : 'K');
                Put(extraHeader, 124, Convert.ToString(payload.Length, 8).PadLeft(11, '0'));
                extension = Pack(extraHeader, payload, false);
                if (scenario == "longname")
                {
                    expectedName = name;
                }
                else
                {
                    expectedLink = name;
                    header[156] = (byte)'2';
                }
                break;
            case "binary":
                mode = 493;
                uid = -123;
                gid = -456;
                Binary(header, 100, 8, mode);
                Binary(header, 108, 8, uid);
                Binary(header, 116, 8, gid);
                Binary(header, 124, 12, 1);
                Binary(header, 136, 12, -86400);
                time = TarHeader.EPOCH.AddDays(-1).ToLocalTime();
                break;
            case "badmode":
                Put(header, 100, "garbage!");
                mode = 0;
                break;
            case "baduid":
                Put(header, 108, "garbage!");
                break;
            case "badgid":
                Put(header, 116, "garbage!");
                break;
            case "badtime":
                Put(header, 136, "garbage!!!!!");
                break;
            case "paddednumbers":
                Put(header, 100, "  644 \0 ");
                Put(header, 108, "\0 12 \0  ");
                Put(header, 124, "  0000001 \0 ");
                uid = 10;
                break;
            case "futuretime":
                Binary(header, 136, 12, 10000000000);
                time = TarHeader.EPOCH.AddSeconds(10000000000).ToLocalTime();
                break;
            case "overflowtime":
                Binary(header, 136, 12, long.MaxValue);
                break;
        }
        var bytes = Pack(header, new byte[] { 42 });
        if (extension is not null)
        {
            bytes = extension.Concat(bytes).ToArray();
        }
        using var stream = new MemoryStream(bytes);
        void Verify(TarEntry entry)
        {
            Assert.Equal(expectedName, entry.Key);
            Assert.Equal(expectedLink, entry.LinkTarget);
            Assert.Equal(mode, entry.Mode);
            Assert.Equal(uid, entry.UserID);
            Assert.Equal(gid, entry.GroupId);
            Assert.Equal(time, entry.LastModifiedTime);
            Assert.Equal(1, entry.Size);
        }
        if (useReader)
        {
            using var forward = new ForwardOnlyStream(stream);
            if (useAsync)
            {
                await using var reader = await TarReader.OpenAsyncReader(
                    forward,
                    ReaderOptions.ForExternalStream,
                    CancellationToken.None
                );
                Assert.True(await reader.MoveToNextEntryAsync(CancellationToken.None));
                Verify((TarEntry)reader.Entry);
                using var data = await reader.OpenEntryStreamAsync(CancellationToken.None);
                var value = new byte[1];
                Assert.Equal(1, await data.ReadAsync(value, 0, 1, CancellationToken.None));
                Assert.Equal(42, value[0]);
            }
            else
            {
                using var reader = TarReader.OpenReader(forward, ReaderOptions.ForExternalStream);
                Assert.True(reader.MoveToNextEntry());
                Verify((TarEntry)reader.Entry);
                using var data = reader.OpenEntryStream();
                Assert.Equal(42, data.ReadByte());
            }
        }
        else if (useAsync)
        {
            await using var archive = await TarArchive.OpenAsyncArchive(
                stream,
                ReaderOptions.ForExternalStream,
                CancellationToken.None
            );
            var entry = await archive.EntriesAsync.SingleAsync();
            Verify((TarEntry)entry);
            using var data = await entry.OpenEntryStreamAsync(CancellationToken.None);
            var value = new byte[1];
            Assert.Equal(1, await data.ReadAsync(value, 0, 1, CancellationToken.None));
            Assert.Equal(42, value[0]);
        }
        else
        {
            using var archive = TarArchive.OpenArchive(stream, ReaderOptions.ForExternalStream);
            var entry = archive.Entries.Single();
            Verify((TarEntry)entry);
            using var data = entry.OpenEntryStream();
            Assert.Equal(42, data.ReadByte());
        }
    }

    [Theory]
    [InlineData(false, "garbage")]
    [InlineData(false, "negative")]
    [InlineData(false, "overflow")]
    [InlineData(true, "garbage")]
    [InlineData(true, "negative")]
    [InlineData(true, "overflow")]
    public async Task Tar_Invalid_Size_Throws(bool useAsync, string scenario)
    {
        var header = Header();
        if (scenario == "negative")
        {
            Binary(header, 124, 12, -1);
        }
        else if (scenario == "overflow")
        {
            Binary(header, 124, 12, 1);
            header[125] = 1; // A nonzero high byte must not be discarded when reading Int64.
        }
        else
        {
            Put(header, 124, "garbage!!!!!");
        }
        using var stream = new MemoryStream(Pack(header, new byte[] { 42 }));
        if (useAsync)
        {
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
            {
                await using var reader = await TarReader.OpenAsyncReader(
                    stream,
                    ReaderOptions.ForExternalStream,
                    CancellationToken.None
                );
                await reader.MoveToNextEntryAsync(CancellationToken.None);
            });
        }
        else
        {
            Assert.Throws<InvalidFormatException>(() =>
            {
                using var reader = TarReader.OpenReader(stream, ReaderOptions.ForExternalStream);
                reader.MoveToNextEntry();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tar_Binary_Numeric_Bounds(bool useAsync)
    {
        var bytes = Header();
        Binary(bytes, 108, 8, 1L << 48);
        Binary(bytes, 116, 8, -1);
        Binary(bytes, 329, 8, 123);
        Binary(bytes, 337, 8, -456);
        Binary(bytes, 136, 12, long.MinValue);
        using var stream = new MemoryStream(Pack(bytes, new byte[] { 42 }));
        var header = new TarHeader(new ArchiveEncoding());
        if (useAsync)
        {
            using var reader = new AsyncBinaryReader(stream, leaveOpen: true);
            Assert.True(await header.ReadAsync(reader, cancellationToken: CancellationToken.None));
        }
        else
        {
            using var reader = new BinaryReader(stream);
            Assert.True(header.Read(reader));
        }
        Assert.Equal(1L << 48, header.UserId);
        Assert.Equal(-1, header.GroupId);
        Assert.Equal(123, header.DeviceMajor);
        Assert.Equal(-456, header.DeviceMinor);
        Assert.Equal(TarHeader.EPOCH.ToLocalTime(), header.LastModifiedTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tar_HeaderFactory_Propagates_Cancellation(bool useAsync)
    {
        using var stream = new CancelledStream();
        if (useAsync)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (
                    var header in TarHeaderFactory.ReadHeaderAsync(
                        StreamingMode.Streaming,
                        stream,
                        new ArchiveEncoding()
                    )
                ) { }
            });
        }
        else
        {
            Assert.Throws<OperationCanceledException>(() =>
                TarHeaderFactory
                    .ReadHeader(StreamingMode.Streaming, stream, new ArchiveEncoding())
                    .First()
            );
        }
    }

    [Theory]
    [InlineData(false, "checksum")]
    [InlineData(true, "checksum")]
    [InlineData(false, "checksumtext")]
    [InlineData(true, "checksumtext")]
    [InlineData(false, "emptyname")]
    [InlineData(true, "emptyname")]
    [InlineData(false, "longname")]
    [InlineData(true, "longname")]
    [InlineData(false, "pax")]
    [InlineData(true, "pax")]
    [InlineData(false, "devmajor")]
    [InlineData(true, "devmajor")]
    [InlineData(false, "devminor")]
    [InlineData(true, "devminor")]
    [InlineData(false, "zero")]
    [InlineData(true, "zero")]
    public async Task Tar_Nonzero_Corrupt_Header_Is_Not_End_Of_Archive(
        bool useAsync,
        string scenario
    )
    {
        var header = Header();
        switch (scenario)
        {
            case "emptyname":
                Array.Clear(header, 0, 100);
                // Match the regression: a non-zero header with no usable POSIX prefix.
                Put(header, 257, "ustar  \0");
                Put(header, 345, "garbage");
                break;
            case "checksum":
                Put(header, 100, "garbage!");
                break;
            case "longname":
                header[156] = (byte)'L';
                break;
            case "pax":
                header[156] = (byte)'x';
                break;
            case "devmajor":
                Put(header, 329, "garbage!");
                break;
            case "devminor":
                Put(header, 337, "garbage!");
                break;
        }
        var tail = Pack(header, new byte[] { 42 });
        if (scenario == "checksum" || scenario == "longname" || scenario == "pax")
        {
            tail[0] ^= 1;
        }
        else if (scenario == "checksumtext")
        {
            Put(tail, 148, "garbage!");
        }
        else if (scenario == "zero")
        {
            tail = new byte[1024];
        }
        var bytes = Pack(Header(), new byte[] { 42 }, false).Concat(tail).ToArray();
        using var stream = new MemoryStream(bytes);
        if (useAsync)
        {
            await using var reader = await TarReader.OpenAsyncReader(
                stream,
                ReaderOptions.ForExternalStream,
                CancellationToken.None
            );
            Assert.True(await reader.MoveToNextEntryAsync(CancellationToken.None));
            if (scenario == "zero")
            {
                Assert.False(await reader.MoveToNextEntryAsync(CancellationToken.None));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                    await reader.MoveToNextEntryAsync(CancellationToken.None)
                );
            }
        }
        else
        {
            using var reader = TarReader.OpenReader(stream, ReaderOptions.ForExternalStream);
            Assert.True(reader.MoveToNextEntry());
            if (scenario == "zero")
            {
                Assert.False(reader.MoveToNextEntry());
            }
            else
            {
                Assert.Throws<InvalidFormatException>(() => reader.MoveToNextEntry());
            }
        }
    }

    private sealed class CancelledStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new OperationCanceledException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => throw new OperationCanceledException();

#if NET6_0_OR_GREATER
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => throw new OperationCanceledException();
#endif
    }

    private static byte[] Header()
    {
        var header = new byte[512];
        Put(header, 0, "dir/report");
        Put(header, 100, "0000644");
        Put(header, 124, "00000000001");
        Put(header, 257, "ustar\000");
        header[156] = (byte)'0';
        return header;
    }

    private static void Put(byte[] buffer, int offset, string value) =>
        Encoding.ASCII.GetBytes(value).CopyTo(buffer, offset);

    private static void Binary(byte[] buffer, int offset, int count, long value)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            buffer[offset + i] = (byte)value;
            value >>= 8;
        }
        buffer[offset] |= 0x80;
    }

    private static byte[] Pack(byte[] header, byte[] payload, bool end = true)
    {
        Put(header, 148, "        ");
        Put(header, 148, Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0') + "\0 ");
        var result = new byte[512 + ((payload.Length + 511) / 512 * 512) + (end ? 1024 : 0)];
        header.CopyTo(result, 0);
        payload.CopyTo(result, 512);
        return result;
    }
}
