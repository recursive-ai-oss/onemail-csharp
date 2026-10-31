using System.Text;

namespace RecursiveAI.OneMail.Text;

/// <summary>UTF-7 (RFC 2152), the encoding some old mail clients still declare as <c>charset=utf-7</c>.</summary>
public static class Utf7
{
    /// <summary>
    /// Decodes UTF-7. Never throws. Any ASCII byte is accepted in direct mode (RFC 2152 says decoders should accept the
    /// optional direct characters). A <c>+</c> starts a base64 run that ends at <c>-</c> (consumed) or at the first
    /// character outside the base64 alphabet (not consumed). A bare <c>+-</c> is a plus sign.
    /// </summary>
    /// <param name="source">UTF-7 bytes.</param>
    /// <param name="issues">Receives <see cref="TextIssueKind.InvalidUtf7"/> for each repair; may be <see langword="null"/>.</param>
    /// <returns>The text. Bytes of 0x80 or above, unpaired surrogates and a dangling partial base64 group become U+FFFD.</returns>
    public static string Decode(ReadOnlySpan<byte> source, List<TextIssue>? issues = null)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            byte b = source[i];
            if (b >= 0x80)
            {
                sb.Append('\uFFFD');
                issues?.Add(new TextIssue(TextIssueKind.InvalidUtf7, i, 1));
                i++;
            }
            else if (b != (byte)'+')
            {
                sb.Append((char)b);
                i++;
            }
            else if (i + 1 < source.Length && source[i + 1] == (byte)'-')
            {
                sb.Append('+');
                i += 2;
            }
            else
            {
                i = DecodeRun(source, i, sb, issues);
            }
        }

        return sb.ToString();
    }

    // Decodes the base64 run that starts with the '+' at 'start'; returns the offset after it.
    private static int DecodeRun(ReadOnlySpan<byte> source, int start, StringBuilder sb, List<TextIssue>? issues)
    {
        int i = start + 1;
        uint bits = 0;
        int nbits = 0;
        char pendingHigh = '\0';
        bool damaged = false;
        int units = 0;
        while (i < source.Length)
        {
            int v = Base64Alphabet.Value(source[i]);
            if (v < 0)
            {
                break;
            }

            bits = (bits << 6) | (uint)v;
            nbits += 6;
            i++;
            if (nbits >= 16)
            {
                nbits -= 16;
                char unit = (char)((bits >> nbits) & 0xFFFF);
                bits &= (1u << nbits) - 1;
                units++;
                Utf16Assembler.Add(sb, ref pendingHigh, unit, ref damaged);
            }
        }

        // RFC 2152 §3: the run ends with '-' (absorbed) or any non-base64 character (which belongs to the text after it).
        if (i < source.Length && source[i] == (byte)'-')
        {
            i++;
        }

        if (pendingHigh != '\0')
        {
            sb.Append('\uFFFD');
            damaged = true;
        }

        // Leftover bits must be fewer than 6 and zero; an empty run ("+" followed by a non-base64 character) is damage too.
        if (nbits >= 6 || bits != 0 || units == 0)
        {
            damaged = true;
        }

        if (damaged)
        {
            issues?.Add(new TextIssue(TextIssueKind.InvalidUtf7, start, i - start));
        }

        return i;
    }

    /// <summary>
    /// Encodes text as UTF-7. Letters, digits and <c>'(),-./:?</c> plus space, tab, CR and LF stay direct; everything else
    /// (including the "optional direct" characters, for safety in headers) goes into base64 runs; <c>+</c> is written <c>+-</c>.
    /// </summary>
    /// <param name="text">The text. Unpaired surrogates are written as U+FFFD.</param>
    /// <returns>ASCII text.</returns>
    public static string Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length + 8);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (IsDirect(c))
            {
                sb.Append(c);
                i++;
            }
            else if (c == '+')
            {
                sb.Append("+-");
                i++;
            }
            else
            {
                int end = i;
                while (end < text.Length && !IsDirect(text[end]) && text[end] != '+')
                {
                    end++;
                }

                sb.Append('+');
                Utf16Assembler.AppendBase64(sb, text.AsSpan(i, end - i), imap: false);
                // The terminating '-' is only needed when the next character could be read as part of the base64.
                if (end < text.Length && (Base64Alphabet.Value((byte)text[end]) >= 0 || text[end] == '-'))
                {
                    sb.Append('-');
                }

                i = end;
            }
        }

        return sb.ToString();
    }

    private static bool IsDirect(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '\'' or '(' or ')' or ',' or '-' or '.' or '/' or ':' or '?'
            or ' ' or '\t' or '\r' or '\n';
}

