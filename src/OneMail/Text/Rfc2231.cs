using System.Text;

namespace RecursiveAI.OneMail.Text;

/// <summary>One header parameter after RFC 2231 continuations, charsets and percent escapes were resolved.</summary>
/// <param name="Name">The parameter name without any <c>*</c> suffix, in the case of its first occurrence.</param>
/// <param name="Value">The decoded value.</param>
/// <param name="Language">The RFC 2231 language tag, or <see langword="null"/>.</param>
/// <param name="Charset">The charset the value was decoded with when it came from an extended value (<c>name*=charset'lang'...</c>), else <see langword="null"/>.</param>
/// <param name="IsExtended">True when the value used RFC 2231 syntax (continuations or an extended value).</param>
public readonly record struct DecodedParameter(string Name, string Value, string? Language, MailEncoding? Charset, bool IsExtended);

/// <summary>
/// Collects the raw <c>name=value</c> pairs of one header in source order and resolves them per RFC 2231: continuations
/// <c>name*0</c>, <c>name*1</c> are joined, <c>name*=charset'language'%XX</c> is percent-decoded and charset-decoded.
/// </summary>
/// <remarks>
/// The caller (the MIME reader) tokenises the header and passes each value already unquoted (backslash escapes removed).
/// The decisions are documented in <c>docs/text-encodings.md</c>:
/// <list type="bullet">
/// <item>Segments are joined in numeric order as bytes and decoded once, so a character split across segments survives (RFC 2231 §4.1).</item>
/// <item>A gap in the segment numbers is reported and the segments present are still joined (<see cref="TextIssueKind.AmbiguousParameterSegments"/>).</item>
/// <item>Two segments with the same number are both kept, in source order (reported as <see cref="TextIssueKind.DuplicateParameterSegment"/>). Two plain parameters of the same name: the first wins.</item>
/// <item>A parameter given both plainly and in RFC 2231 form: the RFC 2231 form wins (<see cref="TextIssueKind.AmbiguousParameterForms"/>).</item>
/// <item>An extended value without <c>charset'language'</c> uses the whole text, percent-decoded, as UTF-8 if valid else windows-1252.</item>
/// <item>A plain value that contains an encoded word is decoded as one (<see cref="TextIssueKind.AmbiguousEncodedWordInQuotedString"/>), as mail clients do.</item>
/// <item>Names are compared ignoring ASCII case. At most <see cref="MaxParameters"/> parameters and <see cref="MaxSegments"/> segments per name are kept.</item>
/// </list>
/// An instance is not thread-safe and can be reused after <see cref="Clear"/>.
/// </remarks>
public sealed class ParameterCollector
{
    /// <summary>The most raw parameters one collector keeps; the rest are ignored with <see cref="TextIssueKind.LimitExceeded"/>.</summary>
    public const int MaxParameters = 1024;

    /// <summary>The highest segment number accepted (<c>name*255</c>); a larger one is ignored with <see cref="TextIssueKind.LimitExceeded"/>.</summary>
    public const int MaxSegments = 256;

    private readonly List<Raw> _raw = [];
    private bool _overflow;

    private readonly record struct Raw(string Base, int Index, bool Extended, byte[] Value, int Offset);

    /// <summary>The number of raw parameters added.</summary>
    public int Count => _raw.Count;

    /// <summary>Removes everything so the collector can be reused.</summary>
    public void Clear()
    {
        _raw.Clear();
        _overflow = false;
    }

    /// <summary>Adds one parameter as it appeared in the header.</summary>
    /// <param name="name">The parameter name as written, for example <c>filename*0*</c>.</param>
    /// <param name="value">The value bytes, unquoted.</param>
    /// <param name="sourceOffset">Where the parameter starts in the header; it is the offset of every issue about this parameter.</param>
    public void Add(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, int sourceOffset = 0)
    {
        if (_raw.Count >= MaxParameters)
        {
            _overflow = true;
            return;
        }

        bool extended = false;
        int index = -1;
        ReadOnlySpan<byte> baseName = name;
        if (baseName.Length > 1 && baseName[^1] == (byte)'*')
        {
            extended = true;
            baseName = baseName[..^1];
        }

        int star = baseName.LastIndexOf((byte)'*');
        if (star > 0 && star < baseName.Length - 1 && IsDigits(baseName[(star + 1)..]))
        {
            // Leading zeros are accepted ("*01"); an absurd number is capped so the arithmetic cannot overflow.
            index = baseName.Length - star - 1 > 6 ? int.MaxValue : int.Parse(Encoding.ASCII.GetString(baseName[(star + 1)..]), System.Globalization.CultureInfo.InvariantCulture);
            baseName = baseName[..star];
        }

        _raw.Add(new Raw(Encoding.ASCII.GetString(baseName), index, extended, value.ToArray(), sourceOffset));
    }

