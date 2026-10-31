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

        if (args.Length == 2 && args[0] == "report")
        {
            return Report(args[1]);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    // Peak working set of a process that did nothing but this row, for about four seconds. "Above start" subtracts the working
    // set after setup (inputs loaded, runtime warm), so it is what the work itself needed.
    private static int Peak(string row, string side, string input)
    {
        bool ours = side == "ours";
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

    // Reads the *-report-full-compressed.json files; prints per row: medians, ratio (ours / MimeKit), bytes per operation.
    private static int Report(string dir)
    {
        var ratios = new List<double>();
        Console.WriteLine("row\tinput\tours_ns\tmimekit_ns\tratio\tours_B/op\tmimekit_B/op");
        foreach (string f in Directory.EnumerateFiles(dir, "*-report-full-compressed.json").Order(StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f));
            string row = Path.GetFileName(f);
            int dash = row.IndexOf("-report", StringComparison.Ordinal);
            row = row[(row.LastIndexOf('.', dash) + 1)..dash];
            var ours = new Dictionary<string, (double Median, long Alloc)>();
            var theirs = new Dictionary<string, (double Median, long Alloc)>();
            foreach (JsonElement b in doc.RootElement.GetProperty("Benchmarks").EnumerateArray())
            {
                string method = b.GetProperty("Method").GetString()!;
                string parms = b.TryGetProperty("Parameters", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
                double median = b.GetProperty("Statistics").GetProperty("Median").GetDouble();
                long alloc = b.GetProperty("Memory").GetProperty("BytesAllocatedPerOperation").GetInt64();
                (method == "Ours" ? ours : theirs)[parms] = (median, alloc);
            }

            foreach ((string parms, (double Median, long Alloc) o) in ours)
            {
                (double Median, long Alloc) t = theirs[parms];
                double r = o.Median / t.Median;
                ratios.Add(r);
                Console.WriteLine(string.Join('\t', row, parms, o.Median.ToString("F0", CultureInfo.InvariantCulture), t.Median.ToString("F0", CultureInfo.InvariantCulture), r.ToString("F3", CultureInfo.InvariantCulture), o.Alloc, t.Alloc));
            }
        }

        if (ratios.Count > 0)
        {
            Console.WriteLine("geomean\t{0:F3}\tworst\t{1:F3}\trows\t{2}", Math.Exp(ratios.Average(Math.Log)), ratios.Max(), ratios.Count);
        }

        return 0;
    }
}
