using SharpFuzz;

namespace RecursiveAI.OneMail.Fuzz;

// One fuzz executable, one target per input surface. The target is chosen by the ONEMAIL_FUZZ_TARGET environment
// variable because libFuzzer's .NET host starts the program without command-line arguments.
//
// To add a target: write a static method `Run(ReadOnlySpan<byte> data)` that lets only the library's typed results
// escape, then add one line to `Targets` below and a line for its maximum input length to run.sh.
//
// Without a libFuzzer host, `replay <target> <file>...` runs saved inputs once each (reproduces a finding).
internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> data);

    private static readonly Dictionary<string, Target> Targets = Collect();

    private static Dictionary<string, Target> Collect()
    {
        Dictionary<string, Target> targets = new(StringComparer.Ordinal)
        {
            ["text"] = TextTarget.Run,
            ["mime-reader"] = MimeTargets.Reader,
            ["mime-tree"] = MimeTargets.Tree,
            ["ndb"] = NdbTarget.Run,
            ["msg"] = MsgTarget.Run,
            ["tnef"] = TnefTarget.Run,
            ["mail-open"] = MailTarget.Run,
            ["ltp"] = LtpTarget.Run,
            ["pst-messaging"] = PstMessagingTarget.Run,
            ["rtf-decompress"] = RtfTargets.Decompress,
            ["rtf-deencap"] = RtfTargets.Deencapsulate,
            ["oxcmail"] = OxcMailTarget.Run,
            ["dbx"] = DbxTarget.Run,
            ["oxocal"] = OxocalTarget.Run,
            ["sanitizer"] = SanitizerTargets.Run,
            ["sanitizer-reparse"] = SanitizerTargets.Reparse,
        };
        Dictionary<string, Action<ReadOnlySpan<byte>>> reports = new(StringComparer.Ordinal);
        ReportTargets.Register(reports);
        foreach (KeyValuePair<string, Action<ReadOnlySpan<byte>>> kv in reports)
        {
            Action<ReadOnlySpan<byte>> a = kv.Value;
            targets[kv.Key] = d => a(d);
        }

        Dictionary<string, Action<ReadOnlySpan<byte>>> calendar = CalendarTargets.Register(new(StringComparer.Ordinal));
        foreach (KeyValuePair<string, Action<ReadOnlySpan<byte>>> kv in calendar)
        {
            Action<ReadOnlySpan<byte>> a = kv.Value;
            targets[kv.Key] = d => a(d);
        }

        foreach (KeyValuePair<string, Action<ReadOnlySpan<byte>>> kv in ContactTargets.Register(new(StringComparer.Ordinal)))
        {
            Action<ReadOnlySpan<byte>> a = kv.Value;
            targets[kv.Key] = d => a(d);
        }

        foreach (KeyValuePair<string, Action<ReadOnlySpan<byte>>> kv in StoreTargets.Register(new(StringComparer.Ordinal)))
        {
            Action<ReadOnlySpan<byte>> a = kv.Value;
            targets[kv.Key] = d => a(d);
        }

        return targets;
    }

    private static int Main(string[] args)
    {
        if (args.Length > 2 && args[0] == "replay")
        {
            return Replay(args);
        }

        string? name = Environment.GetEnvironmentVariable("ONEMAIL_FUZZ_TARGET") ?? (args.Length > 0 ? args[0] : null);
        if (name is null || !Targets.TryGetValue(name, out Target? target))
        {
            Console.Error.WriteLine("usage: set ONEMAIL_FUZZ_TARGET to one of: " + string.Join(", ", Targets.Keys));
            return 2;
        }

        Fuzzer.LibFuzzer.Run(data => target(data));
        return 0;
    }

    private static int Replay(string[] args)
    {
        if (!Targets.TryGetValue(args[1], out Target? target))
        {
            Console.Error.WriteLine("unknown target " + args[1]);
            return 2;
        }

        foreach (string path in args.Skip(2))
        {
            target(File.ReadAllBytes(path));
            Console.WriteLine("ok " + path);
        }

        return 0;
    }
}
