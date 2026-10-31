using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.LZMA.Utilities;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipUpdateTests : TestBase
{
    private sealed class Password(string? value) : IPasswordProvider
    {
        public string? CryptoGetTextPassword() => value;
    }

    private static ArchiveDatabase Database(Stream source, string? password = null)
    {
        source.Position = 0;
        var reader = new ArchiveReader();
        reader.Open(source, false);
        return reader.ReadDatabase(new Password(password));
    }

    private static byte[] Packed(Stream source, ArchiveDatabase database, int index)
    {
        source.Position = database.GetFolderStreamPos(database._folders[index], 0);
        var data = new byte[checked((int)database.GetFolderFullPackSize(index))];
        source.ReadExact(data, 0, data.Length);
        return data;
    }

    private static List<byte[]> Contents(Stream source, ArchiveDatabase database)
    {
        var contents = new List<byte[]>();
        foreach (var folder in database._folders)
        {
            using var decoded = database.GetFolderStream(source, folder, database.PasswordProvider);
            foreach (
                var file in database._files.Where(
                    (f, i) =>
                        f.HasStream
                        && database._fileIndexToFolderIndexMap[i]
                            == database._folders.IndexOf(folder)
                )
            )
            {
                var bytes = new byte[checked((int)file.Size)];
                decoded.ReadExact(bytes, 0, bytes.Length);
                contents.Add(bytes);
            }
        }
        return contents;
    }

    private static async Task<SevenZipUpdate> Open(
        Stream source,
        bool asynchronous,
        string? password = null
    ) =>
        asynchronous
            ? await SevenZipUpdate.OpenAsync(
                new AsyncOnlyStream(source),
                new ReaderOptions { Password = password },
                CancellationToken.None
            )
            : SevenZipUpdate.Open(source, new ReaderOptions { Password = password });

    private static async Task Write(
        SevenZipUpdate update,
        Stream destination,
        bool asynchronous,
        SevenZipWriterOptions? options = null
    )
    {
        options ??= new SevenZipWriterOptions();
        if (asynchronous)
        {
            await update.WriteToAsync(
                new AsyncOnlyStream(destination),
                options,
                cancellationToken: CancellationToken.None
            );
        }
        else
        {
            update.WriteTo(destination, options);
        }
    }

    [Theory]
    [InlineData("solid", false)]
    [InlineData("solid", true)]
    [InlineData("nonsolid", false)]
    [InlineData("nonsolid", true)]
    [InlineData("BCJ2", false)]
    [InlineData("BCJ2", true)]
    public async Task SevenZipUpdate_FolderPackedSize(string name, bool asynchronous)
    {
        using var source = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, $"7Zip.{name}.7z"));
        var database = Database(source);
        using var update = await Open(source, asynchronous);
        var folders = update.Entries.Where(e => e.FolderIndex >= 0).GroupBy(e => e.FolderIndex);
        Assert.NotEmpty(folders);
        foreach (var folder in folders)
        {
            var expected = database.GetFolderFullPackSize(folder.Key);
            Assert.True(expected > 0);
            Assert.All(folder, entry => Assert.Equal(expected, entry.FolderPackedSize));
        }
        if (name == "BCJ2")
        {
            Assert.Contains(database._folders, folder => folder._packStreams.Count == 4);
        }
        Assert.All(
            update.Entries.Where(e => e.FolderIndex < 0),
            entry => Assert.Equal(0, entry.FolderPackedSize)
        );

        // These fixtures have encoded headers. Copy their packed data with an uncompressed
        // header so NextHeaderOffset counts only file data, excluding packed header bytes.
        using var output = new MemoryStream();
        await Write(
            update,
            output,
            asynchronous,
            new SevenZipWriterOptions { CompressHeader = false }
        );
        output.Position = 12;
        using var reader = new BinaryReader(output, System.Text.Encoding.UTF8, leaveOpen: true);
        var packedDataSize = checked((long)reader.ReadUInt64());
        Assert.Equal(packedDataSize, folders.Sum(folder => folder.First().FolderPackedSize));
        using var copied = await Open(output, asynchronous);
        Assert.Equal(
            update.Entries.Select(e => e.FolderPackedSize),
            copied.Entries.Select(e => e.FolderPackedSize)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_StreamlessFolderPackedSize(bool asynchronous)
    {
        using var source = Create();
        using var update = await Open(source, asynchronous);
        var streamless = update.Entries.Where(e => e.FolderIndex < 0).ToArray();
        Assert.Contains(streamless, entry => entry.IsDirectory);
        Assert.Contains(streamless, entry => !entry.IsDirectory);
        Assert.All(streamless, entry => Assert.Equal(0, entry.FolderPackedSize));
    }

    public static IEnumerable<object[]> Archives()
    {
        foreach (
            var name in new[]
            {
                "LZMA",
                "LZMA2",
                "PPMd",
                "BCJ",
                "BCJ2",
                "BZip2",
                "Copy",
                "delta",
                "ARM64",
                "ZSTD",
                "solid",
                "solid.1block",
                "nonsolid",
                "EmptyStream",
                "LZMA.Aes",
                "LZMA2.Aes",
            }
        )
        {
            foreach (var asynchronous in new[] { false, true })
            {
                yield return new object[] { name, asynchronous };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Archives))]
    public async Task SevenZipUpdate_NoOp_PreservesFolders(string name, bool asynchronous)
    {
        using var source = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, $"7Zip.{name}.7z"));
        var password = name.Contains("Aes") ? "testpassword" : null;
        var before = Database(source, password);
        var packed = Enumerable
            .Range(0, before._folders.Count)
            .Select(i => Packed(source, before, i))
            .ToArray();
        var contents = Contents(source, before);
        using var update = await Open(source, asynchronous, password);
        using var output = new MemoryStream();
        await Write(
            update,
            output,
            asynchronous,
            new SevenZipWriterOptions { Password = password }
        );
        Assert.True(source.CanRead);
        var after = Database(output, password);
        Assert.Equal(before._folders.Count, after._folders.Count);
        for (var i = 0; i < packed.Length; i++)
        {
            Assert.Equal(packed[i], Packed(output, after, i));
            Assert.Equal(before._folders[i]._unpackCrc, after._folders[i]._unpackCrc);
            Assert.Equal(before._folders[i]._packStreams, after._folders[i]._packStreams);
            Assert.Equal(before._folders[i]._unpackSizes, after._folders[i]._unpackSizes);
            Assert.Equal(
                before._folders[i]._bindPairs.Select(p => (p._inIndex, p._outIndex)),
                after._folders[i]._bindPairs.Select(p => (p._inIndex, p._outIndex))
            );
            Assert.Equal(
                before._folders[i]._coders.Select(c => (c._numInStreams, c._numOutStreams)),
                after._folders[i]._coders.Select(c => (c._numInStreams, c._numOutStreams))
            );
            Assert.Equal(
                before._folders[i]._coders.Select(c => c._methodId),
                after._folders[i]._coders.Select(c => c._methodId)
            );
            for (var j = 0; j < before._folders[i]._coders.Count; j++)
            {
                Assert.Equal(
                    before._folders[i]._coders[j]._props,
                    after._folders[i]._coders[j]._props
                );
            }
        }
        Assert.Equal(before._packCrCs, after._packCrCs);
        Assert.Equal(
            before._files.Select(f =>
                (
                    f.Name,
                    f.Size,
                    f.Crc,
                    f.IsDir,
                    f.IsAnti,
                    f.HasStream,
                    f.CTime,
                    f.ATime,
                    f.MTime,
                    f.ExtendedAttrib
                )
            ),
            after._files.Select(f =>
                (
                    f.Name,
                    f.Size,
                    f.Crc,
                    f.IsDir,
                    f.IsAnti,
                    f.HasStream,
                    f.CTime,
                    f.ATime,
                    f.MTime,
                    f.ExtendedAttrib
                )
            )
        );
        var actual = Contents(output, after);
        Assert.Equal(contents.Count, actual.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.Equal(contents[i], actual[i]);
        }
    }

    private static MemoryStream Create(
        bool solid = true,
        string? password = null,
        bool encryptHeader = false
    )
    {
        var source = new MemoryStream();
        using (
            var writer = new SevenZipWriter(
                source,
                new SevenZipWriterOptions { Password = password, EncryptHeader = encryptHeader }
            )
        )
        {
            if (solid)
            {
                writer.BeginSolidBlock();
            }
            writer.Write("first", new MemoryStream("first contents"u8.ToArray()), null);
            writer.WriteDirectory("dir", null);
            writer.Write("empty", Stream.Null, null);
            writer.Write("second", new MemoryStream("second contents"u8.ToArray()), null);
            writer.EndSolidBlock();
            writer.Write("third", new MemoryStream("third contents"u8.ToArray()), null);
        }
        return source;
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task SevenZipUpdate_Delete_OnlyAffectedFolder(
        bool asynchronous,
        bool solid,
        bool deleteAll
    )
    {
        using var source = Create(solid);
        var before = Database(source);
        var lastPacked = Packed(source, before, before._folders.Count - 1);
        using var update = await Open(source, asynchronous);
        update.Delete(0);
        if (deleteAll)
        {
            update.Delete(3);
        }
        using var output = new MemoryStream();
        await Write(update, output, asynchronous);
        var after = Database(output);
        Assert.Equal(
            deleteAll
                ? new[] { "dir", "empty", "third" }
                : new[] { "dir", "empty", "second", "third" },
            after._files.Select(f => f.Name)
        );
        Assert.Equal(lastPacked, Packed(output, after, after._folders.Count - 1));
        var actual = Contents(output, after);
        Assert.Equal("third contents"u8.ToArray(), actual[actual.Count - 1]);
        if (!deleteAll)
        {
            Assert.Equal("second contents"u8.ToArray(), actual[0]);
        }
        Assert.Equal(deleteAll ? 1 : 2, after._folders.Count);
        if (!solid)
        {
            Assert.Equal(Packed(source, before, 1), Packed(output, after, 0));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_MetadataAndAdditions(bool asynchronous)
    {
        using var source = Create();
        var before = Database(source);
        var packed = Packed(source, before, 0);
        using var update = await Open(source, asynchronous);
        update.Rename(0, @"C:\renamed\file");
        var time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        update.SetMetadata(
            0,
            new SevenZipWriterEntryOptions
            {
                CreationTime = time,
                LastAccessTime = time.AddDays(1),
                ModificationTime = time.AddDays(2),
                Attributes = 0x81ED8020,
            }
        );
        using var output = new MemoryStream();
        if (asynchronous)
        {
            await update.WriteToAsync(
                new AsyncOnlyStream(output),
                new SevenZipWriterOptions(),
                async (writer, token) =>
                {
                    await writer.WriteAsync(
                        "added",
                        new MemoryStream(new byte[] { 1 }),
                        null,
                        token
                    );
                    writer.BeginSolidBlock();
                    await writer.WriteAsync(
                        "solid1",
                        new MemoryStream(new byte[] { 2 }),
                        null,
                        token
                    );
                    await writer.WriteAsync(
                        "solid2",
                        new MemoryStream(new byte[] { 3 }),
                        null,
                        token
                    );
                    await writer.EndSolidBlockAsync(token);
                    await writer.WriteDirectoryAsync("addedDir", null, token);
                    await writer.WriteAsync("addedEmpty", Stream.Null, null, token);
                },
                CancellationToken.None
            );
        }
        else
        {
            update.WriteTo(
                output,
                new SevenZipWriterOptions(),
                writer =>
                {
                    writer.Write("added", new MemoryStream(new byte[] { 1 }), null);
                    writer.BeginSolidBlock();
                    writer.Write("solid1", new MemoryStream(new byte[] { 2 }), null);
                    writer.Write("solid2", new MemoryStream(new byte[] { 3 }), null);
                    writer.EndSolidBlock();
                    writer.WriteDirectory("addedDir", null);
                    writer.Write("addedEmpty", Stream.Null, null);
                }
            );
        }
        var after = Database(output);
        Assert.Equal(packed, Packed(output, after, 0));
        Assert.Equal("renamed/file", after._files[0].Name);
        Assert.Equal(time, after._files[0].CTime?.ToUniversalTime());
        Assert.Equal(time.AddDays(1), after._files[0].ATime?.ToUniversalTime());
        Assert.Equal(time.AddDays(2), after._files[0].MTime?.ToUniversalTime());
        Assert.Equal(0x81ED8020u, after._files[0].ExtendedAttrib);
        Assert.Equal(
            new[] { "added", "solid1", "solid2", "addedDir", "addedEmpty" },
            after._files.Skip(5).Select(f => f.Name)
        );
        var actual = Contents(output, after);
        Assert.Equal(new byte[] { 1 }, actual[3]);
        Assert.Equal(new byte[] { 2 }, actual[4]);
        Assert.Equal(new byte[] { 3 }, actual[5]);
    }

    [Theory]
    [InlineData(false, "old", "old", false)]
    [InlineData(true, "old", "old", false)]
    [InlineData(false, "old", "new", true)]
    [InlineData(true, "old", "new", true)]
    [InlineData(false, null, "new", true)]
    [InlineData(true, null, "new", true)]
    [InlineData(false, "old", null, false)]
    [InlineData(true, "old", null, false)]
    public async Task SevenZipUpdate_Encryption(
        bool asynchronous,
        string? oldPassword,
        string? newPassword,
        bool encryptHeader
    )
    {
        using var source = Create(password: oldPassword, encryptHeader: oldPassword is not null);
        var before = Database(source, oldPassword);
        var packed = Packed(source, before, 0);
        using var update = await Open(source, asynchronous, oldPassword);
        Assert.Equal(oldPassword is not null, update.IsSourceEncrypted);
        using var output = new MemoryStream();
        await Write(
            update,
            output,
            asynchronous,
            new SevenZipWriterOptions { Password = newPassword, EncryptHeader = encryptHeader }
        );
        var after = Database(output, newPassword);
        Assert.Equal(newPassword is not null, after._folders[0].IsEncrypted());
        Assert.Equal("first contents"u8.ToArray(), Contents(output, after)[0]);
        if (oldPassword == newPassword)
        {
            Assert.Equal(packed, Packed(output, after, 0));
        }
        else
        {
            Assert.False(packed.SequenceEqual(Packed(output, after, 0)));
        }
        if (encryptHeader)
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var wrong = await Open(output, asynchronous, "wrong");
            });
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var missing = await Open(output, asynchronous);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_AntiAndUndefinedMetadata(bool asynchronous)
    {
        using var source = new MemoryStream();
        using (
            var writer = new SevenZipWriter(
                source,
                new SevenZipWriterOptions { CompressHeader = false }
            )
        )
        {
            writer.AppendUpdateEntry(
                new SevenZipWriteEntry
                {
                    Name = "anti",
                    IsEmpty = true,
                    IsAnti = true,
                    Attributes = null,
                }
            );
            writer.AppendUpdateEntry(
                new SevenZipWriteEntry
                {
                    Name = "dir",
                    IsDirectory = true,
                    IsEmpty = true,
                    Attributes = null,
                }
            );
        }
        using var update = await Open(source, asynchronous);
        Assert.True(update.Entries[0].IsAnti);
        Assert.All(update.Entries, e => Assert.Equal(-1, e.FolderIndex));
        Assert.All(update.Entries, e => Assert.Equal(0, e.FolderPackedSize));
        using var output = new MemoryStream();
        await Write(update, output, asynchronous);
        var after = Database(output);
        Assert.True(after._files[0].IsAnti);
        Assert.All(after._files, f => Assert.Null(f.ExtendedAttrib));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_ErrorsAndOwnership(bool asynchronous)
    {
        using var source = Create();
        using var forward = new ForwardOnlyStream(source);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            using var invalid = await Open(forward, asynchronous);
        });
        using var update = await Open(source, asynchronous);
        Assert.Throws<ArgumentOutOfRangeException>(() => update.Delete(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => update.Rename(50, "name"));
        Assert.Throws<ArgumentOutOfRangeException>(() => update.SetMetadata(50, new()));
        using var output = new MemoryStream();
        using var forwardOutput = new ForwardOnlyStream(output);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Write(update, forwardOutput, asynchronous)
        );
        await Write(
            update,
            output,
            asynchronous,
            new SevenZipWriterOptions { LeaveStreamOpen = false }
        );
        Assert.False(output.CanWrite);
        Assert.True(source.CanRead);
        Assert.Throws<InvalidOperationException>(() => update.Delete(0));
        Assert.Throws<InvalidOperationException>(() => update.Rename(0, "name"));
        Assert.Throws<InvalidOperationException>(() => update.SetMetadata(0, new()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Write(update, new MemoryStream(), asynchronous)
        );
        update.Dispose();
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(false, SevenZipFilter.X86, CompressionType.LZMA2)]
    [InlineData(true, SevenZipFilter.X86, CompressionType.LZMA2)]
    [InlineData(false, SevenZipFilter.Arm64, CompressionType.LZMA2)]
    [InlineData(true, SevenZipFilter.Arm64, CompressionType.LZMA2)]
    [InlineData(false, SevenZipFilter.X86, CompressionType.LZMA)]
    [InlineData(true, SevenZipFilter.Arm64, CompressionType.None)]
    public async Task SevenZipUpdate_RecompressionFilter(
        bool asynchronous,
        SevenZipFilter filter,
        CompressionType codec
    )
    {
        using var source = new MemoryStream();
        var bytes = new byte[32768];
        new Random(47).NextBytes(bytes);
        using (var writer = new SevenZipWriter(source, new SevenZipWriterOptions()))
        {
            writer.BeginSolidBlock(new SevenZipSolidBlockOptions { Filter = filter });
            writer.Write("delete", new MemoryStream(bytes), null);
            writer.Write("keep", new MemoryStream(bytes), null);
        }
        using var update = await Open(source, asynchronous);
        update.Delete(0);
        using var output = new MemoryStream();
        await Write(update, output, asynchronous, new SevenZipWriterOptions(codec));
        var after = Database(output);
        Assert.Equal(bytes, Assert.Single(Contents(output, after)));
        Assert.Equal(
            codec == CompressionType.LZMA2 ? 2 : 1,
            Assert.Single(after._folders)._coders.Count
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_SplitSourceAndCancellation(bool asynchronous)
    {
        using var source = Create();
        using var update = asynchronous
            ? await SevenZipUpdate.OpenAsync(source, cancellationToken: CancellationToken.None)
            : SevenZipUpdate.Open(source);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => update.WriteTo(source, new SevenZipWriterOptions()));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await update.WriteToAsync(
                output,
                new SevenZipWriterOptions(),
                cancellationToken: canceled.Token
            )
        );
        Assert.Equal(0, output.Length);
        await Write(update, output, asynchronous);
        var path = Path.Combine(SCRATCH_FILES_PATH, "update.7z.001");
        File.WriteAllBytes(path, source.ToArray());
        using var split = File.OpenRead(path);
        if (asynchronous)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                using var invalid = await SevenZipUpdate.OpenAsync(
                    split,
                    cancellationToken: CancellationToken.None
                );
            });
        }
        else
        {
            Assert.Throws<NotSupportedException>(() => SevenZipUpdate.Open(split));
        }
    }

