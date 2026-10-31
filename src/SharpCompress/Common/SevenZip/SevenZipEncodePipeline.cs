#pragma warning disable VSTHRD103 // Shared sync/async committer; buffer disposal and cancellation registration cleanup are synchronous on all targets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Writers.SevenZip;

namespace SharpCompress.Common.SevenZip;

/// <summary>Caller-owned admission and ordered commit; workers never touch archive state.</summary>
internal sealed class SevenZipEncodePipeline : IDisposable
{
    private sealed class Work
    {
        internal SevenZipParallelFolder Folder = null!;
        internal Task<SevenZipJobBuffer>? Task;
        internal long Estimate;
        internal bool Finished;
    }

    private readonly Queue<Work> queue = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<CancellationTokenRegistration> registrations = new();
    private readonly HashSet<CancellationToken> observed = new();
    private readonly int maxThreads;
    private readonly long? budget;
    private int admitted;
    private long estimated;
    private bool stopped;
    private int nextJob;
    internal Action<int, CancellationToken>? BeforeEncode { get; set; }
    internal Action<int, long, long>? BeforeCapacityWait { get; set; }
    internal int MaxThreads => maxThreads;
    internal long FinishedOutputLimit => Math.Max(256L << 20, (long)maxThreads << 25);

    // Bounds uncommitted jobs behind a slow head (many tiny files): keeps the admission
    // scan short and the per-job overhead below the budget's view of memory.
    internal int MaxQueuedJobs => Math.Max(1024, maxThreads * 64);

    // A finished buffer holds at least one pooled block, whatever its length.
    private const long MinimumFinishedCharge = 64L << 10;
    private readonly object statisticsLock = new();
    private int running;
    private long runningMemory;
    internal int PeakRunningJobs { get; private set; }
    internal long PeakRunningMemory { get; private set; }
    internal int PeakJobs { get; private set; }
    internal long PeakEstimatedMemory { get; private set; }
    internal int PendingJobs => admitted;
    internal CancellationToken Token => cancellation.Token;

    internal SevenZipEncodePipeline(SevenZipWriterOptions options)
    {
        maxThreads = options.MaxThreads;
        budget = options.MemoryBudget;
    }

    internal void Observe(CancellationToken token)
    {
        // Register each caller token once: callers pass the same token for every entry.
        if (token.CanBeCanceled && !observed.Contains(token))
        {
            observed.Add(token);
            registrations.Add(token.Register(() => cancellation.Cancel()));
        }
        Check();
    }

    internal void Check()
    {
        Token.ThrowIfCancellationRequested();
        foreach (var work in queue)
        {
            if (work.Task is { IsFaulted: true } || work.Task is { IsCanceled: true })
            {
                work.Task.WaitForCompletion();
            }
        }
    }

    // Running jobs reserve input + output + encoder memory. Finished jobs retain only
    // their actual output length. Without a budget, stop admission above
    // max(256 MiB, MaxThreads * 32 MiB) of finished, uncommitted output.
    internal static long Estimate(
        long inputSize,
        CompressionType compression,
        LzmaEncoderProperties properties
    )
    {
        var input = Math.Min(inputSize, SevenZipJobBuffer.MemoryLimit);
        if (compression == CompressionType.None)
        {
            return input;
        }
        var output = Math.Min(
            inputSize + (inputSize / 65536 + 1) * 16,
            SevenZipJobBuffer.MemoryLimit
        );
        var encoder =
            (properties.MatchFinder == LzmaMatchFinder.HC4 ? 15L : 23L)
                * properties.DictionarySize
                / 2
            + (4L << 20);
        return checked(input + output + encoder);
    }

