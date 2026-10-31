using System;
using System.Collections.Generic;
using System.Text;

namespace RecursiveAI.OneMail.Bodies;

/// <summary>One paragraph of a <c>format=flowed</c> body: its quote depth and its text with the soft line breaks removed.</summary>
/// <param name="QuoteDepth">The number of quote markers (<c>&gt;</c>) the paragraph had; 0 for text that is not quoted.</param>
/// <param name="Text">The paragraph text, unstuffed. It has no line breaks. A blank line is a paragraph with empty text.</param>
public readonly record struct FlowedParagraph(int QuoteDepth, string Text);

/// <summary>
/// <c>format=flowed</c> (RFC 3676) as a text layer, both ways. Decoding joins the soft line breaks of a flowed body back into
/// paragraphs, removing space-stuffing and reading quote depth; encoding wraps paragraphs for writing.
/// </summary>
/// <remarks>
/// Neither direction throws. Line endings may be CRLF, LF or a lone CR is treated as text; a missing final line ending is accepted.
/// The decoder follows RFC 3676 §4.5: a line is flowed when it ends in a space after the quote prefix and space-stuffing are
/// removed, except the signature separator <c>"-- "</c>; a flowed line joins the line after it only when that line has the same quote
/// depth; <c>DelSp=yes</c> removes the one trailing space of each soft break.
/// </remarks>
public static class FormatFlowed
{
    private const int MaxJoinedParagraph = 64 * 1024 * 1024;

    /// <summary>Reads a flowed body into paragraphs (RFC 3676 §4.5).</summary>
    /// <param name="text">The decoded body text.</param>
    /// <param name="delSp">True when the Content-Type said <c>delsp=yes</c>: the trailing space of a soft break is not part of the text.</param>
    /// <returns>The paragraphs in order. Empty for empty input.</returns>
    public static IReadOnlyList<FlowedParagraph> Parse(string text, bool delSp = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<FlowedParagraph> result = [];
        StringBuilder? open = null;
        int openDepth = 0;
        int pos = 0;
        while (pos < text.Length)
        {
            int eol = text.IndexOf('\n', pos);
            int end = eol < 0 ? text.Length : eol;
            int lineEnd = end > pos && text[end - 1] == '\r' ? end - 1 : end;
            ReadOnlySpan<char> line = text.AsSpan(pos, lineEnd - pos);
            pos = eol < 0 ? text.Length : eol + 1;

            // RFC 3676 §4.5: quote depth is the count of leading ">" characters; then one leading space (stuffing) is removed.
            int depth = 0;
            while (depth < line.Length && line[depth] == '>')
            {
                depth++;
            }

            line = line[depth..];
            if (line.Length > 0 && line[0] == ' ')
            {
                line = line[1..];
            }

            bool signature = line.SequenceEqual("-- ");
            bool flowed = line.Length > 0 && line[^1] == ' ' && !signature;
            ReadOnlySpan<char> content = flowed && delSp ? line[..^1] : line;

            if (open is not null && openDepth != depth)
            {
                result.Add(new FlowedParagraph(openDepth, open.ToString()));
                open = null;
            }

            if (open is null)
            {
                if (!flowed)
                {
                    result.Add(new FlowedParagraph(depth, content.ToString()));
                    continue;
                }

                open = new StringBuilder();
                openDepth = depth;
            }

            if (open.Length + content.Length <= MaxJoinedParagraph)
            {
                open.Append(content);
            }

            if (!flowed)
            {
                result.Add(new FlowedParagraph(openDepth, open.ToString()));
                open = null;
            }
        }

        if (open is not null)
        {
            result.Add(new FlowedParagraph(openDepth, open.ToString()));
        }

        return result;
    }

