using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SharpCompress.SevenZipGate;

internal sealed partial class Runner(Options options, CancellationToken cancellationToken)
{
    private readonly Processes processes = new(options, cancellationToken);
    private readonly Results results = new(options);

    public int Run()
    {
        _ = Directory.CreateDirectory(options.At("out"));
        _ = Directory.CreateDirectory(options.At("results"));
        EnsureEmptyOutput();
        if (processes.Stopped)
        {
            Console.WriteLine("stopped");
            return 2;
        }
        var settings = Probe();
        WriteEnvironment(settings);
        var failed = false;
        foreach (var dataset in Program.Datasets)
        {
            if (options.Datasets is not null && !options.Datasets.Contains(dataset))
            {
                continue;
            }

            var files = Paths.InputFiles(options, dataset);
            var bytes = files.Sum(f => new FileInfo(f).Length);
            foreach (var level in new[] { 1, 5, 9, 0 })
            {
                if (options.Levels is not null && !options.Levels.Contains(level))
                {
                    continue;
                }

                foreach (var engine in level == 0 ? ["sc-enci-today"] : Program.Engines)
                {
                    if (options.Engines is not null && !options.Engines.Contains(engine))
                    {
                        continue;
                    }

                    if (engine == "sc-lzma-par" && dataset is not ("source" or "mixed"))
                    {
                        continue;
                    }

                    if (!options.Rerun && results.Completed(dataset, engine, level))
                    {
                        Console.WriteLine($"skip {dataset} {engine} mx{level}");
                        continue;
                    }
                    if (processes.Stopped)
                    {
                        Console.WriteLine("stopped");
                        return 2;
                    }
                    EnsureEmptyOutput();
                    Paths.Space(options);
                    var (dictionary, fastBytes) = level == 0 ? (1 << 20, 32) : settings[level];
                    var archive = options.At("out", $"{dataset}-{engine}-mx{level}.7z");
                    Measurement measurement;
                    var verify = "n/a";
                    long archiveBytes = 0;
                    var notes =
                        engine == "sc-lzma-par"
                            ? $"Estimate only; no archive overhead; per-file dictionary reduction; {options.ParallelThreads} threads"
                            : "";
                    notes +=
                        $" mode={(level == 1 ? "fast" : "normal")}; matchFinder={(level == 1 ? "HC4" : "BT4")};";
                    try
                    {
                        var single =
                            engine
                            is "7z-lzma-1t"
                                or "sc-lzma-1t"
                                or "sc-lzma2-1t"
                                or "sc-enci-today";
                        try
                        {
                            if (engine.StartsWith("7z-", StringComparison.Ordinal))
                            {
                                measurement = processes.Execute(
                                    options.SevenZip,
                                    NativeArguments(dataset, engine, level, archive),
                                    single
                                );
                            }
                            else
                            {
                                var (executable, arguments) = ChildArguments(
                                    dataset,
                                    engine,
                                    level,
                                    dictionary,
                                    fastBytes
                                );
                                measurement = processes.Execute(executable, arguments, single);
                            }
                        }
                        catch (Exception exception)
                            when (exception
                                    is Win32Exception
                                        or IOException
                                        or InvalidOperationException
                            )
                        {
                            measurement = new Measurement("failed", 0, 0, 0, 0, 0, "");
                            notes += " " + exception.Message;
                        }
                        archiveBytes =
                            engine == "sc-lzma-par" ? measurement.Size
                            : File.Exists(archive) ? new FileInfo(archive).Length
                            : 0;
                        if (engine != "sc-lzma-par")
                        {
                            verify = "failed";
                            if (File.Exists(archive) && !processes.Stopped)
                            {
                                var check = processes.Execute(
                                    options.SevenZip,
                                    ["t", archive, "-bd", "-y"],
                                    false
                                );
                                verify = check.Status == "ok" ? "ok" : "failed";
                                if (check.Status == "stopped")
                                {
                                    measurement = measurement with { Status = "stopped" };
                                }
                            }
                            if (measurement.Status == "ok" && verify != "ok")
                            {
                                measurement = measurement with { Status = "failed" };
                            }
                        }
                        if (
                            measurement.Status == "ok"
                            && engine.StartsWith("7z-", StringComparison.Ordinal)
                        )
                        {
                            measurement = measurement with { Processed = bytes };
                        }

                        if (processes.Stopped)
                        {
                            measurement = measurement with { Status = "stopped" };
                        }

                        if (engine.StartsWith("sc-", StringComparison.Ordinal))
                        {
                            notes +=
                                " BT4; dictionary column is configured maximum, reduced per file";
                        }

                        if (engine.StartsWith("7z-", StringComparison.Ordinal))
                        {
                            notes +=
                                " Native fast-byte/match-finder settings follow -mx; dictionary probe is LZMA";
                            if (
                                engine is "7z-lzma2-mt" or "7z-default"
                                && options.SevenZipMultiThreadExtra.Length > 0
                            )
                            {
                                notes +=
                                    "; extra switches: "
                                    + string.Join(' ', options.SevenZipMultiThreadExtra);
                            }
                        }

                        results.Append(
                            dataset,
                            engine,
                            level,
                            dictionary,
                            fastBytes,
                            files.Length,
                            bytes,
                            measurement,
                            archiveBytes,
                            verify,
                            notes.Trim()
                        );
                        Console.WriteLine(
                            $"{dataset} {engine} mx{level}: {measurement.Status}, verify={verify}, wall={measurement.Wall:F3}s, cpu={measurement.Cpu:F3}s, peak={measurement.Peak / 1048576d:F2}MiB, size={archiveBytes}, processed={measurement.Processed}"
                        );
                        failed |= measurement.Status != "ok";
                    }
                    finally
                    {
                        CleanOutput();
                    }
                    EnsureEmptyOutput();
                    if (processes.Stopped)
                    {
                        return 2;
                    }
                }
            }
        }
        Console.WriteLine($"results: {options.At("results", "results.csv")}");
        return failed ? 1 : 0;
    }

