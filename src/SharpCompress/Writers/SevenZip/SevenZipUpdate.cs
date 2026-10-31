using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.LZMA.Utilities;
using SharpCompress.IO;
using SharpCompress.Readers;

namespace SharpCompress.Writers.SevenZip;

/// <summary>
/// Edits a seekable 7z source into a separate archive, copying unchanged packed folders.
/// The source remains open. An update can be written once and is not thread safe.
/// </summary>
public sealed class SevenZipUpdate : IDisposable
{
    private readonly Stream source;
    private readonly ArchiveDatabase database;
    private readonly string? password;
    private readonly SevenZipUpdateEntry[] entries;
    private bool started;
    private bool disposed;

    private SevenZipUpdate(Stream source, ArchiveDatabase database, PasswordProvider provider)
    {
        this.source = source;
        this.database = database;
        password = provider.Password;
        IsSourceEncrypted =
            provider.Requested || database._folders.Any(folder => folder.IsEncrypted());
        entries = database
            ._files.Select(
                (file, index) =>
                    new SevenZipUpdateEntry(index, file, database._fileIndexToFolderIndexMap[index])
            )
            .ToArray();
        Entries = Array.AsReadOnly(entries);
    }

    /// <summary>Entries in source order, including entries marked deleted.</summary>
    public IReadOnlyList<SevenZipUpdateEntry> Entries { get; }

    /// <summary>Whether any source folder or its header is encrypted.</summary>
    public bool IsSourceEncrypted { get; }

    /// <summary>Open a seekable source, always leaving it open, regardless of reader options.</summary>
    public static SevenZipUpdate Open(Stream source, ReaderOptions? readerOptions = null)
    {
        ValidateSource(source);
        readerOptions ??= ReaderOptions.ForExternalStream;
        source.Position = 0;
        var reader = new ArchiveReader();
        reader.Open(source, readerOptions.LookForHeader);
        var provider = new PasswordProvider(readerOptions.Password);
        return new SevenZipUpdate(source, reader.ReadDatabase(provider), provider);
    }

    /// <summary>Open a seekable source asynchronously, always leaving it open.</summary>
    public static async ValueTask<SevenZipUpdate> OpenAsync(
        Stream source,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateSource(source);
        cancellationToken.ThrowIfCancellationRequested();
        readerOptions ??= ReaderOptions.ForExternalStream;
        source.Position = 0;
        var reader = new ArchiveReader();
        await reader
            .OpenAsync(source, readerOptions.LookForHeader, cancellationToken)
            .ConfigureAwait(false);
        var provider = new PasswordProvider(readerOptions.Password);
        var database = await reader
            .ReadDatabaseAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        return new SevenZipUpdate(source, database, provider);
    }

