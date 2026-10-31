using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Display;

internal static class DisplayHtmlBuilder
{
    private const string Chrome =
        ".omx-display{font-family:system-ui,sans-serif;font-size:14px}"
        + ".omx-header{border-bottom:1px solid #ccc;margin-bottom:8px;padding-bottom:6px}"
        + ".omx-subject{font-size:1.2em;font-weight:bold;margin-bottom:4px;overflow-wrap:anywhere}"
        + ".omx-field{overflow-wrap:anywhere}.omx-label{color:#555;margin-right:4px}"
        + ".omx-q{margin:0 0 0 .5em;padding-left:.7em;border-left:2px solid #ccc}"
        + ".omx-attachments{border-top:1px solid #ccc;margin-top:8px;padding-top:6px}"
        + ".omx-attachments ul{margin:4px 0;padding-left:1.4em}";

    public static DisplayResult Build(IMailItem item, DisplayOptions o)
    {
        Resolver resolver = new(item, o);
        SanitizerOptions so = o.Sanitizer;
        if (so.ResolveReference is null)
        {
            so = Clone(so, resolver.Resolve);
        }

        StringBuilder sb = new();
        sb.Append("<div class=\"omx-display\"><style>").Append(Chrome).Append("</style>");
        if (o.IncludeHeader)
        {
            Header(item, sb);
        }

        if (o.CalendarCard is { } card)
        {
            string? html = null;
            try
            {
                html = card(item);
            }
#pragma warning disable CA1031 // A failing hook must not hide the message.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            if (!string.IsNullOrEmpty(html))
            {
                sb.Append("<div class=\"omx-card\">").Append(html).Append("</div>");
            }
        }

        SanitizedHtml? body = null;
        bool usedText = false;
        MailBody? htmlBody = o.PreferText ? null : item.HtmlBody;
        string? htmlText = htmlBody is null ? null : ReadText(htmlBody);
        if (htmlText is not null)
        {
            body = HtmlSanitizer.Sanitize(htmlText, so);
            sb.Append(body.Html);
        }
        else if (item.TextBody is { } tb && ReadText(tb) is { } text)
        {
            usedText = true;
            SanitizerContext cx = new(so, verify: false);
            if (text.Length > so.MaxInputLength)
            {
                text = text[..so.MaxInputLength];
                cx.Truncated = true;
                cx.Change(SanitizerChangeKind.LimitReached, "input length");
            }

            bool flowed = string.Equals(tb.ContentTypeParameter("format"), "flowed", StringComparison.OrdinalIgnoreCase);
            bool delsp = string.Equals(tb.ContentTypeParameter("delsp"), "yes", StringComparison.OrdinalIgnoreCase);
            sb.Append("<style>").Append(HtmlSanitizer.BoxRuleText).Append("</style>");
            sb.Append(PlainTextHtml.Render(text, flowed, delsp, cx));
            body = new SanitizedHtml(string.Empty, cx.Changes, cx.ChangeCount, cx.Remote, cx.Tracking, cx.Truncated);
        }

        if (o.IncludeAttachmentList)
        {
            AttachmentList(item, resolver, sb);
        }

        sb.Append("</div>");
        return new DisplayResult(sb.ToString(), body, usedText, resolver.Resolved, resolver.Unresolved);
    }

    private static SanitizerOptions Clone(SanitizerOptions s, Func<ContentReference, string?> resolve) => new()
    {
        RemoteContent = s.RemoteContent,
        Policy = s.Policy,
        ResolveReference = resolve,
        AllowRewriteToAnyUrl = s.AllowRewriteToAnyUrl,
        OpenLinksInNewWindow = s.OpenLinksInNewWindow,
        MaxInputLength = s.MaxInputLength,
        MaxDepth = s.MaxDepth,
        MaxNodes = s.MaxNodes,
        MaxCssLength = s.MaxCssLength,
        MaxReported = s.MaxReported,
    };

#pragma warning disable CA1031 // A body that fails to decode is shown as missing, never thrown at the caller.
    private static string? ReadText(MailBody b)
    {
        try
        {
            return b.ReadText();
        }
        catch (Exception)
        {
            return null;
        }
    }
#pragma warning restore CA1031

