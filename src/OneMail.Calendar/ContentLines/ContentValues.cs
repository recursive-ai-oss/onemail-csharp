using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace RecursiveAI.OneMail.Calendar.ContentLines;

/// <summary>How a text value escapes its special characters.</summary>
public enum TextEscapeStyle
{
    /// <summary>RFC 5545 §3.3.11 and RFC 6350 §3.4 (also vCard 3.0): <c>\\</c>, <c>\;</c>, <c>\,</c>, <c>\n</c>/<c>\N</c>.</summary>
    Rfc5545 = 0,

    /// <summary>vCard 2.1: only backslash and semicolon are escaped. Line breaks need quoted-printable.</summary>
    VCard21,
}

/// <summary>
/// A parameter of a content line: a name and zero or more values, or a bare token (vCard 2.1 <c>HOME</c> in
/// <c>TEL;HOME:</c>). Values are decoded: quotes removed and RFC 6868 caret sequences resolved. Instances are immutable.
/// </summary>
public sealed class ContentParameter
{
    /// <summary>Creates a parameter with values.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="values">The decoded values; an empty list is written as <c>NAME=</c>.</param>
    public ContentParameter(string name, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(values);
        Name = name;
        Values = values.ToArray();
        IsBare = false;
    }

    /// <summary>Creates a parameter with one value.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The decoded value.</param>
    public ContentParameter(string name, string value)
        : this(name, [value])
    {
    }

    private ContentParameter(string token)
    {
        Name = token;
        Values = [];
        IsBare = true;
    }

    /// <summary>Creates a bare parameter: just a token, no equals sign (vCard 2.1 <c>TEL;WORK;VOICE:</c>).</summary>
    /// <param name="token">The token.</param>
    /// <returns>The parameter.</returns>
    public static ContentParameter Bare(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new ContentParameter(token);
    }

    /// <summary>The name as written (case preserved); for a bare parameter, the token.</summary>
    public string Name { get; }

    /// <summary>The decoded values; empty for a bare parameter.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>True for a bare parameter (no equals sign).</summary>
    public bool IsBare { get; }

    /// <summary>The first value, or null if there is none.</summary>
    public string? Value => Values.Count > 0 ? Values[0] : null;

    /// <summary>Compares the name to <paramref name="name"/>, ignoring ASCII case.</summary>
    /// <param name="name">The name to compare.</param>
    /// <returns>True when equal.</returns>
    public bool IsNamed(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => IsBare ? Name : Name + "=" + string.Join(',', Values);
}

/// <summary>Charset lookup for text that declares one (vCard 2.1 <c>CHARSET=</c>), using only encodings the .NET runtime ships.</summary>
public static class ContentCharsets
{
    /// <summary>Finds an encoding by its charset name.</summary>
    /// <param name="name">A charset name such as <c>UTF-8</c>, <c>ISO-8859-1</c>, <c>windows-1252</c>.</param>
    /// <returns>The encoding, or null if the runtime does not know the name.</returns>
    public static Encoding? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim();
        if (name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || name.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            // Fall through: the code-page provider knows the single- and double-byte Windows and ISO sets.
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static Encoding Windows1252 { get; } = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

    // Decodes the bytes of one value. Valid UTF-8 is the answer unless a different charset is declared; invalid UTF-8
    // with no usable charset is read as Windows-1252 and flagged (clients' usual guess).
    internal static string Decode(ReadOnlySpan<byte> bytes, string? charset, long offset, int depth, List<CalendarDefect>? defects)
    {
        if (Ascii.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (charset is not null)
        {
            Encoding? enc = Find(charset);
            if (enc is null)
            {
                defects?.Add(new CalendarDefect(CalendarDefectKind.UnknownCharset, offset, bytes.Length, depth));
            }
            else if (enc.CodePage != Encoding.UTF8.CodePage)
            {
                return enc.GetString(bytes);
            }
        }

        if (Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        defects?.Add(new CalendarDefect(CalendarDefectKind.AmbiguousNonUtf8Text, offset, bytes.Length, depth));
        return Windows1252.GetString(bytes);
    }
}

/// <summary>Escaping and transfer-encoding helpers for content-line values and parameters.</summary>
public static class ContentValues
{
    /// <summary>Resolves text escapes. An unknown escape (<c>\x</c>) is kept as written, backslash included.</summary>
    /// <param name="value">The escaped text from a content line.</param>
    /// <param name="style">The escaping rules.</param>
    /// <returns>The plain text.</returns>
    public static string Unescape(string value, TextEscapeStyle style = TextEscapeStyle.Rfc5545)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder sb = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            char n = value[i + 1];
            if (n == '\\' || n == ';' || (style == TextEscapeStyle.Rfc5545 && n == ','))
            {
                sb.Append(n);
                i++;
            }
            else if (style == TextEscapeStyle.Rfc5545 && (n == 'n' || n == 'N'))
            {
                sb.Append('\n');
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Escapes text for a value. Line breaks become <c>\n</c>; a carriage return before a line feed is dropped.</summary>
    /// <param name="text">The plain text.</param>
    /// <param name="style">The escaping rules.</param>
    /// <returns>The escaped text.</returns>
    public static string Escape(string text, TextEscapeStyle style = TextEscapeStyle.Rfc5545)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string? rep = c switch
            {
                '\\' => "\\\\",
                ';' => "\\;",
                ',' when style == TextEscapeStyle.Rfc5545 => "\\,",
                '\n' => "\\n",
                '\r' => i + 1 < text.Length && text[i + 1] == '\n' ? string.Empty : "\\n",
                _ => null,
            };
            if (rep is null)
            {
                sb?.Append(c);
                continue;
            }

            sb ??= new StringBuilder(text.Length + 8).Append(text, 0, i);
            sb.Append(rep);
        }

        return sb?.ToString() ?? text;
    }

    /// <summary>Splits at a separator that is not escaped by a backslash. The parts stay escaped.</summary>
    /// <param name="value">The escaped value.</param>
    /// <param name="separator">The separator, for example <c>,</c> or <c>;</c>.</param>
    /// <returns>The parts; always at least one (possibly empty).</returns>
    public static List<string> SplitUnescaped(string value, char separator)
    {
        ArgumentNullException.ThrowIfNull(value);
        List<string> parts = [];
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (value[i] == separator)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }

        parts.Add(value[start..]);
        return parts;
    }

    /// <summary>
    /// Decodes quoted-printable (RFC 2045 §6.7). Lenient: soft line breaks vanish, and an <c>=</c> not followed by two hex
    /// digits is kept as a literal character.
    /// </summary>
    /// <param name="text">The encoded text (ASCII).</param>
    /// <returns>The decoded bytes.</returns>
    public static byte[] DecodeQuotedPrintable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<byte> o = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '=')
            {
                if (c < 0x80)
                {
                    o.Add((byte)c);
                }
                else
                {
                    o.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                }

                continue;
            }