    private static void ValidateSource(Stream source)
    {
        source = source ?? throw new ArgumentNullException(nameof(source));
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("A readable, seekable source is required.", nameof(source));
        }
        if (
            source is FileStream file
            && int.TryParse(Path.GetExtension(file.Name).TrimStart('.'), out _)
        )
        {
            throw new NotSupportedException("Split 7z sources are not supported.");
        }
        if (source is SourceStream parts && (parts.IsVolumes || parts.Streams.Skip(1).Any()))
        {
            throw new NotSupportedException("Split 7z sources are not supported.");
        }
    }

    private void CheckMutable()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(SevenZipUpdate));
        }
        if (started)
        {
            throw new InvalidOperationException("This update has already started writing.");
        }
    }

    private SevenZipUpdateEntry GetEntry(int index)
    {
        CheckMutable();
        if ((uint)index >= (uint)entries.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return entries[index];
    }

    /// <summary>Omit a source entry.</summary>
    public void Delete(int index) => GetEntry(index).IsDeleted = true;

    /// <summary>Rename an entry, using the same path normalization as SevenZipWriter.</summary>
    public void Rename(int index, string newName)
    {
        var entry = GetEntry(index);
        newName = newName ?? throw new ArgumentNullException(nameof(newName));
        var name = SevenZipWriter.NormalizeFilename(newName);
        entry.Name = entry.IsDirectory ? name.TrimEnd('/') : name;
    }

    /// <summary>Replace all timestamps and raw attributes; null values omit that metadata.</summary>
    public void SetMetadata(int index, SevenZipWriterEntryOptions metadata) =>
        GetEntry(index).Metadata = metadata;

    private void StartWrite(Stream destination, SevenZipWriterOptions options)
    {
        CheckMutable();
        destination = destination ?? throw new ArgumentNullException(nameof(destination));
        options = options ?? throw new ArgumentNullException(nameof(options));
        if (ReferenceEquals(source, destination))
        {
            throw new ArgumentException(
                "The destination must be separate from the source.",
                nameof(destination)
            );
        }
        if (!destination.CanSeek || !destination.CanWrite)
        {
            throw new ArgumentException(
                "A writable, seekable destination is required.",
                nameof(destination)
            );
        }
        started = true;
    }

    // Include streamless entries between files of a folder so their relative order is preserved.
    private int FolderEnd(int start)
    {
        var folderIndex = entries[start].FolderIndex;
        var end = start;
        for (var i = start + 1; i < entries.Length; i++)
        {
            if (entries[i].FolderIndex < 0)
            {
                continue;
            }
            if (entries[i].FolderIndex != folderIndex)
            {
                break;
            }
            end = i;
        }
        return end;
    }

    private bool CanCopy(int start, int end, SevenZipWriterOptions options)
    {
        for (var i = start; i <= end; i++)
        {
            if (entries[i].FolderIndex >= 0 && entries[i].IsDeleted)
            {
                return false;
            }
        }
        var encrypted = database._folders[entries[start].FolderIndex].IsEncrypted();
        return encrypted
            ? options.Password is not null
                && string.Equals(password, options.Password, StringComparison.Ordinal)
            : options.Password is null;
    }

    private bool HasSurvivor(int start, int end)
    {
        for (var i = start; i <= end; i++)
        {
            if (entries[i].FolderIndex >= 0 && !entries[i].IsDeleted)
            {
                return true;
            }
        }
        return false;
    }

    private SevenZipSolidBlockOptions BlockOptions(CFolder folder, SevenZipWriterOptions options)
    {
        var filter = SevenZipFilter.None;
        if (options.CompressionType == CompressionType.LZMA2)
        {
            if (folder._coders.Any(coder => coder._methodId._id == 0x03030103))
            {
                filter = SevenZipFilter.X86;
            }
            else if (folder._coders.Any(coder => coder._methodId._id == 0x0A))
            {
                filter = SevenZipFilter.Arm64;
            }
        }
        return new SevenZipSolidBlockOptions { Filter = filter };
    }

    private PackedStream CloneFolder(int start, int end)
    {
        var original = database._folders[entries[start].FolderIndex];
        var folder = new CFolder
        {
            _coders = original
                ._coders.Select(coder => new CCoderInfo
                {
                    _methodId = coder._methodId,
                    _numInStreams = coder._numInStreams,
                    _numOutStreams = coder._numOutStreams,
                    _props = coder._props is null ? null : (byte[])coder._props.Clone(),
                })
                .ToList(),
            _bindPairs = original
                ._bindPairs.Select(pair => new CBindPair
                {
                    _inIndex = pair._inIndex,
                    _outIndex = pair._outIndex,
                })
                .ToList(),
            _packStreams = new List<int>(original._packStreams),
            _unpackSizes = new List<long>(original._unpackSizes),
            _unpackCrc = original._unpackCrc,
        };
        var files = entries
            .Skip(start)
            .Take(end - start + 1)
            .Where(entry => entry.FolderIndex >= 0)
            .ToArray();
        var substreams = files.Length != 1 || folder._unpackCrc is null;
        return new PackedStream
        {
            PreserveFolderCrc = true,
            Folder = folder,
            Sizes = database
                ._packSizes.Skip(original._firstPackStreamId)
                .Take(original._packStreams.Count)
                .Select(size => checked((ulong)size))
                .ToArray(),
            CRCs = database
                ._packCrCs.Skip(original._firstPackStreamId)
                .Take(original._packStreams.Count)
                .ToArray(),
            SubStreamSizes = substreams
                ? files.Select(entry => checked((ulong)entry.Size)).ToArray()
                : null,
            SubStreamCRCs = substreams ? files.Select(entry => entry.Crc).ToArray() : null,
        };
    }

    /// <summary>
    /// Write kept entries, then invoke the optional additions callback before finalizing.
    /// Do not dispose the callback's writer. Progress reports packed bytes for copied folders
    /// and uncompressed bytes for recompressed and added files. Destination ownership follows options.
    /// </summary>
    public void WriteTo(
        Stream destination,
        SevenZipWriterOptions options,
        Action<SevenZipWriter>? add = null
    )
    {
        StartWrite(destination, options);
        using var writer = new SevenZipWriter(destination, options);
        writer.PrepareUpdate();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.FolderIndex < 0)
            {
                if (!entry.IsDeleted)
                {
                    writer.AppendUpdateEntry(entry.ToWriteEntry());
                }
                continue;
            }
            var end = FolderEnd(index);
            var folder = database._folders[entry.FolderIndex];
            if (CanCopy(index, end, options))
            {
                writer.AppendCopiedFolder(
                    source,
                    database.GetFolderStreamPos(folder, 0),
                    database.GetFolderFullPackSize(entry.FolderIndex),
                    CloneFolder(index, end),
                    entry.Name
                );
                AppendMetadata(writer, index, end);
            }
            else if (HasSurvivor(index, end))
            {
                using var decoded = database.GetFolderStream(
                    source,
                    folder,
                    database.PasswordProvider
                );
                writer.BeginSolidBlock(BlockOptions(folder, options));
                for (var i = index; i <= end; i++)
                {
                    var item = entries[i];
                    if (item.FolderIndex < 0)
                    {
                        if (!item.IsDeleted)
                        {
                            writer.AppendUpdateEntry(item.ToWriteEntry());
                        }
                        continue;
                    }
                    using var input = new ReadOnlySubStream(decoded, item.Size);
                    if (item.IsDeleted)
                    {
                        input.CopyTo(Stream.Null);
                    }
                    else
                    {
                        writer.Write(item.Name, input, item.Metadata);
                        writer.RestoreUpdateMetadata(item.ToWriteEntry());
                    }
                    if (input.Position != item.Size)
                    {
                        throw new EndOfStreamException("Incomplete 7z substream.");
                    }
                }
                writer.EndSolidBlock();
            }
            else
            {
                AppendMetadata(writer, index, end);
            }
            index = end;
        }
        writer.ResetUpdateBlocks();
        add?.Invoke(writer);
    }

    /// <summary>Write the update using asynchronous decode, encode and output I/O. See WriteTo for ownership and progress.</summary>
    public async ValueTask WriteToAsync(
        Stream destination,
        SevenZipWriterOptions options,
        Func<SevenZipWriter, CancellationToken, ValueTask>? add = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartWrite(destination, options);
        await using var writer = new SevenZipWriter(destination, options);
        writer.PrepareUpdate();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.FolderIndex < 0)
            {
                if (!entry.IsDeleted)
                {
                    writer.AppendUpdateEntry(entry.ToWriteEntry());
                }
                continue;
            }
            var end = FolderEnd(index);
            var folder = database._folders[entry.FolderIndex];
            if (CanCopy(index, end, options))
            {
                await writer
                    .AppendCopiedFolderAsync(
                        source,
                        database.GetFolderStreamPos(folder, 0),
                        database.GetFolderFullPackSize(entry.FolderIndex),
                        CloneFolder(index, end),
                        entry.Name,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                AppendMetadata(writer, index, end);
            }
            else if (HasSurvivor(index, end))
            {
                var decoded = await database
                    .GetFolderStreamAsync(
                        source,
                        folder,
                        database.PasswordProvider,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                try
                {
                    writer.BeginSolidBlock(BlockOptions(folder, options));
                    for (var i = index; i <= end; i++)
                    {
                        var item = entries[i];
                        if (item.FolderIndex < 0)
                        {
                            if (!item.IsDeleted)
                            {
                                writer.AppendUpdateEntry(item.ToWriteEntry());
                            }
                            continue;
                        }
                        using var input = new ReadOnlySubStream(decoded, item.Size);
                        if (item.IsDeleted)
                        {
                            await input
                                .CopyToAsync(Stream.Null, options.BufferSize, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await writer
                                .WriteAsync(item.Name, input, item.Metadata, cancellationToken)
                                .ConfigureAwait(false);
                            writer.RestoreUpdateMetadata(item.ToWriteEntry());
                        }
                        if (input.Position != item.Size)
                        {
                            throw new EndOfStreamException("Incomplete 7z substream.");
                        }
                    }
                    await writer.EndSolidBlockAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
#if NETSTANDARD2_0 || NETFRAMEWORK
                    if (decoded is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        decoded.Dispose();
                    }
#else
                    await decoded.DisposeAsync().ConfigureAwait(false);
#endif
                }
            }
            else
            {
                AppendMetadata(writer, index, end);
            }
            index = end;
        }
        writer.ResetUpdateBlocks();
        if (add is not null)
        {
            await add(writer, cancellationToken).ConfigureAwait(false);
        }
    }

    private void AppendMetadata(SevenZipWriter writer, int start, int end)
    {
        for (var i = start; i <= end; i++)
        {
            if (!entries[i].IsDeleted)
            {
                writer.AppendUpdateEntry(entries[i].ToWriteEntry());
            }
        }
    }

    /// <summary>Release this update. The caller-owned source is always left open.</summary>
    public void Dispose() => disposed = true;

    private sealed class PasswordProvider(string? password) : IPasswordProvider
    {
        internal string? Password { get; } = password;
        internal bool Requested { get; private set; }

        public string? CryptoGetTextPassword()
        {
            Requested = true;
            return Password;
        }
    }
}