    private static bool IsDigits(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return !s.IsEmpty;
    }

    /// <summary>Resolves the parameters added so far.</summary>
    /// <param name="issues">Receives the repairs and guesses; may be <see langword="null"/>.</param>
    /// <param name="fallback">The charset for 8-bit values that are not UTF-8 and carry no charset; <see langword="null"/> means windows-1252.</param>
    /// <returns>One entry per distinct name, in order of first appearance. Never throws.</returns>
    public List<DecodedParameter> Resolve(List<TextIssue>? issues = null, MailEncoding? fallback = null)
    {
        fallback ??= MailEncoding.Windows1252;
        var result = new List<DecodedParameter>();
        if (_overflow)
        {
            issues?.Add(new TextIssue(TextIssueKind.LimitExceeded, _raw.Count > 0 ? _raw[^1].Offset : 0, 0));
        }

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Raw first in _raw)
        {
            if (!done.Add(first.Base))
            {
                continue;
            }

            result.Add(ResolveOne(first, issues, fallback));
        }

        return result;
    }

    private DecodedParameter ResolveOne(Raw first, List<TextIssue>? issues, MailEncoding fallback)
    {
        List<Raw> plain = [];
        List<Raw> single = [];
        List<Raw> segments = [];
        foreach (Raw r in _raw)
        {
            if (!string.Equals(r.Base, first.Base, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (r.Index >= 0)
            {
                segments.Add(r);
            }
            else if (r.Extended)
            {
                single.Add(r);
            }
            else
            {
                plain.Add(r);
            }
        }

        bool hasRfc2231 = segments.Count > 0 || single.Count > 0;
        if (hasRfc2231 && plain.Count > 0)
        {
            issues?.Add(new TextIssue(TextIssueKind.AmbiguousParameterForms, plain[0].Offset, 0));
        }

        if (plain.Count > 1 && !hasRfc2231)
        {
            issues?.Add(new TextIssue(TextIssueKind.DuplicateParameter, plain[1].Offset, 0));
        }

        if (segments.Count == 0 && single.Count > 0)
        {
            if (single.Count > 1)
            {
                issues?.Add(new TextIssue(TextIssueKind.DuplicateParameter, single[1].Offset, 0));
            }

            return DecodeExtended(first.Base, [single[0]], issues, fallback);
        }

        if (segments.Count > 0)
        {
            return DecodeExtended(first.Base, segments, issues, fallback);
        }

        return DecodePlainValue(first.Base, plain[0], issues, fallback);
    }

    private static DecodedParameter DecodePlainValue(string name, Raw r, List<TextIssue>? issues, MailEncoding fallback)
    {
        // A plain value: UTF-8 or the fallback; encoded words are decoded because Outlook and Thunderbird write them.
        List<TextIssue>? local = issues is null ? null : [];
        string value = Rfc2047.DecodeText(r.Value, local, EncodedWordContext.QuotedString, fallback);
        Forward(local, issues, r.Offset);
        return new DecodedParameter(name, value, null, null, false);
    }

    // Issues from a nested decoder are about a slice of the value; point them at the parameter.
    private static void Forward(List<TextIssue>? from, List<TextIssue>? to, int offset)
    {
        if (from is null || to is null)
        {
            return;
        }

        foreach (TextIssue t in from)
        {
            to.Add(new TextIssue(t.Kind, offset, 0));
        }
    }

    private static DecodedParameter DecodeExtended(string name, List<Raw> parts, List<TextIssue>? issues, MailEncoding fallback)
    {
        // RFC 2231 §3: segments are ordered by number (stable, so a duplicate follows its twin). Gaps are reported, not repaired.
        parts = [.. parts.OrderBy(static r => r.Index)];
        var bytes = new List<byte>();
        MailEncoding? charset = null;
        string? language = null;
        bool sawCharset = false;
        int expected = 0;
        int previous = -2;
        bool gap = false;
        for (int i = 0; i < parts.Count; i++)
        {
            Raw r = parts[i];
            if (r.Index == previous)
            {
                // Python and MimeKit both keep every copy, in source order, so we do too (and say so).
                issues?.Add(new TextIssue(TextIssueKind.DuplicateParameterSegment, r.Offset, 0));
            }

            if (r.Index >= MaxSegments)
            {
                issues?.Add(new TextIssue(TextIssueKind.LimitExceeded, r.Offset, 0));
                continue;
            }

            previous = r.Index;
            if (Math.Max(r.Index, 0) != expected)
            {
                gap = true;
            }

            expected = Math.Max(r.Index, 0) + 1;
            ReadOnlySpan<byte> value = r.Value;
            if (r.Extended)
            {
                if (!sawCharset)
                {
                    // RFC 2231 §4: only the first segment carries charset'language'.
                    sawCharset = true;
                    value = SplitCharsetAndLanguage(value, r.Offset, issues, out charset, out language);
                }

                PercentDecode(value, bytes, r.Offset, issues);
            }
            else
            {
                foreach (byte b in value)
                {
                    bytes.Add(b);
                }
            }
        }

        if (gap)
        {
            issues?.Add(new TextIssue(TextIssueKind.AmbiguousParameterSegments, parts[0].Offset, 0));
        }

        byte[] all = bytes.ToArray();
        string text;
        if (charset is not null)
        {
            List<TextIssue>? local = issues is null ? null : [];
            text = charset.GetString(all, local);
            Forward(local, issues, parts[0].Offset);
        }
        else if (System.Text.Ascii.IsValid(all) || System.Text.Unicode.Utf8.IsValid(all))
        {
            text = Encoding.UTF8.GetString(all);
        }
        else
        {
            issues?.Add(new TextIssue(TextIssueKind.AmbiguousRaw8Bit, parts[0].Offset, all.Length));
            text = fallback.GetString(all, null);
        }

        return new DecodedParameter(name, text, language, charset, true);
    }

    // "charset'language'rest". Returns rest; charset is null when absent or unknown.
    private static ReadOnlySpan<byte> SplitCharsetAndLanguage(ReadOnlySpan<byte> value, int offset, List<TextIssue>? issues, out MailEncoding? charset, out string? language)
    {
        charset = null;
        language = null;
        int a = value.IndexOf((byte)'\'');
        int b = a < 0 ? -1 : value[(a + 1)..].IndexOf((byte)'\'');
        if (a < 0 || b < 0)
        {
            issues?.Add(new TextIssue(TextIssueKind.MalformedExtendedValue, offset, value.Length));
            return value;
        }

        b += a + 1;
        ReadOnlySpan<byte> label = value[..a];
        if (!label.IsEmpty)
        {
            // Bounded: at most 64 characters (longer labels are not looked up).
            Span<char> chars = stackalloc char[Math.Min(label.Length, 64)];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)label[i];
            }

            charset = label.Length <= 64 ? MailEncoding.ForLabel(chars) : null;
            if (charset is null)
            {
                issues?.Add(new TextIssue(TextIssueKind.AmbiguousUnknownCharset, offset, label.Length));
            }
        }

        ReadOnlySpan<byte> lang = value.Slice(a + 1, b - a - 1);
        if (!lang.IsEmpty)
        {
            language = Encoding.ASCII.GetString(lang);
        }

        return value[(b + 1)..];
    }

    private static void PercentDecode(ReadOnlySpan<byte> value, List<byte> destination, int offset, List<TextIssue>? issues)
    {
        bool bad = false;
        for (int i = 0; i < value.Length; i++)
        {
            byte c = value[i];
            if (c == (byte)'%')
            {
                int hi = i + 2 < value.Length ? Rfc2047.HexValue(value[i + 1]) : -1;
                int lo = i + 2 < value.Length ? Rfc2047.HexValue(value[i + 2]) : -1;
                if (hi >= 0 && lo >= 0)
                {
                    destination.Add((byte)((hi << 4) | lo));
                    i += 2;
                    continue;
                }

                bad = true;
            }

            destination.Add(c);
        }

        if (bad)
        {
            issues?.Add(new TextIssue(TextIssueKind.InvalidPercentEscape, offset, value.Length));
        }
    }
}

