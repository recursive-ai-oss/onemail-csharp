using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Cli;

/// <summary>
/// <c>onemail inspect</c>: names the format of each file or directory given and prints every item through the shared view.
/// It streams: one item at a time is held, so it can walk a corpus of any size. A directory that is not itself a store (not a
/// Maildir or Apple Mail bundle) is walked, and every file in it is inspected on its own.
/// </summary>
internal sealed class Inspect : IDisposable
{
    private readonly TextWriter _out;
    private readonly bool _json;
    private readonly bool _sizes;
    private readonly bool _listDefects;
    private Utf8JsonWriter? _jw;
    private readonly MemoryStream _jsonBuffer = new();

    private Inspect(TextWriter output, bool json, bool sizes, bool listDefects)
    {
        _out = output;
        _json = json;
        _sizes = sizes;
        _listDefects = listDefects;
    }

    public void Dispose()
    {
        _jw?.Dispose();
        _jsonBuffer.Dispose();
    }

    public long Containers { get; private set; }

    public long Items { get; private set; }

    public long Defects { get; private set; }

    public long Ambiguities { get; private set; }

    public long Attachments { get; private set; }

    public long Unsupported { get; private set; }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        bool json = false, sizes = false, defects = false;
        List<string> paths = [];
        foreach (string a in args)
        {
            switch (a)
            {
                case "--json":
                    json = true;
                    break;
                case "--sizes":
                    sizes = true;
                    break;
                case "--defects":
                    defects = true;
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        error.WriteLine("inspect: unknown option " + a);
                        return 2;
                    }

                    paths.Add(a);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            error.WriteLine("usage: onemail inspect [--json] [--sizes] [--defects] <file-or-directory>...");
            return 2;
        }

        using Inspect i = new(output, json, sizes, defects);
        foreach (string p in paths)
        {
            if (!File.Exists(p) && !Directory.Exists(p))
            {
                error.WriteLine("inspect: no such file or directory: " + p);
                return 1;
            }

            i.Walk(p);
        }

