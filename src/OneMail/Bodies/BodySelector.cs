using System;
using System.Collections.Generic;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Rtf;

namespace RecursiveAI.OneMail.Bodies;

/// <summary>The representation of a body.</summary>
public enum BodyKind
{
    /// <summary>The item has no body that can be shown.</summary>
    None,

    /// <summary>Plain text.</summary>
    Text,

    /// <summary>HTML.</summary>
    Html,

    /// <summary>RTF written as RTF (not an encapsulated HTML or text body); only chosen when nothing else is available.</summary>
    Rtf,
}

/// <summary>How a body returned by <see cref="BodySelector"/> was made from what the item holds.</summary>
public enum BodyDerivation
{
    /// <summary>It is the item's own body (for an Outlook item that may be a body taken out of its RTF).</summary>
    None,

    /// <summary>Plain text made from HTML by <see cref="HtmlToText"/>.</summary>
    HtmlToText,

    /// <summary>HTML made from plain text by <see cref="TextToHtml"/>.</summary>
    TextToHtml,

    /// <summary>Plain text with its <c>format=flowed</c> soft breaks joined by <see cref="FormatFlowed"/>.</summary>
    FlowedDecoded,

    /// <summary>A body taken out of the item's RTF by de-encapsulation because the RTF is newer than the stored plain text.</summary>
    RtfDeencapsulated,
}

/// <summary>Why <see cref="BodySelector.Select"/> chose what it chose. One value per rule in <c>docs/bodies.md</c>.</summary>
public enum BodyReason
{
    /// <summary>The item has no usable body.</summary>
    NoBody,

    /// <summary>HTML and plain text were both there, and HTML is preferred.</summary>
    HtmlPreferred,

    /// <summary>The item has only HTML.</summary>
    HtmlOnly,

    /// <summary>The item has only plain text.</summary>
    TextOnly,

    /// <summary>The Outlook item says it was written as plain text (<c>PidTagNativeBody</c>), so its HTML is a rendering of it.</summary>
    NativeText,

    /// <summary>The HTML has nothing to show, so the plain text was chosen.</summary>
    EmptyHtml,

    /// <summary>The Outlook item's RTF is newer than its plain text (<c>PidTagRtfInSync</c> false), so what the RTF holds was chosen.</summary>
    StaleText,

    /// <summary>The item has only RTF written as RTF.</summary>
    RtfOnly,
}

/// <summary>A body made by one of the conversions in this namespace. It holds the text; <see cref="MailBody.Open"/> yields it as UTF-8.</summary>
public sealed class DerivedMailBody : MailBody
{
    private readonly string _contentType;
    private readonly string _text;

    internal DerivedMailBody(string contentType, string text, BodyDerivation derivation, IReadOnlyList<BodyIssue> issues)
    {
        _contentType = contentType;
        _text = text;
        Derivation = derivation;
        Issues = issues;
    }

    /// <summary>How the body was made.</summary>
    public BodyDerivation Derivation { get; }

    /// <summary>What the conversion reported; empty when it was clean.</summary>
    public IReadOnlyList<BodyIssue> Issues { get; }

    /// <inheritdoc/>
    public override string ContentType => _contentType;

    /// <inheritdoc/>
    public override string? Charset => "utf-8";

    /// <inheritdoc/>
    public override System.IO.Stream Open() => new System.IO.MemoryStream(Encoding.UTF8.GetBytes(_text), writable: false);

    /// <inheritdoc/>
    public override string ReadText() => _text;
}

/// <summary>The outcome of <see cref="BodySelector.Select"/>.</summary>
/// <param name="Kind">The kind of the chosen body; <see cref="BodyKind.None"/> when the item has none.</param>
/// <param name="Body">The body: the item's own, or a <see cref="DerivedMailBody"/>. Null when <paramref name="Kind"/> is <see cref="BodyKind.None"/>.</param>
/// <param name="Reason">The rule that chose it.</param>
/// <param name="Issues">Ambiguities and limits met while choosing (for example <see cref="BodyIssueKind.AmbiguousAlternativeOrder"/>).</param>
public sealed record SelectedBody(BodyKind Kind, MailBody? Body, BodyReason Reason, IReadOnlyList<BodyIssue> Issues);

