using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Conversion.Stores;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;

// OxcMailTool: converts Outlook items (MSG, PST, OST) to EML and EML back to Outlook items, and counts what carried over.
//   OxcMailTool export IN OUT [--limit N]   writes OUT/NNNNNN.eml and NNNNNN.json (what the source says), checks our own parse of each EML against the source
//   OxcMailTool rt-mapi IN [--limit N]      MAPI -> MIME -> MAPI: mapped properties come back exact, everything else is in the loss report
//   OxcMailTool rt-store IN                 the same through the store-to-store engine (StoreConverter to Eml): what a user of Mail.ConvertAsync gets
//   OxcMailTool rt-mime DIR [--limit N] [--sample N]   MIME -> MAPI -> MIME over a directory of .eml files (recursively)
//   OxcMailTool view IN [--limit N]         how many senders and recipients resolve to SMTP, and with an X.500 address kept
namespace OxcMailTool;

internal static class Program
{
    private static readonly OxcMailExportOptions Export = new() { BoundaryFactory = NextBoundary() };
    private static readonly OxcMailExportOptions ExportTnef = new() { BoundaryFactory = NextBoundary(), Tnef = OxcMailTnefExport.TnefMessage };

    private static Func<string> NextBoundary()
    {
        int n = 0;
        return () => "=_oxc_" + (n++).ToString("x6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: OxcMailTool export|rt-mapi|rt-tnef|rt-mime|view IN [OUT] [--limit N] [--sample N]");
            return 2;
        }

        int limit = int.MaxValue;
        int sample = 1;
        var rest = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--limit")
            {
                limit = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--sample")
            {
                sample = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        return args[0] switch
        {
            "export" => ExportAll(rest[0], rest[1], limit),
            "rt-mapi" => RoundTripMapi(rest[0], limit, Export),
            // MAPI -> MIME with the properties in a TNEF part -> MAPI
            "rt-tnef" => RoundTripMapi(rest[0], limit, ExportTnef),
            "rt-store" => RoundTripStore(rest[0]),
            "rt-mime" => RoundTripMime(rest[0], limit, sample),
            "view" => ViewStats(rest[0], limit),
            _ => 2,
        };
    }

    private static IEnumerable<string> StoreFiles(string input)
    {
        if (File.Exists(input))
        {
            yield return input;
            yield break;
        }

        foreach (string f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            if (e is ".msg" or ".oft" or ".pst" or ".ost")
            {
                yield return f;
            }
        }
    }

    private static IEnumerable<(string Origin, MapiMailItem Item)> Items(string input, int limit)
    {
        int n = 0;
        foreach (string file in StoreFiles(input))
        {
            MailContainer? c = null;
            try
            {
                c = Mail.Open(file);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"skip {file}: {ex.GetType().Name}");
            }

            if (c is null)
            {
                continue;
            }

            using (c)
            {
                int k = 0;
                foreach (IMailItem item in c.Enumerate())
                {
                    if (n >= limit)
                    {
                        yield break;
                    }

                    if (item is MapiMailItem m && m.Kind == MailItemKind.Message)
                    {
                        yield return (Path.GetFileName(file) + "#" + k, m);
                        n++;
                    }

                    k++;
                }
            }
        }
    }

