using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common.SevenZip;
using SharpCompress.IO;

namespace SharpCompress.Writers.SevenZip;

public partial class SevenZipWriter
{
    private bool isUpdate;

    internal void PrepareUpdate() => isUpdate = true;

    internal void AppendUpdateEntry(SevenZipWriteEntry entry) => entries.Add(entry);

    internal void RestoreUpdateMetadata(SevenZipWriteEntry entry)
    {
        if (!entry.IsEmpty && entries[entries.Count - 1].IsEmpty)
        {
            if (sevenZipOptions.EnableParallelism)
            {
                parallelFolder ??= CreateParallelFolder(blockOptions ?? SnapshotBlockOptions(null));
                parallelFolder.RecordEmptyStream();
            }
            else
            {
                GetBlockEncoder().RecordEmptyStream();
            }
        }
        entries[entries.Count - 1] = entry;
    }

    internal void ResetUpdateBlocks() => explicitBlocks = false;

    internal void AppendCopiedFolder(
        Stream source,
        long offset,
        long packedLength,
        PackedStream packed,
        string name
    )
    {
        EnsurePlaceholderWritten();
        DrainParallelAsync(false).WaitForCompletion();
        source.Position = offset;
        using var limited = new ReadOnlySubStream(source, packedLength);
        WrapWithProgress(limited, name).CopyTo(OutputStream.NotNull());
        if (limited.Position != packedLength)
        {
            throw new EndOfStreamException("Incomplete 7z packed folder.");
        }
        packedStreams.Add(packed);
    }

    internal async ValueTask AppendCopiedFolderAsync(
        Stream source,
        long offset,
        long packedLength,
        PackedStream packed,
        string name,
        CancellationToken cancellationToken
    )
    {
        await EnsurePlaceholderWrittenAsync(cancellationToken).ConfigureAwait(false);
        await DrainParallelAsync(true).ConfigureAwait(false);
        source.Position = offset;
        using var limited = new ReadOnlySubStream(source, packedLength);
        await WrapWithProgress(limited, name)
            .CopyToAsync(OutputStream.NotNull(), sevenZipOptions.BufferSize, cancellationToken)
            .ConfigureAwait(false);
        if (limited.Position != packedLength)
        {
            throw new EndOfStreamException("Incomplete 7z packed folder.");
        }
        packedStreams.Add(packed);
    }
}