#if !LEGACY_DOTNET
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_Recompress64MiB_Streams(bool asynchronous)
    {
        using var source = new MemoryStream();
        using (
            var writer = new SevenZipWriter(source, new SevenZipWriterOptions(CompressionType.None))
        )
        {
            writer.BeginSolidBlock();
            writer.Write("delete", new ZeroStream(1), null);
            writer.Write("keep", new ZeroStream((64L << 20) - 1), null);
        }
        using var update = await Open(source, asynchronous);
        update.Delete(0);
        using var output = new MemoryStream(1 << 20);
        var before = GC.GetAllocatedBytesForCurrentThread();
        await Write(update, output, asynchronous);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 48L << 20);
        var database = Database(output);
        Assert.Equal((64L << 20) - 1, Assert.Single(database._files).Size);
        using var decoded = database.GetFolderStream(
            output,
            Assert.Single(database._folders),
            database.PasswordProvider
        );
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = decoded.Read(buffer, 0, buffer.Length)) != 0)
        {
            Assert.True(buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) < 0);
            total += read;
        }
        Assert.Equal((64L << 20) - 1, total);
    }

    private sealed class ZeroStream(long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(remaining, count);
            Array.Clear(buffer, offset, read);
            remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_EncryptedFiles_DetectionOnly(bool asynchronous)
    {
        // This fixture has no known password; its readable header is sufficient for detection.
        using var source = File.OpenRead(
            Path.Combine(TEST_ARCHIVES_PATH, "7Zip.encryptedFiles.7z")
        );
        using var update = await Open(source, asynchronous);
        Assert.True(update.IsSourceEncrypted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_ZeroLengthSubstream(bool asynchronous)
    {
        using var source = new MemoryStream();
        using (
            var writer = new SevenZipWriter(
                source,
                new SevenZipWriterOptions { CompressHeader = false }
            )
        )
        {
            writer.AppendCopiedFolder(
                Stream.Null,
                0,
                0,
                new PackedStream
                {
                    PreserveFolderCrc = true,
                    Sizes = new ulong[] { 0 },
                    CRCs = new uint?[] { 0 },
                    Folder = new CFolder
                    {
                        _coders = new List<CCoderInfo>
                        {
                            new()
                            {
                                _methodId = CMethodId.K_COPY,
                                _numInStreams = 1,
                                _numOutStreams = 1,
                            },
                        },
                        _packStreams = new List<int> { 0 },
                        _unpackSizes = new List<long> { 0 },
                        _unpackCrc = 0,
                    },
                },
                "zero"
            );
            writer.AppendUpdateEntry(new SevenZipWriteEntry { Name = "zero", IsEmpty = false });
        }
        using var update = await Open(source, asynchronous);
        Assert.Equal(0, update.Entries[0].FolderIndex);
        using var output = new MemoryStream();
        // Encryption change forces recompression of the zero-length stream.
        await Write(
            update,
            output,
            asynchronous,
            new SevenZipWriterOptions { Password = "secret" }
        );
        var after = Database(output, "secret");
        Assert.True(Assert.Single(after._files).HasStream);
        Assert.Single(after._folders);
        Assert.Empty(Assert.Single(Contents(output, after)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_HeaderOnlyEncryptionAndAttributeDefaults(bool asynchronous)
    {
        using var source = new MemoryStream();
        using (
            var writer = new SevenZipWriter(
                source,
                new SevenZipWriterOptions { Password = "secret", EncryptHeader = true }
            )
        )
        {
            writer.AppendUpdateEntry(
                new SevenZipWriteEntry
                {
                    Name = "file",
                    IsEmpty = true,
                    Attributes = null,
                }
            );
            writer.AppendUpdateEntry(
                new SevenZipWriteEntry
                {
                    Name = "dir",
                    IsEmpty = true,
                    IsDirectory = true,
                    Attributes = null,
                }
            );
            writer.AppendUpdateEntry(
                new SevenZipWriteEntry
                {
                    Name = "other",
                    IsEmpty = true,
                    Attributes = null,
                }
            );
        }
        using var update = await Open(source, asynchronous, "secret");
        Assert.True(update.IsSourceEncrypted);
        update.SetMetadata(2, new SevenZipWriterEntryOptions { Attributes = 0x81ED8020 });
        using var output = new MemoryStream();
        await Write(update, output, asynchronous);
        var after = Database(output);
        Assert.Equal(
            new uint?[] { 0x20, 0x10, 0x81ED8020 },
            after._files.Select(f => f.ExtendedAttrib)
        );
    }

    private sealed class Reporter : IProgress<ProgressReport>
    {
        public Dictionary<string, long> Counts { get; } = new();

        public void Report(ProgressReport value) =>
            Counts[value.EntryPath] = value.BytesTransferred;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipUpdate_Progress(bool asynchronous)
    {
        using var source = Create();
        var before = Database(source);
        using var update = await Open(source, asynchronous);
        using var output = new MemoryStream();
        var reporter = new Reporter();
        await Write(
            update,
            output,
            asynchronous,
            new SevenZipWriterOptions { Progress = reporter }
        );
        Assert.Equal(before.GetFolderFullPackSize(0), reporter.Counts["first"]);
        Assert.Equal(before.GetFolderFullPackSize(1), reporter.Counts["third"]);
    }
}
