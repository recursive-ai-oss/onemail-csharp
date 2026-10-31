using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Calendar.Forms;

// The in-between model. A content line becomes an FProperty (lower-case names, a type, values in the shapes RFC 7265 and
// RFC 7095 give them), and JSON and XML are both written from and read into it. Neither form depends on the other.

internal enum FKind
{
    String,
    Number,
    Bool,
    Array,
    Object,
}

internal sealed class FormValue
{
    private FormValue(FKind kind) => Kind = kind;

    internal FKind Kind { get; }

    // String text, or the lexical form of a number.
    internal string Text { get; private init; } = string.Empty;

    internal bool Bool { get; private init; }

    internal List<FormValue> Items { get; private init; } = [];

    internal List<KeyValuePair<string, FormValue>> Members { get; private init; } = [];

    internal long Offset { get; set; } = -1;

    internal static FormValue Str(string s) => new(FKind.String) { Text = s };

    internal static FormValue Num(string lexical) => new(FKind.Number) { Text = lexical };

    internal static FormValue Boolean(bool b) => new(FKind.Bool) { Bool = b };

    internal static FormValue Arr(List<FormValue>? items = null) => new(FKind.Array) { Items = items ?? [] };

    internal static FormValue Obj() => new(FKind.Object);

    internal bool IsScalar => Kind is FKind.String or FKind.Number or FKind.Bool;

    // The text of a scalar as it would sit in a content line or XML element.
    internal string ScalarText => Kind switch
    {
        FKind.Bool => Bool ? "true" : "false",
        FKind.String or FKind.Number => Text,
        _ => string.Empty,
    };
}

internal sealed class FParam
{
    internal FParam(string name) => Name = name;

    internal string Name { get; }

    internal List<string> Values { get; } = [];
}

internal sealed class FProperty
{
    internal string? Group { get; set; }

    internal string Name { get; set; } = string.Empty;

    internal List<FParam> Params { get; } = [];

    internal string Type { get; set; } = "unknown";

    internal List<FormValue> Values { get; } = [];

    internal long Offset { get; set; } = -1;

    internal int Line { get; set; }

    internal int Column { get; set; }

    internal FParam? Find(string name)
    {
        foreach (FParam p in Params)
        {
            if (p.Name == name)
            {
                return p;
            }
        }

        return null;
    }
}

internal sealed class FComponent
{
    internal string Name { get; set; } = string.Empty;

    internal List<FProperty> Props { get; } = [];

    internal List<FComponent> Comps { get; } = [];

    internal long Offset { get; set; } = -1;
}

// Which family of rules applies: iCalendar, or vCard in one of its three versions.
internal enum Dialect
{
    ICal,
    VCard21,
    VCard30,
    VCard40,
}

internal static class DialectExtensions
{
    internal static bool IsVCard(this Dialect d) => d != Dialect.ICal;

    internal static TextEscapeStyle Style(this Dialect d) => d == Dialect.VCard21 ? TextEscapeStyle.VCard21 : TextEscapeStyle.Rfc5545;
}

// Collects losses per (kind, path) and document-wide kinds once.
internal sealed class LossSink
{
    private readonly Dictionary<(FormLossKind, string), int> _counts = [];
    private readonly List<(FormLossKind, string)> _order = [];

    internal void Add(FormLossKind kind, string path)
    {
        (FormLossKind, string) key = (kind, path);
        if (_counts.TryGetValue(key, out int n))
        {
            _counts[key] = n + 1;
        }
        else
        {
            _counts[key] = 1;
            _order.Add(key);
        }
    }

    internal List<FormLoss> ToList()
    {
        List<FormLoss> l = new(_order.Count);
        foreach ((FormLossKind k, string p) in _order)
        {
            l.Add(new FormLoss(k, p, _counts[(k, p)]));
        }

        return l;
    }
}

internal sealed class DefectSink
{
    internal List<FormDefect> Items { get; } = [];

    internal void Add(FormDefectKind kind, long offset, string path, int line = 0, int column = 0) => Items.Add(new FormDefect(kind, offset, line, column, path));
}

internal static class FormText
{
    internal static string Lower(string s) => s.ToLowerInvariant();

    internal static string Upper(string s) => s.ToUpperInvariant();

    internal static bool IsUpper(string s)
    {
        foreach (char c in s)
        {
            if (c is >= 'a' and <= 'z')
            {
                return false;
            }
        }

        return true;
    }

    // Lone surrogates cannot be written as UTF-8 or XML.
    internal static string Sanitize(string s, out bool changed)
    {
        changed = false;
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool bad = false;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb?.Append(c).Append(s[i + 1]);
                    i++;
                    continue;
                }

                bad = true;
            }
            else if (char.IsLowSurrogate(c))
            {
                bad = true;
            }

            if (bad)
            {
                sb ??= new StringBuilder(s.Length).Append(s, 0, i);
                sb.Append('\uFFFD');
                changed = true;
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? s;
    }

    // XML 1.0 Char production; anything else is replaced.
    internal static string XmlSafe(string s, out bool changed)
    {
        s = Sanitize(s, out changed);
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD) || char.IsSurrogate(c);
            if (ok)
            {
                sb?.Append(c);
            }
            else
            {
                sb ??= new StringBuilder(s.Length).Append(s, 0, i);
                sb.Append('\uFFFD');
                changed = true;
            }
        }

        return sb?.ToString() ?? s;
    }

    // JSON number lexical form from an iCalendar/vCard number, or null when it is not a number.
    internal static string? NormalizeNumber(string s, bool integerOnly)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        int i = 0;
        bool neg = false;
        if (s[0] is '+' or '-')
        {
            neg = s[0] == '-';
            i = 1;
        }

        int intStart = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            i++;
        }

        string intPart = s[intStart..i];
        string frac = string.Empty;
        if (i < s.Length && s[i] == '.' && !integerOnly)
        {
            int fs = ++i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            frac = s[fs..i];
        }

        if (i != s.Length || (intPart.Length == 0 && frac.Length == 0))
        {
            return null;
        }

        intPart = intPart.TrimStart('0');
        if (intPart.Length == 0)
        {
            intPart = "0";
        }

        frac = frac.TrimEnd('0');
        bool zero = intPart == "0" && frac.Length == 0;
        return (neg && !zero ? "-" : string.Empty) + intPart + (frac.Length > 0 ? "." + frac : string.Empty);
    }

    // A JSON number lexical form as it is written in iCalendar or vCard: no exponent, and for integers no fraction
    // (RFC 7095 §3.5.9, §3.5.10).
    internal static string NumberToText(string lexical, bool integer)
    {
        if (lexical.AsSpan().IndexOfAny('e', 'E') < 0 && (!integer || lexical.IndexOf('.', StringComparison.Ordinal) < 0))
        {
            return lexical;
        }

        if (decimal.TryParse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
        {
            if (integer)
            {
                d = decimal.Truncate(d);
            }

            return d.ToString("0.#############################", CultureInfo.InvariantCulture);
        }

        if (double.TryParse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
        {
            return (integer ? Math.Truncate(x) : x).ToString("0.################", CultureInfo.InvariantCulture);
        }

        return lexical;
    }
}