/// <summary>
/// Chooses an item's best body and makes the text or HTML an item lacks. The same rules apply to every item type; they are written out in
/// <c>docs/bodies.md</c>.
/// </summary>
/// <remarks>
/// <para>Order: (1) for an Outlook item whose RTF is newer than its plain text (<c>PidTagRtfInSync</c> false), the body de-encapsulated from the RTF;
/// (2) for an Outlook item whose <c>PidTagNativeBody</c> says plain text, the plain text; (3) HTML, when it has visible text or an image;
/// (4) plain text; (5) RTF written as RTF. The item view already applies RFC 2046 §5.1.4 inside the MIME tree (<see cref="IMailItem.HtmlBody"/>
/// is the last HTML alternative, and so on) and offers the HTML and text an Outlook item's RTF encapsulates.</para>
/// <para>Nothing here throws on malformed content; a body that cannot be read counts as absent.</para>
/// </remarks>
public static class BodySelector
{
    /// <summary>Chooses the best body of an item.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The choice. HTML is returned as stored; plain text is returned with <c>format=flowed</c> joined (a <see cref="DerivedMailBody"/> with <see cref="BodyDerivation.FlowedDecoded"/>).</returns>
    public static SelectedBody Select(IMailItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        List<BodyIssue> issues = [];
        MailBody? html = item.HtmlBody;
        MailBody? text = item.TextBody;
        MailBody? rtf = item.RtfBody;
        string? htmlSource = null;
        string? textSource = null;

        BodyReason? forced = null;
        if (item is MapiMailItem mapi && rtf is not null)
        {
            // MS-OXCMSG PidTagRtfInSync: false means the RTF was edited after the plain text was last written, so the text is stale.
            if (mapi.Message.GetBoolean(MapiPid.RtfInSync) == false && TryDeencapsulate(rtf, out MailBody? fromHtml, out MailBody? fromText))
            {
                if (fromHtml is not null || fromText is not null)
                {
                    html = fromHtml ?? html;
                    text = fromText ?? text;
                    forced = BodyReason.StaleText;
                    issues.Add(new BodyIssue(BodyIssueKind.AmbiguousStaleText));
                }
            }
        }

        bool htmlUsable = false;
        if (html is not null)
        {
            htmlSource = TryRead(html);
            htmlUsable = htmlSource is not null && HasVisibleContent(htmlSource);
        }

        bool textUsable = false;
        if (text is not null)
        {
            textSource = TryRead(text);
            textUsable = textSource is not null && !string.IsNullOrWhiteSpace(textSource);
        }

        if (item is MimeMailItem mime && htmlUsable && textUsable && HtmlBeforePlain(mime.Entity, 0))
        {
            issues.Add(new BodyIssue(BodyIssueKind.AmbiguousAlternativeOrder));
        }

        bool preferText = false;
        BodyReason reason;
        if (htmlUsable && textUsable)
        {
            // MS-OXCMSG PidTagNativeBody: 1 = the message was written as plain text, 4 = clear-signed text.
            if (item is MapiMailItem m2 && m2.Message.GetInt32(MapiPid.NativeBody) is 1 or 4)
            {
                preferText = true;
                reason = BodyReason.NativeText;
            }
            else
            {
                reason = forced ?? BodyReason.HtmlPreferred;
            }
        }
        else if (htmlUsable)
        {
            reason = forced ?? BodyReason.HtmlOnly;
        }
        else if (textUsable)
        {
            preferText = true;
            reason = html is not null ? BodyReason.EmptyHtml : forced ?? BodyReason.TextOnly;
            if (html is not null)
            {
                issues.Add(new BodyIssue(BodyIssueKind.AmbiguousEmptyHtml));
            }
        }
        else if (rtf is not null)
        {
            issues.Add(new BodyIssue(BodyIssueKind.OnlyRtfAvailable));
            return new SelectedBody(BodyKind.Rtf, rtf, BodyReason.RtfOnly, issues);
        }
        else
        {
            return new SelectedBody(BodyKind.None, null, BodyReason.NoBody, issues);
        }

        if (preferText)
        {
            return new SelectedBody(BodyKind.Text, DecodeText(text!, textSource!), reason, issues);
        }

        return new SelectedBody(BodyKind.Html, html, reason, issues);
    }

    /// <summary>The item's text body, made from its HTML when it has none: always plain text, or null when the item has neither.</summary>
    /// <param name="item">The item.</param>
    /// <param name="options">Options for the HTML to text step; null for the defaults.</param>
    /// <returns>The stored plain text (with <c>format=flowed</c> joined), else the best HTML converted by <see cref="HtmlToText"/>, else null. A body that cannot be read counts as absent.</returns>
    public static MailBody? GetText(IMailItem item, HtmlToTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        SelectedBody best = Select(item);
        if (best.Kind == BodyKind.Text)
        {
            return best.Body;
        }

        // The stored text, when it is there and has something in it, is the author's own words; otherwise convert the HTML.
        if (item.TextBody is { } t && TryRead(t) is { } ts && !string.IsNullOrWhiteSpace(ts))
        {
            return DecodeText(t, ts);
        }

        if (best.Kind == BodyKind.Html && best.Body is { } h && TryRead(h) is { } hs)
        {
            BodyConversion c = HtmlToText.Convert(hs, options);
            return new DerivedMailBody("text/plain", c.Text, BodyDerivation.HtmlToText, c.Issues);
        }

        return null;
    }

