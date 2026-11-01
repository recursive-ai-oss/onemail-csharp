using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Composition;

/// <summary>
/// Makes the replies and forwards a mail client would: the headers that thread them (RFC 5322 §3.6.4), the recipients, the subject,
/// the quoted body and, for Outlook items, the conversation index (MS-OXOMSG 2.2.1.3). The result is a new MIME message and,
/// for Outlook originals, the same message in the MAPI property model. Nothing is sent and the original is not changed.
/// </summary>
/// <remarks>
/// Behaviour is written down in <c>docs/composition.md</c>. Hostile originals are bounded by the limits on <see cref="CompositionOptions"/>
/// and reported as <see cref="CompositionDefect"/>s; the methods do not throw for the content of the original.
/// </remarks>
public static class MessageComposer
{
    /// <summary>Replies to the sender (or to the <c>Reply-To</c> / <c>Mail-Reply-To</c> address).</summary>
    /// <param name="parent">The message to reply to.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is null.</exception>
    public static ComposedMessage Reply(IMailItem parent, CompositionOptions? options = null) =>
        new Composer(parent, options ?? new CompositionOptions(), Intent.Reply).Run();

    /// <summary>Replies to the sender and everyone else on the original, without the user's own addresses (<see cref="CompositionOptions.OwnAddresses"/>) and without duplicates. A <c>Mail-Followup-To</c> header replaces the list.</summary>
    /// <param name="parent">The message to reply to.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is null.</exception>
    public static ComposedMessage ReplyAll(IMailItem parent, CompositionOptions? options = null) =>
        new Composer(parent, options ?? new CompositionOptions(), Intent.ReplyAll).Run();

    /// <summary>Forwards the message, inline or as an attachment (<see cref="CompositionOptions.ForwardStyle"/>). The recipients are left for the caller.</summary>
    /// <param name="parent">The message to forward.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The forward.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is null.</exception>
    public static ComposedMessage Forward(IMailItem parent, CompositionOptions? options = null) =>
        new Composer(parent, options ?? new CompositionOptions(), Intent.Forward).Run();

