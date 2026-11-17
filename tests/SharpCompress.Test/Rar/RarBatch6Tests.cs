using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Compressors.Rar;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Rar;

public class RarBatch6Tests : TestBase
{
    public static IEnumerable<object[]> InvalidSizes()
    {
        foreach (
            var kind in new[]
            {
                "rar5-packed",
                "rar5-unpacked",
                "rar4-packed",
                "rar4-unpacked",
                "past-end",
            }
        )
        {
            foreach (var useAsync in new[] { false, true })
            {
                foreach (var reader in new[] { false, true })
                {
                    if (kind != "past-end" || !reader)
                    {
                        yield return [kind, useAsync, reader];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidSizes))]
    public async Task Rar_Invalid_Sizes(string kind, bool useAsync, bool reader)
    {
        var bytes = kind.StartsWith("rar4", StringComparison.Ordinal)
            ? Rar4(
                kind == "rar4-packed" ? 0x80000000U : 0,
                kind == "rar4-unpacked" ? 0x80000000U : 0
            )
            : Rar5(
                kind == "rar5-packed" ? ulong.MaxValue
                    : kind == "past-end" ? 100UL
                    : 5UL,
                kind == "rar5-unpacked" ? 1UL << 63 : 5UL
            );
        await Assert.ThrowsAsync<InvalidFormatException>(() =>
            ReadArchive(bytes, useAsync, reader)
        );
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rar5_Unknown_Size(bool useAsync, bool reader)
    {
        var bytes = Rar5(5, ulong.MaxValue, unknown: true);
        Assert.Equal("hello", await ReadArchive(bytes, useAsync, reader, unknown: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar_Header_Eof_And_Truncation(bool useAsync)
    {
        foreach (var seekable in new[] { false, true })
        {
            // Legacy archives may end exactly between headers, but never inside a header.
            foreach (var trailing in new[] { 0, 1, 3 })
            {
                var bytes = Rar5(5, 5, end: false).Concat(new byte[trailing]).ToArray();
                using var memory = new MemoryStream(bytes);
                using Stream source = seekable
                    ? SharpCompressStream.CreateNonDisposing(memory)
                    : new ForwardOnlyStream(memory);
                var factory = new RarHeaderFactory(
                    StreamingMode.Seekable,
                    ReaderOptions.ForExternalStream
                );
                if (!seekable)
                {
                    factory = new RarHeaderFactory(
                        StreamingMode.Streaming,
                        ReaderOptions.ForExternalStream
                    );
                }
                async Task Scan()
                {
                    if (useAsync)
                    {
                        await foreach (var h in factory.ReadHeadersAsync(source))
                        {
                            if (h is FileHeader { PackedStream: { } packed })
                            {
                                await packed.CopyToAsync(
                                    Stream.Null,
                                    81920,
                                    CancellationToken.None
                                );
                            }
                        }
                    }
                    else
                    {
                        foreach (var h in factory.ReadHeaders(source))
                        {
                            if (h is FileHeader { PackedStream: { } packed })
                            {
                                packed.CopyTo(Stream.Null);
                            }
                        }
                    }
                }
                if (trailing == 0)
                {
                    await Scan();
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidFormatException>(Scan);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar4_Encrypted_Header_Wrong_Passwords(bool useAsync)
    {
        var bytes = File.ReadAllBytes(
            Path.Combine(TEST_ARCHIVES_PATH, "Rar.encrypted_filesAndHeader.rar")
        );
        for (var i = 0; i < 32; i++)
        {
            await Assert.ThrowsAsync<CryptographicException>(() =>
                ReadArchive(bytes, useAsync, false, password: "wrong-" + i)
            );
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar_Exact_Length_Checksum(bool useAsync)
    {
        var bytes = Rar5(5, 5);
        // The header checksum remains valid; only the stored payload changes.
        using (var memory = new MemoryStream(bytes))
        {
            var header = new RarHeaderFactory(
                StreamingMode.Seekable,
                ReaderOptions.ForExternalStream
            )
                .ReadHeaders(memory)
                .OfType<FileHeader>()
                .Single();
            bytes[(int)header.DataStartPosition] ^= 1;
        }
        await Assert.ThrowsAsync<InvalidFormatException>(() =>
            ReadArchive(bytes, useAsync, false, exact: true)
        );
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rar5_Encrypted_Checksums(bool useAsync, bool blake)
    {
        // Generated once with RAR 7.23: a -ma5 -m0 -qo- -ptest -ep [-htb] archive payload.txt.
        var name = blake ? "Rar5.batch6.encrypted-blake.rar" : "Rar5.batch6.encrypted-crc.rar";
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, name));
        const string expected = "RAR batch 6 checksum fixture.";
        Assert.Equal(
            expected,
            await ReadArchive(bytes, useAsync, false, password: "test", exact: true)
        );
        Assert.Equal(
            expected,
            await ReadArchive(bytes, useAsync, true, password: "test", exact: true)
        );
        using (var memory = new MemoryStream(bytes))
        {
            var header = new RarHeaderFactory(
                StreamingMode.Seekable,
                ReaderOptions.ForExternalStream
            )
                .ReadHeaders(memory)
                .OfType<FileHeader>()
                .Single();
            bytes[(int)header.DataStartPosition] ^= 1;
        }
        await Assert.ThrowsAsync<CryptographicException>(() =>
            ReadArchive(bytes, useAsync, false, password: "test", exact: true)
        );
        await Assert.ThrowsAsync<CryptographicException>(() =>
            ReadArchive(bytes, useAsync, true, password: "test", exact: true)
        );
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rar4_Encrypted_Stored_Checksum(bool useAsync, bool reader)
    {
        // Deterministic RAR4 AES-CBC stored fixture, salt 00..07, password "test"; verified with WinRAR.
        var bytes = File.ReadAllBytes(
            Path.Combine(TEST_ARCHIVES_PATH, "Rar4.batch6.encrypted-stored.rar")
        );
        Assert.Equal(
            "RAR batch 6 checksum fixture.",
            await ReadArchive(bytes, useAsync, reader, password: "test", exact: true)
        );
        await Assert.ThrowsAsync<CryptographicException>(() =>
            ReadArchive(bytes, useAsync, reader, password: "wrong", exact: true)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar5_Solid_Redirect_Traversal(bool useAsync)
    {
        var original = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Rar5.solid.rar"));
        long insert;
        using (var memory = new MemoryStream(original))
        {
            var first = new RarHeaderFactory(
                StreamingMode.Seekable,
                ReaderOptions.ForExternalStream
            )
                .ReadHeaders(memory)
                .OfType<FileHeader>()
                .First();
            insert = first.DataStartPosition + first.CompressedSize;
        }
        var expected = await ReadArchive(original, useAsync, true);
        foreach (byte type in new byte[] { 1, 2, 3, 4, 5 })
        {
            using var redirect = new MemoryStream();
            using (var writer = new BinaryWriter(redirect, Encoding.UTF8, true))
            {
                // Extra record: size=9, type=5, redir type, flags=0, target length=5, "first".
                WriteHeader(
                    writer,
                    new byte[]
                    {
                        2,
                        1,
                        10,
                        0,
                        0,
                        0,
                        0,
                        1,
                        4,
                        (byte)'l',
                        (byte)'i',
                        (byte)'n',
                        (byte)'k',
                        9,
                        5,
                        type,
                        0,
                        5,
                        (byte)'f',
                        (byte)'i',
                        (byte)'r',
                        (byte)'s',
                        (byte)'t',
                    }
                );
            }
            var bytes = original
                .Take((int)insert)
                .Concat(redirect.ToArray())
                .Concat(original.Skip((int)insert))
                .ToArray();
            Assert.Equal(expected, await ReadArchive(bytes, useAsync, true));
            using var source = new MemoryStream(bytes);
            using var archive = RarArchive.OpenArchive(source, ReaderOptions.ForExternalStream);
            var link = archive.Entries.Cast<RarArchiveEntry>().Single(e => e.IsRedir);
            Assert.Equal(type, link.RedirType);
            Assert.Equal(type <= 3 ? "first" : null, link.LinkTarget);
            Assert.Throws<ArchiveOperationException>(() => link.OpenEntryStream());
            await Assert.ThrowsAsync<ArchiveOperationException>(async () =>
                await link.OpenEntryStreamAsync(CancellationToken.None)
            );
            using var all = archive.ExtractAllEntries();
            using var data = new MemoryStream();
            while (all.MoveToNextEntry())
            {
                if (((RarEntry)all.Entry).IsRedir)
                {
                    continue;
                }
                if (all.Entry.IsDirectory)
                {
                    continue;
                }
                using var input = all.OpenEntryStream();
                input.CopyTo(data);
            }
            Assert.Equal(expected, Encoding.UTF8.GetString(data.ToArray()));
            using var asyncSource = new MemoryStream(bytes);
            await using var asyncArchive = await RarArchive.OpenAsyncArchive(
                asyncSource,
                ReaderOptions.ForExternalStream,
                CancellationToken.None
            );
            await using var asyncAll = await asyncArchive.ExtractAllEntriesAsync();
            using var asyncData = new MemoryStream();
            while (await asyncAll.MoveToNextEntryAsync(CancellationToken.None))
            {
                if (((RarEntry)asyncAll.Entry).IsRedir || asyncAll.Entry.IsDirectory)
                {
                    continue;
                }
                await using var input = await asyncAll.OpenEntryStreamAsync(CancellationToken.None);
                await input.CopyToAsync(asyncData, 81920, CancellationToken.None);
            }
            Assert.Equal(expected, Encoding.UTF8.GetString(asyncData.ToArray()));
        }
    }

#if !LEGACY_DOTNET
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar5_Encrypted_Memory_Read_Checksum(bool blake)
    {
        var name = blake ? "Rar5.batch6.encrypted-blake.rar" : "Rar5.batch6.encrypted-crc.rar";
        var original = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, name));
        foreach (var corrupt in new[] { false, true })
        {
            var bytes = (byte[])original.Clone();
            if (corrupt)
            {
                using var scan = new MemoryStream(bytes);
                var header = new RarHeaderFactory(
                    StreamingMode.Seekable,
                    ReaderOptions.ForExternalStream
                )
                    .ReadHeaders(scan)
                    .OfType<FileHeader>()
                    .Single();
                bytes[(int)header.DataStartPosition] ^= 1;
            }
            using var source = new MemoryStream(bytes);
            await using var archive = await RarArchive.OpenAsyncArchive(
                source,
                ReaderOptions.ForExternalStream.WithPassword("test"),
                CancellationToken.None
            );
            var entry = await archive.EntriesAsync.FirstAsync();
            using var input = await entry.OpenEntryStreamAsync(CancellationToken.None);
            var buffer = new byte[(int)entry.Size];
            async Task ReadExactly()
            {
                Assert.Equal(0, await input.ReadAsync(Memory<byte>.Empty, CancellationToken.None));
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var count = await input.ReadAsync(
                        buffer.AsMemory(offset),
                        CancellationToken.None
                    );
                    Assert.True(count > 0);
                    offset += count;
                }
            }
            if (corrupt)
            {
                await Assert.ThrowsAsync<CryptographicException>(ReadExactly);
            }
            else
            {
                await ReadExactly();
                Assert.Equal("RAR batch 6 checksum fixture.", Encoding.UTF8.GetString(buffer));
                Assert.Equal(0, await input.ReadAsync(buffer.AsMemory(), CancellationToken.None));
            }
        }
    }
#endif

    [Theory]
    [InlineData(false, "Rar5.rar")]
    [InlineData(true, "Rar5.rar")]
    [InlineData(false, "Rar5.solid.rar")]
    [InlineData(true, "Rar5.solid.rar")]
    public async Task Rar5_Unknown_Compressed_Size(bool useAsync, string filename)
    {
        var original = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, filename));
        var expected = await ReadArchive(original, useAsync, true);
        var changed = RewriteFileHeaders(
            original,
            body =>
            {
                var offset = 0;
                _ = ReadVInt(body, ref offset);
                var flags = ReadVInt(body, ref offset);
                if ((flags & 1) != 0)
                {
                    _ = ReadVInt(body, ref offset);
                }
                if ((flags & 2) != 0)
                {
                    _ = ReadVInt(body, ref offset);
                }
                body[offset] |= 8;
            }
        );
        Assert.Equal(expected, await ReadArchive(changed, useAsync, true, unknown: true));
        Assert.Equal(expected, await ReadArchive(changed, useAsync, false, unknown: true));
    }

    [Theory]
    [InlineData(false, "absent")]
    [InlineData(true, "absent")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    [InlineData(false, "plain-crc")]
    [InlineData(true, "plain-crc")]
    public async Task Rar5_Encryption_Metadata_Without_Verifier(bool useAsync, string mode)
    {
        var original = File.ReadAllBytes(
            Path.Combine(TEST_ARCHIVES_PATH, "Rar5.batch6.encrypted-crc.rar")
        );
        var changed = RewriteFileHeaders(
            original,
            body =>
            {
                var offset = 0;
                _ = ReadVInt(body, ref offset);
                _ = ReadVInt(body, ref offset);
                var extraSizeOffset = offset;
                var extraSize = (int)ReadVInt(body, ref offset);
                _ = ReadVInt(body, ref offset); // packed size
                var fileFlags = ReadVInt(body, ref offset); // file flags
                _ = ReadVInt(body, ref offset); // unpacked size
                _ = ReadVInt(body, ref offset); // attributes
                if ((fileFlags & 2) != 0)
                {
                    offset += 4; // optional Unix mtime
                }
                var crcOffset = offset;
                var extra = body.Count - extraSize;
                var record = extra;
                var recordSize = (int)ReadVInt(body, ref record);
                Assert.Equal(1UL, ReadVInt(body, ref record));
                Assert.Equal(0UL, ReadVInt(body, ref record));
                var flagsOffset = record;
                _ = ReadVInt(body, ref record);
                var check = record + 1 + 16 + 16; // KDF count, salt, IV
                if (mode == "absent")
                {
                    body[flagsOffset] &= 0xfe;
                    body.RemoveRange(check, 12);
                    body[extra] = checked((byte)(recordSize - 12));
                    body[extraSizeOffset] = checked((byte)(extraSize - 12));
                }
                else if (mode == "invalid")
                {
                    body[check + 8] ^= 1;
                }
                else
                {
                    body[flagsOffset] &= 0xfd;
                    var payload = Encoding.UTF8.GetBytes("RAR batch 6 checksum fixture.");
                    var crc = ~RarCRC.CheckCrc(uint.MaxValue, payload, 0, payload.Length);
                    for (var i = 0; i < 4; i++)
                    {
                        body[crcOffset + i] = (byte)(crc >> (8 * i));
                    }
                }
            }
        );
        using (var source = new MemoryStream(changed))
        using (
            var archive = RarArchive.OpenArchive(
                source,
                ReaderOptions.ForExternalStream.WithPassword("test")
            )
        )
        {
            Assert.True(Assert.Single(archive.Entries).IsEncrypted);
        }
        Assert.Equal(
            "RAR batch 6 checksum fixture.",
            await ReadArchive(changed, useAsync, false, password: "test", exact: true)
        );
        Assert.Equal(
            "RAR batch 6 checksum fixture.",
            await ReadArchive(changed, useAsync, true, password: "test", exact: true)
        );
        await Assert.ThrowsAsync<CryptographicException>(() =>
            ReadArchive(changed, useAsync, false, password: "wrong", exact: true)
        );
    }

    private static byte[] RewriteFileHeaders(byte[] original, Action<List<byte>> change)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(original, 0, 8);
        var source = original.ToList();
        var position = 8;
        while (position < source.Count)
        {
            position += 4; // header CRC
            var size = (int)ReadVInt(source, ref position);
            var body = source.GetRange(position, size);
            position += size;
            var field = 0;
            var type = ReadVInt(body, ref field);
            var flags = ReadVInt(body, ref field);
            if ((flags & 1) != 0)
            {
                _ = ReadVInt(body, ref field);
            }
            var packed = (flags & 2) != 0 ? (int)ReadVInt(body, ref field) : 0;
            if (type == 2)
            {
                change(body);
            }
            WriteHeader(writer, body.ToArray());
            writer.Write(original, position, packed);
            position += packed;
        }
        return output.ToArray();
    }

    private static ulong ReadVInt(List<byte> bytes, ref int offset)
    {
        ulong value = 0;
        var shift = 0;
        byte next;
        do
        {
            next = bytes[offset++];
            value |= (ulong)(next & 127) << shift;
            shift += 7;
        } while ((next & 128) != 0);
        return value;
    }

    private static async Task<string> ReadArchive(
        byte[] bytes,
        bool useAsync,
        bool reader,
        bool unknown = false,
        string? password = null,
        bool exact = false
    )
    {
        using var source = new MemoryStream(bytes);
        using var output = new MemoryStream();
        var options = ReaderOptions.ForExternalStream.WithPassword(password);
        async Task Read(Stream input, RarEntry entry)
        {
            using (input)
            {
                Assert.Equal(unknown, entry.IsUnpackedSizeUnknown);
                if (unknown)
                {
                    Assert.Equal(long.MaxValue, entry.Size);
                }
                var buffer = new byte[exact ? checked((int)entry.Size) : 8192];
                long total = 0;
                while (!exact || total < entry.Size)
                {
                    var n = useAsync
                        ? await input.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
                        : input.Read(buffer, 0, buffer.Length);
                    if (n == 0)
                    {
                        break;
                    }
                    output.Write(buffer, 0, n);
                    total += n;
                }
                if (unknown)
                {
                    Assert.Equal(
                        0,
                        useAsync
                            ? await input.ReadAsync(
                                buffer,
                                0,
                                buffer.Length,
                                CancellationToken.None
                            )
                            : input.Read(buffer, 0, buffer.Length)
                    );
                }
            }
        }
        if (reader && useAsync)
        {
            using var forward = new ForwardOnlyStream(
                SharpCompressStream.CreateNonDisposing(source)
            );
            await using var rar = await ReaderFactory.OpenAsyncReader(
                forward,
                options,
                CancellationToken.None
            );
            while (await rar.MoveToNextEntryAsync(CancellationToken.None))
            {
                if (rar.Entry.IsDirectory || ((RarEntry)rar.Entry).IsRedir)
                {
                    continue;
                }
                await Read(
                    await rar.OpenEntryStreamAsync(CancellationToken.None),
                    (RarEntry)rar.Entry
                );
            }
        }
        else if (reader)
        {
            using var forward = new ForwardOnlyStream(
                SharpCompressStream.CreateNonDisposing(source)
            );
            using var rar = RarReader.OpenReader(forward, options);
            while (rar.MoveToNextEntry())
            {
                if (rar.Entry.IsDirectory || ((RarEntry)rar.Entry).IsRedir)
                {
                    continue;
                }
                await Read(rar.OpenEntryStream(), (RarEntry)rar.Entry);
            }
        }
        else if (useAsync)
        {
            await using var rar = await RarArchive.OpenAsyncArchive(
                source,
                options,
                CancellationToken.None
            );
            await foreach (var entry in rar.EntriesAsync)
            {
                if (entry.IsDirectory)
                {
                    continue;
                }
                await Read(
                    await entry.OpenEntryStreamAsync(CancellationToken.None),
                    (RarEntry)entry
                );
            }
        }
        else
        {
            using var rar = RarArchive.OpenArchive(source, options);
            foreach (var entry in rar.Entries)
            {
                if (entry.IsDirectory)
                {
                    continue;
                }
                await Read(entry.OpenEntryStream(), (RarEntry)entry);
            }
        }
        Assert.True(source.CanRead);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static byte[] Rar5(ulong packed, ulong unpacked, bool unknown = false, bool end = true)
    {
        using var archive = new MemoryStream();
        using var writer = new BinaryWriter(archive, Encoding.UTF8, true);
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 7, 1, 0 });
        WriteHeader(writer, [1, 0, 0]);
        using var body = new MemoryStream();
        using var header = new BinaryWriter(body, Encoding.UTF8, true);
        VInts(header, 2, 2, packed, unknown ? 12UL : 4UL, unpacked, 0);
        var payload = Encoding.UTF8.GetBytes("hello");
        header.Write(~RarCRC.CheckCrc(uint.MaxValue, payload, 0, payload.Length));
        VInts(header, 0, 1, 1);
        header.Write((byte)'f');
        WriteHeader(writer, body.ToArray());
        writer.Write(payload);
        if (end)
        {
            WriteHeader(writer, [5, 0, 0]);
        }
        return archive.ToArray();
    }

    private static byte[] Rar4(uint highPacked, uint highUnpacked)
    {
        using var archive = new MemoryStream();
        using var writer = new BinaryWriter(archive, Encoding.UTF8, true);
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 7, 0 });
        Write4(writer, 0x73, 0, new byte[6]);
        using var body = new MemoryStream();
        using var header = new BinaryWriter(body, Encoding.UTF8, true);
        header.Write(0U);
        header.Write(0U);
        header.Write((byte)2);
        header.Write(0U);
        header.Write(0U);
        header.Write((byte)29);
        header.Write((byte)0x30);
        header.Write((ushort)1);
        header.Write(0U);
        header.Write(highPacked);
        header.Write(highUnpacked);
        header.Write((byte)'f');
        Write4(writer, 0x74, 0x8100, body.ToArray());
        return archive.ToArray();
    }

    private static void Write4(BinaryWriter writer, byte type, ushort flags, byte[] body)
    {
        using var memory = new MemoryStream();
        using var header = new BinaryWriter(memory);
        header.Write(type);
        header.Write(flags);
        header.Write((ushort)(body.Length + 7));
        header.Write(body);
        var bytes = memory.ToArray();
        writer.Write((ushort)~RarCRC.CheckCrc(uint.MaxValue, bytes, 0, bytes.Length));
        writer.Write(bytes);
    }

    private static void WriteHeader(BinaryWriter writer, byte[] body)
    {
        using var memory = new MemoryStream();
        using var header = new BinaryWriter(memory);
        VInts(header, (ulong)body.Length);
        header.Write(body);
        var bytes = memory.ToArray();
        writer.Write(~RarCRC.CheckCrc(uint.MaxValue, bytes, 0, bytes.Length));
        writer.Write(bytes);
    }

    private static void VInts(BinaryWriter writer, params ulong[] values)
    {
        foreach (var value in values)
        {
            var remaining = value;
            while (remaining >= 0x80)
            {
                writer.Write((byte)((remaining & 0x7f) | 0x80));
                remaining >>= 7;
            }
            writer.Write((byte)remaining);
        }
    }
}
