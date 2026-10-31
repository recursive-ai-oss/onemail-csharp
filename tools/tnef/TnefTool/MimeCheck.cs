using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Tnef;

namespace TnefTool;

/// <summary>
/// Wraps TNEF streams in MIME messages, converts them to MAPI and reads them through the shared view, and checks that the merge holds
/// what the TNEF reader gives except where the precedence rules apply (each such case must be in the loss report).
/// </summary>
internal static class MimeCheck
{
    private sealed class Counts
    {
        public int Messages;
        public int Merged;
        public int Refused;
        public int Properties;
        public int Carried;
        public int Reported;
        public int Derived;
        public int Unexplained;
        public int Attachments;
        public int AttachmentMismatch;
        public int Recipients;
        public int CrLf;
        public readonly SortedDictionary<string, int> ReportedBy = [];
    }

    // Variants: the shape Outlook-made mail has, a bare multipart (nothing from the headers), a single-part message, and one with HTML from MIME.
    private static readonly string[] Variants = ["outlook", "bare", "single", "html"];

    public static int Run(string outDir, string[] files)
    {
        Directory.CreateDirectory(outDir);
        var c = new Counts();
        var view = new List<string>();
        foreach (string f in files)
        {
            byte[] tnef = File.ReadAllBytes(f);
            string stem = Path.GetFileNameWithoutExtension(f);
            foreach (string variant in Variants)
            {
                byte[] eml = Wrap(tnef, variant);
                string name = stem + "." + variant + ".eml";
                File.WriteAllBytes(Path.Combine(outDir, name), eml);
                Check(name, tnef, eml, variant, c, view);
            }
        }

        File.WriteAllLines(Path.Combine(outDir, "view.jsonl"), view);
        Console.WriteLine($"messages={c.Messages} merged={c.Merged} refused={c.Refused} properties={c.Properties} carried={c.Carried} reported={c.Reported} derived={c.Derived} " +
            $"attachments={c.Attachments} attachmentMismatch={c.AttachmentMismatch} recipients={c.Recipients} unpackedWithTrailingCrLf={c.CrLf} unexplained={c.Unexplained}");
        foreach ((string key, int n) in c.ReportedBy)
        {
            Console.WriteLine($"  reported {key} x{n}");
        }

        return c.Unexplained + c.AttachmentMismatch == 0 ? 0 : 1;
    }

    /// <summary>
    /// Converts and views real messages that hold a TNEF part (a list of file names, one per line) and reports what happened to each part. Also
    /// writes the messages' view as JSON lines beside the output directory's other files, for the oracle comparison.
    /// </summary>
    public static int Scan(string outDir, string listFile)
    {
        Directory.CreateDirectory(outDir);
        int messages = 0, parts = 0, merged = 0, refused = 0, convertErrors = 0, attachments = 0, mismatched = 0;
        var refusals = new SortedDictionary<string, int>();
        var view = new List<string>();
        foreach (string path in File.ReadLines(listFile).Where(l => l.Length > 0))
        {
            messages++;
            byte[] eml = File.ReadAllBytes(path);
            string name = "scan" + messages.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".eml";
            File.WriteAllBytes(Path.Combine(outDir, name), eml);
            try
            {
                MimeEntity root = MimeEntity.Parse(eml);
                ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(root);
                MimeMailItem item = MimeMailItem.Parse(eml);
                int tnefParts = root.EnumerateTnefParts().Count();
                parts += tnefParts;
                int unpacked = item.TnefParts.Count;
                merged += unpacked;
                refused += tnefParts - unpacked;
                foreach (LossEntry e in r.Report.Where(e => e.Code == "TnefNotUsed"))
                {
                    refusals[e.Reason] = refusals.GetValueOrDefault(e.Reason) + 1;
                }

                // the conversion and the view must agree on what the attachments are
                string[] fromView = item.Attachments.Select(a => a.FileName ?? string.Empty).ToArray();
                string[] fromMapi = r.Value.Attachments.Select(a => a.FileName ?? string.Empty).ToArray();
                attachments += fromView.Length;
                if (!fromView.Order().SequenceEqual(fromMapi.Order()))
                {
                    mismatched++;
                    Console.WriteLine($"MISMATCH {path}: view [{string.Join(", ", fromView)}] vs converted [{string.Join(", ", fromMapi)}]");
                }

                var list = new List<object>();
                foreach (IMailAttachment a in item.Attachments)
                {
                    using Stream s = a.Open();
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    list.Add(new { name = a.FileName, size = (long?)ms.Length, sha256 = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant(), embeddedSize = (int?)null, embeddedSha256 = (string?)null });
                }

                view.Add(JsonSerializer.Serialize(new { file = name, source = path, attachments = list }));
            }
            catch (Exception e)
            {
                convertErrors++;
                Console.WriteLine($"ERROR {path}: {e.GetType().Name}: {e.Message}");
            }
        }

        File.WriteAllLines(Path.Combine(outDir, "view.jsonl"), view);
        Console.WriteLine($"messages={messages} tnefParts={parts} unpacked={merged} notUnpacked={refused} attachmentsInView={attachments} viewVsConversionMismatch={mismatched} errors={convertErrors}");
        foreach ((string why, int n) in refusals)
        {
            Console.WriteLine($"  not unpacked x{n}: {why}");
        }

        return convertErrors + mismatched == 0 ? 0 : 1;
    }

