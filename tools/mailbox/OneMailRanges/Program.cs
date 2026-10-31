using System.Diagnostics;
using RecursiveAI.OneMail.Sources;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Emlx;
using RecursiveAI.OneMail.Stores.Maildir;
using RecursiveAI.OneMail.Stores.Mbox;

// Usage:
//   OneMailRanges mbox [--any] [--variant mboxo|mboxrd|mboxcl|mboxcl2] <file>...
//       per file: "<path>\t<count>", then per message: offset, end of message (separator excluded), end of record.
//   OneMailRanges scan [--any] <file>
//       enumerates the file and prints message count, bytes allocated, and peak working set.
//   OneMailRanges detect <file>...
//       prints the sniffed format and variant.
//   OneMailRanges maildir <root>        TSV: folder, subdir, unique, flag letters, sha256, size
//   OneMailRanges emlx <bundle root>    TSV: path, sha256 of message, flags word
//   OneMailRanges roundtrip-maildir <src> <dst>   copies through the model, compares the trees
//   OneMailRanges roundtrip-emlx <src>            reads and rewrites every .emlx, compares bytes
//   OneMailRanges roundtrip-mbox <dir>            per .mbox: record copy and message-level rewrite, compares bytes
//   OneMailRanges write <outdir>        writes stores with OneMail's writers for an independent reader to check
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
            foreach (string path in Directory.GetFiles(dir, "*.mbox.expected").Order(StringComparer.Ordinal))
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
                        byte[] message = reader.ReadMessage(reader.Current);
                        string got = Convert.ToHexStringLower(SHA256.HashData(message));
                        if (got != exp[1 + n] && v is MboxVariant.Mboxcl or MboxVariant.Mboxcl2)
                        {
                            // Messages written by OneMail's cl writers carry the Content-Length it inserted; the sidecar
                            // holds the message as it was given, so compare without that header line too.
                            string text = Encoding.Latin1.GetString(message);
                            int at = text.IndexOf("\nContent-Length: ", StringComparison.Ordinal);
                            if (at >= 0)
                            {
                                int end = text.IndexOf('\n', at + 1);
                                got = Convert.ToHexStringLower(SHA256.HashData(Encoding.Latin1.GetBytes(text.Remove(at + 1, end - at))));
                            }
                        }

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
    case "maildir":
    {
        var fs = new PhysicalStoreFileSystem(files[0]);
        var store = new MaildirStore(fs);
        var rows = new List<string>();
        foreach (string folder in store.ListFolders())
        {
            foreach (MaildirEntry e in store.Enumerate(folder))
            {
                string info = e.Name.RawInfo.StartsWith("2,", StringComparison.Ordinal) ? e.Name.RawInfo[2..] : string.Empty;
                int comma = info.IndexOf(',', StringComparison.Ordinal);
                if (comma >= 0)
                {
                    info = info[..comma];
                }

                rows.Add($"{folder}\t{(e.Subdirectory == MaildirSubdirectory.Cur ? "cur" : "new")}\t{e.Name.Unique}\t{info}\t{Sha(fs, e.Path)}\t{e.Size}");
            }
        }

        rows.Sort(StringComparer.Ordinal);
        foreach (string r in rows)
        {
            Console.WriteLine(r);
        }

        return 0;
    }

    case "emlx":
    {
        var fs = new PhysicalStoreFileSystem(files[0]);
        var rows = new List<string>();
        foreach (EmlxItem item in new AppleMailBundle(fs).Enumerate())
        {
            using IRandomAccessSource src = fs.OpenRead(item.Path);
            EmlxEntry e = EmlxReader.Read(src);
            EmlxMetadata m = EmlxReader.ReadMetadata(src, e);
            byte[] msg = new byte[e.MessageLength];
            src.ReadFully(e.MessageOffset, msg);
            rows.Add($"{item.Path}\t{Convert.ToHexStringLower(SHA256.HashData(msg))}\t{m.Flags}");
        }

        rows.Sort(StringComparer.Ordinal);
        foreach (string r in rows)
        {
            Console.WriteLine(r);
        }

        return 0;
    }

    case "roundtrip-maildir":
    {
        var src = new PhysicalStoreFileSystem(files[0]);
        var dst = new PhysicalStoreFileSystem(files[1]);
        int copied = await new MaildirStore(src).CopyToAsync(dst);
        // Compare every file of the source except tmp (deliveries in progress) and dot-files inside cur/new.
        int same = 0;
        var different = new List<string>();
        foreach (string rel in AllFiles(files[0]))
        {
            if (rel.Contains("/tmp/", StringComparison.Ordinal) || rel.StartsWith("tmp/", StringComparison.Ordinal))
            {
                continue;
            }

            string a = Path.Combine(files[0], rel);
            string b = Path.Combine(files[1], rel);
            if (File.Exists(b) && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)))
            {
                same++;
            }
            else
            {
                different.Add(rel);
            }
        }

        Console.WriteLine($"messages copied={copied} files identical={same} not identical={different.Count}");
        foreach (string d in different)
        {
            Console.WriteLine("NOT IDENTICAL " + d);
        }

        return different.Count == 0 ? 0 : 1;
    }

    case "roundtrip-emlx":
    {
        int same = 0;
        var different = new List<string>();
        var fs = new PhysicalStoreFileSystem(files[0]);
        foreach (EmlxItem item in new AppleMailBundle(fs).Enumerate())
        {
            using IRandomAccessSource src = fs.OpenRead(item.Path);
            EmlxEntry e = EmlxReader.Read(src);
            using var ms = new MemoryStream();
            EmlxWriter.WriteFrom(ms, src, e);
            byte[] original = File.ReadAllBytes(Path.Combine(files[0], item.Path));
            if (ms.ToArray().AsSpan().SequenceEqual(original))
            {
                same++;
            }
            else
            {
                different.Add(item.Path);
            }
        }

        Console.WriteLine($"emlx files identical={same} not identical={different.Count}");
        foreach (string d in different)
        {
            Console.WriteLine("NOT IDENTICAL " + d);
        }

        return different.Count == 0 ? 0 : 1;
    }

    case "roundtrip-mbox":
    {
        int recordsSame = 0;
        int messagesSame = 0;
        var different = new List<string>();
        foreach (string path in Directory.GetFiles(files[0], "*.mbox").Order(StringComparer.Ordinal))
        {
            byte[] original = File.ReadAllBytes(path);
            using var src = new MemorySource(original);
            string sidecar = path + ".expected";
            var v = variant;
            if (v == MboxVariant.Unknown && File.Exists(sidecar))
            {
                v = File.ReadLines(sidecar).First().Split('\t')[0] switch { "mboxo" => MboxVariant.Mboxo, "mboxrd" => MboxVariant.Mboxrd, "mboxcl" => MboxVariant.Mboxcl, "mboxcl2" => MboxVariant.Mboxcl2, _ => MboxVariant.Unknown };
            }

            // 1. record copy
            using var records = new MemoryStream();
            var w1 = new MboxWriter(records);
            // 2. message level: unquote, then append again with the same variant, sender and date
            using var again = new MemoryStream();
            var w2 = new MboxWriter(again, new MboxWriterOptions { Variant = v == MboxVariant.Unknown ? MboxVariant.Mboxrd : v, Crlf = Encoding.Latin1.GetString(original).Contains("\r\n", StringComparison.Ordinal) });
            using var reader = new MboxReader(src, new MboxReaderOptions { Variant = v });
            bool first = true;
            while (reader.Read())
            {
                if (first)
                {
                    w1.WritePreamble(src, reader.PreambleLength);
                    w2.WritePreamble(src, reader.PreambleLength);
                    first = false;
                }

                w1.AppendRecord(src, reader.Current);
                MboxEnvelope env = reader.ReadEnvelope(reader.Current);
                w2.Append(reader.ReadMessage(reader.Current), env.Sender, env.Date);
            }

            bool r1 = records.ToArray().AsSpan().SequenceEqual(original);
            bool r2 = again.ToArray().AsSpan().SequenceEqual(original);
            if (r1) { recordsSame++; } else { different.Add(Path.GetFileName(path) + " (records)"); }
            if (r2) { messagesSame++; } else { different.Add(Path.GetFileName(path) + " (messages)"); }
        }

        Console.WriteLine($"mbox files: record copy identical={recordsSame}, message-level rewrite identical={messagesSame}, not identical={different.Count}");
        foreach (string d in different)
        {
            Console.WriteLine("NOT IDENTICAL " + d);
        }

        return different.Count == 0 ? 0 : 1;
    }

    case "write":
        WriteSamples(files[0]);
        return 0;
    default:
        Console.Error.WriteLine("unknown command " + cmd);
        return 2;
}

