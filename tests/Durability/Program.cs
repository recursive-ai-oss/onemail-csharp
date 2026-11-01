using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneMail.Durability;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Tests.Pst;

// Durability checks for in-place PST writing.
//
//   Durability campaign <dir> [--bases ours,tika1,tika2] [--scenarios add,delete,...] [--cases 150] [--seed 1] [--parallel 8] [--corpus <dir>]
//       Records each scenario's writes to an in-memory file, then rebuilds the file as a power cut would leave it after many of those writes
//       (all earlier writes, a random subset of the writes since the last barrier, or the last write torn at a sector boundary) and says whether each
//       leftover opens as one of the states the scenario passes through. Anything else is a failure.
//   Durability kill <dir> <base> <scenario> <count> deterministic|random [--corpus <dir>]
//       Runs the scenario in a child process on a real file and stops it with SIGKILL at the Nth write or barrier (deterministic) or after a random
//       time (random), then classifies the file the same way.
//   Durability soak <dir> <base> <ops> <seed> [--commit-every 100] [--corpus <dir>]
//       Random operations on a real file with a check after every commit.
//   Durability child <scenario> <file> <killAtOp> <killAfterMs>        (used by kill)
//   Durability make <dir> [--bases ...] [--scenarios ...]             writes each base after each scenario, finished, for the libpff comparison
//   Durability pff <dir>                                              runs pffinfo over every .pst in a directory (needs PFFINFO)

string Opt(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

string corpus = Opt("--corpus", Environment.GetEnvironmentVariable("ONEMAIL_CORPUS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus"));
int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: Durability campaign|kill|soak|child|pff ...");
    return 2;
}

switch (args[0])
{
    case "campaign":
        return Campaign.Run(args[1], Opt("--bases", "ours,tika1,tika2").Split(','), Opt("--scenarios", string.Join(',', Scenarios.Names)).Split(','), I(Opt("--cases", "150")), I(Opt("--seed", "1")), I(Opt("--parallel", "8")), corpus);
    case "kill":
        return Campaign.Kill(args[1], args[2], args[3], I(args[4]), args[5], I(Opt("--seed", "1")), I(Opt("--parallel", "4")), corpus);
    case "soak":
        return Soak.Run(args[1], args[2], I(args[3]), I(args[4]), I(Opt("--commit-every", "100")), corpus);
    case "child":
        return Campaign.Child(args[1], args[2], I(args[3]), I(args[4]));
    case "make":
        return Campaign.Make(args[1], Opt("--bases", "ours,tika1,tika2").Split(','), Opt("--scenarios", string.Join(',', Scenarios.Names)).Split(','), corpus);
    case "pff":
        return Campaign.Pff(args[1]);
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 2;
}

