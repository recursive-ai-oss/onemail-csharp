using System;
using System.Collections.Generic;
using System.Text;

namespace RecursiveAI.OneMail.Bodies;

/// <summary>Options for <see cref="TextToHtml"/>.</summary>
public sealed record TextToHtmlOptions
{
    /// <summary>The options used when none are given.</summary>
    public static TextToHtmlOptions Default { get; } = new();

    /// <summary>True (default) to link <c>http://</c>, <c>https://</c>, <c>ftp://</c>, <c>mailto:</c> and <c>www.</c> addresses.</summary>
    public bool DetectLinks { get; init; } = true;

    /// <summary>True when the text is a <c>format=flowed</c> body (RFC 3676): soft breaks are joined before rendering. Default false.</summary>
    public bool Flowed { get; init; }

    /// <summary>True when the flowed body said <c>delsp=yes</c>. Only read when <see cref="Flowed"/> is set.</summary>
    public bool DelSp { get; init; }

    /// <summary>True (default) to write a whole document (<c>DOCTYPE</c>, <c>head</c> with the charset, <c>body</c>); false to write only the body's content.</summary>
    public bool FullDocument { get; init; } = true;

    /// <summary>The number of non-breaking spaces a tab becomes. Default 4.</summary>
    public int TabWidth { get; init; } = 4;

    /// <summary>The input is cut after this many characters (<see cref="BodyIssueKind.InputTruncated"/>). Default 16,777,216.</summary>
    public int MaxInputChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>The output stops growing at this many characters (<see cref="BodyIssueKind.OutputTruncated"/>). Default 64 MiChars.</summary>
    public int MaxOutputChars { get; init; } = 64 * 1024 * 1024;

    /// <summary>Quote levels beyond this nest no deeper (<see cref="BodyIssueKind.QuoteDepthCapped"/>). Default 32.</summary>
    public int MaxQuoteDepth { get; init; } = 32;
}

