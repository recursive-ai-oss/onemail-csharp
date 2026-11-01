using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;

// Compares what the messaging layer reads with what pffexport exports (-f all -m all) for the same file: the folder tree, the items
// in each folder, and for every item the values pffexport exposes (times, size, flags, subject, sender, importance ..., recipients,
// body files, internet headers, conversation index, attachment files and their bytes, embedded messages). Test-side only.
internal sealed partial class MessagingCompare
{
    private int _differences, _folders, _items, _recipients, _attachments, _embedded, _fields, _skippedFolders, _search, _assoc, _assocUnexported, _otherItems;
    private readonly List<string> _log = [];

    public static int Run(PstStore store, string exportDir, bool verbose)
    {
        if (!Directory.Exists(exportDir)) { Console.WriteLine("DIFF pffexport produced no export directory (it could not read the file)"); return 4; }
        var c = new MessagingCompare();
        c.Walk(store, exportDir);
        foreach (string l in c._log.Take(verbose ? int.MaxValue : 60)) Console.WriteLine("DIFF " + l);
        int recovered = Directory.Exists(exportDir.Replace(".export", ".recovered")) ? Directory.GetDirectories(exportDir.Replace(".export", ".recovered"), "Message*", SearchOption.AllDirectories).Length : 0;
        Console.WriteLine($"messaging folders {c._folders} (unmatched-name {c._skippedFolders}, search {c._search}) items {c._items} (non-mail {c._otherItems}, associated {c._assoc}, of which libpff exports {c._assoc - c._assocUnexported}) recipients {c._recipients} attachments {c._attachments} embedded {c._embedded} fields {c._fields} recoveredByLibpff {recovered} differences {c._differences}");
        return c._differences == 0 ? 0 : 3;
    }

    private void Diff(string what)
    {
        _differences++;
        _log.Add(Regex.Replace(what, @"[\x00-\x1f]", m => $"\\x{(int)m.Value[0]:x2}"));
    }

    private static string Safe(string s) => Regex.Replace(s, @"[^\w \.\-\{\}\(\)@,&!#$%'+=\[\]]", "_");

    private void Walk(PstStore store, string exportDir)
    {
        PstFolder? root = store.Root;
        if (root is null) { Diff("no root folder"); return; }
        foreach (PstFolder c in root.EnumerateSubfolders()) Folder(c, Path.Combine(exportDir, Safe(c.Name ?? "")), c.Name ?? "");
        // Items directly in the root folder are exported beside the folders.
        Items(root, exportDir, "(root)", rootLevel: true);
    }

    private void Folder(PstFolder f, string dir, string logical)
    {
        _folders++;
        if (f.IsSearchFolder) _search++;
        if (!Directory.Exists(dir)) { Diff($"folder missing in export: {logical}"); return; }
        Items(f, dir, logical, rootLevel: false);
        var seen = new Dictionary<string, int>();
        var kids = f.EnumerateSubfolders().ToList();
        foreach (PstFolder c in kids) { string n = Safe(c.Name ?? ""); seen[n] = seen.GetValueOrDefault(n) + 1; }
        foreach (PstFolder c in kids)
        {
            string n = Safe(c.Name ?? "");
            if (seen[n] > 1) { _skippedFolders++; continue; }
            Folder(c, Path.Combine(dir, n), logical + "/" + c.Name);
        }

        foreach (string sub in Directory.GetDirectories(dir).Where(d => !ItemDir().IsMatch(Path.GetFileName(d))))
        {
            if (!kids.Any(c => Safe(c.Name ?? "") == Path.GetFileName(sub))) Diff($"{logical}: folder only in libpff: {Path.GetFileName(sub)}");
        }
    }

    private static readonly string[] KeyFields = ["Client submit time", "Delivery time", "Creation time", "Modification time", "Size", "Flags"];
    private static readonly string[] RecipientFields = ["Display name", "Recipient display name", "Email address", "Address type", "Recipient type"];