        output.Flush();
        error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"inspected {i.Containers} files/stores, {i.Items} items, {i.Attachments} attachments, {i.Defects} defects ({i.Ambiguities} ambiguities), {i.Unsupported} not readable"));
        return 0;
    }

    private void Walk(string path)
    {
        if (Directory.Exists(path))
        {
            if (MailFormatSniffer.SniffDirectory(path) != MailFormat.Unknown)
            {
                Container(path);
                return;
            }

            // Not a store: a tree of loose messages. Visit entries in name order so the output is the same on every run.
            string[] entries = Directory.GetFileSystemEntries(path);
            Array.Sort(entries, StringComparer.Ordinal);
            foreach (string e in entries)
            {
                Walk(e);
            }

            return;
        }

        Container(path);
    }

    private void Container(string path)
    {
        Containers++;
        try
        {
            using MailContainer c = Mail.Open(path);
            if (_json)
            {
                Json("container", w =>
                {
                    w.WriteString("path", path);
                    w.WriteString("format", c.Format.ToString());
                });
            }
            else
            {
                _out.WriteLine(path + ": " + c.Format);
            }

            int index = 0;
            foreach (IMailItem item in c.Enumerate())
            {
                Describe(item, index++);
            }
        }
        catch (UnsupportedMailFormatException e)
        {
            Unsupported++;
            if (_json)
            {
                Json("container", w =>
                {
                    w.WriteString("path", path);
                    w.WriteString("format", e.Format.ToString());
                    w.WriteBoolean("readable", false);
                });
            }
            else
            {
                _out.WriteLine(path + ": " + (e.Format == MailFormat.Unknown ? "unrecognised" : e.Format + " (recognised, not readable yet)"));
            }
        }
    }

    private void Describe(IMailItem item, int index)
    {
        Items++;
        int defects = 0;
        int ambiguities = 0;
        List<MailDefect> list = [];
        foreach (MailDefect d in item.Defects)
        {
            defects++;
            if (d.IsAmbiguity)
            {
                ambiguities++;
            }

            if (_json || _listDefects)
            {
                list.Add(d);
            }
        }

        Defects += defects;
        Ambiguities += ambiguities;
        IReadOnlyList<IMailAttachment> atts = item.Attachments;
        Attachments += atts.Count;

        if (_json)
        {
            Json("item", w => WriteItem(w, item, index, list, atts));
            return;
        }

        StringBuilder sb = new();
        sb.Append("  #").Append(index.ToString(CultureInfo.InvariantCulture));
        sb.Append("  ").Append(item.Sent is { } d0 ? d0.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "-");
        sb.Append("  from: ").Append(item.From.Count > 0 ? Clean(item.From[0].ToString()) : "-");
        sb.Append("  subject: ").Append(Clean(item.Subject ?? "-"));
        sb.Append("  attachments: ").Append(atts.Count.ToString(CultureInfo.InvariantCulture));
        if (atts.Count > 0)
        {
            sb.Append(" (");
            for (int i = 0; i < atts.Count; i++)
            {
                sb.Append(i > 0 ? ", " : "").Append(Clean(atts[i].FileName ?? "<" + atts[i].ContentType + ">"));
                if (_sizes)
                {
                    sb.Append(' ').Append(atts[i].Size.ToString(CultureInfo.InvariantCulture)).Append('B');
                }
            }

            sb.Append(')');
        }

        sb.Append("  defects: ").Append(defects.ToString(CultureInfo.InvariantCulture));
        _out.WriteLine(sb.ToString());
        if (_listDefects)
        {
            foreach (MailDefect d in list)
            {
                _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {d.Family}:{d.Code} @{d.Offset}{(d.IsAmbiguity ? " (ambiguity)" : string.Empty)}"));
            }
        }
    }

    private void WriteItem(Utf8JsonWriter w, IMailItem item, int index, List<MailDefect> defects, IReadOnlyList<IMailAttachment> atts)
    {
        w.WriteNumber("index", index);
        w.WriteString("kind", item.Kind.ToString());
        MailProvenance p = item.Provenance;
        w.WriteStartObject("provenance");
        w.WriteString("format", p.Format.ToString());
        w.WriteString("location", p.Location);
        w.WriteString("folder", p.Folder);
        w.WriteNumber("offset", p.Offset);
        w.WriteNumber("length", p.Length);
        w.WriteEndObject();
        w.WriteString("subject", item.Subject);
        Addresses(w, "sender", item.Sender is { } s ? [s] : []);
        Addresses(w, "from", item.From);
        Addresses(w, "to", item.ToRecipients);
        Addresses(w, "cc", item.Cc);
        Addresses(w, "bcc", item.Bcc);
        Addresses(w, "replyTo", item.ReplyTo);
        w.WritePropertyName("date");
        if (item.Sent is { } d)
        {
            w.WriteStartObject();
            w.WriteString("utc", d.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (d.ZoneKnown)
            {
                w.WriteNumber("offsetMinutes", (int)d.Value.Offset.TotalMinutes);
            }
            else
            {
                w.WriteNull("offsetMinutes");
            }

            w.WriteString("source", d.Source.ToString());
            w.WriteEndObject();
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteString("messageId", item.MessageId);
        Strings(w, "inReplyTo", item.InReplyTo);
        Strings(w, "references", item.References);
        w.WriteString("conversationTopic", item.ConversationTopic);
        w.WriteStartArray("attachments");
        foreach (IMailAttachment a in atts)
        {
            w.WriteStartObject();
            w.WriteString("name", a.FileName);
            w.WriteString("type", a.ContentType);
            w.WriteString("contentId", a.ContentId);
            w.WriteBoolean("inline", a.IsInline);
            w.WriteBoolean("embedded", a.Embedded is not null);
            if (_sizes)
            {
                w.WriteNumber("size", a.Size);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("defects");
        foreach (MailDefect x in defects)
        {
            w.WriteStartObject();
            w.WriteString("family", x.Family);
            w.WriteString("code", x.Code);
            w.WriteNumber("offset", x.Offset);
            w.WriteBoolean("ambiguity", x.IsAmbiguity);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void Addresses(Utf8JsonWriter w, string name, IReadOnlyList<MailAddress> list)
    {
        w.WriteStartArray(name);
        foreach (MailAddress a in list)
        {
            w.WriteStartObject();
            w.WriteString("name", a.Name);
            w.WriteString("addr", a.Address);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string> list)
    {
        w.WriteStartArray(name);
        foreach (string s in list)
        {
            w.WriteStringValue(s);
        }

        w.WriteEndArray();
    }

    // One JSON object per line, with a "type" member first; the buffer is reused so the line costs no new allocation per item.
    private void Json(string type, Action<Utf8JsonWriter> body)
    {
        _jsonBuffer.SetLength(0);
        if (_jw is null)
        {
            _jw = new Utf8JsonWriter(_jsonBuffer);
        }
        else
        {
            _jw.Reset(_jsonBuffer);
        }

        _jw.WriteStartObject();
        _jw.WriteString("type", type);
        body(_jw);
        _jw.WriteEndObject();
        _jw.Flush();
        _out.WriteLine(Encoding.UTF8.GetString(_jsonBuffer.GetBuffer(), 0, (int)_jsonBuffer.Length));
    }

    private static string Clean(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsControl(s[i]))
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                sb.Append(' ');
            }
            else
            {
                sb?.Append(s[i]);
            }
        }

        return sb?.ToString() ?? s;
    }
}
