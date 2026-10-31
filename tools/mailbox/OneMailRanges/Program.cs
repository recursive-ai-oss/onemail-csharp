using System.Diagnostics;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Mbox;

// Usage:
//   OneMailRanges mbox [--any] [--variant mboxo|mboxrd|mboxcl|mboxcl2] <file>...
//       per file: "<path>\t<count>", then per message: offset, end of message (separator excluded), end of record.
//   OneMailRanges scan [--any] <file>
//       enumerates the file and prints message count, bytes allocated, and peak working set.
//   OneMailRanges detect <file>...
//       prints the sniffed format and variant.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: OneMailRanges mbox|scan|detect [options] <file>...");
    return 2;
}

string cmd = args[0];
var mode = MboxFromLineMode.Strict;
var variant = MboxVariant.Unknown;
List<string> files = [];
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--any":
            mode = MboxFromLineMode.Any;
            break;
        case "--variant":
            variant = Enum.Parse<MboxVariant>(args[++i], ignoreCase: true);
            break;
        default:
            files.Add(args[i]);
            break;
    }
}

var options = new MboxReaderOptions { FromLineMode = mode, Variant = variant };
switch (cmd)
{
    case "mbox":
        foreach (string path in files)
        {
            using FileSource src = FileSource.Open(path);
            var lines = new List<string>();
            foreach (MboxEntry e in MboxReader.Enumerate(src, options))
            {
                lines.Add($"{e.Offset}\t{e.MessageOffset + e.MessageLength}\t{e.Offset + e.RecordLength}\t{e.Defects}\t{e.ContentLength}");
            }

            Console.WriteLine($"{path}\t{lines.Count}");
            foreach (string l in lines)
            {
                Console.WriteLine(l);
            }
        }

        return 0;
    case "scan":
        foreach (string path in files)
        {
            using FileSource src = FileSource.Open(path);
            using var reader = new MboxReader(src, options);
            reader.Read(); // warm up the window and the JIT before measuring
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            long count = 1;
            long bytes = 0;
            while (reader.Read())
            {
                count++;
                bytes += reader.Current.MessageLength;
            }

            sw.Stop();
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            double ws = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0;
            Console.WriteLine($"{path}\tmessages={count}\tfileMiB={src.Length / 1048576.0:F0}\tms={sw.ElapsedMilliseconds}\tallocBytes={alloc}\tallocPerMessage={(double)alloc / count:F2}\tpeakWorkingSetMiB={ws:F1}");
        }

        return 0;
    case "detect":
        foreach (string path in files)
        {
            using FileSource src = FileSource.Open(path);
            MboxDetection d = MboxSniffer.Detect(src);
            Console.WriteLine($"{path}\tmbox={d.IsMbox}\tvariant={d.Variant}\tevidence={d.Evidence}\tmessages={d.MessagesExamined}");
        }

        return 0;
    case "verify":
        // For every <file>.expected beside the files given as directories: reads with the sidecar's variant and checks the
        // count and the SHA-256 of each message as the independent writer gave it.
        int bad = 0;
        int checkedFiles = 0;
        foreach (string dir in files)
        {
            foreach (string path in Directory.GetFiles(dir, "*.expected").Order(StringComparer.Ordinal))
            {
                string mboxPath = path[..^".expected".Length];
                string[] exp = File.ReadAllLines(path);
                string[] head = exp[0].Split('	');
                var v = head[0] switch { "mboxo" => MboxVariant.Mboxo, "mboxrd" => MboxVariant.Mboxrd, "mboxcl" => MboxVariant.Mboxcl, "mboxcl2" => MboxVariant.Mboxcl2, _ => MboxVariant.Unknown };
                int want = int.Parse(head[1]);
                using FileSource src = FileSource.Open(mboxPath);
                using var reader = new MboxReader(src, new MboxReaderOptions { Variant = v, FromLineMode = mode });
                int n = 0;
                string problem = "";
                while (reader.Read())
                {
                    if (n < want && exp[1 + n] != "-")
                    {
                        string got = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(reader.ReadMessage(reader.Current)));
                        if (got != exp[1 + n] && problem.Length == 0)
                        {
                            problem = $"message {n} differs";
                        }
                    }

                    n++;
                }

                if (n != want)
                {
                    problem = $"count {n}, expected {want}";
                }

                checkedFiles++;
                if (problem.Length > 0)
                {
                    bad++;
                }

                Console.WriteLine($"{(problem.Length == 0 ? "ok  " : "FAIL")}	{Path.GetFileName(mboxPath)}	{head[0]}	{n}	{problem}");
            }
        }

        Console.WriteLine($"{checkedFiles - bad}/{checkedFiles} files match");
        return bad == 0 ? 0 : 1;
    default:
        Console.Error.WriteLine("unknown command " + cmd);
        return 2;
}
