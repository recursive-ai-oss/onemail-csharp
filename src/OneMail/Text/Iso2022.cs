using System.Buffers;
using System.Text;
using RecursiveAI.OneHtml.Encodings;

namespace RecursiveAI.OneMail.Text;

// Decoders for the 7-bit stateful encodings mail still carries: the ISO-2022-JP family, ISO-2022-KR and HZ.
//
// Clients decode these leniently, so there is one tolerant decoder for the whole ISO-2022-JP family. Each variant has a
// repertoire; text outside it is still decoded and reported as AmbiguousIso2022Repertoire. The two-byte character sets
// are not tabulated here: a run of two-byte codes is rewritten into the EUC form of the same set (each byte with the
// high bit set, JIS X 0212 behind 0x8F) and handed to OneHtml's EUC-JP, GBK or EUC-KR decoder, which owns those tables.
internal static class Iso2022
{
    [Flags]
    internal enum CharSet
    {
        None = 0,
        Ascii = 1,
        JisRoman = 2,    // JIS X 0201 Roman: ASCII except 0x5C = yen sign and 0x7E = overline
        JisKana = 4,     // JIS X 0201 Katakana, the half-width kana at U+FF61..U+FF9F
        Jis78 = 8,       // JIS C 6226-1978, same code points as JIS X 0208 for decoding
        Jis83 = 16,      // JIS X 0208-1983
        Jis212 = 32,     // JIS X 0212
        Gb2312 = 64,
        Ksc5601 = 128,
        Latin1High = 256, // ISO-8859-1 high part, as G2 (ESC . A, ESC N)
        GreekHigh = 512,  // ISO-8859-7 high part, as G2 (ESC . F, ESC N)
        Jis0213Plane1 = 1024,
        Jis0213Plane2 = 2048,
    }

    internal enum Variant
    {
        Jp,
        Jp1,
        Jp2,
        Jp3,
        Cp50221,
        Cp50222,
        Kr,
        Hz,
    }

    private const CharSet Base = CharSet.Ascii | CharSet.JisRoman | CharSet.Jis78 | CharSet.Jis83;

    private static CharSet Repertoire(Variant v) => v switch
    {
        Variant.Jp => Base,
        Variant.Jp1 => Base | CharSet.Jis212,
        Variant.Jp2 => Base | CharSet.Jis212 | CharSet.Gb2312 | CharSet.Ksc5601 | CharSet.Latin1High | CharSet.GreekHigh,
        Variant.Jp3 => Base | CharSet.Jis0213Plane1 | CharSet.Jis0213Plane2,
        Variant.Cp50221 or Variant.Cp50222 => Base | CharSet.JisKana,
        _ => CharSet.Ascii,
    };

    public static string Decode(Variant variant, ReadOnlySpan<byte> src, List<TextIssue>? issues)
    {
        return variant == Variant.Hz ? DecodeHz(src, issues) : DecodeEscapes(variant, src, issues);
    }

    private static string DecodeEscapes(Variant variant, ReadOnlySpan<byte> src, List<TextIssue>? issues)
    {
        CharSet allowed = Repertoire(variant);
        bool korean = variant == Variant.Kr;
        bool shifts = variant is Variant.Cp50221 or Variant.Cp50222 or Variant.Kr;
        var sb = new StringBuilder(src.Length);
        CharSet g0 = CharSet.Ascii;
        CharSet g2 = CharSet.None;
        bool shiftOut = false;
        int i = 0;
        while (i < src.Length)
        {
            byte b = src[i];
            if (b == 0x1B)
            {
                i = Escape(src, i, ref g0, ref g2, korean, allowed, sb, issues);
                continue;
            }

            if (b >= 0x80 || (b is 0x0E or 0x0F && !shifts))
            {
                Replace(sb, issues, TextIssueKind.InvalidIso2022, i, 1);
                i++;
                continue;
            }

            if (b == 0x0E || b == 0x0F)
            {
                // RFC 1557 (ISO-2022-KR) and Windows 50221/50222: SO shifts G1 in, SI shifts it out.
                shiftOut = b == 0x0E;
                i++;
                continue;
            }

            if (b is 0x0A or 0x0D)
            {
                // Line ends return to ASCII in every variant (RFC 1468 §3, RFC 1557 §3), so a missing escape cannot swallow the next line.
                sb.Append((char)b);
                g0 = CharSet.Ascii;
                shiftOut = false;
                i++;
                continue;
            }

            CharSet active = shiftOut ? (korean ? CharSet.Ksc5601 : CharSet.JisKana) : g0;
            if (active is CharSet.Ascii || b < 0x21)
            {
                sb.Append((char)b);
                i++;
            }
            else if (active == CharSet.JisRoman)
            {
                sb.Append(b == 0x5C ? '\u00A5' : b == 0x7E ? '\u203E' : (char)b);
                i++;
            }
            else if (active == CharSet.JisKana)
            {
                if (b <= 0x5F)
                {
                    sb.Append((char)(0xFF61 + b - 0x21));
                }
                else
                {
                    Replace(sb, issues, TextIssueKind.InvalidIso2022, i, 1);
                }

                i++;
            }
            else
            {
                i = TwoByteRun(src, i, active, sb, issues);
            }
        }

        return sb.ToString();
    }