            if (i + 2 < text.Length && HexVal(text[i + 1]) >= 0 && HexVal(text[i + 2]) >= 0)
            {
                o.Add((byte)((HexVal(text[i + 1]) << 4) | HexVal(text[i + 2])));
                i += 2;
            }
            else if (i + 1 < text.Length && (text[i + 1] == '\r' || text[i + 1] == '\n'))
            {
                i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 2 : 1;
            }
            else
            {
                o.Add((byte)c);
            }
        }

        return [.. o];
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };

    /// <summary>Encodes bytes as quoted-printable on a single line: everything outside printable ASCII, <c>=</c>, space and tab becomes <c>=XX</c>. The caller folds the line.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The encoded text.</returns>
    public static string EncodeQuotedPrintable(ReadOnlySpan<byte> bytes)
    {
        StringBuilder sb = new(bytes.Length + 8);
        foreach (byte b in bytes)
        {
            if (b is >= 33 and <= 126 && b != '=')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('=').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>Decodes base64, ignoring whitespace and tolerating missing padding.</summary>
    /// <param name="text">The base64 text.</param>
    /// <param name="bytes">The decoded bytes.</param>
    /// <returns>False when the text is not base64.</returns>
    public static bool TryDecodeBase64(string text, out byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder sb = new(text.Length + 3);
        foreach (char c in text)
        {
            if (c is not (' ' or '\t' or '\r' or '\n'))
            {
                sb.Append(c);
            }
        }

        while (sb.Length % 4 != 0)
        {
            sb.Append('=');
        }

        try
        {
            bytes = Convert.FromBase64String(sb.ToString());
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>Resolves RFC 6868 caret sequences in a parameter value: <c>^n</c> to a line feed, <c>^^</c> to <c>^</c>, <c>^'</c> to a double quote.</summary>
    /// <param name="value">The parameter value with quotes already removed.</param>
    /// <returns>The decoded value; other caret sequences are kept.</returns>
    public static string DecodeCaret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('^', StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder sb = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '^' && i + 1 < value.Length)
            {
                char n = value[i + 1];
                char? r = n switch { 'n' or 'N' => '\n', '^' => '^', '\'' => '"', _ => null };
                if (r is not null)
                {
                    sb.Append(r.Value);
                    i++;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Applies RFC 6868 caret encoding to a parameter value: <c>^</c>, line breaks and double quotes.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The encoded value.</returns>
    public static string EncodeCaret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? sb = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            string? rep = c switch
            {
                '^' => "^^",
                '"' => "^'",
                '\n' => "^n",
                '\r' => i + 1 < value.Length && value[i + 1] == '\n' ? string.Empty : "^n",
                _ => null,
            };
            if (rep is null)
            {
                sb?.Append(c);
                continue;
            }

            sb ??= new StringBuilder(value.Length + 4).Append(value, 0, i);
            sb.Append(rep);
        }

        return sb?.ToString() ?? value;
    }

    internal static bool HasCaretSequence(string value)
    {
        for (int i = 0; i + 1 < value.Length; i++)
        {
            if (value[i] == '^' && value[i + 1] is 'n' or 'N' or '^' or '\'')
            {
                return true;
            }
        }

        return false;
    }
}
