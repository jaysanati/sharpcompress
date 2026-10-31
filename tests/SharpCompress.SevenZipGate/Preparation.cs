using System.Formats.Tar;
using System.Text;

namespace SharpCompress.SevenZipGate;

internal static class Preparation
{
    private const string Iso = @"Y:\OS\ubuntu-24.04.3-live-server-amd64.iso";
    private const long Limit = 2L << 30;

    public static void Prepare(Options options, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(options.At("data"));
        foreach (var dataset in Program.Datasets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = options.At("data", dataset + ".manifest.txt");
            if (File.Exists(manifest))
            {
                Console.WriteLine($"skip {dataset}: manifest exists");
                continue;
            }
            var destination = options.At("data", dataset);
            // An interrupted preparation has no manifest. Rebuild only its own
            // dataset directory, checking every descendant before removal.
            if (Directory.Exists(destination))
            {
                foreach (
                    var item in Directory.EnumerateFileSystemEntries(
                        destination,
                        "*",
                        SearchOption.AllDirectories
                    )
                )
                {
                    Paths.Check(item);
                }

                Directory.Delete(destination, true);
            }
            _ = Directory.CreateDirectory(destination);
            var extensions = new SortedDictionary<string, long>(StringComparer.Ordinal);
            switch (dataset)
            {
                case "source":
                    Copy(
                        options,
                        @"J:\source\ENCI\src",
                        dataset,
                        [.. Paths.Files(@"J:\source\ENCI\src", true)],
                        cancellationToken
                    );
                    break;
                case "mixed":
                    var selected = SelectMixed(extensions, cancellationToken);
                    Copy(options, @"Y:\", dataset, selected, cancellationToken);
                    break;
                case "large-iso":
                    Copy(options, Path.GetDirectoryName(Iso)!, dataset, [Iso], cancellationToken);
                    break;
                case "large-bin":
                    MakeTar(options, cancellationToken);
                    break;
            }
            var files = Paths.InputFiles(options, dataset);
            var text = $"files={files.Length}\nbytes={files.Sum(p => new FileInfo(p).Length)}\n";
            foreach (var (extension, bytes) in extensions)
            {
                text += $"extension={extension}\tbytes={bytes}\n";
            }

            File.WriteAllText(manifest, text);
            Console.WriteLine($"prepared {dataset}: {text.Trim()}");
        }
    }

    private static string[] SelectMixed(
        SortedDictionary<string, long> totals,
        CancellationToken cancellationToken
    )
    {
        var candidates = new List<(string Path, long Size)>();
        foreach (var path in Paths.Files(@"Y:\"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, Iso, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var length = new FileInfo(path).Length;
                if (length is >= 1024 and <= 200L * 1024 * 1024)
                {
                    candidates.Add((path, length));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var groups = candidates
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .GroupBy(c => Path.GetExtension(c.Path).ToLowerInvariant())
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Extension: g.Key, Files: new Queue<(string Path, long Size)>(g)))
            .ToArray();
        var result = new List<string>();
        long total = 0;
        bool remaining;
        do
        {
            remaining = false;
            foreach (var (Extension, Files) in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (Files.TryDequeue(out var file))
                {
                    _ = totals.TryGetValue(Extension, out var extensionBytes);
                    if (
                        extensionBytes + file.Size > 300L * 1024 * 1024
                        || total + file.Size > Limit
                    )
                    {
                        continue;
                    }

                    result.Add(file.Path);
                    totals[Extension] = extensionBytes + file.Size;
                    total += file.Size;
                    remaining = true;
                    break;
                }
            }
        } while (remaining);
        return [.. result];
    }

    private static void Copy(
        Options options,
        string source,
        string dataset,
        string[] files,
        CancellationToken cancellationToken
    )
    {
        Paths.Space(options, files.Sum(p => new FileInfo(p).Length));
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = options.At("data", dataset, Path.GetRelativePath(source, file));
            Paths.Space(options, new FileInfo(file).Length);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void MakeTar(Options options, CancellationToken cancellationToken)
    {
        Paths.Space(options, Limit + (200L << 20));
        const string system = @"C:\Windows\System32";
        using var output = File.Create(options.At("data", "large-bin", "system32-binaries.tar"));
        // BCL TarWriter creates an uncompressed portable archive; source files
        // are opened read-only, including files currently used by Windows.
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var path in Paths.Files(system))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length >= Limit)
            {
                break;
            }

            var extension = Path.GetExtension(path);
            if (
                !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }
            // Stage a readable file before adding its header, so a read error
            // cannot leave a half-written entry in the tar.
            var staging = options.At("data", "large-bin", ".tar-input.tmp");
            try
            {
                Paths.Space(options, checked(new FileInfo(path).Length * 2));
                try
                {
                    using var input = File.Open(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    );
                    using var staged = File.Create(staging);
                    input.CopyTo(staged);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
                using var data = File.OpenRead(staging);
                var entry = new PaxTarEntry(
                    TarEntryType.RegularFile,
                    Path.GetRelativePath(system, path).Replace('\\', '/')
                )
                {
                    DataStream = data,
                };
                writer.WriteEntry(entry);
            }
            finally
            {
                File.Delete(staging);
            }
        }
    }

    public static void Smoke(Options options)
    {
        if (
            Directory.Exists(options.Root)
            && Directory.EnumerateFileSystemEntries(options.Root).Any()
        )
        {
            throw new IOException("smoke requires an empty root directory.");
        }

        Paths.Space(options, 16L << 20);
        var directory = options.At("data", "source");
        _ = Directory.CreateDirectory(directory);
        for (var i = 0; i < 300; i++)
        {
            File.WriteAllText(
                options.At("data", "source", $"small-{i:D3}.txt"),
                string.Concat(
                    Enumerable.Repeat($"file {i}: deterministic compression gate text\n", 12)
                )
            );
        }

        using (var stream = File.Create(options.At("data", "source", "large.txt")))
        {
            var block = Encoding.ASCII.GetBytes(new string('a', 65536));
            for (var i = 0; i < 128; i++)
            {
                stream.Write(block);
            }
        }
        File.WriteAllText(
            options.At("data", "source.manifest.txt"),
            $"files=301\nbytes={Paths.InputFiles(options, "source").Sum(p => new FileInfo(p).Length)}\n"
        );
        Console.WriteLine("smoke: generated 300 small text files and one 8 MiB file");
    }
}