    // Reads the escape sequence at src[i] and updates the designations. Returns the offset after what was consumed.
    private static int Escape(ReadOnlySpan<byte> src, int i, ref CharSet g0, ref CharSet g2, bool korean, CharSet allowed, StringBuilder sb, List<TextIssue>? issues)
    {
        ReadOnlySpan<byte> rest = src[(i + 1)..];
        CharSet set = CharSet.None;
        int length = 0;
        bool g2Designation = false;
        if (rest.Length >= 2 && rest[0] == '(' && rest[1] == 'B')
        {
            (set, length) = (CharSet.Ascii, 3);
        }
        else if (rest.Length >= 2 && rest[0] == '(' && rest[1] == 'J')
        {
            (set, length) = (CharSet.JisRoman, 3);
        }
        else if (rest.Length >= 2 && rest[0] == '(' && rest[1] == 'I')
        {
            (set, length) = (CharSet.JisKana, 3);
        }
        else if (rest.Length >= 2 && rest[0] == '$' && rest[1] == '@')
        {
            (set, length) = (CharSet.Jis78, 3);
        }
        else if (rest.Length >= 2 && rest[0] == '$' && rest[1] == 'B')
        {
            (set, length) = (CharSet.Jis83, 3);
        }
        else if (rest.Length >= 2 && rest[0] == '$' && rest[1] == 'A')
        {
            (set, length) = (CharSet.Gb2312, 3);
        }
        else if (rest.Length >= 3 && rest[0] == '$' && rest[1] == '(' && rest[2] == 'C')
        {
            (set, length) = (CharSet.Ksc5601, 4);
        }
        else if (rest.Length >= 3 && rest[0] == '$' && rest[1] == '(' && rest[2] == 'D')
        {
            (set, length) = (CharSet.Jis212, 4);
        }
        else if (rest.Length >= 3 && rest[0] == '$' && rest[1] == '(' && rest[2] is (byte)'O' or (byte)'Q')
        {
            (set, length) = (CharSet.Jis0213Plane1, 4);
        }
        else if (rest.Length >= 3 && rest[0] == '$' && rest[1] == '(' && rest[2] == 'P')
        {
            (set, length) = (CharSet.Jis0213Plane2, 4);
        }
        else if (rest.Length >= 3 && rest[0] == '$' && rest[1] == ')' && rest[2] == 'C')
        {
            // RFC 1557: the ISO-2022-KR header designates KS C 5601 to G1; nothing is written.
            return korean ? i + 4 : BadEscape(src, i, sb, issues);
        }
        else if (rest.Length >= 2 && rest[0] == '.' && rest[1] == 'A')
        {
            (set, length, g2Designation) = (CharSet.Latin1High, 3, true);
        }
        else if (rest.Length >= 2 && rest[0] == '.' && rest[1] == 'F')
        {
            (set, length, g2Designation) = (CharSet.GreekHigh, 3, true);
        }
        else if (rest.Length >= 1 && rest[0] == 'N')
        {
            // Single shift 2: the next byte comes from G2.
            if (i + 2 < src.Length && src[i + 2] is >= 0x20 and <= 0x7F && g2 != CharSet.None)
            {
                byte c = (byte)(src[i + 2] | 0x80);
                sb.Append(g2 == CharSet.GreekHigh ? GreekChar(c) : (char)c);
                return i + 3;
            }

            return BadEscape(src, i, sb, issues);
        }
        else
        {
            return BadEscape(src, i, sb, issues);
        }

        if (korean)
        {
            return BadEscape(src, i, sb, issues);
        }

        if ((allowed & set) == 0)
        {
            issues?.Add(new TextIssue(TextIssueKind.AmbiguousIso2022Repertoire, i, length));
        }

        if (g2Designation)
        {
            g2 = set;
        }
        else
        {
            g0 = set;
        }

        return i + length;
    }

