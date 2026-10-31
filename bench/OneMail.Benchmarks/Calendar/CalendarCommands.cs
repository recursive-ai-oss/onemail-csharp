using System.Diagnostics;
using System.Globalization;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>Helpers behind the calendar rows: item counts (per-item divisors), the equal-work check and the peak-memory loop.</summary>
internal static class CalendarCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Operation factory for a row name as used by <c>verify</c> and <c>peak</c>.</summary>
    public static Func<long> Op(string row, bool ours, string input) => row switch
    {
        "icsparse" => CalendarWorkloads.IcsParse(ours, input),
        "icswrite" => CalendarWorkloads.IcsBuild(ours, input),
        "icsedit" => CalendarWorkloads.IcsEdit(ours, input),
        "vcfparse" => CalendarWorkloads.VcfParse(ours, input),
        "vcfwrite" => CalendarWorkloads.VcfBuild(ours, input),
        "vcfedit" => CalendarWorkloads.VcfEdit(ours, input),
        "recur" => CalendarWorkloads.Recurrence(ours, input),
        _ => throw new ArgumentException("unknown calendar row " + row, nameof(row)),
    };

    private static IEnumerable<(string Row, string Input)> AllRows() =>
    [
        .. CalendarInputs.IcsAll.Select(i => ("icsparse", i)), .. CalendarInputs.IcsBuildAll.Select(i => ("icswrite", i)),
        .. CalendarInputs.IcsAll.Select(i => ("icsedit", i)), .. CalendarInputs.VcfNames.Select(i => ("vcfparse", i)),
        .. CalendarInputs.VcfBuildNames.Select(i => ("vcfwrite", i)), .. CalendarInputs.VcfNames.Select(i => ("vcfedit", i)),
        .. CalendarInputs.RecurrenceNames.Select(i => ("recur", i)),
    ];

    /// <summary>Prints input, items per operation (events, cards or occurrences).</summary>
    public static int Counts()
    {
        foreach (string i in CalendarInputs.IcsAll)
        {
            Console.WriteLine($"{i}\t{CalendarWorkloads.IcsItems(i)}");
        }

        foreach (string i in CalendarInputs.VcfNames)
        {
            Console.WriteLine($"{i}\t{CalendarWorkloads.VcfItems(i)}");
        }

        foreach (string i in CalendarInputs.IcsBuildAll.Concat(CalendarInputs.VcfBuildNames).Distinct())
        {
            Console.WriteLine($"{i}\t{CalendarInputs.BuildCount(i)}");
        }

        foreach (string i in CalendarInputs.RecurrenceNames)
        {
            Console.WriteLine($"{i}\t{CalendarWorkloads.RecurrenceItems(i)}");
        }

        return 0;
    }

    /// <summary>Runs every row once on both sides and prints both checksums; the sides must agree where the work is the same.</summary>
    public static int Verify()
    {
        int bad = 0;
        foreach ((string row, string input) in AllRows())
        {
            string ours, theirs;
            try
            {
                ours = Op(row, true, input)().ToString(Inv);
            }
            catch (Exception e)
            {
                ours = "ERR " + e.GetType().Name + ": " + e.Message;
            }

            try
            {
                theirs = Op(row, false, input)().ToString(Inv);
            }
            catch (Exception e)
            {
                theirs = "ERR " + e.GetType().Name + ": " + e.Message;
            }

            // The written sizes differ by design (each side formats its own output); the parse and expansion sums must match.
            bool exact = row is "icsparse" or "vcfparse" or "recur";
            string verdict = exact ? (ours == theirs ? "same" : "DIFFERENT") : "sizes";
            bad += verdict == "DIFFERENT" ? 1 : 0;
            Console.WriteLine($"{row}\t{input}\t{ours}\t{theirs}\t{verdict}");
        }

        return bad == 0 ? 0 : 1;
    }

    /// <summary>The peak working set and bytes allocated per operation of one row on one side, over about four seconds.</summary>
    public static int Peak(string row, bool ours, string input)
    {
        Func<long> op = Op(row, ours, input);
        op();
        GC.Collect();
        long start = Process.GetCurrentProcess().WorkingSet64;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        int n = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000)
        {
            op();
            n++;
        }

        long perOp = (GC.GetAllocatedBytesForCurrentThread() - a0) / n;
        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        Console.WriteLine(string.Join('\t', "PEAK", row, ours ? "ours" : "theirs", input, (peak >> 10).ToString(Inv), ((peak - start) >> 10).ToString(Inv), perOp.ToString(Inv)));
        return 0;
    }

    /// <summary>Prints the file the holiday input resolves to and its size (for the docs).</summary>
    public static int Describe()
    {
        foreach (string i in CalendarInputs.IcsAll)
        {
            Console.WriteLine($"{i}\t{CalendarInputs.Ics(i).Length}");
        }

        foreach (string i in CalendarInputs.VcfNames)
        {
            Console.WriteLine($"{i}\t{CalendarInputs.Vcf(i).Length}");
        }

        return 0;
    }
}