/// <summary>Writes parameters in RFC 2231 form.</summary>
public static class Rfc2231
{
    /// <summary>One <c>name=value</c> pair ready to be written after <c>;</c>.</summary>
    /// <param name="Name">The parameter name, for example <c>filename</c>, <c>filename*</c>, <c>filename*0*</c> or <c>filename*1</c>.</param>
    /// <param name="Value">The value. Already percent-encoded for extended parts; for a plain part it is the raw value and needs quoting when <paramref name="NeedsQuotes"/>.</param>
    /// <param name="NeedsQuotes">True when the value must be written as a quoted-string (with <c>"</c> and <c>\</c> escaped).</param>
    public readonly record struct EncodedPart(string Name, string Value, bool NeedsQuotes);

    /// <summary>
    /// Encodes one parameter. A value that is an RFC 2045 token comes out as <c>name=value</c>, other printable ASCII as a
    /// quoted-string, and anything else as <c>name*=utf-8''%XX...</c>; a long value is split into <c>name*0*</c>, <c>name*1*</c> ... parts
    /// that never cut a character. The first part carries the charset.
    /// </summary>
    /// <param name="name">The parameter name, ASCII.</param>
    /// <param name="value">The value.</param>
    /// <param name="charset">The charset to encode non-ASCII values with; <see langword="null"/> means UTF-8.</param>
    /// <param name="language">An optional RFC 2231 language tag such as <c>en</c>.</param>
    /// <param name="maxPartLength">The most characters of encoded value in one part (default 50), counting percent escapes. Values that fit are not split.</param>
    /// <returns>One or more parts in the order to write them.</returns>
    public static List<EncodedPart> Encode(string name, string value, MailEncoding? charset = null, string? language = null, int maxPartLength = 50)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        charset ??= MailEncoding.Utf8;
        var parts = new List<EncodedPart>();
        bool ascii = true;
        bool token = value.Length > 0;
        foreach (char c in value)
        {
            if (c is < ' ' or > '~')
            {
                ascii = false;
                token = false;
            }
            else if (!IsTokenChar(c))
            {
                token = false;
            }
        }