    private static string Sha(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] FoldEol(ReadOnlySpan<byte> b)
    {
        var o = new List<byte>(b.Length);
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i] == '\r')
            {
                o.Add((byte)'\n');
                if (i + 1 < b.Length && b[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                o.Add(b[i]);
            }
        }

        return [.. o];
    }

    private static string Fold(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Addrs(IEnumerable<MailAddress> l) =>
        string.Join(",", l.Where(a => a.Address.Contains('@', StringComparison.Ordinal) && !a.Address.StartsWith('/')).Select(a => a.Address.ToLowerInvariant()).Order(StringComparer.Ordinal));

    // The bytes the exporter writes for a body, as the oracles will hash them.
    private static byte[]? BodyBytes(MailBody? b)
    {
        if (b is null)
        {
            return null;
        }

        // The exporter writes text from strings as UTF-8, and passes HTML bytes through in their own code page.
        if (b.ContentType == "text/plain" || b.Charset is null || b.Charset.StartsWith("utf-16", StringComparison.Ordinal) || b.GetType().Name == "MemoryMailBody")
        {
            return Encoding.UTF8.GetBytes(b.ReadText());
        }

        using Stream s = b.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        byte[] raw = ms.ToArray();
        int n = raw.Length;
        while (n > 0 && raw[n - 1] == 0)
        {
            n--;
        }

        return raw[..n];
    }

    // ---- export ----------------------------------------------------------------------------------------------------------

    private static int ExportAll(string input, string output, int limit)
    {
        Directory.CreateDirectory(Path.Combine(output, "eml"));
        Directory.CreateDirectory(Path.Combine(output, "expected"));
        int count = 0, parsedClean = 0, mismatchItems = 0, withLoss = 0;
        long entries = 0;
        var fieldMismatch = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var lossCodes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var manifest = new StringBuilder();
        var mismatches = new StringBuilder();
        var losses = new StringBuilder();
        foreach ((string origin, MapiMailItem item) in Items(input, limit))
        {
            ConversionResult<MimeEntity> r = new MapiToMimeConverter(Export).Convert(item.Message);
            byte[] eml = r.Value.ToArray();
            string id = (++count).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllBytes(Path.Combine(output, "eml", id + ".eml"), eml);
            manifest.Append(id).Append('\t').Append(origin).Append('\n');
            foreach (LossEntry e in r.Report)
            {
                entries++;
                if (e.Code != "NoInternetMapping" && e.Code != "StoreBookkeeping")
                {
                    losses.Append(id).Append('\t').Append(e.Kind).Append('\t').Append(e.Code).Append('\t').Append(e.Name).Append('\t').Append(e.Path).Append('\n');
                }

                string key = e.Kind + ":" + e.Code;
                lossCodes[key] = lossCodes.GetValueOrDefault(key) + 1;
            }

            if (r.Report.HasContentLoss)
            {
                withLoss++;
            }

            MimeMailItem back = MimeMailItem.Parse(eml);
            // A defect the received headers already had is the sender's, kept on purpose; only the others count against the conversion.
            var inherited = new List<string>();
            if (item.Message.GetString(MapiPid.TransportMessageHeaders) is { Length: > 0 } tblock)
            {
                inherited.AddRange(MimeMailItem.Parse(Encoding.UTF8.GetBytes(tblock.TrimEnd('\r', '\n') + "\r\n\r\n")).Defects.Select(d => d.Code));
            }

            var fresh = new List<string>();
            foreach (MailDefect d in back.Defects)
            {
                if (!inherited.Remove(d.Code))
                {
                    fresh.Add(d.Code);
                }
            }

            if (fresh.Count == 0)
            {
                parsedClean++;
            }
            else
            {
                foreach (string d in fresh)
                {
                    fieldMismatch["new-defect:" + d] = fieldMismatch.GetValueOrDefault("new-defect:" + d) + 1;
                    mismatches.Append(id).Append('\t').Append(origin).Append("\tnew-defect:").Append(d).Append('\n');
                }
            }

            Dictionary<string, object?> expected = Expected(item);
            List<string> bad = Compare(item, back);
            if (bad.Count > 0)
            {
                mismatchItems++;
                foreach (string b in bad)
                {
                    fieldMismatch[b] = fieldMismatch.GetValueOrDefault(b) + 1;
                    mismatches.Append(id).Append('\t').Append(origin).Append('\t').Append(b).Append('\n');
                }

                expected["ourMismatch"] = bad;
            }

            File.WriteAllText(Path.Combine(output, "expected", id + ".json"), JsonSerializer.Serialize(expected));
        }

        File.WriteAllText(Path.Combine(output, "manifest.tsv"), manifest.ToString());
        File.WriteAllText(Path.Combine(output, "mismatch.tsv"), mismatches.ToString());
        File.WriteAllText(Path.Combine(output, "losses.tsv"), losses.ToString());
        Console.WriteLine($"exported {count} items; eml parsed with no new defect in OneMail: {parsedClean}; our parse differs from the source in {mismatchItems}; items with content loss entries: {withLoss}; loss entries {entries}");
        foreach ((string k, int v) in fieldMismatch)
        {
            Console.WriteLine($"  diff {k}\t{v}");
        }

        foreach ((string k, int v) in lossCodes)
        {
            Console.WriteLine($"  loss {k}\t{v}");
        }

        return 0;
    }

    private static Dictionary<string, object?> Expected(MapiMailItem v)
    {
        byte[]? text = BodyBytes(v.TextBody);
        byte[]? html = BodyBytes(v.HtmlBody);
        var atts = new List<object>();
        foreach (IMailAttachment a in v.Attachments)
        {
            if (a.Embedded is not null)
            {
                atts.Add(new { name = a.FileName, embedded = true });
                continue;
            }

            using Stream s = a.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            atts.Add(new { name = a.FileName, size = ms.Length, sha = Sha(ms.ToArray()), cid = a.ContentId });
        }

        return new Dictionary<string, object?>
        {
            ["subject"] = v.Subject,
            ["from"] = Addrs(v.From),
            ["to"] = Addrs(v.ToRecipients),
            ["cc"] = Addrs(v.Cc),
            ["date"] = (v.Sent ?? (v.TryGetDate(MailDateSource.Delivery, out MailDate dd) ? dd : null))?.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["messageId"] = v.MessageId,
            ["textEolSha"] = text is null ? null : Sha(FoldEol(text)),
            ["htmlEolSha"] = html is null ? null : Sha(FoldEol(html)),
            ["attachments"] = atts,
        };
    }

    // Our parse of the exported EML against the source's own view: every field the brief names.
    private static List<string> Compare(MapiMailItem src, MimeMailItem back)
    {
        var bad = new List<string>();
        static string Norm(string? s) => string.Join(' ', (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (Norm(src.Subject) != Norm(back.Subject))
        {
            bad.Add("subject");
        }

        if (Addrs(src.From) != Addrs(back.From))
        {
            bad.Add("from");
        }

        if (Addrs(src.ToRecipients) != Addrs(back.ToRecipients))
        {
            bad.Add("to");
        }

        if (Addrs(src.Cc) != Addrs(back.Cc))
        {
            bad.Add("cc");
        }

        long? sd = (src.Sent ?? (src.TryGetDate(MailDateSource.Delivery, out MailDate dd) ? dd : null))?.Value.UtcDateTime.Ticks / TimeSpan.TicksPerSecond;
        long? bd = back.Sent?.Value.UtcDateTime.Ticks / TimeSpan.TicksPerSecond;
        if (sd != bd)
        {
            bad.Add("date");
        }

        if (src.MessageId != back.MessageId)
        {
            bad.Add("message-id");
        }

        if (src.InReplyTo.Count != back.InReplyTo.Count || src.References.Count != back.References.Count)
        {
            bad.Add("in-reply-to/references");
        }

        if (Fold(src.TextBody?.ReadText() ?? string.Empty).TrimEnd() != Fold(back.TextBody?.ReadText() ?? string.Empty).TrimEnd())
        {
            bad.Add("text-body");
        }

        if (Fold(src.HtmlBody?.ReadText() ?? string.Empty).TrimEnd() != Fold(back.HtmlBody?.ReadText() ?? string.Empty).TrimEnd())
        {
            bad.Add("html-body");
        }

        // Attachments without data (by reference, OLE) are not written and are in the report; the others must come back byte for byte.
        string[] sa = src.Attachments.Where((a, i) => src.Message.Attachments[i].EmbeddedMessage is not null || src.Message.Attachments[i].Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary) is not null).Select(Att).Order(StringComparer.Ordinal).ToArray();
        // An RTF body that encapsulates nothing travels as a text/rtf part, which the view lists beside the attachments.
        string[] ba = back.Attachments.Where(a => !(a.ContentType == "text/rtf" && a.FileName is null)).Select(Att).Order(StringComparer.Ordinal).ToArray();
        if (!sa.SequenceEqual(ba))
        {
            bad.Add("attachments");
            if (Environment.GetEnvironmentVariable("OXC_VERBOSE") is not null)
            {
                Console.Error.WriteLine("  attachments: source " + string.Join(" | ", sa.Select(x => x.Length > 90 ? x[..90] : x)) + " => ours " + string.Join(" | ", ba.Select(x => x.Length > 90 ? x[..90] : x)));
            }
        }

        return bad;
    }

    private static string Att(IMailAttachment a)
    {
        if (a.Embedded is not null)
        {
            return "msg:" + a.FileName;
        }

        using Stream s = a.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return a.FileName + ":" + ms.Length + ":" + Sha(ms.ToArray());
    }

    // ---- MAPI -> MIME -> MAPI ----------------------------------------------------------------------------------------------------------

    private static int RoundTripMapi(string input, int limit, OxcMailExportOptions exportOptions)
    {
        int items = 0, exact = 0, withDiff = 0, silentItems = 0;
        long props = 0, same = 0, typeChanged = 0, explained = 0, silent = 0;
        var silentByName = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach ((string origin, MapiMailItem item) in Items(input, limit))
        {
            items++;
            MapiMessage src = item.Message;
            ConversionResult<MimeEntity> ex = new MapiToMimeConverter(exportOptions).Convert(src);
            MimeEntity mime = MimeEntity.Parse(ex.Value.ToArray());
            ConversionResult<MapiMessage> im = new MimeToMapiConverter().Convert(mime);
            if (exportOptions.Tnef == OxcMailTnefExport.TnefMessage)
            {
                foreach (LossEntry e in ex.Report.Concat(im.Report).Where(e => e.Code is "TnefNotUsed" or "TnefWriterLimit"))
                {
                    Console.Error.WriteLine($"  {origin}: {e.Code} {e.Name}: {e.Reason}");
                }
            }

            var reported = new HashSet<string>(ex.Report.Concat(im.Report).Select(e => e.Name).Concat(ex.Report.Concat(im.Report).Select(e => e.Name + "|" + e.Code)), StringComparer.OrdinalIgnoreCase);
            var (p, s, t, e2, sl, names) = CompareMessage(src, im.Value, reported, string.Empty);
            props += p;
            same += s;
            typeChanged += t;
            explained += e2;
            silent += sl;
            if (sl == 0)
            {
                exact++;
            }
            else
            {
                silentItems++;
                foreach (string n in names)
                {
                    silentByName[n] = silentByName.GetValueOrDefault(n) + 1;
                }
            }

            if (s + t != p)
            {
                withDiff++;
            }

            _ = origin;
        }

        Console.WriteLine($"items {items}; message+recipient+attachment properties compared {props}: exact {same}, same value with another string type {typeChanged}, differ or missing and in the loss report {explained}, silent {silent}");
        Console.WriteLine($"items with no silent difference {exact}, with {silentItems}; items with any difference {withDiff}");
        foreach ((string k, int v) in silentByName.OrderByDescending(x => x.Value).Take(40))
        {
            Console.WriteLine($"  silent {k}\t{v}");
        }

        return silent == 0 ? 0 : 1;
    }

    private static int RoundTripStore(string input)
    {
        var sink = new RoundTripSink();
        var converter = new StoreConverter(new StoreConversionOptions { MapiToMime = Export });
        StoreConversionReport r = converter.ConvertAsync(input, ConvertFormat.Eml, sink, null, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"engine: read {r.ItemsRead}, written {r.ItemsWritten}, skipped {r.ItemsSkipped}; mapi mail items checked {sink.Items}");
        Console.WriteLine($"message+recipient+attachment properties compared {sink.Props}: exact {sink.Same}, same value with another string type {sink.TypeChanged}, differ or missing and in the loss report {sink.Explained}, silent {sink.Silent}");
        Console.WriteLine($"items with no silent difference {sink.Items - sink.SilentItems}, with {sink.SilentItems}");
        foreach ((string k, int v) in sink.SilentByName.OrderByDescending(x => x.Value).Take(40))
        {
            Console.WriteLine($"  silent {k}\t{v}");
        }

        return sink.Silent == 0 ? 0 : 1;
    }

    // Takes the EML the engine produced for each Outlook mail item, reads it back as MAPI and compares with the source item.
    private sealed class RoundTripSink : IStoreSink
    {
        public int Items;
        public int SilentItems;
        public long Props, Same, TypeChanged, Explained, Silent;
        public readonly SortedDictionary<string, int> SilentByName = new(StringComparer.Ordinal);

        public ValueTask<string?> WriteAsync(StoreEntry entry, CancellationToken cancellationToken)
        {
            if (entry.Item is MapiMailItem m && entry.Kind == PayloadKind.Message)
            {
                Items++;
                ConversionResult<MapiMessage> im = new MimeToMapiConverter().Convert(MimeEntity.Parse(entry.Content.ToArray()));
                var reported = new HashSet<string>(entry.Report.Concat(im.Report).Select(e => e.Name).Concat(entry.Report.Concat(im.Report).Select(e => e.Name + "|" + e.Code)), StringComparer.OrdinalIgnoreCase);
                var (p, s, t, e2, sl, names) = CompareMessage(m.Message, im.Value, reported, string.Empty);
                Props += p;
                Same += s;
                TypeChanged += t;
                Explained += e2;
                Silent += sl;
                if (sl > 0)
                {
                    SilentItems++;
                    foreach (string n in names)
                    {
                        SilentByName[n] = SilentByName.GetValueOrDefault(n) + 1;
                    }
                }
            }

            return new ValueTask<string?>((string?)null);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A named property is numbered per message or store, so the same name can carry another ID after a conversion; the name, type and bytes decide.
    private static bool SameValue(MapiProperty p, MapiProperty q)
    {
        if (p.ValueEquals(q))
        {
            return true;
        }

        if (p.Name is null || p.Name != q.Name || p.Type != q.Type || p.ValueCount != q.ValueCount)
        {
            return false;
        }

        for (int i = 0; i < p.ValueCount; i++)
        {
            if (!p.GetBytes(i).Span.SequenceEqual(q.GetBytes(i).Span))
            {
                return false;
            }
        }

        return true;
    }

    private static (long Props, long Same, long TypeChanged, long Explained, long Silent, List<string> SilentNames) CompareMessage(MapiMessage a, MapiMessage b, HashSet<string> reported, string path)
    {
        long props = 0, same = 0, typeChanged = 0, explained = 0, silent = 0;
        var names = new List<string>();

        void One(MapiProperty p, MapiObject other, string scope)
        {
            props++;
            string name = p.Name is { } n ? n.ToString() : MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();
            MapiProperty? q = p.Name is { } nn ? other.Properties.Get(nn) : other.Properties.Get(p.Tag);

            // Header names are case-insensitive (RFC 5322 §1.2.2), and stores differ in the case they keep.
            if (q is null && p.Name is { IsString: true } hn)
            {
                q = other.Properties.FirstOrDefault(x => x.Name is { IsString: true } on && on.PropertySet == hn.PropertySet && string.Equals(on.Name, hn.Name, StringComparison.OrdinalIgnoreCase));
            }
            if (q is null && p.Name is null && IsString(p.Type))
            {
                q = other.Properties.Get(p.Id, MapiType.UnicodeString, MapiType.String8);
            }

            if (q is not null && p.Type == q.Type && SameValue(p, q))
            {
                same++;
                return;
            }

            // PidTagHtml is binary in one store and text in another; what counts is the text it decodes to.
            if (p.Id == MapiPid.Html && p.Name is null && other.Properties.Get(MapiPid.Html, MapiType.Binary, MapiType.String8, MapiType.UnicodeString) is not null
                && new MapiMailItem(a, default).HtmlBody?.ReadText().TrimEnd('\0') == new MapiMailItem((MapiMessage)other, default).HtmlBody?.ReadText().TrimEnd('\0'))
            {
                typeChanged++;
                return;
            }

            // A header value comes back with its folding white space as one space (RFC 5322 §2.2.3).
            if (q is not null && p.Name is { IsString: true } && Strings(p, a) is { } hp && Strings(q, other) is { } hq
                && hp.Select(x => string.Join(' ', (x ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).SequenceEqual(hq.Select(x => string.Join(' ', (x ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))))
            {
                typeChanged++;
                return;
            }

            if (q is not null && Strings(p, a) is { } sp && Strings(q, other) is { } sq && sp.SequenceEqual(sq))
            {
                typeChanged++;
                return;
            }

            if (reported.Contains(name) || Aliases(name).Any(reported.Contains) || (p.Name is { IsString: true } an && reported.Contains(an.Name!)))
            {
                explained++;
                return;
            }

            silent++;
            names.Add(scope + name + (q is null ? " (missing)" : " (differs)"));
            if (Environment.GetEnvironmentVariable("OXC_VERBOSE") is not null)
            {
                Console.Error.WriteLine($"  {name}: {Show(p)} => {(q is null ? "-" : Show(q))}");
            }
        }

        foreach (MapiProperty p in a.Properties)
        {
            One(p, b, string.Empty);
        }

        // Recipients come back in table order, one per row that had an SMTP address; the rows that could not be written are in the report.
        MapiRecipient[] ra = a.Recipients.Where(r => r.RecipientType is MapiRecipientType.To or MapiRecipientType.Cc).ToArray();
        MapiRecipient[] rb = b.Recipients.Where(r => r.RecipientType is MapiRecipientType.To or MapiRecipientType.Cc).ToArray();
        int j = 0;
        var resolver = new Func<MapiMessage, MapiRecipient, string?>((m, r) => new MapiMailItem(m, default).ToRecipients.Concat(new MapiMailItem(m, default).Cc).Select(x => x.Address).FirstOrDefault(x => x == (r.GetString(MapiPid.SmtpAddress) ?? r.GetString(MapiPid.EmailAddress))));
        _ = resolver;
        foreach (MapiRecipient r in ra)
        {
            props += 4;
            string? smtp = Smtp(r);
            if (smtp is null)
            {
                explained += 4;
                continue;
            }

            if (j < rb.Length && string.Equals(Smtp(rb[j]), smtp, StringComparison.OrdinalIgnoreCase) && rb[j].RecipientType == r.RecipientType)
            {
                if (string.Equals(Smtp(rb[j]), smtp, StringComparison.Ordinal))
                {
                    same += 3;
                }
                else if (reported.Contains("To|AddressSpellingFromTransportHeader") || reported.Contains("Cc|AddressSpellingFromTransportHeader"))
                {
                    explained += 3;
                }
                else
                {
                    silent += 3;
                    names.Add("recipient address spelling differs");
                }

                string? dn = r.GetString(MapiPid.DisplayName);
                if (dn == rb[j].GetString(MapiPid.DisplayName))
                {
                    same++;
                }
                else if (string.IsNullOrEmpty(dn) || dn == smtp)
                {
                    typeChanged++;
                }
                else if (reported.Contains("To|AddressSpellingFromTransportHeader") || reported.Contains("Cc|AddressSpellingFromTransportHeader"))
                {
                    explained++;
                }
                else
                {
                    silent++;
                    names.Add("recipient display name differs");
                    if (Environment.GetEnvironmentVariable("OXC_VERBOSE") is not null)
                    {
                        Console.Error.WriteLine($"  recipient name: \"{dn}\" => \"{rb[j].GetString(MapiPid.DisplayName)}\" smtp {smtp}");
                    }
                }

                j++;
            }
            else
            {
                silent += 4;
                names.Add("recipient missing or out of order");
            }
        }

        MapiAttachment[] aa = a.Attachments.ToArray();
        MapiAttachment[] ba = b.Attachments.ToArray();
        int bi = 0;
        for (int i = 0; i < aa.Length; i++)
        {
            MapiAttachment x = aa[i];
            props++;
            bool has = x.EmbeddedMessage is not null || x.Properties.Get(MapiPid.AttachDataBinary, MapiType.Binary) is not null;
            if (!has)
            {
                explained++;
                continue;
            }

            if (bi >= ba.Length)
            {
                silent++;
                names.Add("attachment missing");
                continue;
            }

            MapiAttachment y = ba[bi++];
            if (x.EmbeddedMessage is { } em)
            {
                if (y.EmbeddedMessage is { } ym)
                {
                    var (p2, s2, t2, e22, sl2, n2) = CompareMessage(em, ym, reported, path + "attachment/");
                    props += p2;
                    same += s2;
                    typeChanged += t2;
                    explained += e22;
                    silent += sl2;
                    names.AddRange(n2);
                    same++;
                }
                else
                {
                    silent++;
                    names.Add("embedded message missing");
                }

                continue;
            }

            bool ok = (x.OpenData() is { } xs && y.OpenData() is { } ys && Same(xs, ys))
                && x.FileName == y.FileName
                && string.Equals(x.MimeType ?? "application/octet-stream", y.MimeType ?? "application/octet-stream", StringComparison.OrdinalIgnoreCase);
            if (ok)
            {
                same++;
            }
            else if (reported.Contains("PidTagAttachMimeTag") && x.FileName == y.FileName)
            {
                explained++;
            }
            else
            {
                silent++;
                names.Add("attachment differs");
            }
        }

        return (props, same, typeChanged, explained, silent, names);
    }

    private static bool Same(Stream a, Stream b)
    {
        using (a)
        using (b)
        {
            using var ma = new MemoryStream();
            using var mb = new MemoryStream();
            a.CopyTo(ma);
            b.CopyTo(mb);
            return ma.ToArray().AsSpan().SequenceEqual(mb.ToArray());
        }
    }

    private static string? Smtp(MapiRecipient r)
    {
        var m = new MapiMessage();
        m.AddRecipient(r);
        string? s = r.GetString(MapiPid.SmtpAddress);
        if (!string.IsNullOrEmpty(s) && s.Contains('@', StringComparison.Ordinal))
        {
            return s;
        }

        string? e = r.GetString(MapiPid.EmailAddress);
        string? t = r.GetString(MapiPid.AddressType);
        return (t is null || t.Equals("SMTP", StringComparison.OrdinalIgnoreCase)) && e is not null && e.Contains('@', StringComparison.Ordinal) ? e : null;
    }

    private static string Show(MapiProperty p)
    {
        static string T(string? t) => t is null ? "<none>" : System.Text.Json.JsonSerializer.Serialize(t.Length > 50 ? "…" + t[^40..] : t);
        return p.IsMultiValued
            ? string.Join("|", Enumerable.Range(0, p.ValueCount).Select(i => T(p.AsString(null, null, i))))
            : p.AsString() is { } s ? T(s) : Convert.ToHexString(p.GetBytes().Span)[..Math.Min(40, p.GetBytes().Length * 2)];
    }

    // The loss report names an address that cannot be written by its header, so the properties of that address are explained by it.
    private static IEnumerable<string> Aliases(string property)
    {
        if (property.StartsWith("PidTagSender", StringComparison.Ordinal) || property.StartsWith("PidTagSentRepresenting", StringComparison.Ordinal))
        {
            yield return "From";
            yield return "Sender";
        }
        else if (property == "PidTagClientSubmitTime")
        {
            yield return "Date";
        }
        else if (property == "PidTagReplyRecipientNames")
        {
            yield return "PidTagReplyRecipientEntries";
        }
    }

    // The strings of a single or multi-valued string property, or null for any other type.
    private static List<string?>? Strings(MapiProperty p, MapiObject owner)
    {
        if (!(IsString(p.Type) || p.Type is MapiType.MultipleString or MapiType.MultipleString8))
        {
            return null;
        }

        return Enumerable.Range(0, p.ValueCount).Select(i => p.AsString(owner.String8Encoding, null, i)).ToList();
    }

    private static bool IsString(MapiType t) => t is MapiType.String8 or MapiType.UnicodeString;

    // ---- MIME -> MAPI -> MIME ----------------------------------------------------------------------------------------------------------

    private static int RoundTripMime(string dir, int limit, int sample)
    {
        int n = 0, seen = 0, identicalFields = 0, itemsOk = 0, itemsLossy = 0, itemsSilent = 0, headerFieldsSrc = 0, headerFieldsKept = 0;
        long fields = 0, fieldsSame = 0, fieldsExplained = 0, fieldsSilent = 0, lossEntries = 0;
        var silentByField = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var lostHeaders = new Dictionary<string, int>();
        var codes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (seen++ % sample != 0)
            {
                continue;
            }

            if (n >= limit)
            {
                break;
            }

            byte[] data = File.ReadAllBytes(file);
            if (data.Length > 8_000_000)
            {
                continue;
            }

            n++;
            MimeEntity src = MimeEntity.Parse(data);
            ConversionResult<MapiMessage> im = new MimeToMapiConverter().Convert(src);
            ConversionResult<MimeEntity> ex = new MapiToMimeConverter(Export).Convert(im.Value);
            var report = new LossReport();
            foreach (LossEntry e in im.Report) { report.Add(e); }
            foreach (LossEntry e in ex.Report) { report.Add(e); }
            foreach (LossEntry e in report)
            {
                lossEntries++;
                codes[e.Code] = codes.GetValueOrDefault(e.Code) + 1;
            }

            MimeMailItem a = MimeMailItem.Parse(data);
            MimeMailItem b = MimeMailItem.Parse(ex.Value.ToArray());
            List<string> bad = Compare2(a, b);
            fields += 10;
            int silentHere = 0;
            foreach (string f in bad)
            {
                if (Explained(f, report))
                {
                    fieldsExplained++;
                }
                else
                {
                    fieldsSilent++;
                    silentHere++;
                    silentByField[f] = silentByField.GetValueOrDefault(f) + 1;
                    if (Environment.GetEnvironmentVariable("OXC_VERBOSE") is not null)
                    {
                        Console.Error.WriteLine($"  silent {f}: {Path.GetFileName(file)} {Detail(f, a, b)}");
                    }
                }
            }

            fieldsSame += 10 - bad.Count;

            // Header fidelity: every header field of the source (but the structural ones) is still in the re-exported message.
            var kept = new HashSet<string>(ex.Value.Headers.Select(h => h.Name), StringComparer.OrdinalIgnoreCase);
            foreach (MimeHeader h in src.Headers)
            {
                if (h.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || h.IsNamed("MIME-Version") || h.Name.Any(c => c is <= ' ' or > '~'))
                {
                    continue;
                }

                headerFieldsSrc++;
                if (kept.Contains(h.Name))
                {
                    headerFieldsKept++;
                }
                else
                {
                    lostHeaders[h.Name.ToLowerInvariant()] = lostHeaders.GetValueOrDefault(h.Name.ToLowerInvariant()) + 1;
                    if (Environment.GetEnvironmentVariable("OXC_VERBOSE") is not null && !h.IsNamed("Bcc") && !h.IsNamed("Lines"))
                    {
                        Console.Error.WriteLine($"  header not kept: {Path.GetFileName(file)} {h.Name}");
                    }
                }
            }

            if (bad.Count == 0 && !report.HasContentLoss)
            {
                itemsOk++;
            }
            else
            {
                itemsLossy++;
            }

            if (silentHere > 0)
            {
                itemsSilent++;
            }

            _ = identicalFields;
        }

        Console.WriteLine($"messages {n}: all ten compared fields equal and nothing lost {itemsOk}; with loss entries or explained differences {itemsLossy}; with a silent difference {itemsSilent}");
        Console.WriteLine($"fields compared {fields}: equal {fieldsSame}, differ and explained by a loss entry {fieldsExplained}, silent {fieldsSilent}; loss entries {lossEntries}");
        Console.WriteLine($"source header fields (not Content-*) still present after the round trip: {headerFieldsKept} of {headerFieldsSrc}");
        foreach ((string k, int v) in lostHeaders.OrderByDescending(x => x.Value).Take(8))
        {
            Console.WriteLine($"  header not kept {k}\t{v}");
        }

        foreach ((string k, int v) in silentByField)
        {
            Console.WriteLine($"  silent {k}\t{v}");
        }

        foreach ((string k, int v) in codes)
        {
            Console.WriteLine($"  loss {k}\t{v}");
        }

        return fieldsSilent == 0 ? 0 : 1;
    }

    private static string Detail(string field, MimeMailItem a, MimeMailItem b)
    {
        static string Tail(string? t) => t is null ? "<none>" : System.Text.Json.JsonSerializer.Serialize(t.Length > 30 ? t[^30..] : t);
        return field switch
        {
            "text-body" => Tail(a.TextBody?.ReadText()) + " => " + Tail(b.TextBody?.ReadText()),
            "html-body" => Tail(a.HtmlBody?.ReadText()) + " => " + Tail(b.HtmlBody?.ReadText()),
            "date" => a.Sent?.Value + " => " + b.Sent?.Value,
            "attachments" => string.Join("|", a.Attachments.Select(x => x.FileName + ":" + x.ContentType + ":" + x.Size)) + " => " + string.Join("|", b.Attachments.Select(x => x.FileName + ":" + x.ContentType + ":" + x.Size)),
            _ => string.Empty,
        };
    }

    private static bool Explained(string field, LossReport r) => field switch
    {
        "from" => r.Any(e => e.Name is "From" or "Sender"),
        "to" => r.Any(e => e.Name is "To"),
        "cc" => r.Any(e => e.Name is "Cc"),
        "date" => r.Any(e => e.Name is "Date" or "PidTagClientSubmitTime"),
        "subject" => r.Any(e => e.Name is "Subject"),
        "message-id" or "in-reply-to" or "references" => r.Any(e => e.Name is "Message-ID" or "In-Reply-To" or "References"),
        "text-body" or "html-body" => r.Any(e => e.Code is "PartsTooDeep" or "SignatureNotVerifiable"),
        "attachments" => r.Any(e => e.Code is "EmbeddedMessageTooDeep" or "PartsTooDeep" or "AttachmentWithoutData"),
        _ => false,
    };

    private static List<string> Compare2(MimeMailItem a, MimeMailItem b)
    {
        var bad = new List<string>();
        static string Norm(string? s) => string.Join(' ', (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        static string All(IEnumerable<MailAddress> l) => string.Join(",", l.Select(x => x.Address.ToLowerInvariant()));
        if (Norm(a.Subject) != Norm(b.Subject)) { bad.Add("subject"); }
        if (All(a.From) != All(b.From)) { bad.Add("from"); }
        if (All(a.ToRecipients) != All(b.ToRecipients)) { bad.Add("to"); }
        if (All(a.Cc) != All(b.Cc)) { bad.Add("cc"); }
        if (a.Sent?.Value.UtcDateTime != b.Sent?.Value.UtcDateTime) { bad.Add("date"); }
        if (a.MessageId != b.MessageId) { bad.Add("message-id"); }
        if (!a.InReplyTo.SequenceEqual(b.InReplyTo)) { bad.Add("in-reply-to"); }
        if (!a.References.SequenceEqual(b.References)) { bad.Add("references"); }
        if (Fold(a.TextBody?.ReadText() ?? string.Empty).TrimEnd() != Fold(b.TextBody?.ReadText() ?? string.Empty).TrimEnd()) { bad.Add("text-body"); }
        if (Fold(a.HtmlBody?.ReadText() ?? string.Empty).TrimEnd() != Fold(b.HtmlBody?.ReadText() ?? string.Empty).TrimEnd()) { bad.Add("html-body"); }
        // An unnamed text rendering beside the body (text/enriched or text/calendar in an alternative) is an attachment of the new message
        // but not in the view of the source, which does not list renderings of the body; both sides are compared without them.
        static bool Rendering(IMailAttachment x) => x.FileName is null && x.ContentType.StartsWith("text/", StringComparison.Ordinal);
        string[] sa = a.Attachments.Where(x => !Rendering(x)).Select(Att).Order(StringComparer.Ordinal).ToArray();
        string[] sb = b.Attachments.Where(x => !Rendering(x)).Select(Att).Order(StringComparer.Ordinal).ToArray();
        if (!sa.SequenceEqual(sb)) { bad.Add("attachments"); }
        return bad;
    }

    // ---- the view ----------------------------------------------------------------------------------------------------------

    private static int ViewStats(string input, int limit)
    {
        int noAddr = 0, items = 0, senders = 0, sendersSmtp = 0, sendersEx = 0, sendersExResolved = 0, exKept = 0, rec = 0, recSmtp = 0, recEx = 0, recExResolved = 0, replyTo = 0, noMid = 0, midFallback = 0;
        foreach ((string _, MapiMailItem v) in Items(input, limit))
        {
            items++;
            foreach (MailAddress a in v.From)
            {
                senders++;
                if (a.X500Address is not null)
                {
                    sendersEx++;
                    exKept++;
                    if (a.Address.Contains('@', StringComparison.Ordinal))
                    {
                        sendersExResolved++;
                    }
                    else if (Environment.GetEnvironmentVariable("OXC_VERBOSE") is not null)
                    {
                        string tp = v.Message.GetString(MapiPid.TransportMessageHeaders) ?? string.Empty;
                        int fi = tp.IndexOf("\nFrom:", StringComparison.OrdinalIgnoreCase);
                        Console.WriteLine($"  unresolved sender: name={a.Name} transport={(tp.Length > 0)} From={(fi >= 0 ? tp.Substring(fi + 1, Math.Min(80, tp.Length - fi - 1)).Split('\n')[0].Trim() : "-")} smtpProps={v.Message.GetString(MapiPid.SenderSmtpAddress) ?? "-"}/{v.Message.GetString(MapiPid.SentRepresentingSmtpAddress) ?? "-"} rep={v.Message.GetString(MapiPid.SentRepresentingAddressType)}/{v.Message.GetString(MapiPid.SenderAddressType)}");
                    }
                }
                else if (a.Address.Contains('@', StringComparison.Ordinal))
                {
                    sendersSmtp++;
                }
                else
                {
                    noAddr++;
                }
            }

            foreach (MailAddress a in v.ToRecipients.Concat(v.Cc))
            {
                rec++;
                if (a.X500Address is not null)
                {
                    recEx++;
                    if (a.Address.Contains('@', StringComparison.Ordinal))
                    {
                        recExResolved++;
                    }
                }
                else if (a.Address.Contains('@', StringComparison.Ordinal))
                {
                    recSmtp++;
                }
            }

            replyTo += v.ReplyTo.Count > 0 ? 1 : 0;
            if (v.Message.GetString(MapiPid.InternetMessageId) is null)
            {
                noMid++;
                if (v.MessageId is not null)
                {
                    midFallback++;
                }
            }
        }

        Console.WriteLine($"items {items}; senders {senders}: SMTP as stored {sendersSmtp}, EX {sendersEx} of which resolved to SMTP {sendersExResolved}; X.500 kept {exKept}; no address at all {noAddr}");
        Console.WriteLine($"recipients (To+Cc) {rec}: SMTP as stored {recSmtp}, EX {recEx} of which resolved {recExResolved}");
        Console.WriteLine($"items with Reply-To {replyTo}; items without PidTagInternetMessageId {noMid}, of which the view found one in the transport headers {midFallback}");
        return 0;
    }
}