    private static string Key(Dictionary<string, string> h) =>
        string.Join("|", KeyFields.Select(k => h.GetValueOrDefault(k, "")));

    private void Items(PstFolder f, string dir, string logical, bool rootLevel)
    {
        var exported = Directory.GetDirectories(dir, "Message*").OrderBy(d => d, StringComparer.Ordinal).ToList();
        var byKey = new Dictionary<string, Queue<string>>();
        foreach (string m in exported)
        {
            string k = Key(Headers(m));
            if (!byKey.TryGetValue(k, out Queue<string>? q)) byKey[k] = q = new Queue<string>();
            q.Enqueue(m);
        }

        // Folder-associated messages are in the export too when libpff lists them; they are paired by the same key.
        var ours = new List<(PstMessage M, bool Assoc)>();
        foreach (PstMessage m in f.EnumerateMessages(PstMessageSet.Normal)) ours.Add((m, false));
        foreach (PstMessage m in f.EnumerateMessages(PstMessageSet.Associated)) ours.Add((m, true));
        int paired = 0, unpairedAssoc = 0, otherOurs = 0;
        foreach ((PstMessage m, bool assoc) in ours)
        {
            string k = OurKey(m);
            if (assoc) _assoc++;
            // libpff writes appointments, contacts, tasks and the like as AppointmentNNNNN, ContactNNNNN ... with other files; they are
            // counted here and read by the item-semantics layer later.
            if (!assoc && MapiMailItem.KindOf(m.MessageClass) != MailItemKind.Message) { otherOurs++; continue; }
            if (byKey.TryGetValue(k, out Queue<string>? q) && q.Count > 0)
            {
                paired++;
                Message(m, q.Dequeue(), logical + "#" + m.Nid);
            }
            else if (assoc) _assocUnexported++;
            else if (m.MessageClass is { } mc && mc.StartsWith("IPM.Post", StringComparison.OrdinalIgnoreCase)) { }   // pffexport leaves posts out
            else Diff($"{logical}: item {m.Nid} \"{Trunc(m.Subject)}\" is not in the export (key {k})");
        }

        int otherTheirs = Directory.GetDirectories(dir).Count(d => ItemDir().IsMatch(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith("Message", StringComparison.Ordinal));
        _otherItems += otherOurs;
        if (otherOurs != otherTheirs) Diff($"{logical}: non-mail items ours {otherOurs} libpff {otherTheirs}");
        foreach (Queue<string> q in byKey.Values) foreach (string left in q) Diff($"{logical}: libpff item {Path.GetFileName(left)} is not in ours");
        _ = paired; _ = unpairedAssoc; _ = rootLevel;
    }

    [GeneratedRegex(@"^[A-Za-z]+\d{5}$")]
    private static partial Regex ItemDir();

    private static string Trunc(string? s) => s is null ? "" : s.Length > 40 ? s[..40] : s;

    private static string Ft(MapiMessage m, ushort id)
    {
        ulong? v = m.Properties.Get(id, MapiType.Time)?.AsFileTime();
        if (v is null || v == 0 || v > 2650467743999999999UL) return "";
        DateTime t = DateTime.FromFileTimeUtc((long)v.Value);
        return t.ToString("MMM dd, yyyy HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00 UTC";
    }

    private static string OurKey(MapiMessage m)
    {
        int? flags = m.Properties.Get(0x0E07, MapiType.Integer32)?.AsInt32();
        int? size = m.Properties.Get(0x0E08, MapiType.Integer32)?.AsInt32();
        return string.Join("|", Ft(m, 0x0039), Ft(m, 0x0E06), Ft(m, 0x3007), Ft(m, 0x3008), size is null ? "" : ((uint)size.Value).ToString(CultureInfo.InvariantCulture), flags is null ? "" : "0x" + ((uint)flags.Value).ToString("x8", CultureInfo.InvariantCulture));
    }

    private static Dictionary<string, string> Headers(string messageDir)
    {
        var d = new Dictionary<string, string>();
        string p = Path.Combine(messageDir, "OutlookHeaders.txt");
        if (!File.Exists(p)) return d;
        foreach (string line in File.ReadAllText(p, Encoding.UTF8).Split('\n'))
        {
            int i = line.IndexOf(':', StringComparison.Ordinal);
            if (i <= 0) continue;
            string v = line[(i + 1)..].Trim(' ', '\t', '\r');
            // "Flags: 0x00000011 (Read, Has attachments)": only the number is compared.
            if (line.StartsWith("Flags:", StringComparison.Ordinal)) v = v.Split(' ')[0];
            d[line[..i]] = v;
        }

        return d;
    }

    private void Check(string where, string what, string ours, string theirs)
    {
        _fields++;
        if (ours != theirs) Diff($"{where}: {what}: ours \"{Trunc2(ours)}\" libpff \"{Trunc2(theirs)}\"");
    }

    private static string Trunc2(string s) => s.Length > 80 ? s[..80] + "…" : s;

    private static string Name(Importance i) => i.ToString();

    private enum Importance { Low = 0, Normal = 1, High = 2 }

    private void Message(PstMessage m, string dir, string where)
    {
        _items++;
        Dictionary<string, string> h = Headers(dir);
        var view = new MapiMailItem(m, default);
        string S(ushort id) => m.GetString(id) ?? "";
        void Opt(string label, string ours)
        {
            // libpff leaves a line out when the value is not set.
            Check(where, label, ours.TrimEnd(), h.GetValueOrDefault(label, ""));
        }

        Opt("Client submit time", Ft(m, 0x0039));
        Opt("Delivery time", Ft(m, 0x0E06));
        Opt("Creation time", Ft(m, 0x3007));
        Opt("Modification time", Ft(m, 0x3008));
        Opt("Size", m.Properties.Get(0x0E08, MapiType.Integer32)?.AsInt32() is int sz ? ((uint)sz).ToString(CultureInfo.InvariantCulture) : "");
        Opt("Conversation topic", S(0x0070));
        Opt("Subject", (view.Subject ?? "").TrimEnd());
        Opt("Sender name", S(0x0C1A));
        Opt("Sender email address", S(0x0C1F));
        Opt("Sent representing name", S(0x0042));
        Opt("Sent representing email address", S(0x0065));
        string imp = m.GetInt32(0x0017) is int iv ? ((Importance)iv).ToString() : "";
        Check(where, "Importance", imp, h.GetValueOrDefault("Importance", ""));
        // The view agrees with the properties it projects.
        if (m.GetString(0x0042) is { } rep) Check(where, "view From name", view.From.Count > 0 ? view.From[0].Name ?? "" : "", rep);
        else Check(where, "view From name", view.From.Count > 0 ? view.From[0].Name ?? "" : "", S(0x0C1A));

        Recipients(m, view, dir, where);
        Bodies(m, view, dir, where);
        Attachments(m, view, dir, where);
    }

    private void Recipients(PstMessage m, MapiMailItem view, string dir, string where)
    {
        string p = Path.Combine(dir, "Recipients.txt");
        string text = File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : "";
        var theirs = Regex.Split(text, @"(?m)^(?=Display name:)").Where(b => b.StartsWith("Display name:", StringComparison.Ordinal))
            .Select(b => string.Join("/", RecipientFields.Select(l => Field(b, l)))).ToList();
        var ours = m.Recipients.Select(r => string.Join("/", r.GetString(0x3001) ?? "", r.GetString(0x5FF6) ?? "", r.GetString(0x3003) ?? "", r.GetString(0x3002) ?? "",
            r.GetInt32(0x0C15) switch { 1 => "To", 2 => "CC", 3 => "BCC", 0 => "Originator", _ => "" })).ToList();
        _recipients += ours.Count;
        _fields++;
        // Row order is the table's; libpff lists recipients in the same order, so the lists are compared in order.
        if (!ours.SequenceEqual(theirs))
        {
            var o = ours.Except(theirs).Take(2).ToList();
            var t = theirs.Except(ours).Take(2).ToList();
            Diff($"{where}: recipients differ: ours {ours.Count} libpff {theirs.Count}; only ours [{string.Join(" ; ", o)}] only libpff [{string.Join(" ; ", t)}]");
        }

        // The view's To/Cc/Bcc come from the same rows by type.
        _fields++;
        int typed = m.Recipients.Count(r => r.GetInt32(0x0C15) is 1 or 2 or 3);
        if (view.ToRecipients.Count + view.Cc.Count + view.Bcc.Count != typed) Diff($"{where}: view recipients {view.ToRecipients.Count + view.Cc.Count + view.Bcc.Count} vs typed rows {typed}");
    }

    private static string Field(string text, string label)
    {
        Match mm = Regex.Match(text, "^" + Regex.Escape(label) + @":[ \t]*(.*?)\r?$", RegexOptions.Multiline);
        return mm.Success ? mm.Groups[1].Value : "";
    }

    private void Bodies(PstMessage m, MapiMailItem view, string dir, string where)
    {
        string txt = Path.Combine(dir, "Message.txt"), html = Path.Combine(dir, "Message.html"), rtf = Path.Combine(dir, "Message.rtf");
        // libpff exports only the bodies stored in the item; the view also derives a body from another one (HTML from RTF, text from HTML),
        // so a derived body is not compared.
        MailBody? tb = m.Properties.Get(0x1000) is null ? null : view.TextBody, hb = m.Properties.Get(0x1013) is null ? null : view.HtmlBody;
        _fields += 3;
        if ((tb is not null) != File.Exists(txt)) Diff($"{where}: text body present ours {tb is not null} libpff {File.Exists(txt)}");
        else if (tb is not null)
        {
            // libpff writes the body as UTF-8.
            string t = tb.ReadText();
            string theirs = File.ReadAllText(txt, new UTF8Encoding(false));
            if (t != theirs) Diff($"{where}: text body differs ({t.Length} vs {theirs.Length} chars)");
        }

        if ((hb is not null) != File.Exists(html)) Diff($"{where}: html body present ours {hb is not null} libpff {File.Exists(html)}");
        else if (hb is not null)
        {
            byte[] theirs = File.ReadAllBytes(html);
            using Stream s = hb.Open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] ours = ms.ToArray();
            // A string-typed HTML property is exported as UTF-8 text; a binary one byte for byte.
            if (m.Properties.Get(0x1013) is { Type: not MapiType.Binary }) ours = Encoding.UTF8.GetBytes(hb.ReadText());
            if (!ours.AsSpan().SequenceEqual(theirs)) { if (!ours.AsSpan().TrimEnd((byte)0).SequenceEqual(theirs.AsSpan().TrimEnd((byte)0))) Diff($"{where}: html body differs ({ours.Length} vs {theirs.Length} bytes)"); }
        }

        if ((view.RtfBody is not null) != File.Exists(rtf)) Diff($"{where}: rtf present ours {view.RtfBody is not null} libpff {File.Exists(rtf)}");

        string ih = Path.Combine(dir, "InternetHeaders.txt");
        _fields++;
        string? ours2 = m.GetString(0x007D);
        if ((ours2 is not null) != File.Exists(ih)) Diff($"{where}: internet headers present ours {ours2 is not null} libpff {File.Exists(ih)}");
        else if (ours2 is not null && ours2.TrimEnd('\r', '\n', '\0') != File.ReadAllText(ih, new UTF8Encoding(false)).TrimEnd('\r', '\n', '\0')) Diff($"{where}: internet headers differ");

        string ci = Path.Combine(dir, "ConversationIndex.txt");
        _fields++;
        ReadOnlyMemory<byte> idx = view.ConversationIndex;
        // libpff writes ConversationIndex.txt for every message, with no blocks when the property is missing.
        if (idx.Length == 0 ? File.Exists(ci) && File.ReadAllText(ci).Contains("Header block", StringComparison.Ordinal) : !File.Exists(ci)) Diff($"{where}: conversation index ours {idx.Length} bytes, libpff file {File.Exists(ci)}");
        else if (idx.Length >= 22)
        {
            // MS-OXOMSG 2.2.1.3: byte 0 is reserved, bytes 1-5 are the high 40 bits of a FILETIME's upper 48, then a 16-byte GUID.
            ReadOnlySpan<byte> b = idx.Span;
            long ft = ((long)b[1] << 40) | ((long)b[2] << 32) | ((long)b[3] << 24) | ((long)b[4] << 16) | ((long)b[5] << 8) | b[6 - 0];
            _ = ft;
            string text = File.ReadAllText(ci);
            var g = new Guid(b[6..22].ToArray());
            // The GUID in the file is printed in the byte order the header stores it (big-endian groups).
            string asBytes = string.Join("", b[6..22].ToArray().Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            string theirsGuid = Regex.Match(text, @"GUID:\s*([0-9a-f-]+)").Groups[1].Value.Replace("-", "", StringComparison.Ordinal);
            if (theirsGuid != asBytes && theirsGuid != g.ToString("N")) Diff($"{where}: conversation index guid ours {asBytes} libpff {theirsGuid}");
            int children = (idx.Length - 22) / 5;
            string cb = Regex.Match(text, @"Child block:\s*(\d+)").Groups[1].Value;
            if (children > 0 && Regex.Count(text, "Child block:") != children) Diff($"{where}: conversation index child blocks ours {children} libpff {Regex.Count(text, "Child block:")} ({cb})");
        }
    }

    private void Attachments(PstMessage m, MapiMailItem view, string dir, string where)
    {
        string ad = Path.Combine(dir, "Attachments");
        var files = Directory.Exists(ad) ? Directory.GetFiles(ad).OrderBy(f => f, StringComparer.Ordinal).ToList() : [];
        var dirs = Directory.Exists(ad) ? Directory.GetDirectories(ad).OrderBy(f => f, StringComparer.Ordinal).ToList() : [];
        int embeddedOurs = view.Attachments.Count(a => a.Embedded is not null);
        _fields++;
        if (view.Attachments.Count != files.Count + dirs.Count) Diff($"{where}: attachments ours {view.Attachments.Count} (embedded {embeddedOurs}) libpff files {files.Count} dirs {dirs.Count}");
        _attachments += view.Attachments.Count;
        for (int i = 0; i < view.Attachments.Count; i++)
        {
            IMailAttachment a = view.Attachments[i];
            string aw = $"{where} attachment {i + 1}";
            if (a.Embedded is { } em)
            {
                _embedded++;
                string sub = Path.Combine(ad, $"Attachment{i + 1:D5}");
                string inner = Path.Combine(sub, "Message00001");
                if (!Directory.Exists(inner)) { Diff($"{aw}: embedded message not exported by libpff"); continue; }
                var mm = (MapiMailItem)em;
                if (mm.Message is PstMessage pm) Message(pm, inner, aw + " embedded");
                continue;
            }

            string prefix = $"{i + 1}_";
            // libpff zero-pads the number when a message has ten or more attachments, and a name is "<number>_<file name>".
            string? file = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal))
                ?? (string.IsNullOrEmpty(a.FileName) ? null : files.FirstOrDefault(f => Regex.IsMatch(Path.GetFileName(f), @"^0*\d+_" + Regex.Escape(a.FileName) + "$")));
            if (file is null) { Diff($"{aw}: file {prefix}* not exported (name {a.FileName})"); continue; }
            _fields += 2;
            using Stream s = a.Open();
            string ours = Convert.ToHexString(SHA256.HashData(s));
            string theirs = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            if (ours != theirs) Diff($"{aw}: bytes differ ({a.Size} vs {new FileInfo(file).Length}) name {a.FileName}");
            string exportedName = Path.GetFileName(file)[prefix.Length..];
            if (Safe(a.FileName ?? "") != exportedName && (a.FileName ?? "") != exportedName) Diff($"{aw}: name ours \"{a.FileName}\" libpff \"{exportedName}\"");
        }
    }
}
