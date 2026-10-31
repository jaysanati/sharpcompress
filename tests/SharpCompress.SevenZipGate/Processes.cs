using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpCompress.SevenZipGate;

internal sealed record Measurement(
    string Status,
    double Wall,
    double Cpu,
    long Peak,
    long Processed,
    long Size,
    string Output
);

internal sealed class Processes(Options options, CancellationToken cancellationToken)
{
    private readonly object logLock = new();
    public bool Stopped =>
        cancellationToken.IsCancellationRequested || File.Exists(options.At("STOP"));

    public void Log(string message)
    {
        lock (logLock)
        {
            File.AppendAllText(
                options.At("results", "run.log"),
                $"{DateTime.UtcNow:O} {message}\n"
            );
        }
    }

    public Measurement Execute(
        string executable,
        IEnumerable<string> arguments,
        bool pin,
        double? timeoutMinutes = null
    )
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = options.Root,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        // Child runtimes and native tools must keep any temporary writes inside root.
        info.Environment["TEMP"] = options.At("out");
        info.Environment["TMP"] = options.At("out");
        Log(
            "command "
                + executable
                + " "
                + string.Join(
                    " ",
                    info.ArgumentList.Select(a =>
                        '"' + a.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
                    )
                )
        );
        if (Stopped)
        {
            return new Measurement("stopped", 0, 0, 0, 0, 0, "");
        }

        using var process = new Process { StartInfo = info };
        var captured = new StringBuilder();
        long processed = 0;
        long size = 0;
        void Capture(string? line)
        {
            if (line is null)
            {
                return;
            }

            if (
                line.StartsWith("progress ", StringComparison.Ordinal)
                && long.TryParse(
                    line.AsSpan(9),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var bytes
                )
            )
            {
                _ = Interlocked.Exchange(ref processed, bytes);
            }
            else if (
                line.StartsWith("size ", StringComparison.Ordinal)
                && long.TryParse(
                    line.AsSpan(5),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var compressed
                )
            )
            {
                _ = Interlocked.Exchange(ref size, compressed);
            }

            lock (captured)
            {
                // Probes/banner need only a bounded amount of output. The full
                // output still goes to the log, without accumulating in memory.
                if (captured.Length < 1024 * 1024)
                {
                    _ = captured.AppendLine(line);
                }
            }
            Log(line);
        }
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        var watch = Stopwatch.StartNew();
        long peak = 0;
        var status = "ok";
        var started = false;
        try
        {
            _ = process.Start();
            started = true;
            // Pin immediately; do not silently continue with incomparable data.
            if (pin)
            {
                process.ProcessorAffinity = (nint)options.PinMask;
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (Win32Exception) when (process.HasExited) { }
            Log($"start pid={process.Id}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (true)
            {
                Sample();
                if (Stopped)
                {
                    status = "stopped";
                    Kill();
                    break;
                }
                if (watch.Elapsed.TotalMinutes >= (timeoutMinutes ?? options.TimeoutMinutes))
                {
                    status = "timeout";
                    Kill();
                    break;
                }
                if (process.WaitForExit(250))
                {
                    break;
                }
            }
            watch.Stop();
            process.WaitForExit();
            Sample();
            if (status == "ok" && process.ExitCode != 0)
            {
                status = "failed";
            }

            double cpu = 0;
            try
            {
                cpu = process.TotalProcessorTime.TotalSeconds;
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            Log(
                $"end pid={process.Id} status={status} exit={process.ExitCode} wall={watch.Elapsed.TotalSeconds:F6}"
            );
            return new Measurement(
                status,
                watch.Elapsed.TotalSeconds,
                cpu,
                peak,
                Interlocked.Read(ref processed),
                Interlocked.Read(ref size),
                captured.ToString()
            );
        }
        catch (Exception exception)
        {
            Log("failure " + exception.Message);
            throw;
        }
        finally
        {
            if (started)
            {
                Kill();
            }
        }

        void Sample()
        {
            try
            {
                process.Refresh();
                peak = Math.Max(peak, process.PeakWorkingSet64);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        void Kill()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }
}