static string Sha(IStoreFileSystem fs, string path)
{
    using IRandomAccessSource s = fs.OpenRead(path);
    byte[] b = new byte[s.Length];
    s.ReadFully(0, b);
    return Convert.ToHexStringLower(SHA256.HashData(b));
}

static IEnumerable<string> AllFiles(string root) =>
    Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Order(StringComparer.Ordinal);

// Writes stores with OneMail's own writers: one mbox per variant (with a .expected sidecar of the messages as given)
// and a Maildir with flags and keywords (with maildir.expected). An independent reader checks them.
static void WriteSamples(string outDir)
{
    Directory.CreateDirectory(outDir);
    string[] messages =
    [
        "Subject: one\nFrom: a@example.com\n\nplain body\n",
        "Subject: two\nFrom: a@example.com\n\nFrom the desk\nFrom x@y.z Mon Jan  1 00:00:00 2024\ntext\n",
        "Subject: three\nFrom: a@example.com\n\nlast line has no newline",
        "Subject: four\nFrom: a@example.com\n\n\n\nblank lines at the end\n\n",
    ];
    var when = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    foreach (MboxVariant v in new[] { MboxVariant.Mboxo, MboxVariant.Mboxrd, MboxVariant.Mboxcl, MboxVariant.Mboxcl2 })
    {
        string path = Path.Combine(outDir, "w-" + v.ToString().ToLowerInvariant() + ".mbox");
        using (var fs = File.Create(path))
        {
            var w = new MboxWriter(fs, new MboxWriterOptions { Variant = v });
            foreach (string m in messages)
            {
                w.Append(Encoding.Latin1.GetBytes(m), "sender@example.com", when);
            }
        }

        // Append to the existing file with a second writer.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var w = new MboxWriter(fs, new MboxWriterOptions { Variant = v, AppendToExisting = true });
            w.Append(Encoding.Latin1.GetBytes("Subject: appended\nFrom: a@example.com\n\nFrom appended body\n"), "sender@example.com", when);
        }

        File.WriteAllLines(path + ".expected", new[] { v.ToString().ToLowerInvariant() + "\t5" }
            .Concat(messages.Append("Subject: appended\nFrom: a@example.com\n\nFrom appended body\n").Select(m => Convert.ToHexStringLower(SHA256.HashData(Encoding.Latin1.GetBytes(m.EndsWith('\n') ? m : m + "\n"))))));
    }

    int n = 0;
    var mfs = new PhysicalStoreFileSystem(Path.Combine(outDir, "w-maildir"));
    var store = new MaildirStore(mfs, "", new MaildirStoreOptions { UniqueNameFactory = () => $"17000000{n++:D2}.M1P1.written" });
    var rows = new List<string>();
    (string Folder, MessageMarks Marks, string[] Keywords)[] plan =
    [
        ("", MessageMarks.None, []),
        ("", MessageMarks.Seen, []),
        ("", MessageMarks.Seen | MessageMarks.Flagged | MessageMarks.Answered, ["$Junk"]),
        ("Archive", MessageMarks.Draft, []),
        ("Archive.2024", MessageMarks.Deleted | MessageMarks.Forwarded, ["Work", "$Junk"]),
    ];
    foreach (var p in plan)
    {
        byte[] msg = Encoding.Latin1.GetBytes($"Subject: m{n}\nFrom: a@example.com\n\nFrom body {n}\n");
        MaildirEntry e = store.Deliver(msg, p.Folder, p.Marks, p.Keywords);
        string info = e.Name.RawInfo.StartsWith("2,", StringComparison.Ordinal) ? e.Name.RawInfo[2..] : string.Empty;
        rows.Add($"{p.Folder}\t{(e.Subdirectory == MaildirSubdirectory.Cur ? "cur" : "new")}\t{e.Name.Unique}\t{info}\t{Convert.ToHexStringLower(SHA256.HashData(msg))}\t{msg.Length}");
    }

    rows.Sort(StringComparer.Ordinal);
    File.WriteAllLines(Path.Combine(outDir, "maildir.expected"), rows);
}