internal static class Campaign
{
    public static int Run(string dir, string[] bases, string[] scenarios, int cases, int seed, int parallel, string corpus)
    {
        Directory.CreateDirectory(dir);
        int failures = 0;
        long total = 0;
        foreach (string baseName in bases)
        {
            byte[] image = Bases.Load(baseName, corpus);
            bool deep = StoreProblems(image) == 0;
            foreach (string scenario in scenarios)
            {
                var sw = Stopwatch.StartNew();
                Recording rec = Harness.Record(image, scenario);
                int ops = rec.Ops.Count;
                var cuts = new SortedSet<int>();
                for (int i = 0; i < ops; i++)
                {
                    // the points where an ordering rule can matter: around every barrier, and the end
                    if (rec.Ops[i].Kind == OpKind.Barrier)
                    {
                        cuts.Add(i);
                        cuts.Add(i + 1);
                        cuts.Add(Math.Max(0, i - 1));
                    }
                }

                var rnd = new Random(seed ^ (baseName.GetHashCode(StringComparison.Ordinal) * 31) ^ scenario.Length);
                while (cuts.Count < Math.Min(cases, ops + 1))
                {
                    cuts.Add(rnd.Next(0, ops + 1));
                }

                cuts.Add(ops);
                var jobs = new List<(int Cut, int Variant, int Seed)>();
                foreach (int c in cuts)
                {
                    jobs.Add((c, 0, 0));
                    jobs.Add((c, 1, rnd.Next()));
                    jobs.Add((c, 1, rnd.Next()));
                    jobs.Add((c, 2, rnd.Next()));
                }

                int[] byState = new int[rec.States.Count];
                int bad = 0, invalid = 0, repaired = 0, repairBad = 0;
                var messages = new ConcurrentBag<string>();
                Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = parallel }, job =>
                {
                    byte[] file = Harness.Materialize(rec, job.Cut, job.Variant, new Random(job.Seed));
                    (string? problem, int state, bool invalidMaps) = Harness.Classify(file, rec, deep);
                    if (problem is not null)
                    {
                        Interlocked.Increment(ref bad);
                        string name = $"fail-{baseName}-{scenario}-cut{job.Cut}-v{job.Variant}.pst";
                        File.WriteAllBytes(Path.Combine(dir, name), file);
                        messages.Add($"{name}: {problem}");
                        return;
                    }

                    Interlocked.Increment(ref byState[state]);
                    if (invalidMaps)
                    {
                        Interlocked.Increment(ref invalid);
                        if (job.Cut % 5 == 0)
                        {
                            string? r = Harness.RepairAndCheck(file);
                            if (r is null)
                            {
                                Interlocked.Increment(ref repaired);
                            }
                            else
                            {
                                Interlocked.Increment(ref repairBad);
                                messages.Add($"{baseName}/{scenario} cut {job.Cut}: {r}");
                            }
                        }
                    }
                });

                failures += bad + repairBad;
                total += jobs.Count;
                Console.WriteLine($"{baseName,-6} {scenario,-8} ops {ops,6} barriers {rec.Ops.Count(o => o.Kind == OpKind.Barrier),3} leftovers {jobs.Count,5}  old {byState[0],5} new {byState[^1],5} between {byState.Skip(1).Take(byState.Length - 2).Sum(),4}  invalid-maps {invalid,4} (repaired {repaired}, repair failed {repairBad})  FAIL {bad}  {sw.Elapsed.TotalSeconds:F1}s");
                foreach (string m in messages.Take(5))
                {
                    Console.WriteLine("    " + m);
                }
            }
        }

        Console.WriteLine($"campaign: {total} leftovers, {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static int StoreProblems(byte[] image)
    {
        if (!PstStore.TryOpen(image, out PstStore? s, out _))
        {
            return int.MaxValue;
        }

        using (s)
        {
            return StoreChecks.Consistency(s!).Count;
        }
    }

    public static int Make(string dir, string[] bases, string[] scenarios, string corpus)
    {
        Directory.CreateDirectory(dir);
        foreach (string b in bases)
        {
            byte[] image = Bases.Load(b, corpus);
            foreach (string sc in scenarios)
            {
                string file = Path.Combine(dir, $"edited-{b}-{sc}.pst");
                File.WriteAllBytes(file, image);
                Child(sc, file, 0, 0);
            }
        }

        return 0;
    }

    public static int Child(string scenario, string file, int killAtOp, int killAfterMs)
    {
        var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
        var io = new KillingIo(fs, killAtOp, killAfterMs);
        io.StartTimer();
        NdbEditor ed = NdbEditor.Open(io, new NdbEditorOptions());
        using var w = new PstWritableStore(ed, Scenarios.Options);
        Scenarios.Run(scenario, w, w.Commit);
        Console.WriteLine("completed " + io.Operations);
        return 0;
    }

    public static int Kill(string dir, string baseName, string scenario, int count, string mode, int seed, int parallel, string corpus)
    {
        Directory.CreateDirectory(dir);
        byte[] image = Bases.Load(baseName, corpus);
        Recording rec = Harness.Record(image, scenario);
        int ops = rec.Ops.Count;
        var rnd = new Random(seed);
        // The recorded run took about this long, so random kills land across the whole run.
        var sw = Stopwatch.StartNew();
        _ = Harness.Record(image, scenario);
        int spanMs = Math.Max(30, (int)sw.ElapsedMilliseconds * 3);
        var plan = new List<(int AtOp, int AfterMs)>();
        for (int i = 0; i < count; i++)
        {
            plan.Add(mode == "deterministic" ? (1 + (i * ops / Math.Max(1, count)) + rnd.Next(0, Math.Max(1, ops / Math.Max(1, count))), 0) : (0, 5 + rnd.Next(0, spanMs + 400)));
        }

        string self = typeof(Campaign).Assembly.Location;
        int bad = 0, killed = 0;
        int[] byState = new int[rec.States.Count];
        var messages = new ConcurrentBag<string>();
        int n = 0;
        Parallel.ForEach(plan, new ParallelOptions { MaxDegreeOfParallelism = parallel }, p =>
        {
            int id = Interlocked.Increment(ref n);
            string file = Path.Combine(dir, $"kill-{baseName}-{scenario}-{id}.pst");
            File.WriteAllBytes(file, image);
            var psi = new ProcessStartInfo("dotnet", $"\"{self}\" child {scenario} \"{file}\" {p.AtOp} {p.AfterMs}") { RedirectStandardOutput = true, RedirectStandardError = true };
            using Process proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                Interlocked.Increment(ref killed);
            }

            byte[] left = File.ReadAllBytes(file);
            (string? problem, int state, _) = Harness.Classify(left, rec, deep: false);
            if (problem is not null)
            {
                Interlocked.Increment(ref bad);
                messages.Add($"{Path.GetFileName(file)} (exit {proc.ExitCode}, op {p.AtOp}, ms {p.AfterMs}): {problem}");
                return;
            }

            Interlocked.Increment(ref byState[state]);
            File.Delete(file);
        });

        Console.WriteLine($"kill {baseName} {scenario} {mode}: {count} runs, {killed} killed, {count - killed} finished first; old {byState[0]} new {byState[^1]} between {byState.Skip(1).Take(byState.Length - 2).Sum()}; FAIL {bad}");
        foreach (string m in messages.Take(8))
        {
            Console.WriteLine("    " + m);
        }

        return bad == 0 ? 0 : 1;
    }

    public static int Pff(string dir)
    {
        string? tool = Environment.GetEnvironmentVariable("PFFINFO");
        if (tool is null)
        {
            Console.Error.WriteLine("set PFFINFO to the pffinfo binary");
            return 2;
        }

        int ok = 0, bad = 0;
        foreach (string f in Directory.GetFiles(dir, "*.pst"))
        {
            var psi = new ProcessStartInfo(tool, $"\"{f}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
            using Process p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0)
            {
                ok++;
            }
            else
            {
                bad++;
                Console.WriteLine("pffinfo failed on " + f);
            }
        }

        Console.WriteLine($"pffinfo: {ok} opened, {bad} failed");
        return bad == 0 ? 0 : 1;
    }
}