    internal async ValueTask SubmitAsync(
        SevenZipParallelFolder folder,
        SevenZipJobBuffer input,
        bool useAsync
    )
    {
        var estimate = Estimate(input.Length, folder.Compression, folder.Properties);
        try
        {
            Check();
            while (true)
            {
                // Commit every ready head before waiting; a slow head must not prevent
                // admission when another worker has released a thread slot.
                while (
                    queue.Count > 0 && (queue.Peek().Task is null || queue.Peek().Task!.IsCompleted)
                )
                {
                    await CommitOneAsync(useAsync).ConfigureAwait(false);
                }
                Check();
                var active = new List<Task>();
                long finishedOutput = 0;
                foreach (var queued in queue)
                {
                    if (queued.Task is null)
                    {
                        continue;
                    }
                    RefreshCharge(queued);
                    if (queued.Finished)
                    {
                        finishedOutput += queued.Estimate;
                    }
                    else
                    {
                        active.Add(queued.Task);
                    }
                }
                // Permit one worker even when a single estimate exceeds the budget.
                if (
                    active.Count == 0
                    || (
                        active.Count < maxThreads
                        && admitted < MaxQueuedJobs
                        && (
                            budget is long limit
                                ? estimate <= limit - estimated
                                : finishedOutput <= FinishedOutputLimit
                        )
                    )
                )
                {
                    break;
                }
                BeforeCapacityWait?.Invoke(active.Count, estimated, finishedOutput);
                var finished = Task.WhenAny(active);
                if (useAsync)
                {
                    await finished.ConfigureAwait(false);
                }
                else
                {
                    _ = finished.WaitForCompletion();
                }
            }
            input.Position = 0;
            var work = new Work { Folder = folder, Estimate = estimate };
            // Copy has no worker and transfers its input buffer straight to the committer.
            var job = nextJob++;
            work.Task =
                folder.Compression == CompressionType.None
                    ? Task.FromResult(input)
                    : Task.Run(() => EncodeTracked(folder, input, estimate, job));
            queue.Enqueue(work);
            admitted++;
            estimated += estimate;
            PeakJobs = Math.Max(PeakJobs, admitted);
            PeakEstimatedMemory = Math.Max(PeakEstimatedMemory, estimated);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private void RefreshCharge(Work work)
    {
        if (!work.Finished && work.Task is { Status: TaskStatus.RanToCompletion })
        {
            var length = Math.Max(work.Task.WaitForCompletion().Length, MinimumFinishedCharge);
            estimated += length - work.Estimate;
            work.Estimate = length;
            work.Finished = true;
        }
    }

    private SevenZipJobBuffer EncodeTracked(
        SevenZipParallelFolder folder,
        SevenZipJobBuffer input,
        long estimate,
        int job
    )
    {
        lock (statisticsLock)
        {
            running++;
            runningMemory += estimate;
            PeakRunningJobs = Math.Max(PeakRunningJobs, running);
            PeakRunningMemory = Math.Max(PeakRunningMemory, runningMemory);
        }
        try
        {
            try
            {
                BeforeEncode?.Invoke(job, Token);
                return Encode(folder, input);
            }
            catch
            {
                input.Dispose();
                throw;
            }
        }
        finally
        {
            lock (statisticsLock)
            {
                running--;
                runningMemory -= estimate;
            }
        }
    }

    private SevenZipJobBuffer Encode(SevenZipParallelFolder folder, SevenZipJobBuffer input)
    {
        using (input)
        {
            var output = new SevenZipJobBuffer();
            try
            {
                Token.ThrowIfCancellationRequested();
                if (folder.Compression == CompressionType.LZMA2)
                {
                    using var codec = new Lzma2EncoderStream(output, folder.Properties);
                    try
                    {
                        var buffer = new byte[65536];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            codec.WriteAsync(buffer, 0, count, Token).WaitForCompletion();
                        }
                        codec.FinishBlockAsync(Token).WaitForCompletion();
                    }
                    catch
                    {
                        codec.Abort();
                        throw;
                    }
                }
                else
                {
                    using var encoder = new Encoder();
                    encoder.SetCoderProperties(
                        folder.Properties.PropIDs,
                        folder.Properties.Properties
                    );
                    encoder.SetStreams(null!, output, -1, -1);
                    encoder.Code(input, true, Token);
                }
                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
    }

    internal void End(SevenZipParallelFolder folder) => queue.Enqueue(new Work { Folder = folder });

    private async ValueTask CommitOneAsync(bool useAsync)
    {
        var work = queue.Peek();
        Token.ThrowIfCancellationRequested();
        if (work.Task is not null)
        {
            // Running jobs use the pipeline token, so cancellation interrupts both waits
            // and CPU work without abandoning an unobserved task.
            var result = useAsync
                ? await work.Task.ConfigureAwait(false)
                : work.Task.WaitForCompletion();
            RefreshCharge(work);
            await work.Folder.CommitAsync(result, useAsync, Token).ConfigureAwait(false);
            result.Dispose();
            admitted--;
            estimated -= work.Estimate;
        }
        else
        {
            await work.Folder.CommitEndAsync(useAsync, Token).ConfigureAwait(false);
        }
        queue.Dequeue();
    }

    internal async ValueTask DrainAsync(bool useAsync)
    {
        Check();
        while (queue.Count > 0)
        {
            await CommitOneAsync(useAsync).ConfigureAwait(false);
        }
    }

    public void Dispose() => StopAsync(false).WaitForCompletion();

    internal async ValueTask StopAsync(bool useAsync)
    {
        if (stopped)
        {
            return;
        }
        stopped = true;
        cancellation.Cancel();
        while (queue.Count > 0)
        {
            var work = queue.Dequeue();
            try
            {
                if (work.Task is not null)
                {
                    var result = useAsync
                        ? await work.Task.ConfigureAwait(false)
                        : work.Task.WaitForCompletion();
                    result.Dispose();
                }
            }
            catch
            {
                // Observe every task; the caller retains the original failure.
            }
            finally
            {
                work.Folder.Dispose();
            }
        }
        admitted = 0;
        estimated = 0;
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }
        registrations.Clear();
        observed.Clear();
        cancellation.Dispose();
    }
}
