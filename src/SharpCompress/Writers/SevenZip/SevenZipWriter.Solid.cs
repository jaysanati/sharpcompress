using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;

namespace SharpCompress.Writers.SevenZip;

public partial class SevenZipWriter
{
    private SevenZipSolidBlockOptions? blockOptions;
    private SevenZipSolidEncoder? blockEncoder;
    private bool explicitBlocks;

    /// <summary>Start a caller-defined solid block, ending any previous block first.
    /// Empty entries do not participate. Explicit blocks disable automatic grouping.</summary>
    public void BeginSolidBlock(SevenZipSolidBlockOptions? options = null)
    {
        if (finalized)
        {
            throw new ObjectDisposedException(nameof(SevenZipWriter));
        }
        var snapshot = SnapshotBlockOptions(options);
        EndSolidBlock();
        explicitBlocks = true;
        blockOptions = snapshot;
    }

    private SevenZipSolidBlockOptions SnapshotBlockOptions(SevenZipSolidBlockOptions? options)
    {
        var compression = options?.CompressionType ?? sevenZipOptions.CompressionType;
        if (
            compression != CompressionType.None
            && compression != CompressionType.LZMA
            && compression != CompressionType.LZMA2
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Solid blocks support None, LZMA and LZMA2."
            );
        }
        var filter = options?.Filter ?? SevenZipFilter.None;
        if (
            filter != SevenZipFilter.None
            && filter != SevenZipFilter.X86
            && filter != SevenZipFilter.Arm64
        )
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Unsupported branch filter.");
        }
        if (options?.ExpectedSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "ExpectedSize must be non-negative."
            );
        }
        return new SevenZipSolidBlockOptions
        {
            CompressionType = compression,
            LzmaProperties = options?.LzmaProperties ?? sevenZipOptions.LzmaProperties,
            Filter = filter,
            ExpectedSize = options?.ExpectedSize,
        };
    }

    /// <summary>Finish the open solid block. Calling this without an open block has no effect.</summary>
    public void EndSolidBlock()
    {
        var encoder = blockEncoder;
        blockEncoder = null;
        blockOptions = null;
        if (encoder is not null)
        {
            PackedStream packed;
            try
            {
                packed = encoder.Finish();
            }
            catch
            {
                finalized = true;
                throw;
            }
            if (encoder.FileCount > 0)
            {
                packedStreams.Add(packed);
            }
        }
    }

    /// <summary>Finish the open solid block using asynchronous output I/O.</summary>
    public async ValueTask EndSolidBlockAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoder = blockEncoder;
        blockEncoder = null;
        blockOptions = null;
        if (encoder is not null)
        {
            PackedStream packed;
            try
            {
                packed = await encoder.FinishAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                finalized = true;
                throw;
            }
            if (encoder.FileCount > 0)
            {
                packedStreams.Add(packed);
            }
        }
    }

    private bool UsesSolidBlock =>
        blockOptions is not null || (sevenZipOptions.Solid && !explicitBlocks);

    private bool MustSplitBlock(Stream source)
    {
        if (explicitBlocks || blockEncoder is null || blockEncoder.FileCount == 0)
        {
            return false;
        }
        return blockEncoder.FileCount >= sevenZipOptions.SolidBlockFileCount
            || blockEncoder.Size >= sevenZipOptions.SolidBlockSize
            || (
                source.CanSeek
                && source.Length - source.Position
                    > sevenZipOptions.SolidBlockSize - blockEncoder.Size
            );
    }

    private SevenZipSolidEncoder GetBlockEncoder()
    {
        blockOptions ??= SnapshotBlockOptions(null);
        return blockEncoder ??= new SevenZipSolidEncoder(
            OutputStream.NotNull(),
            blockOptions.CompressionType!.Value,
            blockOptions.LzmaProperties,
            blockOptions.Filter,
            blockOptions.ExpectedSize,
            Encryption
        );
    }

    private void AddSolidEntry(string filename, SevenZipWriterEntryOptions options, long size) =>
        entries.Add(
            new SevenZipWriteEntry
            {
                Name = filename,
                ModificationTime = options.ModificationTime,
                CreationTime = options.CreationTime,
                LastAccessTime = options.LastAccessTime,
                Attributes = options.Attributes ?? 0x20,
                IsDirectory = false,
                IsEmpty = size == 0,
            }
        );
}