    /// <summary>Formats a date as RFC 5322 §3.3 writes it: <c>Mon, 1 Jan 2024 10:00:00 +0100</c>.</summary>
    /// <param name="value">The instant with its zone offset.</param>
    /// <returns>The text.</returns>
    public static string FormatDate(DateTimeOffset value)
    {
        TimeSpan o = value.Offset;
        string sign = o < TimeSpan.Zero ? "-" : "+";
        o = o.Duration();
        return value.ToString("ddd, d MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " " + sign + o.Hours.ToString("00", CultureInfo.InvariantCulture) + o.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private enum Intent
    {
        Reply,
        ReplyAll,
        Forward,
    }

    private sealed class Composer(IMailItem parent, CompositionOptions options, Intent intent)
    {
        private readonly List<CompositionDefect> _defects = [];
        private readonly List<MailAddress> _unaddressable = [];
        private readonly DateTimeOffset _now = options.TimeProvider.GetUtcNow();
        private long _carried;

        public ComposedMessage Run()
        {
            ArgumentNullException.ThrowIfNull(parent);
            bool reply = intent != Intent.Forward;
            string subject = SubjectPrefixes.WithPrefix(Clean(parent.Subject ?? string.Empty).TrimEnd(), reply ? options.ReplyPrefix : options.ForwardPrefix);

            var to = new List<MailAddress>();
            var cc = new List<MailAddress>();
            if (reply)
            {
                Recipients(to, cc);
            }

            MimeEntity root = BuildBody();
            var headers = new List<(string, string)>();
            if (options.From is { } from && Usable(from.Address))
            {
                headers.Add(("From", FormatMailbox(from.Name, from.Address)));
            }

            if (to.Count > 0)
            {
                headers.Add(("To", string.Join(", ", to.Select(a => FormatMailbox(a.Name, a.Address)))));
            }

            if (cc.Count > 0)
            {
                headers.Add(("Cc", string.Join(", ", cc.Select(a => FormatMailbox(a.Name, a.Address)))));
            }

            headers.Add(("Subject", HeaderText.Unstructured(subject, options.Utf8Headers)));
            headers.Add(("Date", FormatDate(_now)));
            if (NewMessageId() is { } mid)
            {
                headers.Add(("Message-ID", "<" + mid + ">"));
            }

            if (reply)
            {
                Threading(headers);
            }

            ConversationHeaders(headers);
            headers.Add(("MIME-Version", "1.0"));
            int at = 0;
            foreach ((string n, string v) in headers)
            {
                root.Headers.Insert(at++, HeaderText.Header(n, v, options.Utf8Headers));
            }

            MapiMessage? mapi = null;
            LossReport? report = null;
            if (options.CreateMapi ?? parent is MapiMailItem)
            {
                ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(root);
                mapi = r.Value;
                report = r.Report;
            }

            return new ComposedMessage(root, mapi, report, subject, to, cc, _unaddressable, _defects);
        }

        // ---- recipients ----

        private HashSet<string> Own()
        {
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string a in options.OwnAddresses)
            {
                own.Add(a.Trim());
            }

            return own;
        }

        private MailAddress[] HeaderAddresses(string name)
        {
            if (parent is not MimeMailItem m)
            {
                return [];
            }

            IReadOnlyList<MimeAddress> raw = m.Entity.GetAddresses(name);
            if (raw.Count == 0)
            {
                return [];
            }

            List<MimeMailbox> boxes = AddressParser.Flatten(raw);
            var r = new MailAddress[boxes.Count];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = new MailAddress(boxes[i].Name, boxes[i].Address);
            }

            return r;
        }

        // Adds the usable addresses of 'source' to 'dest', skipping the ones in 'seen' (and recording them in it).
        private void Collect(IEnumerable<MailAddress> source, List<MailAddress> dest, HashSet<string> seen, HashSet<string>? exclude, ref int total, ref bool tooMany)
        {
            foreach (MailAddress a in source)
            {
                string address = a.Address.Trim();
                if (!address.Contains('@', StringComparison.Ordinal) && (a.X500Address is not null || address.StartsWith("/o=", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!_unaddressable.Contains(a))
                    {
                        _unaddressable.Add(a);
                        _defects.Add(new CompositionDefect(CompositionDefectKind.UnaddressableRecipient, address));
                    }

                    continue;
                }

                if (!Usable(address))
                {
                    _defects.Add(new CompositionDefect(CompositionDefectKind.InvalidAddressDropped, Truncate(address)));
                    continue;
                }

                if (exclude is not null && exclude.Contains(address))
                {
                    continue;
                }

                if (!seen.Add(address))
                {
                    continue;
                }

                if (total >= options.MaxRecipients)
                {
                    if (!tooMany)
                    {
                        tooMany = true;
                        _defects.Add(new CompositionDefect(CompositionDefectKind.TooManyRecipients, options.MaxRecipients.ToString(CultureInfo.InvariantCulture)));
                    }

                    return;
                }

                total++;
                dest.Add(new MailAddress(a.Name is null ? null : Clean(a.Name), address));
            }
        }

        private void Recipients(List<MailAddress> to, List<MailAddress> cc)
        {
            HashSet<string> own = Own();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            bool tooMany = false;
            IReadOnlyList<MailAddress> target = intent == Intent.Reply && HeaderAddresses("Mail-Reply-To") is { Length: > 0 } mrt ? mrt
                : parent.ReplyTo.Count > 0 ? parent.ReplyTo : parent.From;

            if (intent == Intent.Reply)
            {
                Collect(target, to, seen, null, ref total, ref tooMany);
                if (to.Count > 0 && to.TrueForAll(a => own.Contains(a.Address)) && parent.ToRecipients.Count > 0)
                {
                    // Replying to one's own message goes to the people it was sent to.
                    var others = new List<MailAddress>();
                    Collect(parent.ToRecipients, others, new HashSet<string>(StringComparer.OrdinalIgnoreCase), own, ref total, ref tooMany);
                    if (others.Count > 0)
                    {
                        to.Clear();
                        to.AddRange(others);
                    }
                }
            }
            else
            {
                MailAddress[] mft = HeaderAddresses("Mail-Followup-To");
                if (mft.Length > 0)
                {
                    // Mail-Followup-To names where follow-ups go (the list and the sender); it replaces To and Cc.
                    Collect(mft, to, seen, own, ref total, ref tooMany);
                }
                else
                {
                    Collect(target, to, seen, own, ref total, ref tooMany);
                    Collect(parent.ToRecipients, to, seen, own, ref total, ref tooMany);
                    Collect(parent.Cc, cc, seen, own, ref total, ref tooMany);
                }

                if (to.Count == 0 && cc.Count > 0)
                {
                    to.AddRange(cc);
                    cc.Clear();
                }

                if (to.Count == 0)
                {
                    // Nobody but the user: answer the people the user wrote to.
                    Collect(parent.ToRecipients.Count > 0 ? parent.ToRecipients : target, to, seen, null, ref total, ref tooMany);
                }
            }

            if (to.Count == 0 && cc.Count == 0)
            {
                _defects.Add(new CompositionDefect(CompositionDefectKind.NoReplyTarget, string.Empty));
            }
        }

        // RFC 5322 §3.4.1 addr-spec with the leniency of this library: one @, no white space, quotes, brackets or specials around it.
        internal static bool Usable(string address)
        {
            if (address.Length is 0 or > 320)
            {
                return false;
            }

            int at = address.IndexOf('@', StringComparison.Ordinal);
            if (at <= 0 || at == address.Length - 1 || address.IndexOf('@', at + 1) >= 0)
            {
                return false;
            }

            foreach (char c in address)
            {
                if (c <= ' ' || c == 0x7F || char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';' or ':' or '\\' or '"' or '(' or ')' or '[' or ']')
                {
                    return false;
                }
            }

            return true;
        }

        // ---- headers ----

        private string? NewMessageId()
        {
            if (options.MessageIdFactory is { } f)
            {
                string id = f();
                return IdUsable(id) ? id : null;
            }

            if (options.MessageIdDomain is { Length: > 0 } d)
            {
                Span<byte> r = new byte[16];
                Random(r);
                return Convert.ToHexStringLower(r) + "@" + d;
            }

            return null;
        }

        private void Random(Span<byte> buffer)
        {
            if (options.RandomBytes is { } f)
            {
                f(buffer);
            }
            else
            {
                RandomNumberGenerator.Fill(buffer);
            }
        }

        private static bool IdUsable(string id)
        {
            if (id.Length is 0 or > 512)
            {
                return false;
            }

            foreach (char c in id)
            {
                if (c <= ' ' || c == 0x7F || c is '<' or '>')
                {
                    return false;
                }
            }

            return true;
        }

        // RFC 5322 §3.6.4: In-Reply-To is the parent's Message-ID; References is the parent's References (the older In-Reply-To when it has none) plus that ID.
        private void Threading(List<(string, string)> headers)
        {
            IReadOnlyList<string> inherited = parent.References.Count > 0 ? parent.References : parent.InReplyTo;
            var refs = new List<string>();
            foreach (string id in inherited)
            {
                if (IdUsable(id))
                {
                    refs.Add(id);
                }
                else
                {
                    _defects.Add(new CompositionDefect(CompositionDefectKind.InvalidMessageIdDropped, Truncate(id)));
                }
            }

            string? pid = parent.MessageId;
            if (pid is not null && !IdUsable(pid))
            {
                _defects.Add(new CompositionDefect(CompositionDefectKind.InvalidMessageIdDropped, Truncate(pid)));
                pid = null;
            }

            if (pid is null)
            {
                _defects.Add(new CompositionDefect(CompositionDefectKind.ParentHasNoMessageId, string.Empty));
            }
            else
            {
                headers.Add(("In-Reply-To", "<" + pid + ">"));
                if (refs.Count == 0 || refs[^1] != pid)
                {
                    refs.Add(pid);
                }
            }

            // Too long a chain keeps the thread root and the most recent IDs (the same rule RFC 5537 §3.4.4 gives for news).
            int max = Math.Max(2, options.MaxReferences);
            if (refs.Count > max)
            {
                _defects.Add(new CompositionDefect(CompositionDefectKind.ReferencesTruncated, refs.Count.ToString(CultureInfo.InvariantCulture)));
                refs.RemoveRange(1, refs.Count - max);
            }

            if (refs.Count > 0)
            {
                headers.Add(("References", string.Join(' ', refs.Select(r => "<" + r + ">"))));
            }
        }

        private void ConversationHeaders(List<(string, string)> headers)
        {
            ReadOnlySpan<byte> index = parent.ConversationIndex.Span;
            bool want = options.ConversationIndex == ConversationIndexMode.Always
                || options.ConversationIndex == ConversationIndexMode.Auto && (parent is MapiMailItem || index.Length > 0);
            if (!want)
            {
                return;
            }

            string topic = Clean(parent.ConversationTopic ?? SubjectPrefixes.Strip(parent.Subject));
            Span<byte> one = new byte[1];
            Random(one);
            byte random = one[0];
            byte[]? next = null;
            bool clamped = false;
            if (ConversationIndex.TryParse(index, out _))
            {
                next = ConversationIndex.AppendLevel(index, _now, random, out clamped);
                if (next is null)
                {
                    _defects.Add(new CompositionDefect(CompositionDefectKind.ConversationIndexFull, string.Empty));
                    next = index.ToArray();
                }
            }
            else
            {
                if (index.Length > 0)
                {
                    _defects.Add(new CompositionDefect(CompositionDefectKind.ConversationIndexInvalid, string.Empty));
                }

                // A fresh thread starts at the original's own time, so the new level's delta says how long the reply took.
                DateTimeOffset t = parent.TryGetDate(MailDateSource.Delivery, out MailDate d) ? d.Value : parent.Sent?.Value ?? _now;
                if (t > _now)
                {
                    t = _now;
                }

                Span<byte> g = new byte[16];
                Random(g);
                next = ConversationIndex.AppendLevel(ConversationIndex.CreateHeader(t, new Guid(g)), _now, random, out clamped)!;
            }

            if (clamped)
            {
                _defects.Add(new CompositionDefect(CompositionDefectKind.ConversationIndexTimeClamped, string.Empty));
            }

            if (topic.Length > 0)
            {
                headers.Add(("Thread-Topic", HeaderText.Unstructured(topic, options.Utf8Headers)));
            }

            headers.Add(("Thread-Index", Convert.ToBase64String(next)));
        }

        // ---- body ----

        private MimeEntity BuildBody()
        {
            bool forward = intent == Intent.Forward;
            bool asAttachment = forward && options.ForwardStyle == ForwardStyle.Attachment;
            bool quote = options.Quote && !asAttachment;
            QuoteBodyFormat fmt = options.BodyFormat;

            string? pText = null;
            string? pHtml = null;
            bool flowed = false;
            bool delsp = false;
            if (quote)
            {
                if (fmt != QuoteBodyFormat.HtmlOnly && parent.TextBody is { } tb)
                {
                    pText = ReadBody(tb);
                    if (tb is MimeMailItem.MimeMailBody mb)
                    {
                        flowed = string.Equals(mb.Part.GetContentTypeParameter("format"), "flowed", StringComparison.OrdinalIgnoreCase);
                        delsp = flowed && string.Equals(mb.Part.GetContentTypeParameter("delsp"), "yes", StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (fmt != QuoteBodyFormat.TextOnly && parent.HtmlBody is { } hb)
                {
                    pHtml = ReadBody(hb);
                }

                // A parent that has only HTML still gets a text part: the text made from its HTML (Bodies.HtmlToText), so plain-text clients see the quote too.
                if (pText is null && fmt != QuoteBodyFormat.HtmlOnly && parent.HtmlBody is { } hb2)
                {
                    string src = pHtml ?? ReadBody(hb2);
                    if (src.Length > 0)
                    {
                        pText = Bodies.HtmlToText.Convert(src).Text;
                    }
                }

                if (pText is null && pHtml is null && (parent.TextBody is not null || parent.HtmlBody is not null))
                {
                    _defects.Add(new CompositionDefect(CompositionDefectKind.BodyNotAvailable, fmt.ToString()));
                }
                else if (pText is null && pHtml is null && fmt != QuoteBodyFormat.Auto)
                {
                    _defects.Add(new CompositionDefect(CompositionDefectKind.BodyNotAvailable, fmt.ToString()));
                }
            }

            bool htmlOut = pHtml is not null;
            bool textOut = pText is not null || !htmlOut;

            MimeEntity? textPart = null;
            if (textOut)
            {
                textPart = TextPart(AssembleText(pText, flowed), flowed, delsp);
            }

            var related = new List<MimeEntity>();
            var mixed = new List<MimeEntity>();
            MimeEntity? htmlPart = null;
            if (htmlOut)
            {
                htmlPart = HtmlPart(AssembleHtml(pHtml!));
                foreach (IMailAttachment a in parent.Attachments)
                {
                    bool referenced = a.ContentId is { Length: > 0 } cid && pHtml!.Contains("cid:" + cid, StringComparison.OrdinalIgnoreCase);
                    if (referenced)
                    {
                        if (Carry(a, inline: true) is { } part)
                        {
                            related.Add(part);
                        }
                    }
                    else if (forward || options.IncludeAttachments)
                    {
                        if (Carry(a, inline: false) is { } part)
                        {
                            mixed.Add(part);
                        }
                    }
                }
            }
            else if (forward && !asAttachment || options.IncludeAttachments)
            {
                foreach (IMailAttachment a in parent.Attachments)
                {
                    if (Carry(a, inline: false) is { } part)
                    {
                        mixed.Add(part);
                    }
                }
            }

            if (asAttachment && EmbeddedParent() is { } embedded)
            {
                var mp = new MimeMessagePart("message/rfc822");
                mp.Message = embedded;
                mp.Headers.Add("Content-Disposition", "attachment" + HeaderText.Parameter("filename", AttachmentName(), options.Utf8Headers));
                mixed.Add(mp);
            }

            MimeEntity body;
            if (htmlPart is not null && related.Count > 0)
            {
                var rel = new MimeMultipart("related", Boundary());
                rel.Add(htmlPart);
                foreach (MimeEntity r in related)
                {
                    rel.Add(r);
                }

                htmlPart = rel;
            }

            if (textPart is not null && htmlPart is not null)
            {
                var alt = new MimeMultipart("alternative", Boundary());
                alt.Add(textPart);
                alt.Add(htmlPart);
                body = alt;
            }
            else
            {
                body = textPart ?? htmlPart!;
            }

            if (mixed.Count == 0)
            {
                return body;
            }

            var top = new MimeMultipart("mixed", Boundary());
            top.Add(body);
            foreach (MimeEntity m in mixed)
            {
                top.Add(m);
            }

            return top;
        }

        private string Boundary()
        {
            Span<byte> r = new byte[12];
            Random(r);
            return "=_onemail_" + Convert.ToHexStringLower(r);
        }

        private string ReadBody(MailBody body)
        {
            string s = body.ReadText();
            if (s.Length <= options.MaxQuoteCharacters)
            {
                return s;
            }

            int cut = s.LastIndexOf('\n', options.MaxQuoteCharacters - 1);
            _defects.Add(new CompositionDefect(CompositionDefectKind.QuoteTruncated, s.Length.ToString(CultureInfo.InvariantCulture)));
            s = s[..(cut > 0 ? cut + 1 : options.MaxQuoteCharacters)];
            if (s.Length > 0 && char.IsHighSurrogate(s[^1]))
            {
                s = s[..^1];
            }

            return s;
        }

        private string Attribution()
        {
            if (options.Attribution is { } f)
            {
                return f(parent);
            }

            string who = parent.From.Count > 0 ? Clean(parent.From[0].ToString()) : string.Empty;
            string? when = parent.Sent is { } d ? FormatDate(d.Value) : null;
            if (who.Length == 0)
            {
                who = "someone";
            }

            return when is null ? who + " wrote:" : "On " + when + ", " + who + " wrote:";
        }

        private static string Stuff(string line, bool flowed) =>
            flowed && (line.StartsWith(' ') || line.StartsWith('>') || line.StartsWith("From ", StringComparison.Ordinal)) ? " " + line : line;

        private static IEnumerable<string> Lines(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                int j = text.AsSpan(i).IndexOfAny('\r', '\n');
                if (j < 0)
                {
                    yield return text[i..];
                    yield break;
                }

                yield return text.Substring(i, j);
                i += j + 1;
                if (text[i - 1] == '\r' && i < text.Length && text[i] == '\n')
                {
                    i++;
                }
            }
        }

        private string AssembleText(string? original, bool flowed)
        {
            var sb = new StringBuilder();
            if (options.Text is { Length: > 0 } author)
            {
                foreach (string l in Lines(author))
                {
                    sb.Append(Stuff(l, flowed)).Append("\r\n");
                }

                sb.Append("\r\n");
            }

            if (intent == Intent.Forward)
            {
                if (options.ForwardStyle == ForwardStyle.Attachment || !options.Quote)
                {
                    return sb.ToString();
                }

                sb.Append("---------- ").Append(Clean(options.ForwardBanner)).Append(" ----------\r\n");
                HeaderLine(sb, "From", parent.From);
                if (parent.Sent is { } d)
                {
                    sb.Append("Date: ").Append(FormatDate(d.Value)).Append("\r\n");
                }

                sb.Append("Subject: ").Append(Clean(parent.Subject ?? string.Empty)).Append("\r\n");
                HeaderLine(sb, "To", parent.ToRecipients);
                HeaderLine(sb, "Cc", parent.Cc);
                sb.Append("\r\n");
                if (original is not null)
                {
                    foreach (string l in Lines(original))
                    {
                        sb.Append(Stuff(l, flowed)).Append("\r\n");
                    }
                }

                return sb.ToString();
            }

            if (!options.Quote)
            {
                return sb.ToString();
            }

            sb.Append(Stuff(Clean(Attribution()), flowed)).Append("\r\n");
            if (original is not null)
            {
                foreach (string l in Lines(original))
                {
                    // RFC 3676 §4.5: in format=flowed a quoted line is ">" plus the line, with no space when the line is itself quoted.
                    sb.Append(l.Length == 0 ? ">" : flowed && l[0] == '>' ? ">" + l : "> " + l).Append("\r\n");
                }
            }

            return sb.ToString();
        }

        private static void HeaderLine(StringBuilder sb, string label, IReadOnlyList<MailAddress> list)
        {
            if (list.Count > 0)
            {
                sb.Append(label).Append(": ").Append(string.Join(", ", list.Select(a => Clean(a.ToString())))).Append("\r\n");
            }
        }

        private string AssembleHtml(string original)
        {
            string inner = BodyInner(original);
            var sb = new StringBuilder("<html><head><meta charset=\"utf-8\"></head><body>");
            if (options.Html is { Length: > 0 } ah)
            {
                sb.Append(ah);
            }
            else if (options.Text is { Length: > 0 } at)
            {
                foreach (string l in Lines(at))
                {
                    sb.Append("<div>").Append(l.Length == 0 ? "<br>" : WebUtility.HtmlEncode(l)).Append("</div>");
                }

                sb.Append("<div><br></div>");
            }

            if (intent == Intent.Forward)
            {
                sb.Append("<div>---------- ").Append(WebUtility.HtmlEncode(Clean(options.ForwardBanner))).Append(" ----------<br>");
                HtmlLine(sb, "From", parent.From);
                if (parent.Sent is { } d)
                {
                    sb.Append("<b>Date:</b> ").Append(WebUtility.HtmlEncode(FormatDate(d.Value))).Append("<br>");
                }

                sb.Append("<b>Subject:</b> ").Append(WebUtility.HtmlEncode(Clean(parent.Subject ?? string.Empty))).Append("<br>");
                HtmlLine(sb, "To", parent.ToRecipients);
                HtmlLine(sb, "Cc", parent.Cc);
                sb.Append("<br></div>").Append(inner);
            }
            else
            {
                sb.Append("<div>").Append(WebUtility.HtmlEncode(Clean(Attribution()))).Append("</div><blockquote type=\"cite\">").Append(inner).Append("</blockquote>");
            }

            return sb.Append("</body></html>").ToString();
        }

        private static void HtmlLine(StringBuilder sb, string label, IReadOnlyList<MailAddress> list)
        {
            if (list.Count > 0)
            {
                sb.Append("<b>").Append(label).Append(":</b> ").Append(WebUtility.HtmlEncode(string.Join(", ", list.Select(a => Clean(a.ToString()))))).Append("<br>");
            }
        }

        // The quoted content is what lies between <body> and </body>; a fragment without them is taken whole.
        private static string BodyInner(string html)
        {
            int b = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (b >= 0)
            {
                int gt = html.IndexOf('>', b);
                if (gt >= 0)
                {
                    int end = html.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
                    return end > gt ? html[(gt + 1)..end] : html[(gt + 1)..];
                }
            }

            return html;
        }

        private static MimePart TextPart(string text, bool flowed, bool delsp)
        {
            string type = "text/plain; charset=utf-8" + (flowed ? "; format=flowed" + (delsp ? "; delsp=yes" : string.Empty) : string.Empty);
            return EncodedText(type, text);
        }

        private static MimePart HtmlPart(string html) => EncodedText("text/html; charset=utf-8", html);

        private static MimePart EncodedText(string type, string text)
        {
            var part = new MimePart(type);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            bool plain = Ascii.IsValid(bytes) && LongestLine(bytes) <= 78;
            part.SetDecodedContent(bytes, plain ? ContentTransferEncoding.SevenBit : ContentTransferEncoding.QuotedPrintable);
            return part;
        }

        private static int LongestLine(ReadOnlySpan<byte> b)
        {
            int best = 0;
            int cur = 0;
            foreach (byte c in b)
            {
                if (c == (byte)'\n')
                {
                    cur = 0;
                }
                else if (++cur > best)
                {
                    best = cur;
                }
            }

            return best;
        }

        // ---- carried content ----

        private MimeEntity? Carry(IMailAttachment a, bool inline)
        {
            byte[]? data = ReadLimited(a);
            if (data is null)
            {
                return null;
            }

            string type = a.ContentType.Contains('/', StringComparison.Ordinal) && Ascii.IsValid(a.ContentType) && a.ContentType.AsSpan().IndexOfAny(" \t\r\n;,\"()<>@[]\\=?".AsSpan()) < 0
                ? a.ContentType : "application/octet-stream";
            string? name = a.FileName is { } fn ? Clean(fn) : null;
            string disposition = (inline ? "inline" : "attachment") + (name is { Length: > 0 } ? HeaderText.Parameter("filename", name, options.Utf8Headers) : string.Empty);
            MimeEntity part;
            if (type == "message/rfc822")
            {
                var mp = new MimeMessagePart(type);
                mp.Message = MimeEntity.Parse(data);
                part = mp;
            }
            else
            {
                var p = new MimePart(type + (name is { Length: > 0 } ? HeaderText.Parameter("name", name, options.Utf8Headers) : string.Empty));
                p.SetDecodedContent(data);
                part = p;
            }

            part.Headers.Add("Content-Disposition", disposition);
            if (a.ContentId is { Length: > 0 } cid && IdUsable(cid))
            {
                part.Headers.Add("Content-ID", "<" + cid + ">");
            }

            return part;
        }

        private byte[]? ReadLimited(IMailAttachment a)
        {
            try
            {
                using Stream s = a.Open();
                long room = options.MaxCarriedBytes - _carried;
                using var ms = new MemoryStream();
                byte[] buffer = new byte[16 * 1024];
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + n > room)
                    {
                        _defects.Add(new CompositionDefect(CompositionDefectKind.AttachmentSkipped, Truncate(a.FileName ?? a.ContentType)));
                        return null;
                    }

                    ms.Write(buffer, 0, n);
                }

                _carried += ms.Length;
                return ms.ToArray();
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException or NotSupportedException)
            {
                _defects.Add(new CompositionDefect(CompositionDefectKind.AttachmentSkipped, Truncate(a.FileName ?? a.ContentType)));
                return null;
            }
        }

        private string AttachmentName()
        {
            var sb = new StringBuilder();
            foreach (char c in Clean(parent.Subject ?? string.Empty))
            {
                if (sb.Length >= 80)
                {
                    break;
                }

                sb.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
            }

            string n = sb.ToString().Trim();
            return (n.Length == 0 ? "message" : n) + ".eml";
        }

        // The original as a message of its own: the item's own bytes for MIME items, the MS-OXCMAIL export for Outlook items.
        private MimeEntity? EmbeddedParent()
        {
            switch (parent)
            {
                case MimeMailItem m:
                {
                    byte[] bytes = m.Entity.ToArray();
                    if (_carried + bytes.Length > options.MaxCarriedBytes)
                    {
                        _defects.Add(new CompositionDefect(CompositionDefectKind.AttachmentSkipped, "original"));
                        return null;
                    }

                    _carried += bytes.Length;
                    return MimeEntity.Parse(bytes);
                }

                case MapiMailItem mi:
                    return new MapiToMimeConverter().Convert(mi.Message).Value;
                default:
                {
                    _defects.Add(new CompositionDefect(CompositionDefectKind.ParentSynthesized, parent.GetType().Name));
                    var p = new MimePart("text/plain; charset=utf-8");
                    if (parent.From.Count > 0)
                    {
                        p.Headers.Add("From", string.Join(", ", parent.From.Where(a => Usable(a.Address)).Select(a => FormatMailbox(a.Name, a.Address))));
                    }

                    if (parent.ToRecipients.Count > 0)
                    {
                        p.Headers.Add("To", string.Join(", ", parent.ToRecipients.Where(a => Usable(a.Address)).Select(a => FormatMailbox(a.Name, a.Address))));
                    }

                    p.Headers.Add(HeaderText.Header("Subject", Clean(parent.Subject ?? string.Empty), options.Utf8Headers));
                    if (parent.Sent is { } d)
                    {
                        p.Headers.Add("Date", FormatDate(d.Value));
                    }

                    if (parent.MessageId is { } id && IdUsable(id))
                    {
                        p.Headers.Add("Message-ID", "<" + id + ">");
                    }

                    string text = parent.TextBody is { } tb ? ReadBody(tb) : string.Empty;
                    p.SetDecodedContent(Encoding.UTF8.GetBytes(text), ContentTransferEncoding.QuotedPrintable);
                    return p;
                }
            }
        }

        // ---- text helpers ----

        private static string Truncate(string s) => s.Length <= 64 ? Clean(s) : Clean(s[..64]);

        // Header injection and display tricks: control characters and line separators become spaces.
        private static string Clean(string s)
        {
            if (!s.AsSpan().ContainsAnyExceptInRange((char)0x20, (char)0x7E) && s.IndexOf((char)0x7F) < 0)
            {
                return s;
            }

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(c < ' ' || c == 0x7F || c is '\u0085' or '\u2028' or '\u2029' ? ' ' : c);
            }

            return sb.ToString();
        }

        private string FormatMailbox(string? name, string address) => HeaderText.Mailbox(name is null ? null : Clean(name), address, options.Utf8Headers);
    }
}
