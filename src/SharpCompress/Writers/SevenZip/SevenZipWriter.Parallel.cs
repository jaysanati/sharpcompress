using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common.SevenZip;

namespace SharpCompress.Writers.SevenZip;

public partial class SevenZipWriter
{
    private SevenZipEncodePipeline? encodePipeline;
    private SevenZipParallelFolder? parallelFolder;
    internal int ParallelPeakJobs => encodePipeline?.PeakJobs ?? 0;
    internal long ParallelPeakEstimatedMemory => encodePipeline?.PeakEstimatedMemory ?? 0;
    internal int ParallelPeakRunningJobs => encodePipeline?.PeakRunningJobs ?? 0;
    internal long ParallelPeakRunningMemory => encodePipeline?.PeakRunningMemory ?? 0;
    internal int ParallelPendingJobs => encodePipeline?.PendingJobs ?? 0;

    internal Action<int, CancellationToken>? ParallelBeforeEncode
    {
        set => Pipeline.BeforeEncode = value;
    }

    private SevenZipEncodePipeline Pipeline =>
        encodePipeline ??= new SevenZipEncodePipeline(sevenZipOptions);

    private SevenZipParallelFolder CreateParallelFolder(SevenZipSolidBlockOptions options) =>
        new(Pipeline, OutputStream.NotNull(), options, Encryption, packedStreams.Add);

    private async ValueTask WriteParallelAsync(
        string filename,
        Stream source,
        SevenZipWriterEntryOptions options,
        bool useAsync,
        CancellationToken token
    )
    {
        try
        {
            Pipeline.Observe(token);
            var solid = UsesSolidBlock;
            if (
                solid
                && !explicitBlocks
                && parallelFolder is { FileCount: > 0 } folder
                && (
                    folder.FileCount >= sevenZipOptions.SolidBlockFileCount
                    || folder.Size >= sevenZipOptions.SolidBlockSize
                    || (
                        source.CanSeek
                        && source.Length - source.Position
                            > sevenZipOptions.SolidBlockSize - folder.Size
                    )
                )
            )
            {
                await EndParallelFolderAsync(useAsync, token).ConfigureAwait(false);
            }
            if (parallelFolder is null)
            {
                var settings = blockOptions ?? SnapshotBlockOptions(null);
                if (!solid)
                {
                    settings = new SevenZipSolidBlockOptions
                    {
                        CompressionType = settings.CompressionType,
                        LzmaProperties = settings.LzmaProperties,
                        ExpectedSize = source.CanSeek ? source.Length - source.Position : null,
                    };
                }
                parallelFolder = CreateParallelFolder(settings);
            }
            var size = await parallelFolder
                .WriteFileAsync(WrapWithProgress(source, filename), useAsync, token)
                .ConfigureAwait(false);
            AddSolidEntry(filename, options, size);
            if (!solid)
            {
                await EndParallelFolderAsync(useAsync, token).ConfigureAwait(false);
            }
        }
        catch
        {
            await AbortParallelAsync(useAsync).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask EndParallelFolderAsync(bool useAsync, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            encodePipeline?.Observe(token);
            if (parallelFolder is not null)
            {
                await parallelFolder.FinishAsync(useAsync).ConfigureAwait(false);
                parallelFolder = null;
            }
            blockOptions = null;
        }
        catch
        {
            await AbortParallelAsync(useAsync).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask AbortParallelAsync(bool useAsync)
    {
        finalized = true;
        if (encodePipeline is not null)
        {
            await encodePipeline.StopAsync(useAsync).ConfigureAwait(false);
        }
        parallelFolder?.Dispose();
        parallelFolder = null;
        blockOptions = null;
    }

    private async ValueTask DrainParallelAsync(bool useAsync)
    {
        if (encodePipeline is not null)
        {
            try
            {
                await encodePipeline.DrainAsync(useAsync).ConfigureAwait(false);
            }
            catch
            {
                await AbortParallelAsync(useAsync).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async ValueTask WriteDirectoryParallelAsync(
        string directoryName,
        SevenZipWriterEntryOptions options,
        CancellationToken token
    )
    {
        try
        {
            token.ThrowIfCancellationRequested();
            encodePipeline?.Observe(token);
#pragma warning disable VSTHRD103 // Directory entries only append metadata; they perform no I/O.
            WriteDirectory(directoryName, options);
#pragma warning restore VSTHRD103
        }
        catch
        {
            await AbortParallelAsync(true).ConfigureAwait(false);
            throw;
        }
    }

    private void CheckParallel()
    {
        try
        {
            encodePipeline?.Check();
        }
        catch
        {
            AbortParallelAsync(false).WaitForCompletion();
            throw;
        }
    }
}
