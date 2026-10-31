using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>The helper commands for the Outlook rows: <c>verify</c>, <c>counts</c>, <c>curve</c> and <c>peak</c>.</summary>
internal static class OutlookCommands
{
    private static IEnumerable<(string Row, string Input)> AllInputs()
    {
        foreach (string n in OutlookInputs.PstReal.Concat(OutlookInputs.PstSmallGenerated).Concat(OutlookInputs.PstLargeGenerated).Concat(OutlookInputs.PstFourKiB))
        {
            yield return ("pst", n);
        }

        foreach (string n in OutlookSets.Msgs.Length == 0 ? [] : OutlookSets.MsgNames)
        {
            yield return ("msg", n);
        }

        foreach (string n in OutlookInputs.TnefNames)
        {
            yield return ("tnef", n);
        }
    }

    /// <summary>Runs both sides over each input and prints what each saw, so unequal work shows before any timing is believed.</summary>
    public static int Verify()
    {
        Console.WriteLine("row\tinput\tside\ttotals\tmatch");
        foreach ((string row, string input) in AllInputs())
        {
            Totals o = Run(row, ours: true, input);
            Totals t;
            try
            {
                t = Run(row, ours: false, input);
            }
            catch (Exception e)
            {
                Console.WriteLine($"{row}\t{input}\tours\t{o}\t");
                Console.WriteLine($"{row}\t{input}\ttheirs\tTHREW {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")[..Math.Min(120, e.Message.Length)]}\tno");
                continue;
            }

            bool same = o.Messages == t.Messages && o.Recipients == t.Recipients && o.Attachments == t.Attachments && o.AttachmentBytes == t.AttachmentBytes;
            Console.WriteLine($"{row}\t{input}\tours\t{o}\t");
            Console.WriteLine($"{row}\t{input}\ttheirs\t{t}\t{(same ? "yes" : "NO")}");
        }

        return 0;
    }

    /// <summary>Prints <c>input TAB messages</c> for every input: how many messages one operation reads (for bytes per message).</summary>
    public static int Counts()
    {
        foreach ((string row, string input) in AllInputs())
        {
            Console.WriteLine($"{input}\t{Run(row, ours: true, input).Messages}");
        }

        return 0;
    }

    private static Totals Run(string row, bool ours, string input)
    {
        switch (row)
        {
            case "pst":
                return ours ? OutlookWorkloads.PstOurs(OutlookInputs.PstPath(input)) : OutlookWorkloads.PstTheirs(OutlookInputs.PstPath(input));
            case "msg":
                var t = new Totals();
                foreach (string f in OutlookSets.MsgSet(input))
                {
                    Totals x = ours ? OutlookWorkloads.MsgOurs(f) : OutlookWorkloads.MsgTheirs(f);
                    t.Messages += x.Messages;
                    t.Recipients += x.Recipients;
                    t.Attachments += x.Attachments;
                    t.AttachmentBytes += x.AttachmentBytes;
                    t.Chars += x.Chars;
                }

                return t;
            case "tnef":
                byte[] data = File.ReadAllBytes(OutlookInputs.TnefPath(input));
                return ours ? OutlookWorkloads.TnefOurs(data) : OutlookWorkloads.TnefTheirs(data);
            default:
                throw new ArgumentException("unknown row " + row, nameof(row));
        }
    }

    /// <summary>
    /// <c>PEAK row side input</c>: runs one row's work for at least four seconds (and at least once) in this process and prints the
    /// peak working set, the growth over the working set at start, and bytes allocated per operation.
    /// </summary>
    public static int Peak(string row, bool ours, string input)
    {
        GC.Collect();
        long start = Process.GetCurrentProcess().WorkingSet64;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        int n = 0;
        var sw = Stopwatch.StartNew();
        do
        {
            Run(row, ours, input);
            n++;
        }
        while (sw.ElapsedMilliseconds < 4000);

        long perOp = (GC.GetAllocatedBytesForCurrentThread() - a0) / n;
        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        Console.WriteLine(string.Join('\t', "PEAK", row, ours ? "ours" : "theirs", input, (peak >> 10).ToString(CultureInfo.InvariantCulture), ((peak - start) >> 10).ToString(CultureInfo.InvariantCulture), perOp.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture)));
        return 0;
    }

    /// <summary>
    /// <c>curve pst SIDE INPUT</c>: walks the store once while a sampler prints, every 100 ms, the messages read so far, the working set
    /// and the managed heap. A flat line is the constant-working-set claim.
    /// </summary>
    public static int Curve(string row, string side, string input)
    {
        bool ours = side == "ours";
        using var done = new ManualResetEventSlim();
        Process self = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        var sampler = new Thread(() =>
        {
            while (!done.Wait(100))
            {
                self.Refresh();
                Console.WriteLine(string.Join('\t', "CURVE", side, input, clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture), Volatile.Read(ref OutlookWorkloads.Progress).ToString(CultureInfo.InvariantCulture), (self.WorkingSet64 >> 20).ToString(CultureInfo.InvariantCulture), (GC.GetTotalMemory(false) >> 20).ToString(CultureInfo.InvariantCulture)));
            }
        })
        { IsBackground = true };
        sampler.Start();
        Totals t = Run(row, ours, input);
        done.Set();
        sampler.Join();
        Console.WriteLine($"CURVE-END\t{side}\t{input}\t{t}\tseconds={clock.Elapsed.TotalSeconds:F1}\tpeak_ws_MiB={self.PeakWorkingSet64 >> 20}");
        return 0;
    }
}
