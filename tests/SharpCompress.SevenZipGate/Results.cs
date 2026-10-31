using System.Globalization;
using System.Text;

namespace SharpCompress.SevenZipGate;

internal sealed class Results(Options options)
{
    private const string Header =
        "timestampUtc,dataset,inputFiles,inputBytes,engine,level,dictionary,fastBytes,matchFinder,threads,wallSec,cpuSec,peakWorkingSetMiB,archiveBytes,ratio,throughputMiBs,bytesProcessed,status,verify,notes";
    private string Csv => options.At("results", "results.csv");

    public List<string[]> Read()
    {
        if (!File.Exists(Csv))
        {
            return [];
        }

        var lines = File.ReadAllLines(Csv);
        if (lines.Length == 0 || lines[0] != Header)
        {
            throw new IOException("Unrecognized results.csv header.");
        }

        return [.. lines.Skip(1).Where(l => l.Length > 0).Select(Parse)];
    }

    public bool Completed(string dataset, string engine, int level) =>
        Read()
            .Any(r =>
                r[1] == dataset
                && r[4] == engine
                && r[5] == level.ToString(CultureInfo.InvariantCulture)
                && r[17] is "ok" or "timeout"
            );

    public void Append(
        string dataset,
        string engine,
        int level,
        int dictionary,
        int fastBytes,
        int files,
        long inputBytes,
        Measurement measurement,
        long archiveBytes,
        string verify,
        string notes
    )
    {
        var managed = engine.StartsWith("sc-", StringComparison.Ordinal);
        object[] values =
        [
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            dataset,
            files,
            inputBytes,
            engine,
            level,
            dictionary,
            fastBytes,
            managed ? "BT4"
            : level == 1 ? "HC4"
            : "BT4",
            engine is "7z-lzma-1t" or "sc-lzma-1t" or "sc-lzma2-1t" or "sc-enci-today"
                ? 1
                : Environment.ProcessorCount,
            measurement.Wall,
            measurement.Cpu,
            measurement.Peak / 1048576d,
            archiveBytes,
            inputBytes == 0 ? 0 : (double)archiveBytes / inputBytes,
            measurement.Wall == 0 ? 0 : inputBytes / 1048576d / measurement.Wall,
            measurement.Processed,
            measurement.Status,
            verify,
            notes,
        ];
        if (!File.Exists(Csv))
        {
            File.WriteAllText(Csv, Header + "\n");
        }

        File.AppendAllText(
            Csv,
            string.Join(
                ',',
                values.Select(v => Escape(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""))
            ) + "\n"
        );
        Markdown();
    }

    public void Markdown()
    {
        var rows = Read();
        var text = new StringBuilder(
            "# SevenZip gate\n\nWall time includes process startup. sc-lzma-par estimates codec throughput and size; it writes no archive.\n\n"
        );
        foreach (var dataset in rows.Select(r => r[1]).Distinct(StringComparer.Ordinal))
        {
            _ = text.AppendLine(
                $"## {dataset}\n\n| Engine | Level | Wall s | CPU s | Peak MiB | Bytes | MiB/s | Status | Verify |\n|---|---:|---:|---:|---:|---:|---:|---|---|"
            );
            foreach (var row in rows.Where(r => r[1] == dataset))
            {
                _ = text.AppendLine(
                    $"| {row[4]} | {row[5]} | {row[10]} | {row[11]} | {row[12]} | {row[13]} | {row[15]} | {row[17]} | {row[18]} |"
                );
            }

            _ = text.AppendLine();
        }
        _ = text.AppendLine(
            "## Gate\n\nOnly successful, verified archive runs (or successful parallel estimates) are compared; latest successful row wins. Size difference is (SC / 7-Zip - 1) × 100%.\n\n| Dataset | Level | SC / 7z LZMA wall | SC LZMA2 / 7z LZMA wall | SC parallel / 7z LZMA2 wall | SC size difference |\n|---|---:|---:|---:|---:|---:|"
        );
        foreach (var dataset in rows.Select(r => r[1]).Distinct(StringComparer.Ordinal))
        {
            foreach (var level in new[] { "1", "5", "9" })
            {
                var selected = rows.Where(r =>
                        r[1] == dataset && r[5] == level && r[17] == "ok" && r[18] is "ok" or "n/a"
                    )
                    .ToArray();
                string[]? Last(string engine) => selected.LastOrDefault(r => r[4] == engine);
                var sc = Last("sc-lzma-1t");
                var native = Last("7z-lzma-1t");
                _ = text.AppendLine(
                    $"| {dataset} | {level} | {Compare(sc, native, 10, false)} | {Compare(Last("sc-lzma2-1t"), native, 10, false)} | {Compare(Last("sc-lzma-par"), Last("7z-lzma2-mt"), 10, false)} | {Compare(sc, native, 13, true)} |"
                );
            }
        }

        File.WriteAllText(options.At("results", "results.md"), text.ToString());
    }

    private static string Compare(string[]? a, string[]? b, int column, bool percent)
    {
        if (a is null || b is null)
        {
            return "n/a";
        }

        var denominator = double.Parse(b[column], CultureInfo.InvariantCulture);
        if (denominator == 0)
        {
            return "n/a";
        }

        var ratio = double.Parse(a[column], CultureInfo.InvariantCulture) / denominator;
        return percent
            ? ((ratio - 1) * 100).ToString("F2", CultureInfo.InvariantCulture) + "%"
            : ratio.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value) =>
        "\""
        + value
            .Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
        + "\"";

    private static string[] Parse(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    _ = field.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (line[i] == ',' && !quoted)
            {
                fields.Add(field.ToString());
                _ = field.Clear();
            }
            else
            {
                _ = field.Append(line[i]);
            }
        }
        fields.Add(field.ToString());
        if (quoted || fields.Count != 20)
        {
            throw new IOException(
                "Malformed results.csv row; preserve it and repair before resuming."
            );
        }

        return [.. fields];
    }
}
