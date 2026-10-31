using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Conversion.Stores;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Olm;

// ConvertTool: store-to-store conversions over corpora, checked by reading the result back with the library's own readers.
//   ConvertTool bytes IN FORMAT WORK     MIME sources -> FORMAT (eml|mbox|maildir|emlx) -> read back; every message is byte-identical (an mbox adds a missing final line ending)
//   ConvertTool store IN FORMAT WORK     any store -> FORMAT; items in = out + skipped, folder trees compared, read back; peak working set
//   ConvertTool olm IN                   OLM mail -> EML -> shared view: every field equal or in the loss report
namespace ConvertTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ConvertTool bytes|store IN FORMAT WORK | olm IN");
            return 2;
        }

        return args[0] switch
        {
            "bytes" => Bytes(args[1], Format(args[2]), args[3]),
            "store" => Store(args[1], Format(args[2]), args[3], args.Length > 4 ? args[4] : null),
            "olm" => Olm(args[1]),
            _ => 2,
        };
    }

    private static ConvertFormat Format(string s) => s switch
    {
        "eml" => ConvertFormat.Eml,
        "mbox" => ConvertFormat.Mbox,
        "maildir" => ConvertFormat.Maildir,
        "emlx" => ConvertFormat.AppleMail,
        "ics" => ConvertFormat.Ics,
        "vcf" => ConvertFormat.VCard,
        "tnef" => ConvertFormat.Tnef,
        _ => throw new ArgumentException("format " + s),
    };

    private static string Hash(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b));

    // ---- reading a destination back ----

    private static IEnumerable<(string Folder, byte[] Bytes)> Reread(string dest, ConvertFormat format)
    {
        switch (format)
        {
            case ConvertFormat.Eml:
                foreach (string f in Directory.EnumerateFiles(dest, "*.eml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    string rel = Path.GetRelativePath(dest, Path.GetDirectoryName(f)!).Replace('\\', '/');
                    if (rel.StartsWith("_items", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    yield return (rel == "." ? string.Empty : rel, File.ReadAllBytes(f));
                }

                break;
            case ConvertFormat.Mbox:
                foreach (string f in Directory.EnumerateFiles(dest, "*.mbox", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    string rel = Path.GetRelativePath(dest, f).Replace('\\', '/');
                    string folder = rel.EndsWith("_root.mbox", StringComparison.Ordinal) ? string.Empty : rel[..^5];
                    using MailContainer c = Mail.Open(f);
                    foreach (IMailItem item in c.Enumerate())
                    {
                        yield return (folder, ((MimeMailItem)item).Entity.ToArray());
                    }
                }

                break;
            default:
                using (MailContainer c = Mail.Open(dest))
                {
                    foreach (IMailItem item in c.Enumerate())
                    {
                        // Maildir++ names a subfolder ".Parent.Child"; an Apple Mail bundle names its folders by directory.
                        string folder = item.Provenance.Folder ?? string.Empty;
                        yield return (format == ConvertFormat.Maildir ? folder.Replace('.', '/') : folder, ((MimeMailItem)item).Entity.ToArray());
                    }
                }

                break;
        }
    }

    private static string Wipe(string work)
    {
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        Directory.CreateDirectory(work);
        return Path.Combine(work, "out");
    }

    // ---- bytes ----

    private static int Bytes(string input, ConvertFormat format, string work)
    {
        string dest = Wipe(work);
        var sw = Stopwatch.StartNew();
        var conv = new StoreConverter();
        // The sink sees every converted message; its hash is what must come back out of the destination.
        var seen = new Dictionary<string, int>();
        StoreConversionReport r = Task.Run(async () =>
        {
            await using IStoreSink inner = StoreSinks.Create(format, dest);
            return await conv.ConvertAsync(input, format, new HashingSink(inner, seen, format), null, CancellationToken.None);
        }).GetAwaiter().GetResult();
        long identical = 0, changed = 0, back = 0;
        foreach ((string _, byte[] bytes) in Reread(dest, format))
        {
            back++;
            string h = Hash(bytes);
            if (seen.TryGetValue(h, out int n) && n > 0)
            {
                seen[h] = n - 1;
                identical++;
            }
            else
            {
                changed++;
            }
        }

        Console.WriteLine($"{input} -> {format}: read {r.ItemsRead}, written {r.ItemsWritten}, skipped {r.ItemsSkipped} [{string.Join(", ", r.SkippedByReason.Select(p => p.Key + " " + p.Value))}], errors {r.Errors.Count}");
        Console.WriteLine($"  read back {back}: byte-identical to what was written {identical}, changed {changed}; {sw.Elapsed.TotalSeconds:F1}s");
        if (HashingSink.RawChecked > 0)
        {
            Console.WriteLine($"  source files compared with what the sink received: {HashingSink.RawChecked}, different {HashingSink.RawDiffers}");
            HashingSink.RawChecked = 0;
            changed += HashingSink.RawDiffers;
            HashingSink.RawDiffers = 0;
        }
        foreach (LossTotal t in r.Losses.Take(8))
        {
            Console.WriteLine($"  {t.Kind} {t.Code} ({t.Family}) in {t.Items} items");
        }

        return changed == 0 ? 0 : 1;
    }

    // Passes entries through and records the hash of every message's bytes as the sink received them.
    private sealed class HashingSink(IStoreSink inner, Dictionary<string, int> seen, ConvertFormat format) : IStoreSink
    {
        public static long RawChecked;
        public static long RawDiffers;

        public async ValueTask<string?> WriteAsync(StoreEntry entry, CancellationToken cancellationToken)
        {
            string? where = await inner.WriteAsync(entry, cancellationToken);

            // Messages that live in a file of their own: what the sink got is the file's bytes (the message range of an .emlx).
            if (entry.Item is MimeMailItem { Provenance: { Format: MailFormat.Eml or MailFormat.Emlx or MailFormat.MaildirFolder or MailFormat.MaildirRoot or MailFormat.AppleMailBundle, Location: { } loc, Length: >= 0 } p } && File.Exists(loc))
            {
                using FileStream fs = File.OpenRead(loc);
                byte[] raw = new byte[p.Length];
                fs.Seek(p.Offset, SeekOrigin.Begin);
                fs.ReadExactly(raw);
                RawChecked++;
                RawDiffers += raw.AsSpan().SequenceEqual(entry.Content.Span) ? 0 : 1;
            }

            if (entry.Kind == PayloadKind.Message)
            {
                // What the destination holds: an mbox cannot end a message without a line ending.
                ReadOnlySpan<byte> b = entry.Content.Span;
                byte[] expected = format == ConvertFormat.Mbox && b.Length > 0 && b[^1] != (byte)'\n' ? [.. b, (byte)'\n'] : b.ToArray();
                string h = Hash(expected);
                seen[h] = seen.GetValueOrDefault(h) + 1;
            }

            return where;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- store ----

    private static int Store(string input, ConvertFormat format, string work, string? dump)
    {
        string dest = Wipe(work);
        var sw = Stopwatch.StartNew();
        var conv = new StoreConverter();
        StoreConversionReport r = conv.ConvertAsync(input, format, dest, null, CancellationToken.None).GetAwaiter().GetResult();
        double secs = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{input} -> {format}: read {r.ItemsRead}, written {r.ItemsWritten}, skipped {r.ItemsSkipped} [{string.Join(", ", r.SkippedByReason.Select(p => p.Key + " " + p.Value))}], with content loss {r.ItemsWithContentLoss}, errors {r.Errors.Count}; in = out + skipped: {(r.ItemsRead == r.ItemsWritten + r.ItemsSkipped ? "yes" : "NO")}");
        Console.WriteLine($"  {secs:F1}s, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB");
        int bad = 0;
        if (r.ItemsWritten > 0 && format is ConvertFormat.Eml or ConvertFormat.Mbox or ConvertFormat.Maildir or ConvertFormat.AppleMail)
        {
            // Folder trees: messages per folder in the destination against what the source folder held that was written as mail.
            var got = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach ((string folder, byte[] _) in Reread(dest, format))
            {
                got[folder] = got.GetValueOrDefault(folder) + 1;
            }

            if (dump is not null)
            {
                File.WriteAllLines(dump, got.Select(p => p.Key + "\t" + p.Value.ToString(CultureInfo.InvariantCulture)));
            }

            long mailBack = got.Values.Sum();
            Console.WriteLine($"  read back {mailBack} messages in {got.Count} folders (the rest of the written items are calendar and contact files beside the store)");
            long side = Directory.Exists(Path.Combine(dest, "_items")) ? Directory.EnumerateFiles(Path.Combine(dest, "_items"), "*", SearchOption.AllDirectories).LongCount() : 0;
            Console.WriteLine($"  side files {side}; messages + side files = {mailBack + side}, written {r.ItemsWritten}: {(mailBack + side == r.ItemsWritten ? "match" : "MISMATCH")}");
            if (mailBack + side != r.ItemsWritten)
            {
                bad++;
            }

            foreach ((string folder, long n) in got)
            {
                long written = r.Folders.Where(p => Same(p.Key, folder, format)).Sum(p => p.Value.Written);
                string sideRoot = Path.Combine(dest, "_items");
                long sideInFolder = side == 0 ? 0 : Directory.EnumerateFiles(sideRoot, "*", SearchOption.AllDirectories).Count(f => Same(RelFolder(sideRoot, f), folder, format));
                if (n + sideInFolder != written)
                {
                    Console.WriteLine($"  folder '{folder}': {n} messages + {sideInFolder} side files, source says {written} written");
                    bad++;
                }
            }

            Console.WriteLine($"  folders in source {r.Folders.Count}, in destination {got.Count}; folder disagreements {bad}");
        }

        foreach (LossTotal t in r.Losses.Where(t => t.Kind != RecursiveAI.OneMail.Conversion.LossKind.Internal).Take(12))
        {
            Console.WriteLine($"  {t.Kind} {t.Code} ({t.Family}) in {t.Items} items, e.g. {t.Example}");
        }

        return bad == 0 && r.ItemsRead == r.ItemsWritten + r.ItemsSkipped ? 0 : 1;
    }

    private static string RelFolder(string root, string file)
    {
        string rel = Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace('\\', '/');
        return rel == "." ? string.Empty : rel;
    }

    // Folder names are compared as the destination spells them: unsafe characters replaced.
    private static bool Same(string source, string dest, ConvertFormat format) => string.Equals(SafeName(source, format), dest, StringComparison.Ordinal);

    private static string SafeName(string folder, ConvertFormat format) => folder.Length == 0 ? string.Empty : string.Join('/', folder.Split('/').Select(part =>
    {
        string p = format == ConvertFormat.Maildir ? part.Replace('.', '_') : part;
        string safe = new string([.. p.Select(c => c < 0x20 || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c)]).TrimEnd('.', ' ');
        return safe.Length == 0 || safe is "." or ".." ? "_" : safe;
    }));

    // ---- olm ----

    private static int Olm(string input)
    {
        long items = 0, fields = 0, equal = 0, reported = 0, silent = 0;
        var silentBy = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using MailContainer c = Mail.Open(input);
        foreach (IMailItem it in c.Enumerate())
        {
            if (it is not OlmItem { OlmKind: OlmItemKind.Email } olm)
            {
                continue;
            }

            items++;
            var r = Mail.Convert(olm, ConvertFormat.Eml);
            IMailItem back = Mail.Parse(r.Value!);
            HashSet<string> rep = [.. r.Report.Select(e => e.Code + "|" + e.Name)];

            void Check(string field, bool ok, string? lossKey = null, string? element = null)
            {
                fields++;
                if (ok)
                {
                    equal++;
                }
                else if ((lossKey is not null && rep.Any(x => x.StartsWith(lossKey, StringComparison.Ordinal))) || (element is not null && rep.Contains("OlmFieldDiffersFromSource|" + element)))
                {
                    reported++;
                }
                else
                {
                    silent++;
                    Console.Error.WriteLine($"  silent {field}: item '{olm.Subject}' folder {olm.Folder} source={(olm.MimeSource is null ? "none" : "present")}");
                    if (Environment.GetEnvironmentVariable("CONVERT_TOOL_VERBOSE") == "1")
                    {
                        Console.Error.WriteLine("    raw: " + Encoding.UTF8.GetString(r.Value!).Split("\r\n")[..12].FirstOrDefault(l => l.StartsWith("Subject", StringComparison.Ordinal)));
                        Console.Error.WriteLine($"    subject view [{olm.Subject}] back [{back.Subject}] topic [{olm.ConversationTopic}] [{back.ConversationTopic}]");
                        Console.Error.WriteLine($"    view: From [{string.Join("; ", olm.From)}] To [{string.Join("; ", olm.ToRecipients)}] Cc [{string.Join("; ", olm.Cc)}]\n    back: From [{string.Join("; ", back.From)}] To [{string.Join("; ", back.ToRecipients)}] Cc [{string.Join("; ", back.Cc)}]");
                    }

                    silentBy[field] = silentBy.GetValueOrDefault(field) + 1;
                }
            }

            // Only real addr-specs are written; the others are reported (AddressUnresolved) and are not part of the comparison.
            static bool Valid(MailAddress a) => a.Address.Contains('@', StringComparison.Ordinal) && !a.Address.Any(c => char.IsWhiteSpace(c) || "<>,;()[]\\\"".Contains(c, StringComparison.Ordinal));
            void Unresolved(string header, IEnumerable<MailAddress> l) => Check(header + " unresolved addresses reported", !l.Any(a => !Valid(a)) || rep.Contains("AddressUnresolved|" + header));
            string Addr(IEnumerable<MailAddress> l) => string.Join(",", l.Where(a => Valid(a)).Select(a => a.Address.ToLowerInvariant()));
            Check("Subject", (olm.Subject ?? string.Empty) == (back.Subject ?? string.Empty), element: "OPFMessageCopySubject");
            Check("From", Addr(olm.From) == Addr(back.From), element: "OPFMessageCopyFromAddresses");
            Check("To", Addr(olm.ToRecipients) == Addr(back.ToRecipients), element: "OPFMessageCopyToAddresses");
            Check("Cc", Addr(olm.Cc) == Addr(back.Cc), element: "OPFMessageCopyCCAddresses");
            Check("Bcc", Addr(olm.Bcc) == Addr(back.Bcc), "BccNotWritten");
            Check("ReplyTo", Addr(olm.ReplyTo) == Addr(back.ReplyTo));
            Unresolved("From", olm.From);
            Unresolved("To", olm.ToRecipients);
            Unresolved("Cc", olm.Cc);
            Unresolved("Reply-To", olm.ReplyTo);
            Check("MessageId", olm.MessageId == back.MessageId, element: "OPFMessageCopyMessageID");
            Check("InReplyTo", string.Join(' ', olm.InReplyTo) == string.Join(' ', back.InReplyTo));
            Check("References", string.Join(' ', olm.References) == string.Join(' ', back.References));
            Check("Sent", olm.Sent?.Value.ToUniversalTime().ToUnixTimeSeconds() == back.Sent?.Value.ToUniversalTime().ToUnixTimeSeconds(), element: "OPFMessageCopySentTime");
            Check("ConversationTopic", olm.ConversationTopic == back.ConversationTopic, element: "OPFMessageCopyThreadTopic");
            Check("ConversationIndex", olm.ConversationIndex.Span.SequenceEqual(back.ConversationIndex.Span));
            Check("TextBody", Norm(olm.TextBody?.ReadText()) == Norm(back.TextBody?.ReadText()), element: "OPFMessageCopyBody");
            Check("HtmlBody", Norm(olm.HtmlBody?.ReadText()) == Norm(back.HtmlBody?.ReadText()), element: "OPFMessageCopyHTMLBody");
            Check("Attachments", olm.Attachments.Count == back.Attachments.Count && olm.Attachments.Zip(back.Attachments).All(p => p.First.FileName == p.Second.FileName && Digest(p.First) == Digest(p.Second)));
        }

        Console.WriteLine($"olm mail items {items}; fields compared {fields}: equal {equal}, differ and in the loss report {reported}, silent {silent}");
        foreach ((string k, int v) in silentBy)
        {
            Console.WriteLine($"  silent {k}\t{v}");
        }

        return silent == 0 ? 0 : 1;

        static string Norm(string? s) => (s ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        static string Digest(IMailAttachment a)
        {
            using Stream s = a.Open();
            return Convert.ToHexString(SHA256.HashData(s));
        }
    }
}
