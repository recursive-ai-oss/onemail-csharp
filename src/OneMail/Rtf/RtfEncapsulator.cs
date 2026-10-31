using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Rtf;

/// <summary>
/// The reverse of <see cref="RtfDeencapsulator"/>: wraps HTML or plain text in RTF the way MS-OXRTFEX describes, so
/// that the original comes back from <see cref="RtfDeencapsulator.Deencapsulate"/> and an RTF reader that ignores the
/// encapsulation still shows the text. Contains no dependency on the rest of OneMail. The output is 7-bit ASCII.
/// </summary>
/// <remarks>
/// Line breaks in the input (CR, LF or CRLF) come back as CRLF, which MS-OXRTFEX allows (1.3.1): everything else
/// round-trips character for character. This is not a general HTML-to-RTF converter: tags are carried in
/// <c>\*\htmltag</c> groups, visible text is written as plain RTF text, and no formatting is generated.
/// </remarks>
public static class RtfEncapsulator
{
    private const string FontTable = "{\\fonttbl{\\f0\\fswiss Arial;}}";

    /// <summary>Encapsulates an HTML document in RTF marked <c>\fromhtml1</c> (MS-OXRTFEX 2.1.3.2).</summary>
    /// <param name="html">The HTML.</param>
    /// <returns>The RTF bytes.</returns>
    public static byte[] EncapsulateHtml(ReadOnlySpan<char> html)
    {
        var sb = new StringBuilder(html.Length * 2 + 128);
        // The empty INBODY/P group first, as MS-OXRTFEX 2.1.3.2 suggests, so RTF readers start a paragraph.
        sb.Append("{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 \\deff0").Append(FontTable).Append("\r\n{\\*\\htmltag64}\\uc1\\pard\\plain\\f0\\fs24 ");
        int i = 0;
        while (i < html.Length)
        {
            int tagEnd = html[i] == '<' ? TagEnd(html, i) : -1;
            if (tagEnd > 0)
            {
                WriteTag(sb, html[i..tagEnd]);
                i = tagEnd;
                continue;
            }

            int next = i + 1;
            while (next < html.Length && !(html[next] == '<' && TagEnd(html, next) > 0))
            {
                next++;
            }

            WriteText(sb, html[i..next]);
            i = next;
        }

        sb.Append('}');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Encapsulates plain text in RTF marked <c>\fromtext</c> (MS-OXRTFEX 2.1.3.3).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The RTF bytes.</returns>
    public static byte[] EncapsulateText(ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(text.Length * 2 + 128);
        sb.Append("{\\rtf1\\ansi\\ansicpg1252\\fromtext \\deff0").Append(FontTable).Append("\r\n\\uc1\\pard\\plain\\f0\\fs24 ");
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                sb.Append("\\par\r\n");
            }
            else
            {
                WriteChar(sb, c);
            }
        }

        sb.Append('}');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // Returns the offset just after the markup that starts at html[start] == '<', or -1 when it is not markup.
    private static int TagEnd(ReadOnlySpan<char> html, int start)
    {
        if (start + 1 >= html.Length)
        {
            return -1;
        }

        char c = html[start + 1];
        if (html[start..].StartsWith("<!--", StringComparison.Ordinal))
        {
            int close = html[(start + 4)..].IndexOf("-->", StringComparison.Ordinal);
            return close < 0 ? html.Length : start + 4 + close + 3;
        }

        if (!(char.IsAsciiLetter(c) || c is '/' or '!' or '?'))
        {
            return -1;
        }

        int i = start + 1;
        char quote = '\0';
        for (; i < html.Length; i++)
        {
            char d = html[i];
            if (quote != '\0')
            {
                if (d == quote)
                {
                    quote = '\0';
                }
            }
            else if (d is '"' or '\'')
            {
                quote = d;
            }
            else if (d == '>')
            {
                break;
            }
        }

        int end = i < html.Length ? i + 1 : html.Length;
        // The content of <script> and <style> is not text: carry it, up to its closing tag, in the same group.
        string? name = TagName(html[(start + 1)..end], out bool closing);
        if (!closing && name is "script" or "style" && end < html.Length)
        {
            int close = IndexOfIgnoreCase(html[end..], "</" + name);
            if (close >= 0)
            {
                int gt = html[(end + close)..].IndexOf('>');
                return gt < 0 ? html.Length : end + close + gt + 1;
            }

            return html.Length;
        }

        return end;
    }

    private static int IndexOfIgnoreCase(ReadOnlySpan<char> s, string needle) => s.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