    /// <summary>Decodes a flowed body into plain text with one line per paragraph and quote markers written as <c>"&gt; "</c> prefixes.</summary>
    /// <param name="text">The decoded body text.</param>
    /// <param name="delSp">See <see cref="Parse"/>.</param>
    /// <returns>
    /// The text with LF line endings and no final line ending. A quoted paragraph is written as <c>&gt;</c> repeated for the depth, a space and the
    /// text (just the markers when the text is empty); an unquoted paragraph that starts with <c>&gt;</c> gets one leading space so it is not read as a quote.
    /// </returns>
    public static string Decode(string text, bool delSp = false) => ToPlainText(Parse(text, delSp));

    /// <summary>Writes paragraphs as plain text with <c>"&gt; "</c> quote prefixes. See <see cref="Decode"/>.</summary>
    /// <param name="paragraphs">The paragraphs.</param>
    /// <returns>The text.</returns>
    public static string ToPlainText(IReadOnlyList<FlowedParagraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        StringBuilder sb = new();
        for (int i = 0; i < paragraphs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            FlowedParagraph p = paragraphs[i];
            if (p.QuoteDepth > 0)
            {
                sb.Append('>', p.QuoteDepth);
                if (p.Text.Length > 0)
                {
                    sb.Append(' ');
                }
            }
            else if (p.Text.Length > 0 && p.Text[0] == '>')
            {
                sb.Append(' ');
            }

            sb.Append(p.Text);
        }

        return sb.ToString();
    }

    /// <summary>Reads plain text into paragraphs the way <see cref="Encode(string,int,bool)"/> does: each line is a paragraph, and leading <c>&gt;</c> markers (optionally separated by single spaces) give its quote depth.</summary>
    /// <param name="text">The text, with LF or CRLF line endings.</param>
    /// <returns>The paragraphs.</returns>
    public static IReadOnlyList<FlowedParagraph> ParsePlain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<FlowedParagraph> result = [];
        int pos = 0;
        while (pos < text.Length)
        {
            int eol = text.IndexOf('\n', pos);
            int end = eol < 0 ? text.Length : eol;
            int lineEnd = end > pos && text[end - 1] == '\r' ? end - 1 : end;
            ReadOnlySpan<char> line = text.AsSpan(pos, lineEnd - pos);
            pos = eol < 0 ? text.Length : eol + 1;
            result.Add(ReadQuotePrefix(line));
        }

