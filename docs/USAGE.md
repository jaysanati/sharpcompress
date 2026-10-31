# SharpCompress Usage

## Async/Await Support

SharpCompress now provides full async/await support for all I/O operations. All `Read`, `Write`, and extraction operations have async equivalents ending in `Async` that accept an optional `CancellationToken`. This enables better performance and scalability for I/O-bound operations.

**Key Async Methods:**
- `reader.WriteEntryToAsync(stream, cancellationToken)` - Extract entry asynchronously  
- `reader.WriteAllToDirectoryAsync(path, cancellationToken: cancellationToken)` - Extract all asynchronously
- `writer.WriteAsync(filename, stream, modTime, cancellationToken)` - Write entry asynchronously
- `writer.WriteAllAsync(directory, pattern, searchOption, cancellationToken)` - Write directory asynchronously
- `entry.OpenEntryStreamAsync(cancellationToken)` - Open entry stream asynchronously

See [Async Examples](#async-examples) section below for usage patterns.

## Stream Rules

When dealing with Streams, the rule should be that you don't close a stream you didn't create. This, in effect, should mean you should always put a Stream in a using block to dispose it. 

However, the .NET Framework often has classes that will dispose streams by default to make things "easy" like the following:

```C#
using (var reader = new StreamReader(File.Open("foo")))
{
    ...
}
```

In this example, reader should get disposed. However, stream rules should say the the `FileStream` created by `File.Open` should remain open. However, the .NET Framework closes it for you by default unless you override the constructor. In general, you should be writing Stream code like this:

```C#
using (var fileStream = File.Open("foo"))
using (var reader = new StreamReader(fileStream))
{
    ...
}
```

To deal with the "correct" rules as well as the expectations of users, I've decided to always close wrapped streams as of 0.21.

To be explicit though, consider always using the overloads that use `ReaderOptions` or `WriterOptions` and explicitly set `LeaveStreamOpen` the way you want.

Default behavior in factory APIs:
- File path / `FileInfo` overloads set `LeaveStreamOpen = false`.
- Caller-provided `Stream` overloads set `LeaveStreamOpen = true`.

If using Compression Stream classes directly and you don't want the wrapped stream to be closed.  Use the `NonDisposingStream` as a wrapper to prevent the stream being disposed.  The change in 0.21 simplified a lot even though the usage is a bit more convoluted.

## Samples

Also, look over the tests for more thorough [examples](https://github.com/adamhathcock/sharpcompress/tree/master/tests/SharpCompress.Test)

### Create Zip Archive from multiple files
```C#
using(var archive = ZipArchive.CreateArchive())
{
    archive.AddEntry("file01.txt", "C:\\file01.txt");
    archive.AddEntry("file02.txt", "C:\\file02.txt");
    ...
    
    archive.SaveTo("C:\\temp.zip", CompressionType.Deflate);
}
```

### Create Zip Archive from all files in a directory to a file

```C#
using (var archive = ZipArchive.CreateArchive())
{
    archive.AddAllFromDirectory("D:\\temp");
    archive.SaveTo("C:\\temp.zip", CompressionType.Deflate);
}
```

### Create Zip Archive from all files in a directory and save in memory

```C#
var memoryStream = new MemoryStream();
using (var archive = ZipArchive.CreateArchive())
{
    archive.AddAllFromDirectory("D:\\temp");
    archive.SaveTo(memoryStream, new WriterOptions(CompressionType.Deflate)
                                {
                                    LeaveStreamOpen = true
                                });
}
//reset memoryStream to be usable now
memoryStream.Position = 0;
```

### Extract all files from a rar file to a directory using RarArchive

Note: Extracting a solid rar or 7z file needs to be done in sequential order to get acceptable decompression speed.
`ExtractAllEntries` is primarily intended for solid archives (like solid Rar) or 7Zip archives, where sequential extraction provides the best performance. For general/simple extraction with any supported archive type, use `archive.WriteToDirectory()` instead.

```C#
// Use ReaderOptions for open-time behavior and ExtractionOptions for extract-time behavior
using (var archive = RarArchive.OpenArchive("Test.rar", ReaderOptions.ForFilePath))
{
    // Simple extraction with RarArchive; this WriteToDirectory pattern works for all archive types
    archive.WriteToDirectory(
        @"D:\temp",
        new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
            BufferSize = 131072,
            CheckCrc = true, // Default: validate payload checksums when available
        }
    );
}
```

### Iterate over all files from a Rar file using RarArchive

```C#
using (var archive = RarArchive.OpenArchive("Test.rar"))
{
    foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
    {
        Console.WriteLine($"{entry.Key}: {entry.Size} bytes");
    }
}
```

### Extract solid Rar or 7Zip archives with progress reporting

For optimal performance with solid Rar and 7Zip archives, extract entries sequentially. `WriteToDirectory` handles that internally for simple extraction. Use `ExtractAllEntries` when you need to manually iterate in sequential order.

```C#
using SharpCompress.Common;
using SharpCompress.Readers;

var progress = new Progress<ProgressReport>(report =>
{
    Console.WriteLine($"Extracting {report.EntryPath}: {report.PercentComplete}%");
});

using (var archive = RarArchive.OpenArchive("archive.rar",
    ReaderOptions.ForFilePath
        .WithProgress(progress)))
{
    archive.WriteToDirectory(
        @"D:\output",
        new ExtractionOptions { ExtractFullPath = true, Overwrite = true, CheckCrc = true }
    );
}
```

Manual sequential extraction:

```C#
using (var archive = RarArchive.OpenArchive("archive.rar",
    ReaderOptions.ForFilePath.WithProgress(progress)))
using (var reader = archive.ExtractAllEntries())
{
    while (reader.MoveToNextEntry())
    {
        if (!reader.Entry.IsDirectory)
        {
            reader.WriteEntryToDirectory(@"D:\output");
        }
    }
}
```

### Use ReaderFactory to autodetect archive type and Open the entry stream

```C#
using (Stream stream = File.OpenRead("Tar.tar.bz2"))
using (var reader = ReaderFactory.OpenReader(stream))
{
    while (reader.MoveToNextEntry())
    {
        if (!reader.Entry.IsDirectory)
        {
            Console.WriteLine(reader.Entry.Key);
            reader.WriteEntryToDirectory(@"C:\temp");
        }
    }
}
```

### Use ReaderFactory to autodetect archive type and Open the entry stream

```C#
using (Stream stream = File.OpenRead("Tar.tar.bz2"))
using (var reader = ReaderFactory.OpenReader(stream))
{
    while (reader.MoveToNextEntry())
    {
        if (!reader.Entry.IsDirectory)
        {
            using (var entryStream = reader.OpenEntryStream())
            {
                entryStream.CopyTo(...);
            }
        }
    }
}
```

### Use WriterFactory to write all files from a directory in a streaming manner.

```C#
using (Stream stream = File.OpenWrite("C:\\temp.tgz"))
using (var writer = WriterFactory.OpenWriter(
    stream,
    ArchiveType.Tar,
    WriterOptions.ForTar(CompressionType.GZip).WithLeaveStreamOpen(true)))
{
    writer.WriteAll("D:\\temp", "*", SearchOption.AllDirectories);
}
```

### Use ArchiveFactory to autodetect and open archives

```C#
if (ArchiveFactory.IsArchive("archive.zip", out var archiveType))
{
    Console.WriteLine($"Detected {archiveType}");
}

using (var archive = ArchiveFactory.OpenArchive("archive.zip"))
{
    archive.WriteToDirectory(@"D:\output");
}
```

### Detect the format and choose the right API

```C#
using SharpCompress.Detection;

var archivePath = "archive.arc";
var detection = ArchiveDetector.DetectArchive(archivePath);
if (detection is null)
{
    Console.WriteLine("Not a supported archive");
}
else if (detection.SupportedApis.HasFlag(ArchiveAccessMode.Archive))
{
    using var archive = ArchiveFactory.OpenArchive(archivePath);
    archive.WriteToDirectory(@"D:\output");
}
else
{
    using var reader = ReaderFactory.OpenReader(archivePath);
    reader.WriteAllToDirectory(@"D:\output");
}
```

`DetectArchive` does not enumerate entries. It reports both Archive and Reader API availability; reader-only formats include Ace, Arc, Arj, standalone LZW, and compressed TAR wrappers.

### Inspect archive metadata

```C#
using SharpCompress.Detection;

var information = ArchiveInspector.InspectArchive(archivePath);
if (information is not null)
{
    Console.WriteLine($"Entries: {information.EntryCount}");
    Console.WriteLine($"Compressed bytes: {information.CompressedPayloadSize}");
    Console.WriteLine($"Solid streams: {information.SolidStreamCount}");
}
```

`InspectArchive` parses archive metadata, which can require a complete sequential scan for TAR and compressed TAR files. It returns partial information for encrypted headers without a password or missing archive parts; inspect `Status` and `Limitations` before using nullable metadata values.

Detection and inspection entry points and result types are in the `SharpCompress.Detection` namespace.

The stream overload preserves the caller's current position and leaves the supplied stream open.

### Open multi-volume archives

```C#
var parts = ArchiveFactory.GetFileParts("archive.part1.rar")
    .Select(path => new FileInfo(path))
    .ToArray();

using (var archive = ArchiveFactory.OpenArchive(parts))
{
    archive.WriteToDirectory(@"D:\output");
}
```

### Use ReaderOptions for self-extracting archives and detection hints

```C#
var sfxOptions = ReaderOptions.ForSelfExtractingArchive("password");
using (var archive = RarArchive.OpenArchive("setup.exe", sfxOptions))
{
    archive.WriteToDirectory(@"D:\output");
}

using (Stream stream = File.OpenRead("backup"))
using (var reader = ReaderFactory.OpenReader(
    stream,
    ReaderOptions.ForExternalStream.WithExtensionHint("tar.gz")))
{
    while (reader.MoveToNextEntry())
    {
        if (!reader.Entry.IsDirectory)
        {
            reader.WriteEntryToDirectory(@"D:\output");
        }
    }
}
```

### Write ZIP entries with per-entry options

```C#
using Stream archiveStream = File.Create("output.zip");
using var writer = new ZipWriter(
    archiveStream,
    new ZipWriterOptions(CompressionType.Deflate)
    {
        ArchiveComment = "Archive comment",
        UseZip64 = true,
    });

using Stream source = File.OpenRead("input.txt");
writer.Write("entry.txt", source, new ZipWriterEntryOptions
{
    CompressionType = CompressionType.ZStandard,
    CompressionLevel = 3,
    EntryComment = "Entry comment",
    ModificationDateTime = DateTime.UtcNow,
    EnableZip64 = true,
});
```

### Write ZIP64 to a non-seekable stream

Pass `ZipWriterOptions` directly to the general writer factory. Enable ZIP64 before
writing entries whose compressed or uncompressed sizes may reach 4 GiB; it cannot
be enabled retroactively after a non-seekable entry's header has been written.
The same options work with seekable output and synchronous `OpenWriter`.

```C#
// outputStream can be a caller-owned network stream or pipe.
await using var writer = await WriterFactory.OpenAsyncWriter(
    outputStream,
    ArchiveType.Zip,
    new ZipWriterOptions(CompressionType.Deflate)
    {
        UseZip64 = true,
        LeaveStreamOpen = true,
    },
    cancellationToken);

using Stream source = File.OpenRead("large-file.bin");
await writer.WriteAsync("large-file.bin", source, DateTime.UtcNow, cancellationToken);
```

### Write a 7z archive

```C#
using Stream stream = File.Create("output.7z");
using var writer = WriterFactory.OpenWriter(
    stream,
    ArchiveType.SevenZip,
    new SevenZipWriterOptions(CompressionType.LZMA2)
    {
        CompressHeader = true,
    });

using Stream source = File.OpenRead("input.txt");
writer.Write("input.txt", source, DateTime.UtcNow);
```

For fast LZMA or LZMA2 compression, set `LzmaProperties` on the writer options:

```csharp
LzmaProperties = new LzmaEncoderProperties(
    eos: false, dictionary: 1 << 20, numFastBytes: 32,
    matchFinder: LzmaMatchFinder.HC4, mode: LzmaEncoderMode.Fast),
```

These types are in `SharpCompress.Compressors.LZMA`. Use normal + BT4 (the defaults)
when compression ratio matters more than encoding speed. All three match finders
can be combined with either parsing mode.

### Store 7z files with per-entry metadata

```csharp
using var output = File.Create("stored.7z");
await using var writer = new SevenZipWriter(
    output,
    new SevenZipWriterOptions(CompressionType.None)
    {
        Password = "example password",
        EncryptHeader = true,
    });
using var source = File.OpenRead("input.txt");
await writer.WriteAsync(
    "input.txt",
    source,
    new SevenZipWriterEntryOptions
    {
        CreationTime = File.GetCreationTimeUtc("input.txt"),
        LastAccessTime = File.GetLastAccessTimeUtc("input.txt"),
        ModificationTime = File.GetLastWriteTimeUtc("input.txt"),
        Attributes = 0x81A48000, // Unix regular file, mode 0644.
    },
    CancellationToken.None);
await writer.WriteDirectoryAsync(
    "directory",
    new SevenZipWriterEntryOptions { Attributes = 0x41ED8000 }, // Unix directory, mode 0755.
    CancellationToken.None);
```

Use `Write` and `WriteDirectory` with the same entry options for synchronous writing.
Omit `Password` and `EncryptHeader` for an unencrypted archive.

### Extract zip which has non-utf8 encoded filename(cp932)

```C#
var encoding = Encoding.GetEncoding(932);
var opts = new ReaderOptions()
    .WithArchiveEncoding(new ArchiveEncoding
    {
        CustomDecoder = (data, x, y) => encoding.GetString(data)
    });

using var archive = ZipArchive.OpenArchive("test.zip", opts);
foreach(var entry in archive.Entries)
{
    Console.WriteLine($"{entry.Key}");
}
```

## Custom Compression Providers

By default `ReaderOptions` and `WriterOptions` already include `CompressionProviderRegistry.Default` via their `Providers` property, so you can read and write without touching the registry yet still get SharpCompress’s built-in implementations.

The configured registry is used consistently across Reader APIs, Writer APIs, Archive APIs, and async entry-stream extraction, including compressed TAR wrappers and ZIP async decompression.

To replace specific algorithms (for example to use `System.IO.Compression` for GZip or Deflate), create a modified registry and pass it through the same options:

```C#
var customRegistry = CompressionProviderRegistry.Default
    .With(new SystemGZipCompressionProvider())
    .With(new SystemDeflateCompressionProvider());

var readerOptions = ReaderOptions.ForFilePath
    .WithProviders(customRegistry);
using var reader = ReaderFactory.OpenReader(stream, readerOptions);

var writerOptions = new WriterOptions(CompressionType.GZip)
    .WithProviders(customRegistry);
using var writer = WriterFactory.OpenWriter(outputStream, ArchiveType.GZip, writerOptions);
```

The registry is immutable. `With(provider)` returns a new registry and replaces any existing provider for that provider's `CompressionType`. You can inspect or use providers directly with `GetProvider`, `CreateCompressStream`, `CreateDecompressStream`, and their async/context overloads, but most application code should flow the registry through `ReaderOptions` or `WriterOptions` so archive readers and writers can supply the right `CompressionContext`.

To implement a custom provider, implement `ICompressionProvider` directly or derive from `CompressionProviderBase` for default async methods. Derive from `DecompressionOnlyProviderBase` for read-only codecs. Providers can use `CompressionContext` for stream size, seekability, reader options, compression properties, and format-specific metadata.

The registry also exposes `GetCompressingProvider` (now returning `ICompressionProviderHooks`) when a compression format needs pre- or post-stream data (e.g., LZMA/PPMd). Implementations that need extra headers can supply those bytes through the `ICompressionProviderHooks` members while the rest of the API still works through the `Providers` property.

## Async Examples

### Async Reader Examples

**Extract single entry asynchronously:**
```C#
using Stream stream = File.OpenRead("archive.zip");
await using var reader = await ReaderFactory.OpenAsyncReader(stream, cancellationToken: cancellationToken);
while (await reader.MoveToNextEntryAsync(cancellationToken))
{
    if (!reader.Entry.IsDirectory)
    {
        using var outputStream = File.Create("output.bin");
        await reader.WriteEntryToAsync(outputStream, cancellationToken);
    }
}
```

**Extract all entries asynchronously:**
```C#
using Stream stream = File.OpenRead("archive.tar.gz");
await using var reader = await ReaderFactory.OpenAsyncReader(stream, cancellationToken: cancellationToken);
await reader.WriteAllToDirectoryAsync(
    @"D:\temp",
    cancellationToken: cancellationToken
);
```

**Open and process entry stream asynchronously:**
```C#
await using var archive = await ZipArchive.OpenAsyncArchive("archive.zip", cancellationToken: cancellationToken);
await foreach (var entry in archive.EntriesAsync)
{
    if (!entry.IsDirectory)
    {
        using var entryStream = await entry.OpenEntryStreamAsync(cancellationToken);
        // Process the decompressed stream asynchronously
        await ProcessStreamAsync(entryStream, cancellationToken);
    }
}
```

### Async Writer Examples

**Write single file asynchronously:**
```C#
using Stream archiveStream = File.OpenWrite("output.zip");
await using var writer = await WriterFactory.OpenAsyncWriter(archiveStream, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate), cancellationToken);
using Stream fileStream = File.OpenRead("input.txt");
await writer.WriteAsync("entry.txt", fileStream, DateTime.Now, cancellationToken);
```

**Write entire directory asynchronously:**
```C#
using Stream stream = File.OpenWrite("backup.tar.gz");
await using var writer = await WriterFactory.OpenAsyncWriter(stream, ArchiveType.Tar, new WriterOptions(CompressionType.GZip), cancellationToken);
await writer.WriteAllAsync(
    @"D:\files",
    "*",
    SearchOption.AllDirectories,
    cancellationToken
);
```

**Write with progress tracking and cancellation:**
```C#
var cts = new CancellationTokenSource();

// Set timeout or cancel from UI
cts.CancelAfter(TimeSpan.FromMinutes(5));

using Stream stream = File.OpenWrite("archive.zip");
await using var writer = await WriterFactory.OpenAsyncWriter(stream, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate), cts.Token);
try
{
    await writer.WriteAllAsync(@"D:\data", "*", SearchOption.AllDirectories, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Operation was cancelled");
}
```

### Archive Async Examples

**Extract from archive asynchronously:**
```C#
await using var archive = await ZipArchive.OpenAsyncArchive("archive.zip", cancellationToken: cancellationToken);
// Simple async extraction - works for all archive types
await archive.WriteToDirectoryAsync(
    @"C:\output",
    cancellationToken: cancellationToken
);
```

**Benefits of Async Operations:**
- Non-blocking I/O for better application responsiveness
- Improved scalability for server applications
- Support for cancellation via CancellationToken
- Better resource utilization in async/await contexts
- Compatible with modern .NET async patterns
## Malformed input and memory use

ZIP64 end-record extensible data is skipped with bounded buffering. Invalid record sizes and truncated extensible data are rejected rather than driving allocations from the declared size.

ARC LZW compressed data is buffered incrementally from the bytes actually present. A truncated entry is rejected without preallocating its declared compressed size.

ZIP Shrink output buffering grows with decoded output rather than the declared uncompressed size. Invalid codes and output-size mismatches are rejected.

LZMA/LZMA2 dictionary storage starts small and grows with decoded history, up to the supported dictionary size. These safeguards avoid allocations based solely on forged sizes; they do not impose an overall decompressed-output or process-memory limit.

### Write caller-defined solid 7z blocks

Use a seekable destination and group similar files to share their compression dictionary.
The writer does not buffer a whole block. Empty files and directories keep their entry order
but do not consume a substream. Files outside blocks are compressed independently.

```csharp
await using var destination = File.Create("solid.7z");
await using var writer = new SevenZipWriter(destination, new SevenZipWriterOptions());
writer.BeginSolidBlock(new SevenZipSolidBlockOptions
{
    CompressionType = CompressionType.LZMA2,
    Filter = SevenZipFilter.X86,
    ExpectedSize = totalExecutableBytes,
});
foreach (var path in executablePaths)
{
    await using var source = File.OpenRead(path);
    await writer.WriteAsync(Path.GetFileName(path), source, File.GetLastWriteTimeUtc(path), cancellationToken);
}
await writer.EndSolidBlockAsync(cancellationToken);
```

Use `SevenZipFilter.Arm64` for ARM64 code or omit the filter for ordinary data.
`ExpectedSize` is optional; it reduces the dictionary for small blocks. To begin another
block with entirely asynchronous output, await `EndSolidBlockAsync` first. Disposing the
writer closes the last block automatically. Set `Password` and optionally `EncryptHeader`
on the writer options for per-folder encryption.

For automatic grouping instead, set `Solid = true` in `SevenZipWriterOptions`, optionally
with `SolidBlockSize` (input bytes, default 64 MiB) and `SolidBlockFileCount` (default 1,000).
Do not call `BeginSolidBlock` in that mode. Limits split between files; a single oversized
file is allowed and non-seekable file sizes are checked after writing.

### Update a 7z archive without recompressing unchanged folders

```csharp
using var source = File.OpenRead("original.7z");
using var update = await SevenZipUpdate.OpenAsync(
    source, ReaderOptions.ForExternalStream, cancellationToken);
foreach (var entry in update.Entries)
{
    if (entry.Name == "obsolete.txt")
        update.Delete(entry.Index);
    if (entry.Name == "old-name.txt")
        update.Rename(entry.Index, "new-name.txt");
}
using var destination = File.Create("updated.7z");
await update.WriteToAsync(destination, new SevenZipWriterOptions(),
    async (writer, token) =>
    {
        using var addition = File.OpenRead("new-file.txt");
        await writer.WriteAsync("new-file.txt", addition, null, token);
    }, cancellationToken);
```

Use `SharpCompress.Writers.SevenZip` and `SharpCompress.Readers`. Both archive
streams must be seekable and refer to different files. The source always stays
open; replace the original only after successful completion and closing both
streams. `WriteTo` and `Open` provide synchronous equivalents. The callback may
plan additions with `BeginSolidBlock`/`EndSolidBlockAsync` but must not dispose the
writer. Updates are single-use. Set the source password in `ReaderOptions` and
the destination password/header encryption in `SevenZipWriterOptions`; changing
the data password recompresses the affected folders. Progress counts copied
packed bytes and recompressed/added uncompressed bytes.

### Parallel 7z writing

Opt in when compression throughput matters, and set a memory budget appropriate for the host:

```csharp
await using var writer = new SevenZipWriter(destination, new SevenZipWriterOptions
{
    EnableParallelism = true,
    MaxThreads = 4,
    MemoryBudget = 256L << 20,
    LeaveStreamOpen = true,
});
await writer.WriteAsync("data.bin", source, modificationTime: null, cancellationToken);
```

The same options work with `SevenZipUpdate.WriteTo` and `WriteToAsync`. Existing solid-block
and encryption APIs are unchanged. LZMA2 can parallelize blocks of a large file; LZMA needs
multiple folders for parallel work. A 32 MiB dictionary gives 128 MiB blocks, so a 96 MiB
file occupies one job. A smaller budget can reduce concurrency; even a budget below one
job's estimate allows that job to run alone. Parallel output may differ from serial output,
but changing only `MaxThreads` does not change unencrypted archive bytes.