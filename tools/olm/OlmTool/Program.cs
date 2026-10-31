using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Stores.Olm;

namespace OneMail.Tools.Olm;

// olmtool dump <file.olm>          one JSON line per item, in the form tools/olm/compare.py reads
// olmtool stats <file.olm>         counts per kind, unmapped elements per kind, defects, folders
// olmtool memory <file.olm>        enumerates the whole archive reading every field, then prints peak working set and allocations
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "zipdir")
        {
            // The zip reader alone: opens the directory and walks every entry, to see what the directory costs without OneMail on top.
            using var fs = File.OpenRead(args[1]);
            using var zip = new RecursiveAI.OneArchive.Zip.ZipReader(fs, new RecursiveAI.OneArchive.Zip.ZipReaderOptions
            {
                Limits = new RecursiveAI.OneArchive.ArchiveLimits { MaxEntryCount = int.MaxValue, MaxTotalSize = long.MaxValue },
                MaxCentralDirectorySize = 4L << 30,
            }, leaveOpen: false);
            long entries = 0;
            while (zip.GetNextEntry() is not null)
            {
                entries++;
            }

            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            Console.WriteLine($"zip reader alone: {entries} entries, peak working set {proc.PeakWorkingSet64 / (1024 * 1024)} MiB");
            return 0;
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: olmtool dump|stats|memory <file.olm>");
            return 2;
        }

        using OlmArchive olm = OlmArchive.Open(args[1]);
        return args[0] switch
        {
            "dump" => Dump(olm),
            "stats" => Stats(olm),
            "memory" => Memory(olm),
            _ => 2,
        };
    }

    private static int Dump(OlmArchive olm)
    {
        using Stream stdout = Console.OpenStandardOutput();
        foreach (OlmItem it in olm.Enumerate())
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteString("olmKind", it.OlmKind.ToString());
                w.WriteString("entry", it.Entry);
                w.WriteString("folder", it.Folder);
                w.WriteString("subject", it.Subject);
                Addrs(w, "from", it.From);
                Addrs(w, "to", it.ToRecipients);
                Addrs(w, "cc", it.Cc);
                Addrs(w, "bcc", it.Bcc);
                Addrs(w, "sender", it.Sender is { } s ? [s] : []);
                w.WriteString("sent", it.TryGetDate(MailDateSource.ClientSubmit, out MailDate sd) ? sd.Value.ToString("yyyy-MM-dd'T'HH:mm:ss") : null);
                w.WriteString("received", it.TryGetDate(MailDateSource.Delivery, out MailDate rd) ? rd.Value.ToString("yyyy-MM-dd'T'HH:mm:ss") : null);
                w.WriteString("created", it.TryGetDate(MailDateSource.Creation, out MailDate cd) ? cd.Value.ToString("yyyy-MM-dd'T'HH:mm:ss") : null);
                w.WriteString("messageId", it.MessageId);
                w.WriteString("text", it.TextBody?.ReadText());
                w.WriteString("html", it.HtmlBody?.ReadText());
                w.WriteStartArray("attachments");
                foreach (IMailAttachment a in it.Attachments)
                {
                    using Stream s2 = a.Open();
                    using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buf = new byte[81920];
                    long n = 0;
                    int got;
                    while ((got = s2.Read(buf, 0, buf.Length)) > 0)
                    {
                        h.AppendData(buf, 0, got);
                        n += got;
                    }

                    w.WriteStartObject();
                    w.WriteString("name", a.FileName);
                    w.WriteString("type", a.ContentType);
                    w.WriteString("cid", a.ContentId);
                    w.WriteNumber("size", n);
                    w.WriteString("sha256", Convert.ToHexStringLower(h.GetHashAndReset()));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartArray("defects");
                foreach (OlmDefect d in it.OlmDefects)
                {
                    w.WriteStringValue(d.Kind.ToString());
                }

                w.WriteEndArray();
                w.WriteNumber("unmapped", it.UnmappedElements().Count);
                w.WriteStartObject("raw");
                OlmElement r = it.Element;
                foreach ((string key, string element) in new[]
                {
                    ("location", "OPFCalendarEventCopyLocation"), ("uuid", "OPFCalendarEventCopyUUID"), ("start", "OPFCalendarEventCopyStartTime"),
                    ("end", "OPFCalendarEventCopyEndTime"), ("first", "OPFContactCopyFirstName"), ("last", "OPFContactCopyLastName"), ("due", "OPFTaskCopyDueDateTime"),
                })
                {
                    if (r.ChildText(element) is { } v)
                    {
                        w.WriteString(key, v);
                    }
                }

                if (r.Child("OPFGroupCopyMemberList") is { } members)
                {
                    w.WriteStartArray("members");
                    foreach (OlmElement a in members.Children)
                    {
                        w.WriteStartArray();
                        w.WriteStringValue(a.Attribute("OPFContactEmailAddressName") is { Length: > 0 } n ? n : null);
                        w.WriteStringValue(a.Attribute("OPFContactEmailAddressAddress") is { Length: > 0 } ad ? ad : null);
                        w.WriteEndArray();
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            stdout.Write(ms.ToArray());
            stdout.WriteByte((byte)'\n');
        }

        foreach (OlmDefect d in olm.Defects)
        {
            Console.Error.WriteLine("archive defect: " + d);
        }

        return 0;
    }

    private static void Addrs(Utf8JsonWriter w, string name, IReadOnlyList<MailAddress> list)
    {
        w.WriteStartArray(name);
        foreach (MailAddress a in list)
        {
            w.WriteStartArray();
            w.WriteStringValue(a.Name);
            w.WriteStringValue(a.Address);
            w.WriteEndArray();
        }

        w.WriteEndArray();
    }

    private static int Stats(OlmArchive olm)
    {
        var count = new SortedDictionary<string, int>();
        var unmapped = new SortedDictionary<string, SortedDictionary<string, int>>();
        var defects = new SortedDictionary<string, int>();
        int total = 0;
        foreach (OlmItem it in olm.Enumerate())
        {
            total++;
            string k = it.OlmKind.ToString();
            count[k] = count.GetValueOrDefault(k) + 1;
            if (!unmapped.TryGetValue(k, out var u))
            {
                unmapped[k] = u = [];
            }

            foreach (OlmElement e in it.UnmappedElements())
            {
                u[e.Name] = u.GetValueOrDefault(e.Name) + 1;
            }

            foreach (OlmDefect d in it.OlmDefects)
            {
                defects[d.Kind.ToString()] = defects.GetValueOrDefault(d.Kind.ToString()) + 1;
            }
        }

        Console.WriteLine("items " + total);
        foreach (var kv in count)
        {
            int distinct = unmapped.GetValueOrDefault(kv.Key)?.Count ?? 0;
            int occurrences = unmapped.GetValueOrDefault(kv.Key)?.Values.Sum() ?? 0;
            Console.WriteLine($"  {kv.Key}: {kv.Value} items, unmapped element names {distinct}, unmapped occurrences {occurrences}");
        }

        foreach (var kv in defects)
        {
            Console.WriteLine($"  defect {kv.Key}: {kv.Value}");
        }

        foreach (OlmDefect d in olm.Defects)
        {
            Console.WriteLine("  archive defect: " + d);
        }

        Console.WriteLine("folders " + olm.GetFolders().Count + " (" + olm.GetFolders().Count(f => f.IsMail) + " mail), attachment entries " + olm.AttachmentEntryCount);
        foreach (var kv in unmapped)
        {
            Console.WriteLine("  unmapped " + kv.Key + ": " + string.Join(", ", kv.Value.Select(p => p.Key + "x" + p.Value)));
        }

        return 0;
    }

    private static int Memory(OlmArchive olm)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long items = 0;
        long attBytes = 0;
        long peakManaged = 0;
        foreach (OlmItem it in olm.Enumerate())
        {
            items++;
            _ = it.Subject;
            _ = it.From;
            _ = it.ToRecipients;
            _ = it.TextBody?.ReadText();
            _ = it.HtmlBody?.ReadText();
            foreach (IMailAttachment a in it.Attachments)
            {
                using Stream s = a.Open();
                byte[] buf = new byte[81920];
                int got;
                while ((got = s.Read(buf, 0, buf.Length)) > 0)
                {
                    attBytes += got;
                }
            }

            if ((items & 0x3FF) == 0)
            {
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
            }
        }

        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        Console.WriteLine($"items {items}, attachment bytes {attBytes}, allocated {alloc} ({(items == 0 ? 0 : alloc / items)} per item), peak working set {p.PeakWorkingSet64 / (1024 * 1024)} MiB, managed heap sampled max {peakManaged / (1024 * 1024)} MiB, defects {olm.Defects.Count}");
        return 0;
    }
}
