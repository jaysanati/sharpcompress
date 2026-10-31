using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipWriterParallelTests(ITestOutputHelper log)
{
    private static byte[] Data(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }
        return data;
    }

    private static async Task Write(
        SevenZipWriter writer,
        string name,
        byte[] data,
        bool asynchronous
    )
    {
        using var source = new MemoryStream(data);
        if (asynchronous)
        {
            await using var input = new AsyncOnlyStream(source, false);
            await writer.WriteAsync(name, input, null, CancellationToken.None);
        }
        else
        {
            writer.Write(name, source, null);
        }
        Assert.True(source.CanRead);
        Assert.Equal(source.Length, source.Position);
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
        Assert.Equal(0, writer.ParallelPendingJobs);
    }

    private static async Task Verify(
        MemoryStream output,
        byte[][] expected,
        bool asynchronous,
        string? password = null
    )
    {
        output.Position = 0;
        var options = new ReaderOptions { LeaveStreamOpen = true, Password = password };
        if (asynchronous)
        {
            await using var archive = await SevenZipArchive.OpenAsyncArchive(
                output,
                options,
                CancellationToken.None
            );
            var entries = (await archive.EntriesAsync.ToListAsync())
                .Where(e => !e.IsDirectory)
                .ToArray();
            Assert.Equal(expected.Length, entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                using var input = await entries[i].OpenEntryStreamAsync(CancellationToken.None);
                using var restored = new MemoryStream();
                await input.CopyToAsync(restored, 65536, CancellationToken.None);
                Assert.Equal(expected[i], restored.ToArray());
            }
        }
        else
        {
            using var archive = SevenZipArchive.OpenArchive(output, options);
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToArray();
            Assert.Equal(expected.Length, entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                using var input = entries[i].OpenEntryStream();
                using var restored = new MemoryStream();
                input.CopyTo(restored);
                Assert.Equal(expected[i], restored.ToArray());
            }
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 32)]
    [InlineData(true, 32)]
    public async Task SevenZipWriter_Parallel_LargeFile(bool asynchronous, int dictionaryMiB)
    {
        var data = Data(96 << 20);
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions
            {
                EnableParallelism = true,
                MaxThreads = 4,
                CompressHeader = false,
                LzmaProperties = new LzmaEncoderProperties(false, dictionaryMiB << 20),
            }
        );
        await Write(writer, "large", data, asynchronous);
        await Finish(writer, asynchronous);
        Assert.InRange(writer.ParallelPeakRunningJobs, 1, 4);
        if (dictionaryMiB == 1)
        {
            Assert.True(CountResets(output.ToArray()) > 1);
        }
        await Verify(output, new[] { data }, asynchronous);
    }

    // Parse framing, rather than counting E0 bytes that may occur in compressed payloads.
    private static int CountResets(byte[] archive)
    {
        var offset = 32;
        var resets = 0;
        while (archive[offset] != 0)
        {
            var control = archive[offset++];
            if (control >= 0xE0)
            {
                resets++;
            }
            var unpacked = (archive[offset] << 8) + archive[offset + 1] + 1;
            offset += 2;
            if (control >= 0x80)
            {
                var packed = (archive[offset] << 8) + archive[offset + 1] + 1;
                offset += 2 + (control >= 0xC0 ? 1 : 0) + packed;
            }
            else
            {
                offset += unpacked;
            }
        }
        return resets;
    }

    [Theory]
    [InlineData(false, CompressionType.None)]
    [InlineData(true, CompressionType.None)]
    [InlineData(false, CompressionType.LZMA)]
    [InlineData(true, CompressionType.LZMA)]
    [InlineData(false, CompressionType.LZMA2)]
    [InlineData(true, CompressionType.LZMA2)]
    public async Task SevenZipWriter_Parallel_FoldersAndDeterminism(
        bool asynchronous,
        CompressionType codec
    )
    {
        byte[]? baseline = null;
        var files = Enumerable.Range(0, 30).Select(i => Data(i * 317)).ToArray();
        foreach (var threads in new[] { 2, 4, 8 })
        {
            using var output = new MemoryStream();
            var writer = new SevenZipWriter(
                output,
                new SevenZipWriterOptions(codec) { EnableParallelism = true, MaxThreads = threads }
            );
            writer.WriteDirectory("dir", null);
            for (var i = 0; i < files.Length; i++)
            {
                await Write(writer, i.ToString(), files[i], asynchronous);
            }
            await Finish(writer, asynchronous);
            Assert.InRange(writer.ParallelPeakRunningJobs, 0, threads);
            var bytes = output.ToArray();
            if (baseline is not null)
            {
                Assert.Equal(baseline, bytes);
            }
            baseline = bytes;
            await Verify(output, files, asynchronous);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SevenZipWriter_Parallel_SplitDeterminism(bool asynchronous, bool unknownSize)
    {
        var data = Data(13 << 20);
        byte[]? acrossThreads = null;
        foreach (var threads in new[] { 2, 4, 8 })
        {
            byte[]? baseline = null;
            for (var run = 0; run < 2; run++)
            {
                using var output = new MemoryStream();
                var writer = new SevenZipWriter(
                    output,
                    new SevenZipWriterOptions { EnableParallelism = true, MaxThreads = threads }
                );
                using Stream source = unknownSize ? new ShortInput(data) : new MemoryStream(data);
                if (asynchronous)
                {
                    await writer.WriteAsync("split", source, null, CancellationToken.None);
                }
                else
                {
                    writer.Write("split", source, null);
                }
                await Finish(writer, asynchronous);
                var bytes = output.ToArray();
                if (baseline is not null)
                {
                    Assert.Equal(baseline, bytes);
                }
                baseline = bytes;
            }
            if (unknownSize && acrossThreads is not null)
            {
                Assert.Equal(acrossThreads, baseline);
            }
            acrossThreads = baseline;
        }
    }

    [Theory]
    [InlineData(false, 8, 8, false)]
    [InlineData(true, 8, 8, false)]
    [InlineData(false, 2, 4, false)]
    [InlineData(true, 2, 4, false)]
    [InlineData(false, 8, 8, true)]
    [InlineData(true, 8, 8, true)]
    public async Task SevenZipWriter_Parallel_AdaptiveBlocks(
        bool asynchronous,
        int threads,
        int resets,
        bool solid
    )
    {
        var data = Data(64 << 20);
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions
            {
                EnableParallelism = true,
                MaxThreads = threads,
                CompressHeader = false,
                LzmaProperties = new LzmaEncoderProperties(false, 4 << 20),
            }
        );
        if (solid)
        {
            writer.BeginSolidBlock(new SevenZipSolidBlockOptions { ExpectedSize = data.Length });
        }
        await Write(writer, "adaptive", data, asynchronous);
        await Finish(writer, asynchronous);
        Assert.Equal(resets, CountResets(output.ToArray()));
        await Verify(output, new[] { data }, asynchronous);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SevenZipWriter_Parallel_SlowHead(bool asynchronous, bool withBudget)
    {
        using var releaseHead = new ManualResetEventSlim();
        using var firstWave = new CountdownEvent(4);
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions
            {
                EnableParallelism = true,
                MaxThreads = 4,
                MemoryBudget = withBudget ? 20L << 20 : null,
            }
        );
        var passedHead = false;
        writer.ParallelBeforeEncode = (job, token) =>
        {
            if (job == 0)
            {
                firstWave.Signal();
                Assert.True(releaseHead.Wait(TimeSpan.FromSeconds(30), token));
            }
            else if (job <= 3)
            {
                firstWave.Signal();
                Assert.True(firstWave.Wait(TimeSpan.FromSeconds(30), token));
            }
            else if (job == 7)
            {
                passedHead = !releaseHead.IsSet;
                Assert.Equal(4, writer.ParallelPeakRunningJobs);
                releaseHead.Set();
            }
        };
        var files = Enumerable.Range(0, 20).Select(_ => Data(65536)).ToArray();
        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                await Write(writer, i.ToString(), files[i], asynchronous);
            }
            await Finish(writer, asynchronous);
            Assert.True(passedHead);
            Assert.True(writer.ParallelPeakJobs > 4);
            Assert.Equal(4, writer.ParallelPeakRunningJobs);
            if (withBudget)
            {
                Assert.InRange(writer.ParallelPeakEstimatedMemory, 1, 20L << 20);
            }
            await Verify(output, files, asynchronous);
        }
        finally
        {
            releaseHead.Set();
            await Finish(writer, asynchronous);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SevenZipWriter_Parallel_FinishedOutputAdmission(
        bool asynchronous,
        bool withBudget
    )
    {
        using var releaseHead = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pipeline = new SevenZipEncodePipeline(
            new SevenZipWriterOptions
            {
                MaxThreads = 2,
                MemoryBudget = withBudget ? 80L << 20 : null,
            }
        );
        pipeline.Observe(cancellation.Token);
        pipeline.BeforeEncode = (_, token) => releaseHead.Wait(token);
        using var output = new MemoryStream();
        using var head = new SevenZipParallelFolder(
            pipeline,
            output,
            new SevenZipSolidBlockOptions
            {
                CompressionType = CompressionType.LZMA2,
                LzmaProperties = new LzmaEncoderProperties(false, 1 << 20),
            },
            null,
            _ => { }
        );
        using var copy = new SevenZipParallelFolder(
            pipeline,
            output,
            new SevenZipSolidBlockOptions { CompressionType = CompressionType.None },
            null,
            _ => { }
        );
        var observed = false;
        var completedCopies = withBudget ? 2 : 9;
        pipeline.BeforeCapacityWait = (active, charged, finished) =>
        {
            observed = true;
            Assert.Equal(1, active);
            Assert.Equal((long)completedCopies * (32L << 20), finished);
            Assert.Equal(
                finished + SevenZipEncodePipeline.Estimate(0, head.Compression, head.Properties),
                charged
            );
            Assert.Equal(256L << 20, pipeline.FinishedOutputLimit);
            // Interrupt before committing, so the test needs no second copy of the output.
            throw new IOException("Capacity reached");
        };
        try
        {
            await pipeline.SubmitAsync(head, new SevenZipJobBuffer(), asynchronous);
            for (var i = 0; i < completedCopies; i++)
            {
                var buffer = new SevenZipJobBuffer();
                buffer.SetLength(32L << 20);
                await pipeline.SubmitAsync(copy, buffer, asynchronous);
            }
            var next = new SevenZipJobBuffer();
            next.SetLength(32L << 20);
            var error = await Assert.ThrowsAsync<IOException>(async () =>
                await pipeline.SubmitAsync(copy, next, asynchronous)
            );
            Assert.Equal("Capacity reached", error.Message);
            Assert.True(observed);
            Assert.Equal(completedCopies + 1, pipeline.PeakJobs);
            if (withBudget)
            {
                Assert.InRange(pipeline.PeakEstimatedMemory, 1, 80L << 20);
            }
        }
        finally
        {
            releaseHead.Set();
            await pipeline.StopAsync(asynchronous);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SevenZipWriter_Parallel_QueuedJobLimit(bool asynchronous, bool withBudget)
    {
        // Many tiny finished jobs behind a slow head: admission stops at MaxQueuedJobs, and
        // each finished buffer is charged at least one pooled block.
        using var releaseHead = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pipeline = new SevenZipEncodePipeline(
            new SevenZipWriterOptions
            {
                MaxThreads = 2,
                MemoryBudget = withBudget ? 16L << 30 : null,
            }
        );
        pipeline.Observe(cancellation.Token);
        pipeline.BeforeEncode = (_, token) => releaseHead.Wait(token);
        using var output = new MemoryStream();
        using var head = new SevenZipParallelFolder(
            pipeline,
            output,
            new SevenZipSolidBlockOptions
            {
                CompressionType = CompressionType.LZMA2,
                LzmaProperties = new LzmaEncoderProperties(false, 1 << 20),
            },
            null,
            _ => { }
        );
        using var copy = new SevenZipParallelFolder(
            pipeline,
            output,
            new SevenZipSolidBlockOptions { CompressionType = CompressionType.None },
            null,
            _ => { }
        );
        var tiny = pipeline.MaxQueuedJobs - 1;
        var observed = false;
        pipeline.BeforeCapacityWait = (active, charged, finished) =>
        {
            observed = true;
            Assert.Equal(1, active);
            Assert.Equal(pipeline.MaxQueuedJobs, pipeline.PendingJobs);
            Assert.Equal((long)tiny * (64L << 10), finished);
            throw new IOException("Queue full");
        };
        try
        {
            await pipeline.SubmitAsync(head, new SevenZipJobBuffer(), asynchronous);
            for (var i = 0; i < tiny; i++)
            {
                var buffer = new SevenZipJobBuffer();
                buffer.SetLength(100);
                await pipeline.SubmitAsync(copy, buffer, asynchronous);
            }
            var next = new SevenZipJobBuffer();
            next.SetLength(100);
            var error = await Assert.ThrowsAsync<IOException>(async () =>
                await pipeline.SubmitAsync(copy, next, asynchronous)
            );
            Assert.Equal("Queue full", error.Message);
            Assert.True(observed);
        }
        finally
        {
            releaseHead.Set();
            await pipeline.StopAsync(asynchronous);
        }
    }

    [Theory]
    [InlineData(false, SevenZipFilter.X86)]
    [InlineData(true, SevenZipFilter.X86)]
    [InlineData(false, SevenZipFilter.Arm64)]
    [InlineData(true, SevenZipFilter.Arm64)]
    public async Task SevenZipWriter_Parallel_SolidFilterAndEncryption(
        bool asynchronous,
        SevenZipFilter filter
    )
    {
        var data = Data((5 << 20) + 13);
        for (var i = 0; i + 8 < data.Length; i += 8)
        {
            data[i] = 0xE8;
            data[i + 1] = 0x10;
            data[i + 2] = 0;
            data[i + 3] = 0x94;
            data[i + 4] = 0;
        }
        var files = new[]
        {
            data.Take(3).ToArray(),
            data.Skip(3).ToArray(),
            Array.Empty<byte>(),
            Data(123),
        };
        using var output = new MemoryStream();
        await using var destination = new AsyncOnlyStream(output, false);
        var writer = new SevenZipWriter(
            asynchronous ? destination : output,
            new SevenZipWriterOptions
            {
                EnableParallelism = true,
                MaxThreads = 4,
                Password = "secret",
                EncryptHeader = true,
            }
        );
        writer.BeginSolidBlock(new SevenZipSolidBlockOptions { Filter = filter });
        for (var i = 0; i < files.Length; i++)
        {
            await Write(writer, i.ToString(), files[i], asynchronous);
        }
        if (asynchronous)
        {
            await writer.EndSolidBlockAsync(CancellationToken.None);
        }
        else
        {
            writer.EndSolidBlock();
        }
        writer.BeginSolidBlock();
        await Write(writer, "next", data, asynchronous);
        await Finish(writer, asynchronous);
        await Verify(output, files.Concat(new[] { data }).ToArray(), asynchronous, "secret");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 60L << 20)]
    [InlineData(true, 60L << 20)]
    public async Task SevenZipWriter_Parallel_Budget(bool asynchronous, long budget)
    {
        var data = Data(17 << 20);
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions
            {
                EnableParallelism = true,
                MaxThreads = 4,
                MemoryBudget = budget,
            }
        );
        await Write(writer, "budget", data, asynchronous);
        await Finish(writer, asynchronous);
        if (budget == 1)
        {
            Assert.Equal(1, writer.ParallelPeakJobs);
            Assert.Equal(1, writer.ParallelPeakRunningJobs);
        }
        else
        {
            Assert.InRange(writer.ParallelPeakEstimatedMemory, 1, budget);
            Assert.InRange(writer.ParallelPeakRunningMemory, 1, budget);
            Assert.InRange(writer.ParallelPeakRunningJobs, 1, 4);
            Assert.InRange(writer.ParallelPeakRunningJobs, 1, 4);
        }
        await Verify(output, new[] { data }, asynchronous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Parallel_SourceFailure(bool asynchronous)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions { EnableParallelism = true }
        );
        using var source = new FailingInput();
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
        Assert.Equal(0, writer.ParallelPendingJobs);
        await Finish(writer, asynchronous);
        Assert.True(output.CanWrite);
    }

    private sealed class FailingInput : MemoryStream
    {
        private int reads;
        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++reads > 80)
            {
                throw new IOException("Injected source failure");
            }
            Array.Clear(buffer, offset, count);
            return count;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken token
        ) => Task.FromResult(Read(buffer, offset, count));
    }

    [Fact]
    public async Task SevenZipWriter_Parallel_Cancellation()
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions { EnableParallelism = true, MaxThreads = 2 }
        );
        using var source = new MemoryStream(Data(96 << 20));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.WriteAsync("cancel", source, null, cancellation.Token)
        );
        Assert.Equal(0, writer.ParallelPendingJobs);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        await writer.DisposeAsync();
    }

    [Fact]
    public void SevenZipWriter_Parallel_Options()
    {
        var options = new SevenZipWriterOptions();
        Assert.False(options.EnableParallelism);
        Assert.Equal(Environment.ProcessorCount, options.MaxThreads);
        Assert.Null(options.MemoryBudget);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxThreads = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MemoryBudget = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MemoryBudget = -1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Parallel_Update(bool asynchronous)
    {
        var data = Data(10000);
        using var source = new MemoryStream();
        using (var writer = new SevenZipWriter(source, new SevenZipWriterOptions()))
        {
            writer.BeginSolidBlock();
            await Write(writer, "keep", data, asynchronous);
            await Write(writer, "delete", data, asynchronous);
            writer.EndSolidBlock();
            await Write(writer, "copied", data, asynchronous);
        }
        source.Position = 0;
        using var update = asynchronous
            ? await SevenZipUpdate.OpenAsync(
                source,
                ReaderOptions.ForExternalStream,
                CancellationToken.None
            )
            : SevenZipUpdate.Open(source, ReaderOptions.ForExternalStream);
        update.Delete(1);
        using var output = new MemoryStream();
        var options = new SevenZipWriterOptions { EnableParallelism = true, MaxThreads = 4 };
        if (asynchronous)
        {
            await update.WriteToAsync(
                output,
                options,
                async (writer, token) =>
                {
                    using var input = new MemoryStream(data);
                    await writer.WriteAsync("added", input, null, token);
                },
                CancellationToken.None
            );
        }
        else
        {
            update.WriteTo(
                output,
                options,
                writer =>
                {
                    using var input = new MemoryStream(data);
                    writer.Write("added", input, null);
                }
            );
        }
        await Verify(output, new[] { data, data, data }, asynchronous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Parallel_WorkerFailure(bool asynchronous)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions
            {
                EnableParallelism = true,
                LzmaProperties = new LzmaEncoderProperties(false, -1),
            }
        );
        await Write(writer, "invalid", new byte[100], asynchronous);
        if (asynchronous)
        {
            await Assert.ThrowsAsync<InvalidParamException>(async () =>
                await writer.DisposeAsync()
            );
        }
        else
        {
            Assert.Throws<InvalidParamException>(() => writer.Dispose());
        }
        Assert.Equal(0, writer.ParallelPendingJobs);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task SevenZipWriter_Parallel_PreCanceledCallJoinsEarlierJobs()
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions { EnableParallelism = true }
        );
        await Write(writer, "pending", Data(4 << 20), true);
        using var source = new MemoryStream(new byte[1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.WriteAsync("cancel", source, null, new CancellationToken(true))
        );
        Assert.Equal(0, writer.ParallelPendingJobs);
        await writer.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Parallel_AutomaticBlocks(bool asynchronous)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions
            {
                EnableParallelism = true,
                Solid = true,
                SolidBlockFileCount = 2,
            }
        );
        var files = Enumerable.Range(0, 5).Select(_ => Data(100)).ToArray();
        for (var i = 0; i < files.Length; i++)
        {
            await Write(writer, i.ToString(), files[i], asynchronous);
        }
        await Finish(writer, asynchronous);
        await Verify(output, files, asynchronous);
        output.Position = 0;
        using var archive = (SevenZipArchive)
            SevenZipArchive.OpenArchive(output, ReaderOptions.ForExternalStream);
        Assert.Equal(
            new[] { 2, 2, 1 },
            archive.Entries.GroupBy(e => e.FilePart.Folder).Select(g => g.Count())
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZipWriter_Parallel_CanceledMetadataJoinsWorkers(bool directory)
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions { EnableParallelism = true }
        );
        await Write(writer, "pending", Data(4 << 20), true);
        var token = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (directory)
            {
                await writer.WriteDirectoryAsync("dir", null, token);
            }
            else
            {
                await writer.EndSolidBlockAsync(token);
            }
        });
        Assert.Equal(0, writer.ParallelPendingJobs);
        await writer.DisposeAsync();
    }

    [Fact]
    public void SevenZipWriter_Parallel_SpillStorage()
    {
        using var buffer = new SevenZipJobBuffer();
        var block = Data(1 << 20);
        var blocks = (int)(SevenZipJobBuffer.MemoryLimit >> 20) + 1;
        for (var i = 0; i < blocks; i++)
        {
            buffer.Write(block, 0, block.Length);
        }
        Assert.Equal((long)blocks << 20, buffer.Length);
        buffer.Position = SevenZipJobBuffer.MemoryLimit - 16;
        var restored = new byte[32];
        buffer.ReadExact(restored, 0, restored.Length);
        Assert.Equal(block.Skip(block.Length - 16).Concat(block.Take(16)), restored);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SevenZipWriter_Parallel_OwnershipAndUnknownSize(
        bool asynchronous,
        bool leaveOpen
    )
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(
            output,
            new SevenZipWriterOptions { EnableParallelism = true, LeaveStreamOpen = leaveOpen }
        );
        var data = Data(65549);
        using var source = new ShortInput(data);
        if (asynchronous)
        {
            await writer.WriteAsync("unknown", source, null, CancellationToken.None);
        }
        else
        {
            writer.Write("unknown", source, null);
        }
        Assert.True(source.CanRead);
        Assert.Equal(data.Length, source.Position);
        await Finish(writer, asynchronous);
        Assert.Equal(leaveOpen, output.CanWrite);
        using var archiveBytes = new MemoryStream(output.ToArray());
        await Verify(archiveBytes, new[] { data }, asynchronous);
    }

    private sealed class ShortInput(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(997, count));

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken token
        ) => base.ReadAsync(buffer, offset, Math.Min(997, count), token);
    }

    [Fact]
    public async Task SevenZipWriter_Parallel_ManyFilesSpeedSmoke()
    {
        var large = Data(16 << 20);
        var small = Data(128 << 10);
        foreach (var parallel in new[] { false, true })
        {
            using var output = new MemoryStream();
            var stopwatch = Stopwatch.StartNew();
            var writer = new SevenZipWriter(
                output,
                new SevenZipWriterOptions { EnableParallelism = parallel, MaxThreads = 4 }
            );
            await Write(writer, "large", large, false);
            for (var i = 0; i < 256; i++)
            {
                await Write(writer, i.ToString(), small, false);
            }
            await Finish(writer, false);
            log.WriteLine(
                $"257 files / 48 MiB, parallel={parallel}: {stopwatch.Elapsed.TotalSeconds:F3}s, {48 / stopwatch.Elapsed.TotalSeconds:F2} MiB/s, {output.Length} bytes"
            );
        }
    }

    [Fact]
    public async Task SevenZipWriter_Parallel_SpeedSmoke()
    {
        var data = Data(96 << 20);
        var times = new List<double>();
        foreach (var parallel in new[] { false, true })
        {
            using var output = new MemoryStream();
            var stopwatch = Stopwatch.StartNew();
            var writer = new SevenZipWriter(
                output,
                new SevenZipWriterOptions { EnableParallelism = parallel, MaxThreads = 4 }
            );
            await Write(writer, "speed", data, false);
            await Finish(writer, false);
            stopwatch.Stop();
            times.Add(stopwatch.Elapsed.TotalSeconds);
            log.WriteLine(
                $"96 MiB, 1 MiB dictionary, parallel={parallel}: {stopwatch.Elapsed.TotalSeconds:F3}s, {96 / stopwatch.Elapsed.TotalSeconds:F2} MiB/s, {output.Length} bytes"
            );
        }
        log.WriteLine($"Speed ratio: {times[0] / times[1]:F2}x");
    }
}