    private static void Header(IMailItem item, StringBuilder sb)
    {
        sb.Append("<div class=\"omx-header\">");
        sb.Append("<div class=\"omx-subject\">");
        HtmlSanitizer.EscapeText(item.Subject ?? string.Empty, sb);
        sb.Append("</div>");
        Field(sb, "From", item.From);
        Field(sb, "To", item.ToRecipients);
        Field(sb, "Cc", item.Cc);
        Field(sb, "Bcc", item.Bcc);
        if (item.Sent is { } d)
        {
            string text = d.Value.ToString("ddd, d MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture)
                + (d.ZoneKnown ? " " + FormatOffset(d.Value.Offset) : " UTC");
            sb.Append("<div class=\"omx-field\"><span class=\"omx-label\">Date:</span>");
            HtmlSanitizer.EscapeText(text, sb);
            sb.Append("</div>");
        }

        sb.Append("</div>");
    }

    private static string FormatOffset(TimeSpan t) => (t < TimeSpan.Zero ? "-" : "+") + Math.Abs(t.Hours).ToString("00", CultureInfo.InvariantCulture)
        + Math.Abs(t.Minutes).ToString("00", CultureInfo.InvariantCulture);

    private static void Field(StringBuilder sb, string label, IReadOnlyList<MailAddress> list)
    {
        if (list.Count == 0)
        {
            return;
        }

        sb.Append("<div class=\"omx-field\"><span class=\"omx-label\">").Append(label).Append(":</span>");
        for (int i = 0; i < list.Count && i < 200; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            HtmlSanitizer.EscapeText(list[i].ToString(), sb);
        }

        if (list.Count > 200)
        {
            sb.Append(" ...");
        }

        sb.Append("</div>");
    }

    private static void AttachmentList(IMailItem item, Resolver resolver, StringBuilder sb)
    {
        IReadOnlyList<IMailAttachment> atts;
        try
        {
            atts = item.Attachments;
        }
#pragma warning disable CA1031 // The list is decoration; the message is shown without it.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        bool any = false;
        for (int i = 0; i < atts.Count && i < 1000; i++)
        {
            if (resolver.WasUsed(atts[i]))
            {
                continue;
            }

            if (!any)
            {
                sb.Append("<div class=\"omx-attachments\"><div class=\"omx-label\">Attachments</div><ul>");
                any = true;
            }

            IMailAttachment a = atts[i];
            sb.Append("<li>");
            HtmlSanitizer.EscapeText(a.FileName ?? "(unnamed)", sb);
            sb.Append(" <span class=\"omx-label\">");
            HtmlSanitizer.EscapeText(a.ContentType + ", " + FormatSize(Size(a)), sb);
            sb.Append("</span></li>");
        }

        if (any)
        {
            sb.Append("</ul></div>");
        }
    }

#pragma warning disable CA1031
    private static long Size(IMailAttachment a)
    {
        try
        {
            return a.Size;
        }
        catch (Exception)
        {
            return 0;
        }
    }
#pragma warning restore CA1031

    private static string FormatSize(long n) => n < 1024 ? n.ToString(CultureInfo.InvariantCulture) + " B"
        : n < 1024 * 1024 ? (n / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
        : (n / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Turns <c>cid:</c> and location references into URLs for the message's own attachments.</summary>
    private sealed class Resolver(IMailItem item, DisplayOptions o)
    {
        private IReadOnlyList<IMailAttachment>? _atts;
        private readonly HashSet<int> _used = [];
        private long _total;
        public readonly List<string> Resolved = [];
        public readonly List<string> Unresolved = [];

#pragma warning disable CA1031 // An attachment list that cannot be read means nothing resolves.
        private IReadOnlyList<IMailAttachment> Attachments
        {
            get
            {
                if (_atts is null)
                {
                    try
                    {
                        _atts = item.Attachments;
                    }
                    catch (Exception)
                    {
                        _atts = [];
                    }
                }

                return _atts;
            }
        }
#pragma warning restore CA1031

        public bool WasUsed(IMailAttachment a)
        {
            for (int i = 0; i < Attachments.Count; i++)
            {
                if (ReferenceEquals(Attachments[i], a))
                {
                    return _used.Contains(i);
                }
            }

            return false;
        }

        public string? Resolve(ContentReference r)
        {
            int found = -1;
            IReadOnlyList<IMailAttachment> atts = Attachments;
            for (int pass = 0; pass < 2 && found < 0; pass++)
            {
                for (int i = 0; i < atts.Count && found < 0; i++)
                {
                    if (r.IsContentId)
                    {
                        string? id = atts[i].ContentId;
                        if (id is not null && string.Equals(id, r.Value, pass == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        {
                            found = i;
                        }
                    }
                    else if (atts[i].ContentLocation is { Length: > 0 } loc && MatchesLocation(loc, r.Value, pass == 1))
                    {
                        found = i;
                    }
                }
            }

            if (found < 0)
            {
                NoteUnresolved(r);
                return null;
            }

            string? url = Url(atts[found]);
            if (url is null)
            {
                NoteUnresolved(r);
                return null;
            }

            _used.Add(found);
            Resolved.Add(Short(r.Value));
            return url;
        }

        // An absolute http(s) URL that matches no part is an ordinary remote reference, not a missing attachment.
        private void NoteUnresolved(ContentReference r)
        {
            if (r.IsContentId || !r.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                Unresolved.Add(Short(r.Value));
            }
        }

        private static string Short(string s) => s.Length > 128 ? s[..128] : s;

        // RFC 2557 5: a reference matches a part's Content-Location when they are equal, or when one is the other's tail after a '/'
        // (a relative reference against an absolute location). The second pass ignores case.
        private static bool MatchesLocation(string loc, string reference, bool ignoreCase)
        {
            StringComparison c = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            loc = loc.Trim();
            return loc.Equals(reference, c) || loc.EndsWith("/" + reference, c) || reference.EndsWith("/" + loc, c);
        }

#pragma warning disable CA1031 // A part that cannot be read is not resolved.
        private string? Url(IMailAttachment a)
        {
            try
            {
                if (o.ReferenceForm == ContentReferenceForm.Callback)
                {
                    return o.AttachmentUrl?.Invoke(a);
                }

                using Stream s = a.Open();
                long cap = Math.Min(o.MaxInlineImageBytes, o.MaxInlineTotalBytes - _total);
                if (cap <= 0)
                {
                    return null;
                }

                byte[] buf = new byte[Math.Min(cap + 1, 1 << 16)];
                using MemoryStream ms = new();
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > cap)
                    {
                        return null;
                    }
                }

                byte[] data = ms.ToArray();
                string? type = SniffImage(data);
                if (type is null)
                {
                    return null;
                }

                _total += data.Length;
                return "data:" + type + ";base64," + Convert.ToBase64String(data);
            }
            catch (Exception)
            {
                return null;
            }
        }
#pragma warning restore CA1031

        // The type comes from the bytes, not the declared content type: a part that says image/png and holds anything else gets no data URI.
        // SVG is not among them (it can carry script).
        private static string? SniffImage(byte[] d)
        {
            if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G')
            {
                return "image/png";
            }

            if (d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8')
            {
                return "image/gif";
            }

            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
            {
                return "image/webp";
            }

            if (d.Length >= 2 && d[0] == 'B' && d[1] == 'M')
            {
                return "image/bmp";
            }

            return null;
        }
    }
}