        if (ascii && value.Length <= maxPartLength)
        {
            parts.Add(new EncodedPart(name, value, !token));
            return parts;
        }

        string prefix = charset.Name + "'" + (language ?? string.Empty) + "'";
        var chunks = new List<string>();
        var current = new StringBuilder();
        int i = 0;
        while (i < value.Length)
        {
            int len = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
            string piece = PercentEncode(charset.GetBytes(value.Substring(i, len)));
            int limit = chunks.Count == 0 ? maxPartLength - prefix.Length : maxPartLength;
            if (current.Length > 0 && current.Length + piece.Length > Math.Max(limit, 9))
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            current.Append(piece);
            i += len;
        }

        chunks.Add(current.ToString());
        if (chunks.Count == 1)
        {
            parts.Add(new EncodedPart(name + "*", prefix + chunks[0], false));
            return parts;
        }

        for (int n = 0; n < chunks.Count; n++)
        {
            parts.Add(new EncodedPart(name + "*" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*", (n == 0 ? prefix : string.Empty) + chunks[n], false));
        }

        return parts;
    }

    // RFC 2231 §7 attribute-char: any ASCII except controls, space, and "*'%()<>@,;:\"/[]?=".
    private static string PercentEncode(byte[] bytes)
    {
        const string Hex = "0123456789ABCDEF";
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (byte b in bytes)
        {
            if (IsTokenChar((char)b) && b < 0x7F && b is not (byte)'%' and not (byte)'\'' and not (byte)'*')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 15]);
            }
        }

        return sb.ToString();
    }

    // RFC 2045 token: printable ASCII except space and tspecials.
    private static bool IsTokenChar(char c) => c is > ' ' and < (char)0x7F && "()<>@,;:\\\"/[]?=".IndexOf(c) < 0;
}