    private static int BadEscape(ReadOnlySpan<byte> src, int i, StringBuilder sb, List<TextIssue>? issues)
    {
        // An unknown escape: the ESC becomes U+FFFD and the bytes after it are read as ordinary text (as the Encoding Standard does).
        Replace(sb, issues, TextIssueKind.InvalidIso2022, i, 1);
        return i + 1;
    }

    private static char GreekChar(byte highByte)
    {
        Span<byte> one = [highByte];
        string s = MailEncoding.DecodeWhatwg(WhatwgEncoding.ForLabel("iso-8859-7")!, one, null);
        return s.Length == 1 ? s[0] : '\uFFFD';
    }

    // Decodes a run of two-byte codes of 'set' starting at src[i]; returns the offset after the run.
    private static int TwoByteRun(ReadOnlySpan<byte> src, int i, CharSet set, StringBuilder sb, List<TextIssue>? issues, bool hz = false)
    {
        int start = i;
        int pairs = 0;
        // In HZ a 0x7E lead byte is the start of an escape ("~}"), never a character.
        while (i + 1 < src.Length && src[i] is >= 0x21 and <= 0x7E && src[i + 1] is >= 0x21 and <= 0x7E && !(hz && src[i] == 0x7E))
        {
            pairs++;
            i += 2;
        }

        if (pairs == 0)
        {
            // A lone byte with no partner.
            Replace(sb, issues, TextIssueKind.InvalidIso2022, start, 1);
            return start + 1;
        }

        if (set == CharSet.Jis0213Plane2)
        {
            sb.Append('\uFFFD', pairs);
            issues?.Add(new TextIssue(TextIssueKind.UnsupportedCharacterSet, start, i - start));
            return i;
        }

        bool jis212 = set == CharSet.Jis212;
        int euc = pairs * (jis212 ? 3 : 2);
        byte[] rented = ArrayPool<byte>.Shared.Rent(euc);
        try
        {
            int w = 0;
            for (int p = start; p < i; p += 2)
            {
                if (jis212)
                {
                    rented[w++] = 0x8F;
                }

                rented[w++] = (byte)(src[p] | 0x80);
                rented[w++] = (byte)(src[p + 1] | 0x80);
            }

            string label = set switch
            {
                CharSet.Gb2312 => "gbk",
                CharSet.Ksc5601 => "euc-kr",
                _ => "euc-jp",
            };
            sb.Append(MailEncoding.DecodeWhatwg(WhatwgEncoding.ForLabel(label)!, rented.AsSpan(0, euc), null, out int errors));
            if (errors > 0)
            {
                issues?.Add(new TextIssue(TextIssueKind.InvalidIso2022, start, i - start));
            }

        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return i;
    }

    // HZ-GB-2312 (RFC 1843): "~{" starts GB 2312 pairs, "~}" ends them, "~~" is a tilde, "~" + LF is a line continuation.
    private static string DecodeHz(ReadOnlySpan<byte> src, List<TextIssue>? issues)
    {
        var sb = new StringBuilder(src.Length);
        bool gb = false;
        int i = 0;
        while (i < src.Length)
        {
            byte b = src[i];
            if (b == (byte)'~' && i + 1 < src.Length)
            {
                byte n = src[i + 1];
                if (n == (byte)'{')
                {
                    gb = true;
                    i += 2;
                    continue;
                }

                if (n == (byte)'}')
                {
                    gb = false;
                    i += 2;
                    continue;
                }

                if (n == (byte)'~')
                {
                    sb.Append('~');
                    i += 2;
                    continue;
                }

                if (n == (byte)'\n')
                {
                    i += 2;
                    continue;
                }

                if (n == (byte)'\r' && i + 2 < src.Length && src[i + 2] == (byte)'\n')
                {
                    i += 3;
                    continue;
                }
            }

            if (b >= 0x80)
            {
                Replace(sb, issues, TextIssueKind.InvalidIso2022, i, 1);
                i++;
            }
            else if (b is 0x0A or 0x0D)
            {
                sb.Append((char)b);
                gb = false;
                i++;
            }
            else if (gb && b >= 0x21)
            {
                i = TwoByteRun(src, i, CharSet.Gb2312, sb, issues, hz: true);
            }
            else
            {
                sb.Append((char)b);
                i++;
            }
        }

        return sb.ToString();
    }

    private static void Replace(StringBuilder sb, List<TextIssue>? issues, TextIssueKind kind, int offset, int length)
    {
        sb.Append('\uFFFD');
        issues?.Add(new TextIssue(kind, offset, length));
    }
}