    /// <summary>The item's HTML body, made from its plain text when it has none.</summary>
    /// <param name="item">The item.</param>
    /// <param name="options">Options for the text to HTML step; null for the defaults. <see cref="TextToHtmlOptions.Flowed"/> is set from the body itself.</param>
    /// <returns>The stored HTML, else the plain text converted by <see cref="TextToHtml"/>, else null.</returns>
    public static MailBody? GetHtml(IMailItem item, TextToHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        SelectedBody best = Select(item);
        if (best.Kind == BodyKind.Html)
        {
            return best.Body;
        }

        if (item.HtmlBody is { } h && TryRead(h) is { } hs && HasVisibleContent(hs))
        {
            return h;
        }

        if (best.Kind == BodyKind.Text && best.Body is { } t && TryRead(t) is { } ts)
        {
            // The text is already unwrapped (Select decoded it), so the conversion must not unwrap it again.
            BodyConversion c = TextToHtml.Convert(ts, (options ?? TextToHtmlOptions.Default) with { Flowed = false });
            return new DerivedMailBody("text/html", c.Text, BodyDerivation.TextToHtml, c.Issues);
        }

        return null;
    }

    private static MailBody DecodeText(MailBody text, string source)
    {
        if (!text.IsFormatFlowed)
        {
            return text;
        }

        // Quoted lines come out as "> " prefixes, which is also what TextToHtml reads.
        string decoded = FormatFlowed.Decode(source, text.FlowedDelSp);
        return new DerivedMailBody("text/plain", decoded, BodyDerivation.FlowedDecoded, []);
    }

    private static string? TryRead(MailBody body)
    {
        try
        {
            return body.ReadText();
        }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // HTML with no text and no picture would show a blank page.
    private static bool HasVisibleContent(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (html.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HtmlToText.Convert(html, EmptyCheck).Text.Length > 0;
    }

    private static readonly HtmlToTextOptions EmptyCheck = new() { ApplyStyleSheets = false, Images = ImageStyle.None, MaxOutputChars = 1 << 16 };

    private static bool TryDeencapsulate(MailBody rtf, out MailBody? html, out MailBody? text)
    {
        html = null;
        text = null;
        try
        {
            using System.IO.Stream s = rtf.Open();
            using System.IO.MemoryStream ms = new();
            s.CopyTo(ms);
            RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate(ms.ToArray());
            switch (r.Kind)
            {
                case RtfEncapsulation.Html:
                    html = new DerivedMailBody("text/html", r.Text, BodyDerivation.RtfDeencapsulated, []);
                    return true;
                case RtfEncapsulation.PlainText:
                    text = new DerivedMailBody("text/plain", r.Text, BodyDerivation.RtfDeencapsulated, []);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    // RFC 2046 §5.1.4 puts the preferred alternative last. When HTML comes before the plain text, the order says "plain text is preferred";
    // mail clients show the HTML regardless, so Select does too, and says that it saw the order.
    private static bool HtmlBeforePlain(MimeEntity e, int depth)
    {
        if (e is not MimeMultipart m || depth > 32)
        {
            return false;
        }

        if (m.MediaSubType == "alternative")
        {
            int html = -1;
            int plain = -1;
            for (int i = 0; i < m.Children.Count; i++)
            {
                if (html < 0 && Has(m.Children[i], "html", 0))
                {
                    html = i;
                }

                if (plain < 0 && Has(m.Children[i], "plain", 0))
                {
                    plain = i;
                }
            }

            if (html >= 0 && plain >= 0 && html < plain)
            {
                return true;
            }
        }

        foreach (MimeEntity c in m.Children)
        {
            if (HtmlBeforePlain(c, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Has(MimeEntity e, string subtype, int depth)
    {
        switch (e)
        {
            case MimePart p:
                return p.MediaTopLevelType == "text" && p.MediaSubType == subtype && p.Disposition != "attachment";
            case MimeMultipart m when depth < 32:
                foreach (MimeEntity c in m.Children)
                {
                    if (Has(c, subtype, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}