/// <summary>IMAP modified UTF-7 (RFC 3501 §5.1.3), the encoding of mailbox names on the wire.</summary>
public static class ImapUtf7
{
    /// <summary>
    /// Decodes a mailbox name. <c>&amp;-</c> is an ampersand; <c>&amp;</c> starts a base64 run (alphabet with <c>,</c> for <c>/</c>)
    /// of UTF-16BE that ends at <c>-</c>. Never throws.
    /// </summary>
    /// <param name="source">The name as sent by the server.</param>
    /// <param name="issues">Receives <see cref="TextIssueKind.InvalidImapUtf7"/> for each repair; may be <see langword="null"/>.</param>
    /// <returns>The text. A run missing its closing <c>-</c> is still decoded; bytes outside 0x20–0x7E, unpaired surrogates and
    /// bad base64 become U+FFFD.</returns>
    public static string Decode(ReadOnlySpan<byte> source, List<TextIssue>? issues = null)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            byte b = source[i];
            if (b != (byte)'&')
            {
                if (b is < 0x20 or > 0x7E)
                {
                    sb.Append('\uFFFD');
                    issues?.Add(new TextIssue(TextIssueKind.InvalidImapUtf7, i, 1));
                }
                else
                {
                    sb.Append((char)b);
                }

                i++;
            }
            else if (i + 1 < source.Length && source[i + 1] == (byte)'-')
            {
                sb.Append('&');
                i += 2;
            }
            else
            {
                i = DecodeRun(source, i, sb, issues);
            }
        }

        return sb.ToString();
    }

    private static int DecodeRun(ReadOnlySpan<byte> source, int start, StringBuilder sb, List<TextIssue>? issues)
    {
        int i = start + 1;
        uint bits = 0;
        int nbits = 0;
        char pendingHigh = '\0';
        bool damaged = false;
        int units = 0;
        bool closed = false;
        while (i < source.Length)
        {
            byte b = source[i];
            if (b == (byte)'-')
            {
                closed = true;
                i++;
                break;
            }

            int v = b == (byte)'/' ? -1 : Base64Alphabet.Value(b == (byte)',' ? (byte)'/' : b);
            if (v < 0)
            {
                // Not part of the run: the closing '-' is missing. Keep what was decoded and resume in direct mode.
                break;
            }

            bits = (bits << 6) | (uint)v;
            nbits += 6;
            i++;
            if (nbits >= 16)
            {
                nbits -= 16;
                char unit = (char)((bits >> nbits) & 0xFFFF);
                bits &= (1u << nbits) - 1;
                units++;
                Utf16Assembler.Add(sb, ref pendingHigh, unit, ref damaged);
            }
        }

        if (pendingHigh != '\0')
        {
            sb.Append('\uFFFD');
            damaged = true;
        }

        if (!closed || nbits >= 6 || bits != 0 || units == 0)
        {
            damaged = true;
        }

        if (damaged)
        {
            issues?.Add(new TextIssue(TextIssueKind.InvalidImapUtf7, start, i - start));
        }

        return i;
    }

    /// <summary>Encodes a mailbox name: printable ASCII stays (except <c>&amp;</c>, written <c>&amp;-</c>); other runs become <c>&amp;base64-</c>.</summary>
    /// <param name="text">The name. Unpaired surrogates are written as U+FFFD.</param>
    /// <returns>ASCII text.</returns>
    public static string Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length + 8);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is >= ' ' and <= '~')
            {
                sb.Append(c);
                if (c == '&')
                {
                    sb.Append('-');
                }

                i++;
            }
            else
            {
                int end = i;
                while (end < text.Length && text[end] is < ' ' or > '~')
                {
                    end++;
                }

                sb.Append('&');
                Utf16Assembler.AppendBase64(sb, text.AsSpan(i, end - i), imap: true);
                sb.Append('-');
                i = end;
            }
        }

        return sb.ToString();
    }
}

internal static class Base64Alphabet
{
    private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    public static int Value(byte b) => b switch
    {
        >= (byte)'A' and <= (byte)'Z' => b - 'A',
        >= (byte)'a' and <= (byte)'z' => b - 'a' + 26,
        >= (byte)'0' and <= (byte)'9' => b - '0' + 52,
        (byte)'+' => 62,
        (byte)'/' => 63,
        _ => -1,
    };

    public static char Char(int v) => Chars[v];
}

internal static class Utf16Assembler
{
    // Adds one UTF-16 code unit decoded from a base64 run, pairing surrogates and replacing unpaired ones.
    public static void Add(StringBuilder sb, ref char pendingHigh, char unit, ref bool damaged)
    {
        if (char.IsHighSurrogate(unit))
        {
            if (pendingHigh != '\0')
            {
                sb.Append('\uFFFD');
                damaged = true;
            }

            pendingHigh = unit;
        }
        else if (char.IsLowSurrogate(unit))
        {
            if (pendingHigh != '\0')
            {
                sb.Append(pendingHigh).Append(unit);
                pendingHigh = '\0';
            }
            else
            {
                sb.Append('\uFFFD');
                damaged = true;
            }
        }
        else
        {
            if (pendingHigh != '\0')
            {
                sb.Append('\uFFFD');
                pendingHigh = '\0';
                damaged = true;
            }

            sb.Append(unit);
        }
    }

    // Writes the UTF-16BE of 'text' as unpadded base64; 'imap' swaps '/' for ','. Unpaired surrogates become U+FFFD.
    public static void AppendBase64(StringBuilder sb, ReadOnlySpan<char> text, bool imap)
    {
        uint bits = 0;
        int nbits = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char unit = text[i];
            if (char.IsHighSurrogate(unit) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                Push(sb, ref bits, ref nbits, unit, imap);
                unit = text[++i];
            }
            else if (char.IsSurrogate(unit))
            {
                unit = '\uFFFD';
            }

            Push(sb, ref bits, ref nbits, unit, imap);
        }

        if (nbits > 0)
        {
            Emit(sb, (int)((bits << (6 - nbits)) & 0x3F), imap);
        }
    }

    private static void Push(StringBuilder sb, ref uint bits, ref int nbits, char unit, bool imap)
    {
        bits = (bits << 16) | unit;
        nbits += 16;
        while (nbits >= 6)
        {
            nbits -= 6;
            Emit(sb, (int)((bits >> nbits) & 0x3F), imap);
        }

        bits &= (1u << nbits) - 1;
    }

    private static void Emit(StringBuilder sb, int v, bool imap)
    {
        char c = Base64Alphabet.Char(v);
        sb.Append(imap && c == '/' ? ',' : c);
    }
}
