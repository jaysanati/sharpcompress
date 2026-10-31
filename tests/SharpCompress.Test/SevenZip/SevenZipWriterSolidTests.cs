using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Crypto;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipWriterSolidTests
{
    private static async Task Write(
        SevenZipWriter writer,
        string name,
        byte[] data,
        bool asynchronous,
        bool seekable = true
    )
    {
        using var source = new InputStream(data, seekable);
        if (asynchronous)
        {
            await writer.WriteAsync(name, source, null, CancellationToken.None);
        }
        else
        {
            writer.Write(name, source, null);
        }
        Assert.True(source.CanRead);
    }

    private static async Task Finish(SevenZipWriter writer, bool asynchronous)
    {
        if (asynchronous)
        {
            await writer.DisposeAsync();
        }
        else
        {
            writer.Dispose();
        }
    }

    private static byte[] Read(SevenZipArchiveEntry entry)
    {
        using var input = entry.OpenEntryStream();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Solid_ManySmallFiles(bool asynchronous)
    {
        var files = Enumerable
            .Range(0, 200)
            .Select(i =>
                Encoding.UTF8.GetBytes(
                    $"Similar text file {i}: the quick brown fox jumps over the lazy dog. Shared content for solid compression.\n"
                )
            )
            .ToArray();
        using var solid = new MemoryStream();
        using var separate = new MemoryStream();
        foreach (var output in new[] { solid, separate })
        {
            var writer = new SevenZipWriter(output, new SevenZipWriterOptions());
            if (output == solid)
            {
                writer.BeginSolidBlock();
            }
            for (var i = 0; i < files.Length; i++)
            {
                await Write(writer, $"{i}.txt", files[i], asynchronous);
            }
            await Finish(writer, asynchronous);
        }
        Assert.True(
            solid.Length < separate.Length * 0.7,
            $"Solid {solid.Length}, separate {separate.Length}"
        );
        solid.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(solid);
        Assert.True(archive.IsSolid);
        var entries = archive.Entries.ToArray();
        Assert.Equal(200, entries.Length);
        Assert.Single(entries.Select(e => e.FilePart.Folder).Distinct());
        Assert.Null(entries[0].FilePart.Folder!._unpackCrc);
        for (var i = 0; i < files.Length; i++)
        {
            Assert.Equal(files[i], Read(entries[i]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Solid_MixedBlocks(bool asynchronous)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(output, new SevenZipWriterOptions());
        var data = "file content"u8.ToArray();
        await Write(writer, "outside", data, asynchronous);
        writer.BeginSolidBlock();
        await Write(writer, "lzma1", data, asynchronous);
        await Write(writer, "empty", [], asynchronous, false);
        writer.WriteDirectory("dir", null);
        await Write(writer, "lzma2", data, asynchronous);
        writer.BeginSolidBlock(
            new SevenZipSolidBlockOptions { CompressionType = CompressionType.None }
        );
        await Write(writer, "store1", data, asynchronous);
        await Write(writer, "store2", data, asynchronous);
        if (asynchronous)
        {
            await writer.EndSolidBlockAsync(CancellationToken.None);
        }
        else
        {
            writer.EndSolidBlock();
        }
        await Write(writer, "outside2", data, asynchronous);
        writer.BeginSolidBlock(); // An empty block must not produce a folder.
        await Finish(writer, asynchronous);
        output.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(output);
        var entries = archive.Entries.ToArray();
        Assert.True(archive.IsSolid);
        Assert.Equal(
            new[] { "outside", "lzma1", "empty", "dir", "lzma2", "store1", "store2", "outside2" },
            entries.Select(e => e.Key)
        );
        Assert.Equal(
            4,
            entries.Where(e => e.Size > 0).Select(e => e.FilePart.Folder).Distinct().Count()
        );
        Assert.Equal(CompressionType.None, entries[5].CompressionType);
        Assert.Same(entries[1].FilePart.Folder, entries[4].FilePart.Folder);
        Assert.Same(entries[5].FilePart.Folder, entries[6].FilePart.Folder);
        foreach (var entry in entries.Where(e => !e.IsDirectory))
        {
            Assert.Equal(entry.Size == 0 ? [] : data, Read(entry));
        }
    }

    public static IEnumerable<object[]> FilterCases()
    {
        foreach (var filter in new[] { SevenZipFilter.X86, SevenZipFilter.Arm64 })
        {
            foreach (var codec in new[] { CompressionType.LZMA, CompressionType.LZMA2 })
            {
                foreach (var encryption in new[] { 0, 1, 2 })
                {
                    foreach (var asynchronous in new[] { false, true })
                    {
                        yield return [filter, codec, encryption, asynchronous];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(FilterCases))]
    public async Task SevenZipWriter_Solid_Filters(
        SevenZipFilter filter,
        CompressionType codec,
        int encryption,
        bool asynchronous
    )
    {
        var data = new byte[150013];
        new Random(781).NextBytes(data);
        for (var i = 0; i + 8 < 70000; i += 8)
        {
            if (filter == SevenZipFilter.X86)
            {
                data[i] = (byte)((i & 8) == 0 ? 0xE8 : 0xE9);
                data[i + 1] = 0x10;
                data[i + 2] = 0;
                data[i + 3] = 0;
                data[i + 4] = 0;
            }
            else
            {
                data[i] = 0x10;
                data[i + 1] = 0;
                data[i + 2] = 0;
                data[i + 3] = 0x94; // BL
                data[i + 4] = 0;
                data[i + 5] = 0;
                data[i + 6] = 0;
                data[i + 7] = 0x90; // ADRP
            }
        }
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions(codec)
            {
                Password = encryption > 0 ? "secret" : null,
                EncryptHeader = encryption == 2,
            }
        );
        writer.BeginSolidBlock(new SevenZipSolidBlockOptions { Filter = filter });
        // Split inside an instruction, and use short, non-seekable source reads.
        var first = data.Take(3).ToArray();
        var second = data.Skip(3).ToArray();
        await Write(writer, "first", first, asynchronous, false);
        await Write(writer, "second", second, asynchronous, false);
        await Finish(writer, asynchronous);
        output.Position = 0;
        using var archive = (SevenZipArchive)
            SevenZipArchive.OpenArchive(
                output,
                new ReaderOptions { Password = encryption > 0 ? "secret" : null }
            );
        var entries = archive.Entries.ToArray();
        Assert.Same(entries[0].FilePart.Folder, entries[1].FilePart.Folder);
        Assert.Equal(encryption > 0 ? 3 : 2, entries[0].FilePart.Folder!._coders.Count);
        Assert.Equal(first, Read(entries[0]));
        Assert.Equal(second, Read(entries[1]));
    }

    [Theory]
    [InlineData(CompressionType.LZMA, false)]
    [InlineData(CompressionType.LZMA, true)]
    [InlineData(CompressionType.LZMA2, false)]
    [InlineData(CompressionType.LZMA2, true)]
    public async Task SevenZipWriter_Solid_ExpectedSize(CompressionType codec, bool asynchronous)
    {
        foreach (var expected in new long?[] { null, 10240 })
        {
            using var output = new MemoryStream();
            var writer = new SevenZipWriter(
                output,
                new SevenZipWriterOptions(codec)
                {
                    LzmaProperties = new LzmaEncoderProperties(false, 1 << 20, 32),
                }
            );
            writer.BeginSolidBlock(new SevenZipSolidBlockOptions { ExpectedSize = expected });
            await Write(writer, "file", new byte[10240], asynchronous);
            await Finish(writer, asynchronous);
            output.Position = 0;
            using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(output);
            var entry = Assert.Single(archive.Entries);
            var properties = Assert.Single(entry.FilePart.Folder!._coders)._props!;
            var dictionary =
                codec == CompressionType.LZMA
                    ? BitConverter.ToInt32(properties, 1)
                    : (2 | (properties[0] & 1)) << ((properties[0] >> 1) + 11);
            Assert.Equal(expected.HasValue ? 12288 : 1 << 20, dictionary);
            Assert.Equal(new byte[10240], Read(entry));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SevenZipWriter_Solid_AutomaticLimits(bool bySize, bool asynchronous)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions(CompressionType.None)
            {
                Solid = true,
                SolidBlockSize = bySize ? 25 : 1000,
                SolidBlockFileCount = bySize ? 1000 : 2,
            }
        );
        for (var i = 0; i < 5; i++)
        {
            await Write(writer, $"{i}", new byte[10], asynchronous);
        }
        await Finish(writer, asynchronous);
        output.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(output);
        var groups = archive.Entries.GroupBy(e => e.FilePart.Folder).ToArray();
        Assert.Equal(new[] { 2, 2, 1 }, groups.Select(g => g.Count()));
    }

    [Fact]
    public async Task SevenZipWriter_Solid_ValidationAndCancellation()
    {
        using var output = new MemoryStream();
        await using var writer = new SevenZipWriter(output, new SevenZipWriterOptions());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.BeginSolidBlock(new SevenZipSolidBlockOptions { ExpectedSize = -1 })
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.BeginSolidBlock(new SevenZipSolidBlockOptions { Filter = (SevenZipFilter)99 })
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            writer.BeginSolidBlock(
                new SevenZipSolidBlockOptions { CompressionType = CompressionType.Deflate }
            )
        );
        writer.BeginSolidBlock();
        await Write(writer, "file", [1, 2, 3], true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.EndSolidBlockAsync(new CancellationToken(true))
        );
        await writer.EndSolidBlockAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.LZMA)]
    [InlineData(CompressionType.LZMA2)]
    public async Task SevenZipWriter_Solid_AsyncOnlyOutput(CompressionType codec)
    {
        using var output = new MemoryStream();
        await using var destination = new AsyncOnlyStream(output, false);
        var writer = new SevenZipWriter(
            destination,
            new SevenZipWriterOptions(codec) { Password = "secret", EncryptHeader = true }
        );
        writer.BeginSolidBlock(new SevenZipSolidBlockOptions { Filter = SevenZipFilter.X86 });
        await Write(writer, "a", [0xE8, 1], true);
        await Write(writer, "b", [0, 0, 0, 42], true);
        await writer.EndSolidBlockAsync(CancellationToken.None);
        writer.BeginSolidBlock();
        await Write(writer, "c", [1, 2, 3], true);
        await writer.DisposeAsync();
        Assert.True(output.CanWrite);
        output.Position = 0;
        await using var archive = await SevenZipArchive.OpenAsyncArchive(
            output,
            new ReaderOptions { Password = "secret", LeaveStreamOpen = true }
        );
        var entries = (await archive.EntriesAsync.ToListAsync())
            .Cast<SevenZipArchiveEntry>()
            .ToArray();
        Assert.NotEqual(
            entries[0].FilePart.Folder!._coders[0]._props,
            entries[2].FilePart.Folder!._coders[0]._props
        );
        using var input = await entries[1].OpenEntryStreamAsync(CancellationToken.None);
        using var restored = new MemoryStream();
        await input.CopyToAsync(restored, 8192, CancellationToken.None);
        Assert.Equal(new byte[] { 0, 0, 0, 42 }, restored.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Solid_ExplicitOverridesAutomatic(bool asynchronous)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions(CompressionType.None)
            {
                Solid = true,
                SolidBlockFileCount = 1,
            }
        );
        writer.BeginSolidBlock();
        await Write(writer, "a", [1], asynchronous);
        await Write(writer, "b", [2], asynchronous);
        if (asynchronous)
        {
            await writer.EndSolidBlockAsync();
        }
        else
        {
            writer.EndSolidBlock();
        }
        await Write(writer, "c", [3], asynchronous);
        await Write(writer, "d", [4], asynchronous);
        await Finish(writer, asynchronous);
        output.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(output);
        Assert.Equal(
            new[] { 2, 1, 1 },
            archive.Entries.GroupBy(e => e.FilePart.Folder).Select(g => g.Count())
        );
    }

    [Theory]
    [InlineData(CompressionType.None, false)]
    [InlineData(CompressionType.None, true)]
    [InlineData(CompressionType.LZMA, false)]
    [InlineData(CompressionType.LZMA, true)]
    [InlineData(CompressionType.LZMA2, false)]
    [InlineData(CompressionType.LZMA2, true)]
    public async Task SevenZipWriter_NonSolid_OriginalBytes(
        CompressionType codec,
        bool asynchronous
    )
    {
        byte[][] files = ["first file"u8.ToArray(), "second file content"u8.ToArray()];
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions(codec) { CompressHeader = false }
        );
        for (var i = 0; i < files.Length; i++)
        {
            await Write(writer, $"{i}", files[i], asynchronous);
        }
        await Finish(writer, asynchronous);

        // Reproduce the pre-solid writer's one-folder-per-file metadata and packing path.
        using var expected = new MemoryStream();
        SevenZipSignatureHeaderWriter.WritePlaceholder(expected);
        var packed = new List<PackedStream>();
        foreach (var file in files)
        {
            using var source = new MemoryStream(file);
            packed.Add(new SevenZipStreamsCompressor(expected).Compress(source, codec));
        }
        var crcs = packed.Select(p => p.Folder._unpackCrc).ToArray();
        foreach (var item in packed)
        {
            item.Folder._unpackCrc = null;
        }
        var folders = packed.Select(p => p.Folder).ToArray();
        var streams = new SevenZipStreamsInfoWriter
        {
            PackInfo = new SevenZipPackInfoWriter
            {
                PackPos = 0,
                Sizes = packed.SelectMany(p => p.Sizes).ToArray(),
                CRCs = packed.SelectMany(p => p.CRCs).ToArray(),
            },
            UnPackInfo = new SevenZipUnPackInfoWriter { Folders = folders },
            SubStreamsInfo = new SevenZipSubStreamsInfoWriter
            {
                Folders = folders,
                NumUnPackStreamsInFolders = [1, 1],
                UnPackSizes = files.Select(f => (ulong)f.Length).ToArray(),
                CRCs = crcs,
            },
        };
        using var header = new MemoryStream();
        ArchiveHeaderWriter.WriteRawHeader(
            header,
            streams,
            new SevenZipFilesInfoWriter
            {
                Entries = files
                    .Select((f, i) => new SevenZipWriteEntry { Name = $"{i}", Attributes = 0x20 })
                    .ToArray(),
            }
        );
        var position = expected.Position;
        header.Position = 0;
        header.CopyTo(expected);
        using var crc = new Crc32Stream(Stream.Null);
        header.WriteTo(crc);
        SevenZipSignatureHeaderWriter.WriteFinal(
            expected,
            (ulong)(position - 32),
            (ulong)header.Length,
            crc.Crc
        );
        Assert.Equal(expected.ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Solid_ProgressAndUnknownSizeLimit(bool asynchronous)
    {
        using var output = new MemoryStream();
        var progress = new ProgressCollector();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions(CompressionType.None)
            {
                Solid = true,
                SolidBlockSize = 15,
                Progress = progress,
            }
        );
        for (var i = 0; i < 3; i++)
        {
            await Write(writer, $"{i}", new byte[10], asynchronous, false);
        }
        await Finish(writer, asynchronous);
        Assert.Equal(new[] { "0", "1", "2" }, progress.Reports.Select(p => p.EntryPath).Distinct());
        Assert.All(
            progress.Reports.GroupBy(p => p.EntryPath),
            group => Assert.Equal(10, group.Last().BytesTransferred)
        );
        output.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(output);
        Assert.Equal(
            new[] { 2, 1 },
            archive.Entries.GroupBy(e => e.FilePart.Folder).Select(g => g.Count())
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Solid_FailedInputRejectsFurtherWrites(bool asynchronous)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(output, new SevenZipWriterOptions());
        writer.BeginSolidBlock();
        using var source = new FailedInput();
        if (asynchronous)
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await writer.WriteAsync("bad", source, null, CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<IOException>(() => writer.Write("bad", source, null));
        }
        Assert.Throws<ObjectDisposedException>(() => writer.BeginSolidBlock());
        await Finish(writer, asynchronous);
    }

    private sealed class FailedInput : MemoryStream
    {
        public override bool CanSeek => false;
        private bool read;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (read)
            {
                throw new IOException("Input failed after a partial file.");
            }
            read = true;
            buffer[offset] = 42;
            return 1;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => Task.FromResult(Read(buffer, offset, count));
    }

    private sealed class ProgressCollector : IProgress<ProgressReport>
    {
        public List<ProgressReport> Reports { get; } = [];

        public void Report(ProgressReport value) => Reports.Add(value);
    }

    private sealed class InputStream(byte[] data, bool seekable) : MemoryStream(data)
    {
        public override bool CanSeek => seekable;

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, 997));

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => base.ReadAsync(buffer, offset, Math.Min(count, 997), cancellationToken);
    }
}
