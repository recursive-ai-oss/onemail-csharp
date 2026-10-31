using System.Text;

namespace RecursiveAI.OneMail.Display;

/// <summary>
/// Plain text as HTML: escaped, one block per line, <c>format=flowed</c> text (RFC 3676) re-joined into paragraphs with quote levels as
/// block quotes, and <c>http://</c> and <c>https://</c> addresses made into links through the remote-content policy.
/// </summary>
internal static class PlainTextHtml
{
    private readonly record struct Para(int Depth, string Text);

    // Longest run of address characters considered for a link (the link itself is further limited to 2,048 characters).
    private const int MaxLinkScan = 4096;

    // Deepest block quote written. RFC 3676 sets no limit, and one line of '>' signs would otherwise write a block quote for each.
    private const int MaxQuoteDepth = 32;

    public static string Render(string text, bool flowed, bool delSp, SanitizerContext cx)
    {
        List<Para> paras = flowed ? Unflow(text, delSp) : Lines(text);
        StringBuilder sb = new();
        sb.Append("<div class=\"").Append(HtmlSanitizer.BoxClass).Append("\"><div class=\"").Append(CssSanitizer.ScopeClass)
            .Append(" omx-text\" style=\"white-space:pre-wrap;word-wrap:break-word;overflow-wrap:anywhere\">");
        int depth = 0;
        int nodes = 0;
        foreach (Para p in paras)
        {
            if (++nodes > cx.Options.MaxNodes)
            {
                cx.Truncated = true;
                cx.Change(SanitizerChangeKind.LimitReached, "node count");
                break;
            }

            while (depth < p.Depth)
            {
                sb.Append("<blockquote class=\"omx-q\">");
                depth++;
            }

            while (depth > p.Depth)
            {
                sb.Append("</blockquote>");
                depth--;
            }

            sb.Append("<div>");
            if (p.Text.Length == 0)
            {
                sb.Append("<br>");
            }
            else
            {
                Linkify(p.Text, sb, cx);
            }

            sb.Append("</div>");
        }

        while (depth-- > 0)
        {
            sb.Append("</blockquote>");
        }

        sb.Append("</div></div>");
        return sb.ToString();
    }

    private static List<Para> Lines(string text)
    {
        List<Para> list = [];
        foreach (string line in SplitLines(text))
        {
            list.Add(new Para(0, line));
        }

        return list;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        int i = 0;
        while (i <= text.Length)
        {
            int j = i;
            while (j < text.Length && text[j] is not ('\r' or '\n'))
            {
                j++;
            }

            yield return text[i..j];
            if (j >= text.Length)
            {
                yield break;
            }

            i = text[j] == '\r' && j + 1 < text.Length && text[j + 1] == '\n' ? j + 2 : j + 1;
            if (i == text.Length)
            {
                // A final line break does not start another line.
                yield break;
            }
        }
    }

    // RFC 3676 4.2 and 4.5: quote depth is the count of leading '>'; one space of space-stuffing is removed; a line ending in a space is
    // continued by the next line of the same depth; "-- " alone is the signature separator and never flows; with DelSp=Yes the space
    // that marks a soft break is deleted when lines are joined.
    private static List<Para> Unflow(string text, bool delSp)
    {
        List<Para> list = [];
        StringBuilder cur = new();
        int curDepth = -1;
        foreach (string raw in SplitLines(text))
        {
            int d = 0;
            while (d < raw.Length && raw[d] == '>')
            {
                d++;
            }

            string line = raw[d..];
            if (line.StartsWith(' '))
            {
                line = line[1..];
            }

            if (curDepth >= 0 && curDepth != d)
            {
                list.Add(new Para(Math.Min(curDepth, MaxQuoteDepth), cur.ToString()));
                cur.Clear();
                curDepth = -1;
            }

            curDepth = d;
            bool soft = line.EndsWith(' ') && line != "-- " && line != "--";
            if (soft)
            {
                cur.Append(delSp ? line[..^1] : line);
            }
            else
            {
                cur.Append(line);
                list.Add(new Para(Math.Min(d, MaxQuoteDepth), cur.ToString()));
                cur.Clear();
                curDepth = -1;
            }
        }

        if (curDepth >= 0)
        {
            list.Add(new Para(Math.Min(curDepth, MaxQuoteDepth), cur.ToString()));
        }

        return list;
    }

    private static void Linkify(string line, StringBuilder sb, SanitizerContext cx)
    {
        int i = 0;
        int plain = 0;
        while (i < line.Length)
        {
            int at = FindScheme(line, i);
            if (at < 0)
            {
                break;
            }

            int end = at;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] is not ('<' or '>' or '"' or '\u00a0'))
            {
                end++;
            }

            if (end - at > MaxLinkScan)
            {
                // Not an address anyone typed; skipped whole so a run of address characters costs a single pass.
                i = end;
                continue;
            }

            end = TrimTrailing(line, at, end);
            string url = line[at..end];
            string? href = url.Length is > 10 and <= 2048 && SanitizerContext.Classify(url, out _) == UrlForm.Http
                ? cx.ApplyPolicy(SanitizerContext.AbsoluteRemote(url, UrlForm.Http), RemoteContentKind.Link, "text")
                : null;
            if (href is null)
            {
                i = Math.Max(end, at + 1);
                continue;
            }

            HtmlSanitizer.EscapeText(line.AsSpan(plain, at - plain), sb);
            sb.Append("<a href=\"");
            HtmlSanitizer.EscapeAttribute(href, sb);
            sb.Append("\" rel=\"noopener noreferrer\"");
            if (cx.Options.OpenLinksInNewWindow)
            {
                sb.Append(" target=\"_blank\"");
            }

            sb.Append('>');
            HtmlSanitizer.EscapeText(url, sb);
            sb.Append("</a>");
            plain = end;
            i = end;
        }

        HtmlSanitizer.EscapeText(line.AsSpan(plain), sb);
    }

    private static int FindScheme(string s, int from)
    {
        for (int i = from; i < s.Length; i++)
        {
            if ((s[i] is 'h' or 'H') && (i == 0 || !char.IsAsciiLetterOrDigit(s[i - 1]))
                && (string.Compare(s, i, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0
                    || string.Compare(s, i, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) == 0))
            {
                return i;
            }
        }

        return -1;
    }

    // Punctuation that ends a sentence is not part of the address; a closing bracket is, only when its opener is inside the address.
    // The brackets are counted once, when the first trailing one is met, and adjusted as characters are given up, so a run of closing
    // brackets costs time linear in its length.
    private static int TrimTrailing(string s, int start, int end)
    {
        int[]? opens = null;
        int[]? closes = null;
        while (end > start)
        {
            char c = s[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '*')
            {
                end--;
                continue;
            }

            int b = BracketIndex(c, out bool isClose);
            if (b < 0 || !isClose)
            {
                break;
            }

            if (opens is null || closes is null)
            {
                opens = new int[3];
                closes = new int[3];
                for (int k = start; k < end; k++)
                {
                    int kb = BracketIndex(s[k], out bool kClose);
                    if (kb >= 0)
                    {
                        (kClose ? closes : opens)[kb]++;
                    }
                }
            }

            if (closes[b] <= opens[b])
            {
                break;
            }

            closes[b]--;
            end--;
        }

        return end;
    }

    private static int BracketIndex(char c, out bool isClose)
    {
        isClose = c is ')' or ']' or '}';
        return c switch { '(' or ')' => 0, '[' or ']' => 1, '{' or '}' => 2, _ => -1 };
    }
}