    private static string? TagName(ReadOnlySpan<char> afterLt, out bool closing)
    {
        closing = !afterLt.IsEmpty && afterLt[0] == '/';
        ReadOnlySpan<char> rest = closing ? afterLt[1..] : afterLt;
        int n = 0;
        while (n < rest.Length && char.IsAsciiLetterOrDigit(rest[n]))
        {
            n++;
        }

        return n == 0 ? null : rest[..n].ToString().ToLowerInvariant();
    }

    // MS-OXRTFEX 2.1.3.1.4.1: the HTMLTagParameter is the TagType flag, with CLOSE (8) for end tags.
    private static int TagParameter(ReadOnlySpan<char> markup)
    {
        string? name = TagName(markup[1..], out bool closing);
        int type = name switch
        {
            "html" => 0x10,
            "head" => 0x20,
            "body" => 0x30,
            "p" => 0x40,
            "br" => 0x70,
            "pre" => 0x80,
            "font" => 0x90,
            "title" => 0xB0,
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => 0xA0,
            _ => 0xF0,
        };
        return type | (closing ? 0x08 : 0);
    }

    private static void WriteTag(StringBuilder sb, ReadOnlySpan<char> markup)
    {
        sb.Append("{\\*\\htmltag").Append(TagParameter(markup).ToString(CultureInfo.InvariantCulture)).Append(' ');
        for (int i = 0; i < markup.Length; i++)
        {
            char c = markup[i];
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < markup.Length && markup[i + 1] == '\n')
                {
                    i++;
                }

                sb.Append("\\par ");
            }
            else
            {
                WriteChar(sb, c);
            }
        }

        sb.Append('}');
    }

    // Text between tags: visible text is written as RTF text; entities and line breaks go through \htmltag, with the
    // visible rendering after \htmlrtf so that de-encapsulation drops it (MS-OXRTFEX 3.1's pattern).
    private static void WriteText(StringBuilder sb, ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '&' && EntityEnd(text, i) is int e and > 0)
            {
                ReadOnlySpan<char> entity = text[i..e];
                sb.Append("{\\*\\htmltag0 ").Append(entity).Append('}');
                string? visible = Rendering(entity);
                if (visible is not null)
                {
                    sb.Append("\\htmlrtf ").Append(visible).Append("\\htmlrtf0 ");
                }

                i = e - 1;
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                sb.Append("{\\*\\htmltag0 \\par}\\htmlrtf\\~\\htmlrtf0 ");
            }
            else
            {
                WriteChar(sb, c);
            }
        }
    }

    private static int EntityEnd(ReadOnlySpan<char> text, int amp)
    {
        int i = amp + 1;
        if (i < text.Length && text[i] == '#')
        {
            i++;
        }

        int start = i;
        while (i < text.Length && i - start < 32 && char.IsAsciiLetterOrDigit(text[i]))
        {
            i++;
        }

        return i > start && i < text.Length && text[i] == ';' ? i + 1 : -1;
    }

    // The RTF that shows an entity on screen; null when this encapsulator does not know a rendering.
    private static string? Rendering(ReadOnlySpan<char> entity)
    {
        switch (entity.ToString())
        {
            case "&amp;": return "&";
            case "&lt;": return "<";
            case "&gt;": return ">";
            case "&quot;": return "\"";
            case "&apos;": return "'";
            case "&nbsp;": return "\\~";
        }

        if (entity.Length > 3 && entity[1] == '#')
        {
            ReadOnlySpan<char> digits = entity[2..^1];
            bool hex = digits[0] is 'x' or 'X';
            if (int.TryParse(hex ? digits[1..] : digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int cp)
                && cp is > 0 and <= 0xFFFF && !char.IsSurrogate((char)cp))
            {
                return Unicode((char)cp);
            }
        }

        return null;
    }

    private static string Unicode(char c) => "\\u" + ((short)c).ToString(CultureInfo.InvariantCulture) + "?";

    // One character as RTF text (also valid inside \htmltag). Control words always get their delimiting space.
    private static void WriteChar(StringBuilder sb, char c)
    {
        switch (c)
        {
            case '\\':
            case '{':
            case '}':
                sb.Append('\\').Append(c);
                break;
            case '\t':
                sb.Append("\\tab ");
                break;
            case < ' ' or '\u007F':
                sb.Append("\\'").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                break;
            case > '\u007F':
                sb.Append(Unicode(c));
                break;
            default:
                sb.Append(c);
                break;
        }
    }
}