    public static void Parts(string path)
    {
        MimeEntity root = MimeEntity.Parse(File.ReadAllBytes(path));
        Console.WriteLine(Path.GetFileName(path) + ": " + root.MediaType);
        Walk(root, 1);

        static void Walk(MimeEntity e, int depth)
        {
            string pad = new(' ', depth * 2);
            switch (e)
            {
                case MimeMultipart m:
                    Console.WriteLine($"{pad}{m.MediaType} ({m.Children.Count} children)");
                    foreach (MimeEntity c in m.Children)
                    {
                        Walk(c, depth + 1);
                    }

                    break;
                case MimePart p:
                    byte[] data = p.GetDecodedContent();
                    string head = Convert.ToHexString(data.AsSpan(0, Math.Min(6, data.Length)));
                    Console.WriteLine($"{pad}{p.MediaType} name={p.FileName} decoded={data.Length} head={head} isTnef={p.IsTnef()} tnefDefects={string.Join(",", TnefMessage.Read(data).TnefDefects.Select(d => d.Kind))}");
                    break;
                default:
                    Console.WriteLine($"{pad}{e.MediaType}");
                    break;
            }
        }
    }

    private static byte[] Wrap(byte[] tnef, string variant)
    {
        TnefMessage t = TnefMessage.Read(tnef);
        var sb = new StringBuilder();
        string b64 = Convert.ToBase64String(tnef, Base64FormattingOptions.InsertLineBreaks);
        string correlator = t.Properties.Get(MapiPid.TnefCorrelationKey, MapiType.Binary) is { } k ? "X-MS-TNEF-Correlator: " + Encoding.ASCII.GetString(k.GetBytes().Span).TrimEnd('\0') + "\r\n" : string.Empty;
        string envelope = "From: Alice Example <alice@example.com>\r\nTo: Bob Example <bob@example.com>\r\nSubject: RE: wrapped tnef\r\nDate: Mon, 02 Jan 2023 10:00:00 +0000\r\nMessage-ID: <wrap@example.com>\r\n";
        string tnefPart = "Content-Type: application/ms-tnef; name=\"winmail.dat\"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"winmail.dat\"\r\n\r\n" + b64 + "\r\n";
        switch (variant)
        {
            case "outlook":
                sb.Append(envelope).Append("MIME-Version: 1.0\r\n").Append(correlator).Append("Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n");
                sb.Append("--b1\r\nContent-Type: text/plain; charset=us-ascii\r\n\r\nplain text of the message\r\n--b1\r\n").Append(tnefPart).Append("--b1--\r\n");
                break;
            case "bare":
                sb.Append("MIME-Version: 1.0\r\n").Append(correlator).Append("Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n");
                sb.Append("--b1\r\n").Append(tnefPart).Append("--b1--\r\n");
                break;
            case "single":
                sb.Append(envelope).Append("MIME-Version: 1.0\r\n").Append(correlator).Append(tnefPart);
                break;
            default:
                sb.Append(envelope).Append("MIME-Version: 1.0\r\n").Append(correlator).Append("Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n");
                sb.Append("--b1\r\nContent-Type: multipart/alternative; boundary=\"b2\"\r\n\r\n--b2\r\nContent-Type: text/plain\r\n\r\nplain\r\n--b2\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>html from mime</p>\r\n--b2--\r\n");
                sb.Append("--b1\r\n").Append(tnefPart).Append("--b1--\r\n");
                break;
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // What the part rules say should happen to the stream: the harness states it separately from the library's code.
    private static bool ExpectMerged(byte[] tnef)
    {
        TnefMessage t = TnefMessage.Read(tnef);
        bool fatal = t.TnefDefects.Any(d => d.Kind is TnefDefectKind.NotTnef or TnefDefectKind.UnexpectedVersion or TnefDefectKind.AttributeTruncated or TnefDefectKind.ChecksumMismatch or TnefDefectKind.LimitReached);
        return !fatal;
    }

    private static string Label(MapiProperty p) => p.Name is { } n ? n.ToString() : MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();

    private static bool Equal(MapiProperty a, MapiProperty b)
    {
        if (a.Type == b.Type && a.ValueCount == b.ValueCount)
        {
            for (int i = 0; i < a.ValueCount; i++)
            {
                if (!a.GetBytes(i).Span.SequenceEqual(b.GetBytes(i).Span))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static void Check(string name, byte[] tnef, byte[] eml, string variant, Counts c, List<string> view)
    {
        c.Messages++;
        bool expectMerged = ExpectMerged(tnef);
        MimeEntity root = MimeEntity.Parse(eml);
        ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(root);
        MapiMessage m = r.Value;
        TnefMessage fresh = TnefMessage.Read(tnef);
        MimeMailItem item = MimeMailItem.Parse(eml);

        // ---- the view
        var names = new List<object>();
        foreach (IMailAttachment a in item.Attachments)
        {
            if (a.Embedded is MapiMailItem { Message: TnefMessage inner })
            {
                byte[] nested = new byte[inner.Document.Source.Length];
                inner.Document.Source.ReadFully(0, nested);
                names.Add(new { name = a.FileName, size = (long?)null, sha256 = (string?)null, embeddedSize = nested.Length, embeddedSha256 = Convert.ToHexString(SHA256.HashData(nested)).ToLowerInvariant() });
            }
            else
            {
                using Stream s = a.Open();
                var ms = new MemoryStream();
                s.CopyTo(ms);
                names.Add(new { name = a.FileName, size = (long?)ms.Length, sha256 = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant(), embeddedSize = (int?)null, embeddedSha256 = (string?)null });
            }
        }

        view.Add(JsonSerializer.Serialize(new { file = name, tnef = name[..name.IndexOf('.', StringComparison.Ordinal)], variant, merged = expectMerged, attachments = names }));

        if (!expectMerged)
        {
            c.Refused++;
            bool ok = item.TnefParts.Count == 0 && item.Attachments.Count == 1 && item.Attachments[0].FileName == "winmail.dat"
                && m.Attachments.Count == 1 && m.Attachments[0].MimeType == "application/octet-stream" && r.Report.Any(e => e.Code == "TnefNotUsed");
            if (!ok)
            {
                c.Unexplained++;
                Console.WriteLine($"UNEXPLAINED {name}: refused TNEF part was not kept as an octet-stream attachment");
            }

            return;
        }

        c.Merged++;
        bool endsInCrLf = tnef.Length >= 2 && tnef[^2] == 0x0D && tnef[^1] == 0x0A && fresh.Document.TrailingLength == 2;
        if (endsInCrLf != r.Report.Any(e => e.Code == "TnefTrailingCrLfIgnored") || endsInCrLf != item.Defects.Any(d => d.Code == "AmbiguousTnefTrailingCrLf"))
        {
            c.Unexplained++;
            Console.WriteLine($"UNEXPLAINED {name}: CR LF at the end of the stream is not reported (or reported without it)");
        }

        if (endsInCrLf)
        {
            c.CrLf++;
        }

        if (item.TnefParts.Count != 1 || !r.Report.Any(e => e.Code == "TnefPartUnpacked"))
        {
            c.Unexplained++;
            Console.WriteLine($"UNEXPLAINED {name}: part not unpacked by the view or the conversion");
        }

        // ---- properties
        foreach (MapiProperty p in fresh.Properties)
        {
            c.Properties++;
            if (p.Id is MapiPid.InternetCodepage or MapiPid.NativeBody)
            {
                c.Derived++;
                continue;
            }

            MapiProperty? got = p.Name is { } n ? m.Properties.Get(n, p.Type) : m.Properties.Get(new MapiTag(p.Id, p.Type));
            string label = Label(p);
            if (got is not null && Equal(got, p))
            {
                c.Carried++;
            }
            else if (r.Report.Any(e => e.Name == label && e.Path.Length == 0 && e.Code is "TnefPropertyIgnored" or "TnefPropertyWithoutName" or "TnefCodePageDiffers"))
            {
                c.Reported++;
                c.ReportedBy[variant + " " + label] = c.ReportedBy.GetValueOrDefault(variant + " " + label) + 1;
            }
            else if (p.Id == MapiPid.MessageClass && r.Report.Any(e => e.Code == "TnefMessageClassUsed"))
            {
                c.Reported++;
            }
            else
            {
                c.Unexplained++;
                Console.WriteLine($"UNEXPLAINED {name}: {label} neither carried nor reported");
            }
        }

        // ---- attachments: the merged ones are the TNEF's, with the same properties (AttachNumber is renumbered by the conversion)
        IReadOnlyList<MapiAttachment> want = fresh.Attachments;
        c.Attachments += want.Count;
        if (m.Attachments.Count != want.Count || item.Attachments.Count != want.Count)
        {
            c.AttachmentMismatch++;
            Console.WriteLine($"MISMATCH {name}: {m.Attachments.Count} converted / {item.Attachments.Count} in the view vs {want.Count} in the TNEF");
        }
        else
        {
            for (int i = 0; i < want.Count; i++)
            {
                foreach (MapiProperty p in want[i].Properties)
                {
                    if (p.Id == MapiPid.AttachNumber)
                    {
                        continue;
                    }

                    MapiProperty? got = m.Attachments[i].Properties.Get(p.Tag);
                    if (got is null || !Equal(got, p))
                    {
                        c.AttachmentMismatch++;
                        Console.WriteLine($"MISMATCH {name}: attachment {i} property {Label(p)}");
                    }
                }
            }
        }

        // ---- recipients: the headers' when there are any, else the TNEF's
        bool headerRecipients = variant is not "bare";
        c.Recipients += m.Recipients.Count;
        int expectRecipients = headerRecipients ? 1 : fresh.Recipients.Count;
        if (m.Recipients.Count != expectRecipients)
        {
            c.Unexplained++;
            Console.WriteLine($"UNEXPLAINED {name}: {m.Recipients.Count} recipients, expected {expectRecipients}");
        }

        // ---- bodies: MIME HTML wins; otherwise the TNEF's HTML or RTF-derived HTML shows in the view
        if (variant == "html" && item.HtmlBody?.ReadText() != "<p>html from mime</p>")
        {
            c.Unexplained++;
            Console.WriteLine($"UNEXPLAINED {name}: MIME HTML body not preferred");
        }

        if (fresh.Properties.Get(MapiPid.RtfCompressed, MapiType.Binary) is not null && item.RtfBody is null)
        {
            c.Unexplained++;
            Console.WriteLine($"UNEXPLAINED {name}: the TNEF's RTF body is not in the view");
        }
    }
}