/// <summary>
/// Plain text to HTML: escaped, with paragraphs, line breaks, quote levels as nested <c>blockquote</c> elements and conservative link
/// detection. The HTML parses without errors in a conforming parser.
/// </summary>
/// <remarks>
/// Rules (see <c>docs/bodies.md</c>): a blank line ends a paragraph, any other line break inside one is a <c>&lt;br&gt;</c>; a line's leading
/// <c>&gt;</c> markers (optionally separated by single spaces) give its quote depth, one blockquote per level; leading and repeated spaces
/// and tabs are kept with non-breaking spaces; control characters are removed. Nothing is ever executed or fetched, and the output
/// contains no attributes other than <c>href</c> on links (scheme-checked: only the listed ones) and <c>type="cite"</c>.
/// </remarks>
public static class TextToHtml
{
    /// <summary>Converts text to HTML.</summary>
    /// <param name="text">The text, with any line ending. Never throws on content.</param>
    /// <param name="options">Options; null for <see cref="TextToHtmlOptions.Default"/>.</param>
    /// <returns>The HTML and what the conversion reported.</returns>
    public static BodyConversion Convert(string text, TextToHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= TextToHtmlOptions.Default;
        List<BodyIssue> issues = [];
        if (text.Length > options.MaxInputChars)
        {
            text = text[..options.MaxInputChars];
            issues.Add(new BodyIssue(BodyIssueKind.InputTruncated, options.MaxInputChars));
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        IReadOnlyList<FlowedParagraph> paragraphs = options.Flowed ? FormatFlowed.Parse(text, options.DelSp) : FormatFlowed.ParsePlain(text);
        return Render(paragraphs, options, issues);
    }

    /// <summary>Converts paragraphs (for example from <see cref="FormatFlowed.Parse"/>) to HTML.</summary>
    /// <param name="paragraphs">The paragraphs; their text must not contain line breaks.</param>
    /// <param name="options">Options; null for the defaults. <see cref="TextToHtmlOptions.Flowed"/> is ignored.</param>
    /// <returns>The HTML and what the conversion reported.</returns>
    public static BodyConversion Convert(IReadOnlyList<FlowedParagraph> paragraphs, TextToHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        return Render(paragraphs, options ?? TextToHtmlOptions.Default, []);
    }

    private static BodyConversion Render(IReadOnlyList<FlowedParagraph> paragraphs, TextToHtmlOptions o, List<BodyIssue> issues)
    {
        StringBuilder sb = new();
        if (o.FullDocument)
        {
            sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title></title>\n</head>\n<body>\n");
        }

        int depth = 0;
        bool inP = false;
        bool capped = false;
        bool control = false;
        foreach (FlowedParagraph para in paragraphs)
        {
            if (sb.Length > o.MaxOutputChars)
            {
                issues.Add(new BodyIssue(BodyIssueKind.OutputTruncated));
                break;
            }

            if (para.Text.Length == 0)
            {
                if (inP)
                {
                    sb.Append("</p>\n");
                    inP = false;
                }

                continue;
            }

            int want = para.QuoteDepth;
            if (want > o.MaxQuoteDepth)
            {
                want = o.MaxQuoteDepth;
                if (!capped)
                {
                    issues.Add(new BodyIssue(BodyIssueKind.QuoteDepthCapped));
                    capped = true;
                }
            }

            if (want != depth && inP)
            {
                sb.Append("</p>\n");
                inP = false;
            }

            while (depth > want)
            {
                sb.Append("</blockquote>\n");
                depth--;
            }

            while (depth < want)
            {
                sb.Append("<blockquote type=\"cite\">\n");
                depth++;
            }

            if (inP)
            {
                sb.Append("<br>\n");
            }
            else
            {
                sb.Append("<p>");
                inP = true;
            }

            control |= AppendLine(sb, para.Text, o);
        }

        if (inP)
        {
            sb.Append("</p>\n");
        }

        while (depth-- > 0)
        {
            sb.Append("</blockquote>\n");
        }

        if (control)
        {
            issues.Add(new BodyIssue(BodyIssueKind.ControlCharactersRemoved));
        }

        if (o.FullDocument)
        {
            sb.Append("</body>\n</html>\n");
        }

        return new BodyConversion(sb.ToString(), issues);
    }

    // Writes one line: escaped, spaces kept, links found. Returns true when it dropped a control character.
    private static bool AppendLine(StringBuilder sb, string line, TextToHtmlOptions o)
    {
        bool dropped = false;
        bool prevSpace = true; // a space at the start of the line must survive, so it is written as &nbsp;
        int i = 0;
        while (i < line.Length)
        {
            if (o.DetectLinks && TryLink(line, i, out int len, out string href))
            {
                sb.Append("<a href=\"");
                AppendEscaped(sb, href);
                sb.Append("\">");
                AppendEscaped(sb, line.AsSpan(i, len));
                sb.Append("</a>");
                i += len;
                prevSpace = false;
                continue;
            }

            char c = line[i];
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    prevSpace = false;
                    break;
                case '<':
                    sb.Append("&lt;");
                    prevSpace = false;
                    break;
                case '>':
                    sb.Append("&gt;");
                    prevSpace = false;
                    break;
                case ' ':
                    sb.Append(prevSpace ? "&nbsp;" : " ");
                    prevSpace = true;
                    break;
                case '\t':
                    for (int t = 0; t < Math.Max(o.TabWidth, 1); t++)
                    {
                        sb.Append("&nbsp;");
                    }

                    prevSpace = true;
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
                    {
                        // A pair is fine unless it is a noncharacter (U+xFFFE, U+xFFFF), which the HTML parser reports.
                        int cp = char.ConvertToUtf32(c, line[i + 1]);
                        if ((cp & 0xFFFE) == 0xFFFE)
                        {
                            dropped = true;
                        }
                        else
                        {
                            sb.Append(c).Append(line[i + 1]);
                        }

                        i += 2;
                        prevSpace = false;
                        continue;
                    }

                    if (char.IsSurrogate(c))
                    {
                        sb.Append('\uFFFD');
                    }
                    else if (c < 0x20 || (c >= 0x7F && c <= 0x9F) || (c >= 0xFDD0 && c <= 0xFDEF) || c >= 0xFFFE)
                    {
                        dropped = true;
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    prevSpace = false;
                    break;
            }

            i++;
        }

        return dropped;
    }

    private static void AppendEscaped(StringBuilder sb, ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }

    // Conservative link detection: a known scheme (or "www.") at the start of a word, a plausible address after it, trailing punctuation left out.
    private static bool TryLink(string s, int i, out int length, out string href)
    {
        length = 0;
        href = string.Empty;
        char c = s[i];
        if (c is not ('h' or 'H' or 'f' or 'F' or 'm' or 'M' or 'w' or 'W') || (i > 0 && char.IsLetterOrDigit(s[i - 1])))
        {
            return false;
        }

        ReadOnlySpan<char> rest = s.AsSpan(i);
        int prefix;
        bool addHttp = false;
        if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            prefix = 7;
        }
        else if (rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            prefix = 8;
        }
        else if (rest.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            prefix = 6;
        }
        else if (rest.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = 7;
        }
        else if (rest.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            prefix = 4;
            addHttp = true;
        }
        else
        {
            return false;
        }

        int end = i + prefix;
        while (end < s.Length && !IsUrlStop(s[end]))
        {
            end++;
        }

        // Trailing punctuation belongs to the sentence; a closing bracket belongs to the address only when its opener is inside it.
        while (end > i + prefix)
        {
            char last = s[end - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"')
            {
                end--;
            }
            else if (last is ')' or ']' or '}' && Unbalanced(s, i, end, last))
            {
                end--;
            }
            else
            {
                break;
            }
        }

        ReadOnlySpan<char> tail = s.AsSpan(i + prefix, end - (i + prefix));
        if (tail.Length == 0 || end - i > 8192)
        {
            return false;
        }

        bool alnum = false;
        foreach (char t in tail)
        {
            if (char.IsLetterOrDigit(t))
            {
                alnum = true;
                break;
            }
        }

        bool mail = prefix == 7 && s[i] is 'm' or 'M';
        if (!alnum || (mail && tail.IndexOf('@') <= 0) || (addHttp && tail.IndexOf('.') < 0))
        {
            return false;
        }

        length = end - i;
        href = addHttp ? string.Concat("http://", s.AsSpan(i, length)) : s.Substring(i, length);
        return true;
    }

    private static bool IsUrlStop(char c) => c <= ' ' || c is '<' or '>' or '"' or '\u00A0' or '\u007F' || (c >= '\u0080' && c <= '\u009F');

    // A closing bracket at the end belongs to the address only when it closes an opener inside the address.
    private static bool Unbalanced(string s, int from, int to, char closer)
    {
        char open = closer == ')' ? '(' : closer == ']' ? '[' : '{';
        int balance = 0;
        for (int k = from; k < to; k++)
        {
            if (s[k] == open)
            {
                balance++;
            }
            else if (s[k] == closer)
            {
                balance--;
            }
        }

        return balance < 0;
    }
}
