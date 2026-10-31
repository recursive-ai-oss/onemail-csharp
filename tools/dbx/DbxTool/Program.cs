using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Dbx;
using RecursiveAI.OneMail.Tests.Stores;

namespace DbxTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: DbxTool gen <dir> | extract <store dir> <out dir> | summary <store dir> | dump <store dir> | genbig <file> <count> | enum <file> [read]");
            return 2;
        }

        switch (args[0])
        {
            case "gen":
                Gen(args[1]);
                return 0;
            case "extract":
                return Extract(args[1], args[2]);
            case "genbig":
                GenBig(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
                return 0;
            case "enum":
                return Enum(args[1], args.Length > 2 && args[2] == "read");
            case "dump":
                return Dump(args[1]);
            case "summary":
                return Summary(args[1]);
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }

    // ---- gen ----

    private static void Gen(string dir)
    {
        Directory.CreateDirectory(dir);
        Random rng = new(2004);
        Store(Path.Combine(dir, "small"), rng, new DbxBuild(), [("Inbox", 12, 1), ("Sent Items", 5, 3), ("Deleted Items", 0, 4), ("Drafts", 2, 5), ("Outbox", 1, 2)], userFolders: true);
        Store(Path.Combine(dir, "deep"), rng, new DbxBuild { TreeFanout = 3, ShuffleSeed = 7 }, [("Inbox", 400, 1), ("Sent Items", 31, 3)], userFolders: false);
        Store(Path.Combine(dir, "narrow"), rng, new DbxBuild { TreeFanout = 1, ShuffleSeed = 9 }, [("Inbox", 40, 1)], userFolders: false);
        Store(Path.Combine(dir, "wide"), rng, new DbxBuild(), [("Inbox", 3000, 1)], userFolders: false);
        Odd(Path.Combine(dir, "odd"));
        Store(Path.Combine(dir, "large"), rng, new DbxBuild { ShuffleSeed = 1 }, [("Inbox", 6, 1)], userFolders: false, bigBodies: true);
    }

    // Records the format notes warn about: wrong stated lengths, a header-only record, an empty message, flags and odd bytes.
    private static void Odd(string dir)
    {
        Directory.CreateDirectory(dir);
        byte[] Text(string subject, int size) => Encoding.Latin1.GetBytes("Subject: " + subject + "\r\n\r\n" + new string('x', size));
        List<DbxTestMessage> m =
        [
            new() { Text = Text("short claim", 3000), DeclaredLength = 10, Subject = "short claim" },
            new() { Text = Text("long claim", 40), DeclaredLength = 99999, Subject = "long claim" },
            new() { Text = Text("zero claim", 700), DeclaredLength = 0, Subject = "zero claim" },
            new() { Subject = "header only", NoBody = true },
            new() { Text = [], Subject = "empty" },
            new() { Text = Text("exact 512", 512 - 13), Subject = "exact 512" },
            new() { Text = Text("exact 1024", 1024 - 14), Subject = "exact 1024" },
            new() { Text = [0, 1, 2, 0, 255, 13, 10, 0], Subject = "binary" },
        ];
        File.WriteAllBytes(Path.Combine(dir, "Inbox.dbx"), DbxTestBuilder.MessageDatabase(m));
        File.WriteAllBytes(Path.Combine(dir, "Folders.dbx"), DbxTestBuilder.FolderDatabase(
        [
            new() { Index = 0, Parent = -1, Name = "Outlook Express" },
            new() { Index = 1, Parent = 0, Name = "Inbox", File = "Inbox.dbx", Special = 1, Messages = m.Count },
        ]));
    }

    private static void Store(string dir, Random rng, DbxBuild build, (string Name, int Count, int Special)[] folders, bool userFolders, bool bigBodies = false)
    {
        Directory.CreateDirectory(dir);
        List<DbxTestFolder> fl =
        [
            new() { Index = 0, Parent = -1, Name = "Outlook Express" },
            new() { Index = 1, Parent = 0, Name = "Local Folders" },
        ];
        int index = 2;
        foreach ((string name, int count, int special) in folders)
        {
            fl.Add(new DbxTestFolder { Index = index++, Parent = 1, Name = name, File = name + ".dbx", Special = special, Messages = count });
            File.WriteAllBytes(Path.Combine(dir, name + ".dbx"), DbxTestBuilder.MessageDatabase(Messages(rng, name, count, bigBodies), build));
        }

        if (userFolders)
        {
            fl.Add(new DbxTestFolder { Index = index, Parent = 1, Name = "Projects", File = "Projects.dbx", Messages = 4 });
            File.WriteAllBytes(Path.Combine(dir, "Projects.dbx"), DbxTestBuilder.MessageDatabase(Messages(rng, "Projects", 4, false), build));
            fl.Add(new DbxTestFolder { Index = index + 1, Parent = index, Name = "Archive 2003", File = "Archive 2003.dbx", Messages = 3 });
            File.WriteAllBytes(Path.Combine(dir, "Archive 2003.dbx"), DbxTestBuilder.MessageDatabase(Messages(rng, "Archive 2003", 3, false), build));
        }

        File.WriteAllBytes(Path.Combine(dir, "Folders.dbx"), DbxTestBuilder.FolderDatabase(fl, build));
    }

    private static List<DbxTestMessage> Messages(Random rng, string folder, int count, bool big)
    {
        List<DbxTestMessage> l = [];
        for (int i = 0; i < count; i++)
        {
            int kind = rng.Next(5);
            string subject = folder + " message " + i;
            StringBuilder sb = new();
            sb.Append("From: Sender <s").Append(i).Append("@example.com>\r\nTo: you@example.com\r\nSubject: ").Append(subject)
                .Append("\r\nDate: Thu, 06 May 2004 07:00:").Append((i % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture)).Append(" +0000\r\nMessage-ID: <").Append(folder.Replace(' ', '_')).Append('.').Append(i).Append("@example.com>\r\nMIME-Version: 1.0\r\n");
            int body = big ? (i % 2 == 0 ? 100_000 + rng.Next(1000) : 3_000_000 + rng.Next(1000)) : kind switch { 0 => 0, 1 => 80, 2 => 511 + rng.Next(3), 3 => 2000 + rng.Next(3000), _ => 20_000 + rng.Next(30_000) };
            if (kind == 3 && !big)
            {
                sb.Append("Content-Type: multipart/mixed; boundary=\"b\"\r\n\r\n--b\r\nContent-Type: text/plain; charset=iso-8859-1\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nCaf=E9 =3D ok\r\n--b\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\n\r\n");
                byte[] raw = new byte[body];
                rng.NextBytes(raw);
                sb.Append(Convert.ToBase64String(raw, Base64FormattingOptions.InsertLineBreaks).Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n--b--\r\n");
            }
            else
            {
                sb.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
                int target = sb.Length + body;
                while (sb.Length < target)
                {
                    sb.Append("Line ").Append(rng.Next(1000000)).Append(" of text, with accents caf\u00e9 and a lone LF\n").Append("and CR LF\r\n");
                }
            }

            l.Add(new DbxTestMessage
            {
                Text = Encoding.UTF8.GetBytes(sb.ToString()),
                Subject = subject,
                MessageId = "<" + folder.Replace(' ', '_') + "." + i + "@example.com>",
                SenderName = "Sender",
                SenderAddress = "s" + i + "@example.com",
                Flags = (uint)(rng.Next(2) == 0 ? 0x81 : 0x01),
            });
        }

        return l;
    }

    // ---- extract ----

    // Writes every message under <out>/<folder path>/<position>.eml and a listing; returns the number of unreadable files.
    private static int Extract(string storeDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using StreamWriter list = new(Path.Combine(outDir, "list.tsv"));
        list.WriteLine("folder\tposition\tlength\tsha256\tdefects");
        PhysicalStoreFileSystem fs = new(storeDir);
        DbxStoreFolder store = new(fs);
        Dictionary<string, int> pos = [];
        foreach (DbxStoreMessage m in store.EnumerateMessages())
        {
            byte[] b = store.ReadMessage(m, out DbxChainInfo info);
            int p = pos.GetValueOrDefault(m.Folder);
            pos[m.Folder] = p + 1;
            string d = Path.Combine(outDir, m.Folder);
            Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, p.ToString("00000", System.Globalization.CultureInfo.InvariantCulture) + ".eml"), b);
            list.WriteLine($"{m.Folder}\t{p}\t{b.Length}\t{Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()}\t{info.Defects | m.Message.Defects | m.Defects}");
        }

        // Folders that exist but hold no messages still count.
        foreach (DbxFolderEntry f in store.Folders)
        {
            list.WriteLine($"#folder\t{store.PathOf(f)}\t{f.FileName}\t{f.Index}\t{f.ParentIndex}");
        }

        return 0;
    }

    // One line per message: folder, index, flags, stated length, chain length, segments, defects.
    private static int Dump(string storeDir)
    {
        DbxStoreFolder store = new(new PhysicalStoreFileSystem(storeDir));
        foreach (DbxStoreMessage m in store.EnumerateMessages())
        {
            DbxChainInfo c = store.MeasureMessage(m);
            Console.WriteLine($"{m.Folder}\t{m.Message.Index}\t0x{(uint)m.Message.Flags:x}\t{m.Message.DeclaredLength}\t{c.Length}\t{c.Segments}\t{c.Defects | m.Message.Defects}");
        }

        return 0;
    }

    // genbig / enum: the constant-memory check. genbig writes one message database of <count> small messages (about 800 bytes of file
    // each); enum, in a fresh process, walks every record and reads (or with read, copies) every message through a file source and reports the peak working set,
    // the managed bytes allocated per message and the time.
    private static void GenBig(string file, int count)
    {
        List<DbxTestMessage> m = new(count);
        byte[] text = Encoding.ASCII.GetBytes("From: a@example.com\r\nSubject: x\r\n\r\n" + new string('y', 260));
        for (int i = 0; i < count; i++)
        {
            m.Add(new DbxTestMessage { Text = text, Subject = "message " + i, MessageId = "<" + i + "@example.com>", SenderAddress = "a@example.com" });
        }

        File.WriteAllBytes(file, DbxTestBuilder.MessageDatabase(m));
    }

    private static int Enum(string file, bool read)
    {
        using FileSource src = FileSource.Open(file);
        DbxReader r = new(src);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        long bytes = 0;
        foreach (DbxMessageEntry e in r.EnumerateMessages())
        {
            n++;
            bytes += read ? r.ReadMessage(e, out _).Length : r.MeasureMessage(e).Length;
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long peak = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
        Console.WriteLine($"{n} messages, {bytes} message bytes, file {src.Length} bytes, peak working set {peak / (1024 * 1024)} MiB, managed allocation {allocated / Math.Max(1, n)} bytes/message, {sw.ElapsedMilliseconds} ms, defects {r.Defects}");
        return 0;
    }

    private static int Summary(string storeDir)
    {
        PhysicalStoreFileSystem fs = new(storeDir);
        DbxStoreFolder store = new(fs);
        int n = 0;
        long bytes = 0;
        foreach (DbxStoreMessage m in store.EnumerateMessages())
        {
            bytes += store.MeasureMessage(m).Length;
            n++;
        }

        Console.WriteLine($"{store.Folders.Count} folders, {n} messages, {bytes} bytes");
        return 0;
    }
}