    private void CleanOutput()
    {
        // out was empty when this test began and is exclusively owned by the
        // harness. Remove native temporary output as well as the named archive.
        foreach (var path in Directory.EnumerateFileSystemEntries(options.At("out")))
        {
            Paths.Check(path);
            if (Directory.Exists(path))
            {
                foreach (
                    var child in Directory.EnumerateFileSystemEntries(
                        path,
                        "*",
                        SearchOption.AllDirectories
                    )
                )
                {
                    Paths.Check(child);
                }

                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    private void EnsureEmptyOutput()
    {
        if (Directory.EnumerateFileSystemEntries(options.At("out")).Any())
        {
            throw new IOException(
                "out must be empty between tests. Inspect and remove leftovers before resuming."
            );
        }
    }

    private string[] NativeArguments(string dataset, string engine, int level, string archive)
    {
        var arguments = new List<string> { "a", "-t7z", $"-mx{level}" };
        if (engine == "7z-lzma-1t")
        {
            arguments.AddRange(["-m0=LZMA", "-mmt=1", "-ms=off", "-mf=off"]);
        }

        if (engine == "7z-lzma2-mt")
        {
            arguments.AddRange(["-m0=LZMA2", "-mmt=on", "-ms=off", "-mf=off"]);
        }

        if (engine is "7z-lzma2-mt" or "7z-default")
        {
            arguments.AddRange(options.SevenZipMultiThreadExtra);
        }

        arguments.AddRange([
            "-bd",
            "-y",
            "-w" + options.At("out"),
            archive,
            Path.Combine(options.At("data", dataset), "*"),
        ]);
        return [.. arguments];
    }

    private (string Executable, List<string> Arguments) ChildArguments(
        string dataset,
        string engine,
        int level,
        int dictionary,
        int fastBytes
    )
    {
        var executable =
            Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate this executable.");
        var arguments = new List<string>();
        // AOT launches itself. Also support dotnet <assembly.dll> for development.
        if (
            Path.GetFileNameWithoutExtension(executable)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        )
        {
            arguments.Add(Environment.GetCommandLineArgs()[0]);
        }

        arguments.AddRange([
            "child",
            "--root",
            options.Root,
            "--dataset",
            dataset,
            "--engine",
            engine,
            "--level",
            level.ToString(CultureInfo.InvariantCulture),
            "--dictionary",
            dictionary.ToString(CultureInfo.InvariantCulture),
            "--fast-bytes",
            fastBytes.ToString(CultureInfo.InvariantCulture),
            "--par-threads",
            options.ParallelThreads.ToString(CultureInfo.InvariantCulture),
        ]);
        return (executable, arguments);
    }

    private Dictionary<int, (int Dictionary, int FastBytes)> Probe()
    {
        var settings = new Dictionary<int, (int, int)>();
        var input = options.At("out", "probe.bin");
        try
        {
            // 7-Zip reduces the dictionary to the input size, so the probe must be larger than
            // any -mx dictionary. Extending the length writes no data: NTFS reads it back as zeros.
            Paths.Space(options, 1536L << 20);
            using (var file = File.Create(input))
            {
                file.SetLength(1536L << 20);
            }
            foreach (var level in new[] { 1, 5, 9 })
            {
                var archive = options.At("out", $"probe-mx{level}.7z");
                try
                {
                    _ = Require(
                        processes.Execute(
                            options.SevenZip,
                            [
                                "a",
                                "-t7z",
                                $"-mx{level}",
                                "-m0=LZMA",
                                "-mmt=1",
                                "-bd",
                                "-y",
                                "-w" + options.At("out"),
                                archive,
                                input,
                            ],
                            true
                        )
                    );
                    var listing = Require(
                        processes.Execute(options.SevenZip, ["l", "-slt", archive], false)
                    );
                    var match = MethodPattern().Match(listing);
                    if (!match.Success)
                    {
                        throw new IOException("7-Zip probe did not report Method = LZMA:<size>.");
                    }

                    var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var dictionary = match.Groups[2].Value.ToLowerInvariant() switch
                    {
                        "k" => checked(number * 1024),
                        "m" => checked(number * 1024 * 1024),
                        "" when number is >= 0 and <= 30 => 1 << number,
                        _ => throw new IOException("Unsupported probe dictionary size."),
                    };
                    settings.Add(level, (dictionary, level == 9 ? 64 : 32));
                    Console.WriteLine(
                        $"probe mx{level}: dictionary={dictionary}, fastBytes={(level == 9 ? 64 : 32)}, SC mode={(level == 1 ? "fast" : "normal")}, matchFinder={(level == 1 ? "HC4" : "BT4")}"
                    );
                }
                finally
                {
                    File.Delete(archive);
                }
            }
        }
        finally
        {
            File.Delete(input);
        }
        return settings;
    }

    private static string Require(Measurement measurement)
    {
        if (measurement.Status == "stopped")
        {
            throw new OperationCanceledException();
        }

        if (measurement.Status != "ok")
        {
            throw new IOException(
                $"7-Zip probe failed: {measurement.Status}\n{measurement.Output}"
            );
        }

        return measurement.Output;
    }

    private void WriteEnvironment(Dictionary<int, (int Dictionary, int FastBytes)> settings)
    {
        var text = new StringBuilder();
        using var cpu = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"
        );
        _ = text.AppendLine(
            $"CPU: {cpu?.GetValue("ProcessorNameString") ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}"
        );
        _ = text.AppendLine($"Logical CPUs: {Environment.ProcessorCount}");
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        _ = text.AppendLine(
            $"RAM bytes: {(GlobalMemoryStatusEx(ref memory) ? memory.TotalPhysical.ToString(CultureInfo.InvariantCulture) : "unavailable")}"
        );
        _ = text.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        _ = text.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        _ = text.AppendLine(
            $"Execution: {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "AOT")}"
        );
        _ = text.AppendLine($"Pin mask: 0x{options.PinMask:X}");
        _ = text.AppendLine(
            "7-Zip: "
                + Require(processes.Execute(options.SevenZip, [], false))
                    .Split(
                        '\n',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .FirstOrDefault()
        );
        try
        {
            // Read-only git query; the harness never changes the checkout.
            var git = processes.Execute(
                "git",
                ["-C", AppContext.BaseDirectory, "rev-parse", "HEAD"],
                false
            );
            _ = text.AppendLine(
                "Fork HEAD: " + (git.Status == "ok" ? git.Output.Trim() : "unavailable")
            );
        }
        catch (Win32Exception)
        {
            _ = text.AppendLine("Fork HEAD: unavailable (git not installed)");
        }
        foreach (var (level, setting) in settings)
        {
            _ = text.AppendLine(
                $"mx{level}: LZMA dictionary={setting.Dictionary}; fastBytes={setting.FastBytes}; SC mode={(level == 1 ? "fast" : "normal")}; SC match finder={(level == 1 ? "HC4" : "BT4")}; 7-Zip match finder={(level == 1 ? "HC4" : "BT4")}"
            );
        }

        _ = text.AppendLine(
            "sc-enci-today mx0: default properties; dictionary=1048576; fastBytes=32; mode=normal; matchFinder=BT4"
        );
        _ = text.AppendLine(
            "Dictionary values are configured maxima; known per-file sizes reduce the SC dictionary. Native LZMA2/default settings remain controlled by 7-Zip -mx."
        );
        File.WriteAllText(options.At("results", "environment.txt"), text.ToString());
    }

    [GeneratedRegex(
        @"Method = LZMA:(\d+)([mk]?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex MethodPattern();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
