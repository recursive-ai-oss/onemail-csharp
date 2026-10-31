using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;

// MsgTool: reads MSG files with OneMail and prints what it found.
//   MsgTool summary <file|dir>...   one line per file: counts, defects, allocation and time of opening
//   MsgTool json <file>             every object and property as JSON, for comparison with other readers
//   MsgTool view <file>             the shared view of the first item (subject, addresses by kind, dates, bodies, attachments, embedded items) as JSON
//   MsgTool streams <file|dir>...   re-encodes each object's property and value streams and compares them with the file's
//   MsgTool gen <dir>               writes the generated fixtures (the same files the tests build in memory)
namespace MsgTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: MsgTool summary|json|streams <file|dir>...");
            return 2;
        }

        if (args[0] == "gen")
        {
            Directory.CreateDirectory(args[1]);
            foreach ((string name, byte[] bytes) in RecursiveAI.OneMail.Tests.Msg.MsgFixtures.All())
            {
                File.WriteAllBytes(Path.Combine(args[1], name), bytes);
                Console.WriteLine($"{name}	{bytes.Length}");
            }

            return 0;
        }

        string[] files = args.Skip(1).SelectMany(Expand).ToArray();
        switch (args[0])
        {
            case "summary":
                Console.WriteLine("file\tbytes\topen\tprops\trecipients\tattachments\tembedded\tobjects\tdefects\topen_alloc\topen_ms\tall_alloc\tdefect_kinds");
                foreach (string f in files)
                {
                    Summary(f);
                }

                return 0;
            case "json":
                Json(files[0]);
                return 0;
            case "view":
                View(files[0]);
                return 0;
            case "streams":
                int bad = 0;
                foreach (string f in files)
                {
                    bad += Streams(f);
                }

                return bad == 0 ? 0 : 1;
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }

    private static void View(string path)
    {
        using RecursiveAI.OneMail.MailContainer c = RecursiveAI.OneMail.Mail.Open(path);
        using var stream = Console.OpenStandardOutput();
        using var w = new Utf8JsonWriter(stream);
        foreach (RecursiveAI.OneMail.IMailItem item in c.Enumerate())
        {
            ViewItem(w, item);
            break;
        }
    }

    private static void Addrs(Utf8JsonWriter w, string name, IReadOnlyList<RecursiveAI.OneMail.MailAddress> list)
    {
        w.WriteStartArray(name);
        foreach (RecursiveAI.OneMail.MailAddress a in list)
        {
            w.WriteStartObject();
            w.WriteString("name", a.Name);
            w.WriteString("addr", a.Address);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void ViewItem(Utf8JsonWriter w, RecursiveAI.OneMail.IMailItem item)
    {
        w.WriteStartObject();
        w.WriteString("kind", item.Kind.ToString());
        w.WriteString("subject", item.Subject);
        Addrs(w, "from", item.From);
        w.WriteStartArray("sender");
        if (item.Sender is { } snd)
        {
            w.WriteStartObject();
            w.WriteString("name", snd.Name);
            w.WriteString("addr", snd.Address);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        Addrs(w, "to", item.ToRecipients);
        Addrs(w, "cc", item.Cc);
        Addrs(w, "bcc", item.Bcc);
        foreach (RecursiveAI.OneMail.MailDateSource src in new[] { RecursiveAI.OneMail.MailDateSource.ClientSubmit, RecursiveAI.OneMail.MailDateSource.Delivery, RecursiveAI.OneMail.MailDateSource.Creation })
        {
            w.WriteString("date" + src, item.TryGetDate(src, out RecursiveAI.OneMail.MailDate d) ? d.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) : null);
        }

        w.WriteString("messageId", item.MessageId);
        w.WriteString("conversationTopic", item.ConversationTopic);
        w.WriteString("text", item.TextBody?.ReadText());
        w.WriteString("html", item.HtmlBody?.ReadText());
        w.WriteString("htmlCharset", item.HtmlBody?.Charset);
        w.WriteBoolean("hasRtf", item.RtfBody is not null);
        w.WriteStartArray("attachments");
        foreach (RecursiveAI.OneMail.IMailAttachment a in item.Attachments)
        {
            w.WriteStartObject();
            w.WriteString("name", a.FileName);
            w.WriteString("type", a.ContentType);
            w.WriteString("contentId", a.ContentId);
            w.WriteBoolean("inline", a.IsInline);
            w.WriteNumber("size", a.Size);
            using (Stream s = a.Open())
            {
                w.WriteString("sha256", Convert.ToHexString(SHA256.HashData(s)));
            }

            w.WritePropertyName("embedded");
            if (a.Embedded is { } e)
            {
                ViewItem(w, e);
            }
            else
            {
                w.WriteNullValue();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static IEnumerable<string> Expand(string p) =>
        Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".msg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".oft", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            : [p];

    private static void Summary(string path)
    {
        long bytes = new FileInfo(path).Length;
        // Warm up so JIT and static initialisation are not counted.
        if (MsgFile.TryOpen(path, out MsgFile? w, out _))
        {
            w.Dispose();
        }

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        if (!MsgFile.TryOpen(path, out MsgFile? file, out IReadOnlyList<MapiDefect> openDefects))
        {
            sw.Stop();
            long failAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            Console.WriteLine($"{Path.GetFileName(path)}\t{bytes}\tFAIL\t0\t0\t0\t0\t0\t{openDefects.Count}\t{failAlloc}\t{sw.Elapsed.TotalMilliseconds:F2}\t0\t{string.Join(",", openDefects.Select(d => d.Kind))}");
            return;
        }

        sw.Stop();
        long openAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
        using (file)
        {
            int props = file.Message.Properties.Count;
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            file.ReadEverything();
            long allAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            _ = a1;
            Count(file.Message, out int rec, out int att, out int emb, out int objs);
            string kinds = string.Join(",", file.Defects.GroupBy(d => d.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"));
            Console.WriteLine($"{Path.GetFileName(path)}\t{bytes}\tok\t{props}\t{rec}\t{att}\t{emb}\t{objs}\t{file.Defects.Count}\t{openAlloc}\t{sw.Elapsed.TotalMilliseconds:F2}\t{allAlloc}\t{kinds}");
        }
    }

    private static void Count(MapiMessage m, out int rec, out int att, out int emb, out int objs)
    {
        rec = m.Recipients.Count;
        att = m.Attachments.Count;
        emb = 0;
        objs = 1 + rec + att;
        foreach (MapiAttachment a in m.Attachments)
        {
            if (a.EmbeddedMessage is { } inner)
            {
                Count(inner, out int r, out int at, out int e, out int o);
                rec += r;
                att += at;
                emb += 1 + e;
                objs += o;
            }
        }
    }

    // ---- JSON dump

    private static void Json(string path)
    {
        if (!MsgFile.TryOpen(path, out MsgFile? file, out IReadOnlyList<MapiDefect> defects))
        {
            using Stream o = Console.OpenStandardOutput();
            using var w = new Utf8JsonWriter(o, new JsonWriterOptions { Indented = false });
            w.WriteStartObject();
            w.WriteString("error", string.Join("; ", defects));
            w.WriteEndObject();
            return;
        }

        using (file)
        {
            using Stream stdout = Console.OpenStandardOutput();
            using var w = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = false });
            w.WriteStartObject();
            w.WriteString("file", Path.GetFileName(path));
            if (file.HasUnicodeStrings is bool u)
            {
                w.WriteBoolean("unicode", u);
            }

            w.WritePropertyName("message");
            WriteObject(w, file.Message, "Message");
            w.WritePropertyName("defects");
            w.WriteStartArray();
            foreach (MapiDefect d in file.ReadEverything())
            {
                w.WriteStartObject();
                w.WriteString("kind", d.Kind.ToString());
                w.WriteString("at", d.Location);
                w.WriteNumber("offset", d.Offset);
                w.WriteString("detail", d.Detail);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }
    }

    private static void WriteObject(Utf8JsonWriter w, MapiObject obj, string kind)
    {
        w.WriteStartObject();
        w.WriteString("kind", kind);
        var item = (IMsgItem)obj;
        w.WriteString("path", item.Layout.Path);
        w.WriteString("string8", obj.String8Encoding.Name);
        w.WriteString("string8Source", obj.String8EncodingSource.ToString());
        w.WriteString("header", Convert.ToHexString(item.Layout.RawHeader));
        w.WritePropertyName("props");
        w.WriteStartArray();
        for (int i = 0; i < obj.Properties.Count; i++)
        {
            MapiProperty p = obj.Properties[i];
            MsgEntryMeta meta = item.Layout.Entries[i];
            w.WriteStartObject();
            w.WriteString("tag", p.Tag.ToHex());
            w.WriteNumber("flags", meta.Flags);
            w.WriteString("tail", meta.Tail.ToString("X16", CultureInfo.InvariantCulture));
            w.WriteBoolean("entry", meta.HasEntry);
            w.WriteString("storage", meta.Storage.ToString());
            if (p.Name is { } n)
            {
                w.WriteString("name", n.ToString());
            }

            w.WritePropertyName("values");
            w.WriteStartArray();
            for (int k = 0; k < p.ValueCount; k++)
            {
                long len = p.GetByteLength(k);
                if (len > (1 << 20))
                {
                    using Stream s = p.OpenRead(k);
                    w.WriteStringValue($"sha256:{len}:{Convert.ToHexString(SHA256.HashData(s))}");
                }
                else
                {
                    w.WriteStringValue(Convert.ToHexString(p.GetBytes(k).Span));
                }
            }

            w.WriteEndArray();
            if (p.AsString(obj.String8Encoding) is { } text)
            {
                w.WriteString("text", text);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (obj is MapiMessage m)
        {
            w.WritePropertyName("recipients");
            w.WriteStartArray();
            foreach (MapiRecipient r in m.Recipients)
            {
                WriteObject(w, r, "Recipient");
            }

            w.WriteEndArray();
            w.WritePropertyName("attachments");
            w.WriteStartArray();
            foreach (MapiAttachment a in m.Attachments)
            {
                w.WriteStartObject();
                w.WritePropertyName("attachment");
                WriteObject(w, a, "Attachment");
                if (a.EmbeddedMessage is { } inner)
                {
                    w.WritePropertyName("embedded");
                    WriteObject(w, inner, "EmbeddedMessage");
                }
                else if (a.ObjectStorage is { } st)
                {
                    w.WritePropertyName("storage");
                    w.WriteStartObject();
                    w.WriteString("class", st.ClassId.ToString());
                    w.WritePropertyName("entries");
                    w.WriteStartArray();
                    foreach (MapiRawStorageEntry e in st.Entries)
                    {
                        w.WriteStringValue($"{e.Path}:{(e.IsStorage ? "storage" : e.Length.ToString(CultureInfo.InvariantCulture))}");
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    // ---- re-encoding check

    private static int Streams(string path)
    {
        if (!MsgFile.TryOpen(path, out MsgFile? file, out _))
        {
            Console.WriteLine($"{Path.GetFileName(path)}\tNOT-OPENED");
            return 0;
        }

        int objects = 0;
        int streams = 0;
        var diffs = new List<string>();
        using (file)
        using (var reader = new CompoundFileReader(File.ReadAllBytes(path)))
        {
            void Check(MapiObject o)
            {
                var item = (IMsgItem)o;
                objects++;
                CfbEntry? storage = item.Layout.Path.Length == 0 ? reader.Root : reader.Find(item.Layout.Path.TrimStart('/'));
                if (storage is null)
                {
                    diffs.Add($"{item.Layout.Path}: storage not found");
                    return;
                }

                Dictionary<string, CfbEntry> original = reader.GetChildren(storage)
                    .Where(k => k.ObjectType == CfbObjectType.Stream && (k.RawName.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase) || k.RawName.Equals("__properties_version1.0", StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(k => k.RawName.ToUpperInvariant(), k => k);
                foreach (MsgStreamData s in MsgStreams.Encode(o, item.Layout))
                {
                    streams++;
                    string key = s.Name.ToUpperInvariant();
                    if (!original.Remove(key, out CfbEntry? e))
                    {
                        diffs.Add($"{item.Layout.Path}/{s.Name}: written but not in the file");
                        continue;
                    }

                    using Stream st = reader.OpenEntryStream(e);
                    var bytes = new byte[e.Size];
                    st.ReadExactly(bytes);
                    if (!bytes.AsSpan().SequenceEqual(s.Data))
                    {
                        diffs.Add($"{item.Layout.Path}/{s.Name}: bytes differ (file {bytes.Length}, written {s.Data.Length}{FirstDiff(bytes, s.Data)})");
                    }
                }

                foreach (string left in original.Keys)
                {
                    diffs.Add($"{item.Layout.Path}/{left}: in the file but not written");
                }
            }

            void Visit(MapiMessage m)
            {
                Check(m);
                foreach (MapiRecipient r in m.Recipients)
                {
                    Check(r);
                }

                foreach (MapiAttachment a in m.Attachments)
                {
                    Check(a);
                    if (a.EmbeddedMessage is { } inner)
                    {
                        Visit(inner);
                    }
                }
            }

            Visit(file.Message);
        }

        Console.WriteLine($"{Path.GetFileName(path)}\t{(diffs.Count == 0 ? "IDENTICAL" : "DIFF")}\tobjects={objects}\tstreams={streams}");
        foreach (string d in diffs.Take(12))
        {
            Console.WriteLine("    " + d);
        }

        return diffs.Count == 0 ? 0 : 1;
    }

    private static string FirstDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return $", first at {i}";
            }
        }

        return string.Empty;
    }
}