        return result;
    }

    // Quote markers as people type them: ">>", "> >" and "> > " all mean depth 2; one space after the last marker is not text.
    internal static FlowedParagraph ReadQuotePrefix(ReadOnlySpan<char> line)
    {
        int depth = 0;
        int i = 0;
        while (i < line.Length && line[i] == '>')
        {
            depth++;
            i++;
            if (i + 1 < line.Length && line[i] == ' ' && line[i + 1] == '>')
            {
                i++;
            }
        }

        if (depth > 0 && i < line.Length && line[i] == ' ')
        {
            i++;
        }

        return new FlowedParagraph(depth, line[i..].ToString());
    }

    /// <summary>Encodes plain text as a flowed body: every line is a paragraph, wrapped with soft breaks and space-stuffed.</summary>
    /// <param name="text">The text; lines starting with <c>&gt;</c> markers are quoted paragraphs.</param>
    /// <param name="width">The longest line to write, in characters, counting the quote prefix and the trailing space of a soft break. At least 20; default 78 (RFC 3676 §4.2).</param>
    /// <param name="delSp">True to write for <c>delsp=yes</c>: words longer than a line are split and the soft-break space is not part of the text.</param>
    /// <returns>The body with CRLF line endings and a final line ending when the input had text.</returns>
    public static string Encode(string text, int width = 78, bool delSp = false) => Encode(ParsePlain(text), width, delSp);

    /// <summary>Encodes paragraphs as a flowed body. See <see cref="Encode(string,int,bool)"/>.</summary>
    /// <param name="paragraphs">The paragraphs.</param>
    /// <param name="width">See <see cref="Encode(string,int,bool)"/>.</param>
    /// <param name="delSp">See <see cref="Encode(string,int,bool)"/>.</param>
    /// <returns>The body.</returns>
    /// <remarks>
    /// Trailing spaces of a paragraph are dropped (a fixed line cannot end in a space), except the signature separator <c>"-- "</c>. A paragraph with
    /// a line break or a CR inside is written as is, since a flowed paragraph cannot hold one. Without <paramref name="delSp"/> a word longer than a line
    /// is not split.
    /// </remarks>
    public static string Encode(IEnumerable<FlowedParagraph> paragraphs, int width = 78, bool delSp = false)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        width = Math.Max(width, 20);
        StringBuilder sb = new();
        foreach (FlowedParagraph p in paragraphs)
        {
            string content = p.Text.Replace('\r', ' ').Replace('\n', ' ');
            if (content != "-- ")
            {
                content = content.TrimEnd(' ');
            }

            int depth = Math.Max(p.QuoteDepth, 0);
            int prefix = depth > 0 ? depth + 1 : 0;
            if (content.Length == 0)
            {
                sb.Append('>', depth).Append("\r\n");
                continue;
            }

            if (content == "-- ")
            {
                WriteLine(sb, depth, content, stuff: false, soft: false);
                continue;
            }

            int pos = 0;
            while (pos < content.Length)
            {
                bool stuff = depth == 0 && NeedsStuffing(content.AsSpan(pos));
                int room = width - prefix - (stuff ? 1 : 0);
                int remaining = content.Length - pos;
                if (remaining <= room)
                {
                    WriteLine(sb, depth, content.Substring(pos), stuff, soft: false);
                    break;
                }

                int take = delSp ? BreakDelSp(content, pos, room - 1) : BreakSpace(content, pos, room);
                if (take >= remaining)
                {
                    WriteLine(sb, depth, content.Substring(pos), stuff, soft: false);
                    break;
                }

                // Without DelSp the chunk's own trailing space is the soft break; with it a space is added that the reader deletes.
                string chunk = content.Substring(pos, take);
                WriteLine(sb, depth, chunk, stuff, soft: delSp);
                pos += take;
            }
        }

        return sb.ToString();
    }

    private static bool NeedsStuffing(ReadOnlySpan<char> line) =>
        line.Length > 0 && (line[0] is ' ' or '>' || line.StartsWith("From ", StringComparison.Ordinal));

    private static void WriteLine(StringBuilder sb, int depth, string content, bool stuff, bool soft)
    {
        sb.Append('>', depth);
        if (depth > 0)
        {
            sb.Append(' ');
        }

        if (stuff)
        {
            sb.Append(' ');
        }

        sb.Append(content);
        if (soft)
        {
            sb.Append(' ');
        }

        sb.Append("\r\n");
    }

    // Length of the next chunk when the chunk keeps its own trailing space: up to the last space that fits in `room`.
    private static int BreakSpace(string s, int pos, int room)
    {
        int limit = Math.Min(pos + room, s.Length);
        for (int k = limit - 1; k > pos; k--)
        {
            if (s[k] == ' ' && HasNonSpace(s, pos, k))
            {
                return k + 1 - pos;
            }
        }

        // No space fits: the word overflows the line, up to the first space after it.
        for (int k = limit; k < s.Length; k++)
        {
            if (s[k] == ' ')
            {
                return k + 1 - pos;
            }
        }

        return s.Length - pos;
    }

    // Length of the next chunk when a soft-break space is added: at most `room` characters, at the last word boundary in reach.
    private static int BreakDelSp(string s, int pos, int room)
    {
        room = Math.Max(room, 1);
        int limit = Math.Min(pos + room, s.Length);
        if (limit >= s.Length)
        {
            return s.Length - pos;
        }

        for (int k = limit; k > pos; k--)
        {
            if (s[k] == ' ' && HasNonSpace(s, pos, k))
            {
                return k - pos;
            }
        }

        int cut = limit;
        if (char.IsLowSurrogate(s[cut]) && cut > pos + 1)
        {
            cut--;
        }

        return cut - pos;
    }

    private static bool HasNonSpace(string s, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (s[i] != ' ')
            {
                return true;
            }
        }

        return false;
    }
}
