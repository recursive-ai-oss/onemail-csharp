using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Running;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>
/// Runs the benchmark classes of this assembly (pass <c>--filter</c> to pick rows), or one of two helpers:
/// <c>peak ROW SIDE INPUT</c> runs one row's work in a loop and prints the peak working set and bytes allocated per operation;
/// <c>report DIR</c> reads BenchmarkDotNet's JSON results and prints the ratio table.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "peak")
        {
            return Peak(args[1], args[2], args[3]);
        }

        if (args.Length >= 2 && args[0] == "report")
        {
            return Report(args[1], args.Length > 2 ? args[2] : null);
        }

        if (args.Length >= 1 && args[0] == "gen")
        {
            StoreGenerator.Generate(args.Length > 1 ? args[1] : OutlookInputs.Stores, args[2..]);
            return 0;
        }

        if (args.Length == 3 && args[0] == "path" && args[1] == "pst")
        {
            Console.WriteLine(OutlookInputs.PstPath(args[2]));
            return 0;
        }

        if (args.Length == 1 && args[0] == "verify")
        {
            return OutlookCommands.Verify();
        }

        if (args.Length == 1 && args[0] == "counts")
        {
            return OutlookCommands.Counts();
        }

        if (args.Length == 4 && args[0] == "curve")
        {
            return OutlookCommands.Curve(args[1], args[2], args[3]);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    // Peak working set of a process that did nothing but this row, for about four seconds. "Above start" subtracts the working
    // set after setup (inputs loaded, runtime warm), so it is what the work itself needed.
    private static int Peak(string row, string side, string input)
    {
        bool ours = side == "ours";
        if (row is "pst" or "msg" or "tnef")
        {
            return OutlookCommands.Peak(row, side == "ours", input);
        }

        byte[] data = row == "mbox" ? Inputs.DovecotMbox() : row == "build" ? MakeAttachment() : Inputs.Get(input);
        Func<long> op = (row, ours) switch
        {
            ("parse", true) => () => Workloads.ParseOneMail(data),
            ("parse", false) => () => Workloads.ParseMimeKit(data),
            ("headers", true) => () => Workloads.HeadersOneMail(data),
            ("headers", false) => () => Workloads.HeadersMimeKit(data),
            ("bodies", true) => () => Workloads.BodiesOneMail(data),
            ("bodies", false) => () => Workloads.BodiesMimeKit(data),
            ("write", true) => PrepareWrite(() => Workloads.PrepareOneMail(data), Workloads.WriteOneMail),
            ("write", false) => PrepareWrite(() => Workloads.PrepareMimeKit(data), Workloads.WriteMimeKit),
            ("build", true) => () => Workloads.BuildWriteOneMail(data),
            ("build", false) => () => Workloads.BuildWriteMimeKit(data),
            ("mbox", true) => () => Workloads.MboxOneMail(data),
            ("mbox", false) => () => Workloads.MboxMimeKit(data),
            _ => throw new ArgumentException("unknown row/side"),
        };
        op();
        GC.Collect();
        long start = Process.GetCurrentProcess().WorkingSet64;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        int n = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000)
        {
            op();
            n++;
        }

        long perOp = (GC.GetAllocatedBytesForCurrentThread() - a0) / n;
        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        Console.WriteLine(string.Join('\t', "PEAK", row, side, input, (peak >> 10).ToString(CultureInfo.InvariantCulture), ((peak - start) >> 10).ToString(CultureInfo.InvariantCulture), perOp.ToString(CultureInfo.InvariantCulture)));
        return 0;
    }

    private static byte[] MakeAttachment()
    {
        byte[] b = new byte[1_000_000];
        new Random(7).NextBytes(b);
        return b;
    }

    private static Func<long> PrepareWrite<T>(Func<T> prepare, Func<T, long> write)
    {
        T t = prepare();
        return () => write(t);
    }

    // Reads the *-report-full-compressed.json files; prints per row: medians, ratio (ours / comparator), bytes per operation, and, when a
    // counts file (from the counts command) names the input, bytes per message. Then the geometric mean and worst ratio per area.
    private static int Report(string dir, string? countsFile)
    {
        var counts = new Dictionary<string, long>();
        if (countsFile is not null && File.Exists(countsFile))
        {
            foreach (string line in File.ReadLines(countsFile))
            {
                string[] p = line.Split('	');
                if (p.Length == 2)
                {
                    counts[p[0]] = long.Parse(p[1], CultureInfo.InvariantCulture);
                }
            }
        }

        var byArea = new SortedDictionary<string, List<double>>(StringComparer.Ordinal);
        Console.WriteLine("area	row	input	ours_ns	other_ns	ratio	ours_B/op	other_B/op	msgs/op	ours_B/msg	other_B/msg");
        foreach (string f in Directory.EnumerateFiles(dir, "*-report-full-compressed.json").Order(StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f));
            string row = Path.GetFileName(f);
            int dash = row.IndexOf("-report", StringComparison.Ordinal);
            row = row[(row.LastIndexOf('.', dash) + 1)..dash];
            string area = row switch { "PstRow" or "PstLargeRow" or "PstFourKiBRow" => "PST", "MsgRow" => "MSG", "TnefRow" => "TNEF", _ => "MIME" };
            var ours = new Dictionary<string, (double Median, long Alloc)>();
            var theirs = new Dictionary<string, (double Median, long Alloc)>();
            foreach (JsonElement b in doc.RootElement.GetProperty("Benchmarks").EnumerateArray())
            {
                string method = b.GetProperty("Method").GetString()!;
                string parms = b.TryGetProperty("Parameters", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
                if (b.GetProperty("Statistics").ValueKind != JsonValueKind.Object || b.GetProperty("Memory").GetProperty("BytesAllocatedPerOperation").ValueKind != JsonValueKind.Number)
                {
                    continue;  // the run produced no result for this method (it crashed): leave the row out and let the missing side show
                }

                double median = b.GetProperty("Statistics").GetProperty("Median").GetDouble();
                long alloc = b.GetProperty("Memory").GetProperty("BytesAllocatedPerOperation").GetInt64();
                (method == "Ours" ? ours : theirs)[parms] = (median, alloc);
            }

            foreach ((string parms, (double Median, long Alloc) o) in ours)
            {
                if (!theirs.TryGetValue(parms, out (double Median, long Alloc) t))
                {
                    // No comparator row (it cannot read this input): print ours alone, outside every ratio.
                    Console.WriteLine(string.Join('\t', area + "-ours-only", row, parms, o.Median.ToString("F0", CultureInfo.InvariantCulture), "", "", o.Alloc, ""));
                    continue;
                }

                double r = o.Median / t.Median;
                if (!byArea.TryGetValue(area, out List<double>? list))
                {
                    byArea[area] = list = [];
                }

                list.Add(r);
                string input = parms.Replace("Input=", "", StringComparison.Ordinal);
                string perMsg = "			";
                if (counts.TryGetValue(input, out long msgs) && msgs > 0)
                {
                    perMsg = string.Join('	', msgs, (o.Alloc / msgs).ToString(CultureInfo.InvariantCulture), (t.Alloc / msgs).ToString(CultureInfo.InvariantCulture));
                }

                Console.WriteLine(string.Join('	', area, row, parms, o.Median.ToString("F0", CultureInfo.InvariantCulture), t.Median.ToString("F0", CultureInfo.InvariantCulture), r.ToString("F3", CultureInfo.InvariantCulture), o.Alloc, t.Alloc, perMsg));
            }
        }

        foreach ((string area, List<double> ratios) in byArea)
        {
            Console.WriteLine("geomean	{0}	{1:F3}	worst	{2:F3}	rows	{3}", area, Math.Exp(ratios.Average(Math.Log)), ratios.Max(), ratios.Count);
        }

        return 0;
    }
}
