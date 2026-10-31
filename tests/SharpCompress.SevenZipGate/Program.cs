using System.Globalization;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

namespace SharpCompress.SevenZipGate;

internal static class Program
{
    internal static readonly string[] Datasets = ["source", "mixed", "large-bin", "large-iso"];
    internal static readonly string[] Engines =
    [
        "7z-lzma-1t",
        "7z-lzma2-mt",
        "7z-default",
        "sc-lzma-1t",
        "sc-lzma-par",
    ];

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            var options = Options.Parse(args);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += handler;
            try
            {
                switch (options.Command)
                {
                    case "prepare":
                        Preparation.Prepare(options, cancellation.Token);
                        return 0;
                    case "run":
                        return new Runner(options, cancellation.Token).Run();
                    case "smoke":
                        Preparation.Smoke(options);
                        return new Runner(options, cancellation.Token).Run();
                    case "child":
                        ManagedCompression.Run(options);
                        return 0;
                    default:
                        throw new ArgumentException(
                            "Use prepare, run, or smoke, with --root <dir> and --7z <exe>."
                        );
                }
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("stopped");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}

internal sealed class Options
{
    public string Command { get; private set; } = "";
    public string Root { get; private set; } = @"C:\7zSandbox";
    public string SevenZip { get; private set; } = @"C:\Program Files\7-Zip\7z.exe";
    public long PinMask { get; private set; } = 4;
    public double TimeoutMinutes { get; private set; } = 30;
    public bool Rerun { get; private set; }
    public HashSet<string>? Datasets { get; private set; }
    public HashSet<string>? Engines { get; private set; }
    public HashSet<int>? Levels { get; private set; }
    public int Dictionary { get; private set; }
    public int FastBytes { get; private set; }

    /// <summary>Extra 7-Zip switches for the multi-threaded engines, e.g. a memory cap.</summary>
    public string[] SevenZipMultiThreadExtra { get; private set; } = [];

    /// <summary>Maximum concurrent files for sc-lzma-par; defaults to the logical CPU count.</summary>
    public int ParallelThreads { get; private set; } = Environment.ProcessorCount;

    public static Options Parse(string[] args)
    {
        var result = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                if (result.Command.Length != 0)
                {
                    throw new ArgumentException($"Unexpected argument: {key}");
                }

                result.Command = key;
                continue;
            }
            if (key == "--rerun")
            {
                result.Rerun = true;
                continue;
            }
            if (++i == args.Length)
            {
                throw new ArgumentException($"Missing value for {key}");
            }

            var value = args[i];
            switch (key)
            {
                case "--root":
                    result.Root = Path.GetFullPath(value);
                    break;
                case "--7z":
                    result.SevenZip = Path.GetFullPath(value);
                    break;
                case "--pin-mask":
                    result.PinMask = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToInt64(value[2..], 16)
                        : long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--timeout-min":
                    result.TimeoutMinutes = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--dataset":
                    result.Datasets = value.Split(',').ToHashSet(StringComparer.Ordinal);
                    break;
                case "--engine":
                    result.Engines = value.Split(',').ToHashSet(StringComparer.Ordinal);
                    break;
                case "--level":
                    result.Levels = [.. value
                        .Split(',')
                        .Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
                    break;
                case "--dictionary":
                    result.Dictionary = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--fast-bytes":
                    result.FastBytes = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--par-threads":
                    result.ParallelThreads = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--7z-mt-extra":
                    result.SevenZipMultiThreadExtra = value.Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries
                    );
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {key}");
            }
        }
        if (
            result.PinMask <= 0
            || !double.IsFinite(result.TimeoutMinutes)
            || result.TimeoutMinutes <= 0
        )
        {
            throw new ArgumentException("Pin mask and timeout must be positive.");
        }

        if (
            result.Datasets?.Any(d => !Program.Datasets.Contains(d)) == true
            || result.Engines?.Any(e => !Program.Engines.Contains(e) && e != "sc-enci-today")
                == true
            || result.Levels?.Any(l => l is not (0 or 1 or 5 or 9)) == true
        )
        {
            throw new ArgumentException("Unknown dataset, engine, or level.");
        }

        if (result.Command == "smoke")
        {
            if (!args.Contains("--root"))
            {
                throw new ArgumentException(
                    "smoke requires an explicit --root pointing to an empty temporary directory."
                );
            }

            result.Datasets = new HashSet<string>(["source"], StringComparer.Ordinal);
            result.Engines = null;
            result.Levels = new HashSet<int>([0, 1]);
            result.TimeoutMinutes = 2;
            result.Rerun = true;
        }
        Paths.Check(result.Root);
        return result;
    }

    public string At(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine([Root, .. parts]));
        var prefix = Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Path escapes --root.");
        }

        Paths.Check(path);
        return path;
    }
}

internal static class Paths
{
    // Reject junctions and symlinks in destination ancestry: lexical containment
    // alone would allow writes outside the selected root through a reparse point.
    public static void Check(string path)
    {
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
        {
            try
            {
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        $"Reparse point is not allowed in an output path: {item.FullName}"
                    );
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void Space(Options options, long additionalBytes = 0)
    {
        const long reserve = 30L << 30;
        var drive = new DriveInfo(Path.GetPathRoot(options.Root)!);
        if (drive.AvailableFreeSpace - additionalBytes < reserve)
        {
            throw new IOException(
                $"Insufficient free space on {drive.Name}: at least 30 GiB must remain (planned write: {additionalBytes} bytes)."
            );
        }
    }

    public static IEnumerable<string> Files(string directory, bool sourceExclusions = false)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (
            var file in Directory
                .EnumerateFiles(directory, "*", enumeration)
                .Order(StringComparer.Ordinal)
        )
        {
            if (!sourceExclusions || !file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        foreach (
            var subdirectory in Directory
                .EnumerateDirectories(directory, "*", enumeration)
                .Order(StringComparer.Ordinal)
        )
        {
            var name = Path.GetFileName(subdirectory);
            if (
                sourceExclusions
                && new[] { "bin", "obj", ".vs", "TestData" }.Contains(
                    name,
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            foreach (var file in Files(subdirectory, sourceExclusions))
            {
                yield return file;
            }
        }
    }

    public static string[] InputFiles(Options options, string dataset) =>
        [.. Files(options.At("data", dataset))
            .OrderBy(
                p => Path.GetRelativePath(options.At("data", dataset), p),
                StringComparer.Ordinal
            )];
}
