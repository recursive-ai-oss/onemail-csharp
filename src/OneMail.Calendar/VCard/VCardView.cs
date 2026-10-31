using System.Globalization;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Calendar.VCard;

/// <summary>The vCard version a card declares.</summary>
public enum VCardVersion
{
    /// <summary>No VERSION property, or one that is not 2.1, 3.0 or 4.0.</summary>
    Unknown = 0,

    /// <summary>vCard 2.1.</summary>
    V21,

    /// <summary>vCard 3.0 (RFC 2426).</summary>
    V30,

    /// <summary>vCard 4.0 (RFC 6350).</summary>
    V40,
}

/// <summary>The N property: five structured components, each a list (RFC 6350 §6.2.2).</summary>
/// <param name="Family">Family names.</param>
/// <param name="Given">Given names.</param>
/// <param name="Additional">Additional names.</param>
/// <param name="Prefixes">Honorific prefixes.</param>
/// <param name="Suffixes">Honorific suffixes.</param>
public sealed record VCardName(
    IReadOnlyList<string> Family, IReadOnlyList<string> Given, IReadOnlyList<string> Additional, IReadOnlyList<string> Prefixes, IReadOnlyList<string> Suffixes);

/// <summary>The ADR property: seven structured components (RFC 6350 §6.3.1), each a list.</summary>
/// <param name="PoBox">Post office box.</param>
/// <param name="Extended">Extended address (apartment, suite).</param>
/// <param name="Street">Street address.</param>
/// <param name="Locality">City.</param>
/// <param name="Region">State or province.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="Country">Country.</param>
/// <param name="Types">The TYPE values and bare types, as written.</param>
/// <param name="Property">The property itself.</param>
public sealed record VCardAddress(
    IReadOnlyList<string> PoBox, IReadOnlyList<string> Extended, IReadOnlyList<string> Street, IReadOnlyList<string> Locality,
    IReadOnlyList<string> Region, IReadOnlyList<string> PostalCode, IReadOnlyList<string> Country, IReadOnlyList<string> Types, ContentProperty Property);

/// <summary>A simple typed property such as EMAIL or TEL.</summary>
/// <param name="Value">The plain text value (for TEL in vCard 4.0 this may be a <c>tel:</c> URI).</param>
/// <param name="Types">The TYPE values and bare types (vCard 2.1/3.0 style), as written.</param>
/// <param name="Preference">The PREF parameter (1 is most preferred), or 1 for a <c>pref</c> type in 2.1/3.0, or null.</param>
/// <param name="Property">The property itself.</param>
public sealed record VCardTyped(string Value, IReadOnlyList<string> Types, int? Preference, ContentProperty Property);

/// <summary>A PHOTO, LOGO or SOUND: inline data or a URI.</summary>
/// <param name="Data">The decoded bytes of inline data, or null.</param>
/// <param name="Uri">The URI text of a linked resource, or null.</param>
/// <param name="MediaType">The media type, from MEDIATYPE, a <c>data:</c> URI or the TYPE parameter; null if unknown.</param>
/// <param name="Property">The property itself.</param>
public sealed record VCardMedia(byte[]? Data, string? Uri, string? MediaType, ContentProperty Property);

/// <summary>A BDAY or ANNIVERSARY value (RFC 6350 §4.3.4), where any part may be missing (<c>--0412</c> has only month and day).</summary>
/// <param name="Year">Year or null.</param>
/// <param name="Month">Month or null.</param>
/// <param name="Day">Day or null.</param>
/// <param name="Hour">Hour or null.</param>
/// <param name="Minute">Minute or null.</param>
/// <param name="Second">Second or null.</param>
/// <param name="Offset">Zone designator as written (<c>Z</c>, <c>-0500</c>), or null.</param>
public readonly record struct VCardDate(int? Year, int? Month, int? Day, int? Hour, int? Minute, int? Second, string? Offset)
{
    /// <summary>Parses a vCard date-and-or-time in basic or extended form, or null if it has no recognisable part.</summary>
    /// <param name="text">The text, for example <c>19961022</c>, <c>1996-10-22</c>, <c>--1022</c>, <c>---22</c>, <c>19961022T140000Z</c>.</param>
    /// <returns>The date, or null (free text such as <c>circa 1800</c> is not a date).</returns>
    public static VCardDate? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string s = text.Trim();
        string datePart = s;
        string timePart = string.Empty;
        int t = s.IndexOfAny(['T', 't']);
        if (t >= 0)
        {
            datePart = s[..t];
            timePart = s[(t + 1)..];
        }

        int? y = null, mo = null, d = null, h = null, mi = null, se = null;
        string? off = null;
        if (datePart.Length > 0)
        {
            bool noYear = datePart.StartsWith("--", StringComparison.Ordinal);
            bool onlyDay = datePart.StartsWith("---", StringComparison.Ordinal);
            string digits = datePart.Replace("-", string.Empty, StringComparison.Ordinal);
            if (!digits.All(char.IsAsciiDigit))
            {
                return null;
            }

            if (onlyDay && digits.Length == 2)
            {
                d = N(digits, 0, 2);
            }
            else if (noYear && digits.Length is 4 or 2)
            {
                mo = N(digits, 0, 2);
                d = digits.Length == 4 ? N(digits, 2, 2) : null;
            }
            else if (!noYear && digits.Length is 4 or 6 or 8)
            {
                y = N(digits, 0, 4);
                mo = digits.Length >= 6 ? N(digits, 4, 2) : null;
                d = digits.Length == 8 ? N(digits, 6, 2) : null;
            }
            else
            {
                return null;
            }
        }

        if (timePart.Length > 0)
        {
            int oi = timePart.IndexOfAny(['Z', 'z', '+']);
            if (oi < 0)
            {
                oi = timePart.LastIndexOf('-');
                if (oi == 0)
                {
                    oi = -1;
                }
            }

            if (oi >= 0)
            {
                off = timePart[oi..];
                timePart = timePart[..oi];
            }

            string td = timePart.Replace(":", string.Empty, StringComparison.Ordinal);
            int p = 0;
            int?[] parts = new int?[3];
            for (int i = 0; i < 3 && p < td.Length; i++)
            {
                if (td[p] == '-')
                {
                    p++;
                    continue;
                }

                if (p + 2 > td.Length || !char.IsAsciiDigit(td[p]) || !char.IsAsciiDigit(td[p + 1]))
                {
                    return null;
                }

                parts[i] = N(td, p, 2);
                p += 2;
            }

            (h, mi, se) = (parts[0], parts[1], parts[2]);
        }

        return y is null && mo is null && d is null && h is null && mi is null && se is null ? null : new VCardDate(y, mo, d, h, mi, se, off);
    }

    private static int N(string s, int start, int len) => int.Parse(s.AsSpan(start, len), NumberStyles.None, CultureInfo.InvariantCulture);
}

/// <summary>
/// A typed view over a VCARD <see cref="Component"/>: vCard 2.1, 3.0 and 4.0 side by side. Version differences are
/// surfaced, not hidden: <see cref="Version"/> says which dialect the card is in, text is unescaped by that dialect's
/// rules, and every accessor also exposes the underlying <see cref="ContentProperty"/> or leaves it reachable through
/// <see cref="Component"/>. Accessors return null or empty lists when a property is missing; malformed values stay in the tree.
/// </summary>
public sealed class VCardView : IEquatable<VCardView>
{
    /// <summary>Wraps a component.</summary>
    /// <param name="component">A VCARD component; the view does not check its name.</param>
    public VCardView(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        Component = component;
    }

    /// <summary>The underlying tree node.</summary>
    public Component Component { get; }

    /// <summary>The declared version.</summary>
    public VCardVersion Version => (Component.FindProperty("VERSION")?.Value.Trim()) switch
    {
        "2.1" => VCardVersion.V21,
        "3.0" => VCardVersion.V30,
        "4.0" => VCardVersion.V40,
        _ => VCardVersion.Unknown,
    };

    /// <summary>The VERSION value as written, or null.</summary>
    public string? VersionText => Component.FindProperty("VERSION")?.Value.Trim();

    private TextEscapeStyle Style => Version == VCardVersion.V21 ? TextEscapeStyle.VCard21 : TextEscapeStyle.Rfc5545;

    private string? Text(string name) => Component.FindProperty(name)?.GetText(Style);

    /// <summary>The first FN.</summary>
    public string? FormattedName => Text("FN");

    /// <summary>Every FN (vCard 4.0 allows several).</summary>
    public IReadOnlyList<string> FormattedNames => Component.FindProperties("FN").Select(p => p.GetText(Style)).ToList();

    /// <summary>The N property.</summary>
    public VCardName? Name
    {
        get
        {
            ContentProperty? p = Component.FindProperty("N");
            if (p is null)
            {
                return null;
            }

            List<IReadOnlyList<string>> c = Structured(p, 5);
            return new VCardName(c[0], c[1], c[2], c[3], c[4]);
        }
    }

    // Splits at unescaped semicolons into components, then each at unescaped commas (RFC 6350 §6.2.2). vCard 2.1
    // has no comma lists, so a 2.1 component is one value.
    private List<IReadOnlyList<string>> Structured(ContentProperty p, int count)
    {
        string decoded = p.GetDecodedValue();
        List<string> comps = ContentValues.SplitUnescaped(decoded, ';');
        List<IReadOnlyList<string>> result = [];
        for (int i = 0; i < count; i++)
        {
            string raw = i < comps.Count ? comps[i] : string.Empty;
            if (raw.Length == 0)
            {
                result.Add([]);
            }
            else if (Version == VCardVersion.V21)
            {
                result.Add([ContentValues.Unescape(raw, Style)]);
            }
            else
            {
                // An empty list element ("5,") carries nothing and is dropped.
                result.Add(ContentValues.SplitUnescaped(raw, ',').Where(v => v.Length > 0).Select(v => ContentValues.Unescape(v, Style)).ToList());
            }
        }

        return result;
    }

    /// <summary>The TYPE values of a property plus its bare parameters other than encodings and charsets (vCard 2.1/3.0 style), as written.</summary>
    /// <param name="p">The property.</param>
    /// <returns>The types.</returns>
    public static IReadOnlyList<string> GetTypes(ContentProperty p)
    {
        ArgumentNullException.ThrowIfNull(p);
        List<string> types = [];
        foreach (ContentParameter c in p.Parameters)
        {
            if (c.IsNamed("TYPE"))
            {
                types.AddRange(c.Values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
            else if (c.IsBare && !(c.IsNamed("QUOTED-PRINTABLE") || c.IsNamed("BASE64") || c.IsNamed("B") || c.IsNamed("8BIT") || c.IsNamed("7BIT")))
            {
                types.Add(c.Name);
            }
        }

        return types;
    }

    private static int? Preference(ContentProperty p, IReadOnlyList<string> types)
    {
        if (int.TryParse(p.GetParameter("PREF"), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
        {
            return n;
        }

        return types.Any(t => t.Equals("pref", StringComparison.OrdinalIgnoreCase)) ? 1 : null;
    }

    private List<VCardTyped> Typed(string name) => Component.FindProperties(name).Select(p =>
    {
        IReadOnlyList<string> t = GetTypes(p);
        return new VCardTyped(p.GetText(Style), t, Preference(p, t), p);
    }).ToList();

    /// <summary>Every EMAIL.</summary>
    public IReadOnlyList<VCardTyped> Emails => Typed("EMAIL");

    /// <summary>Every TEL.</summary>
    public IReadOnlyList<VCardTyped> Phones => Typed("TEL");

    /// <summary>Every URL.</summary>
    public IReadOnlyList<VCardTyped> Urls => Typed("URL");

    /// <summary>Every ADR.</summary>
    public IReadOnlyList<VCardAddress> Addresses => Component.FindProperties("ADR").Select(p =>
    {
        List<IReadOnlyList<string>> c = Structured(p, 7);
        return new VCardAddress(c[0], c[1], c[2], c[3], c[4], c[5], c[6], GetTypes(p), p);
    }).ToList();

    /// <summary>The first ORG as its organization name followed by organizational units.</summary>
    public IReadOnlyList<string> Organization
    {
        get
        {
            ContentProperty? p = Component.FindProperty("ORG");
            return p is null ? [] : ContentValues.SplitUnescaped(p.GetDecodedValue(), ';').Select(v => ContentValues.Unescape(v, Style)).ToList();
        }
    }

    /// <summary>TITLE.</summary>
    public string? Title => Text("TITLE");

    /// <summary>ROLE.</summary>
    public string? Role => Text("ROLE");

    /// <summary>NOTE.</summary>
    public string? Note => Text("NOTE");

    /// <summary>NICKNAME values, split at commas.</summary>
    public IReadOnlyList<string> Nicknames => Component.FindProperties("NICKNAME")
        .SelectMany(p => ContentValues.SplitUnescaped(p.GetDecodedValue(), ',').Select(v => ContentValues.Unescape(v, Style))).ToList();

    /// <summary>CATEGORIES values, split at commas.</summary>
    public IReadOnlyList<string> Categories => Component.FindProperties("CATEGORIES")
        .SelectMany(p => ContentValues.SplitUnescaped(p.GetDecodedValue(), ',').Select(v => ContentValues.Unescape(v, Style))).ToList();

    /// <summary>UID.</summary>
    public string? Uid => Text("UID");

    /// <summary>REV as written.</summary>
    public string? Revision => Text("REV");

    /// <summary>KIND (vCard 4.0, RFC 6350 §6.1.4) as written; null when absent (the RFC default is <c>individual</c>).</summary>
    public string? Kind => Text("KIND");

    /// <summary>MEMBER URIs (vCard 4.0 groups).</summary>
    public IReadOnlyList<string> Members => Component.FindProperties("MEMBER").Select(p => p.GetText(Style)).ToList();

    /// <summary>BDAY parsed; null when absent or not a date (the property stays readable through the tree).</summary>
    public VCardDate? Birthday => VCardDate.TryParse(Component.FindProperty("BDAY")?.Value);

    /// <summary>ANNIVERSARY parsed.</summary>
    public VCardDate? Anniversary => VCardDate.TryParse(Component.FindProperty("ANNIVERSARY")?.Value);

    /// <summary>Every PHOTO.</summary>
    public IReadOnlyList<VCardMedia> Photos => Media("PHOTO");

    /// <summary>Every LOGO.</summary>
    public IReadOnlyList<VCardMedia> Logos => Media("LOGO");

    private List<VCardMedia> Media(string name) => Component.FindProperties(name).Select(MediaOf).ToList();

    private static VCardMedia MediaOf(ContentProperty p)
    {
        string v = p.Value.Trim();
        string? mt = p.GetParameter("MEDIATYPE");
        if (mt is null)
        {
            string? t = GetTypes(p).FirstOrDefault(x => !x.Equals("pref", StringComparison.OrdinalIgnoreCase));
            mt = t is null ? null : t.Contains('/', StringComparison.Ordinal) ? t : "image/" + t.ToLowerInvariant();
        }

        // vCard 4.0 (RFC 6350 §6.2.4) embeds data in a data: URI.
        if (v.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = v.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                string head = v[5..comma];
                bool b64 = head.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
                string type = b64 ? head[..^7] : head;
                if (type.Length > 0)
                {
                    mt = type;
                }

                byte[]? data = null;
                if (b64 && ContentValues.TryDecodeBase64(v[(comma + 1)..], out byte[] b))
                {
                    data = b;
                }
                else if (!b64)
                {
                    data = System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(v[(comma + 1)..]));
                }

                return new VCardMedia(data, data is null ? v : null, mt, p);
            }
        }

        // vCard 2.1 and 3.0 mark inline data with ENCODING=BASE64 or b; otherwise the value is a URI.
        if (p.Encoding is "BASE64" or "B" && p.TryGetBytes(out byte[] bytes))
        {
            return new VCardMedia(bytes, null, mt, p);
        }

        return new VCardMedia(null, v, mt, p);
    }

    /// <summary>The distinct group names (<c>item1</c> in <c>item1.EMAIL</c>) in order of first use.</summary>
    public IReadOnlyList<string> Groups => Component.Properties.Where(p => p.Group is not null).Select(p => p.Group!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The properties of one group, for example <c>item1.EMAIL</c> and <c>item1.X-ABLabel</c> together.</summary>
    /// <param name="group">The group name (ASCII case-insensitive).</param>
    /// <returns>The properties in order.</returns>
    public IEnumerable<ContentProperty> GetGroup(string group) =>
        Component.Properties.Where(p => string.Equals(p.Group, group, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public bool Equals(VCardView? other) => other is not null && ReferenceEquals(Component, other.Component);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VCardView o && Equals(o);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Component);
}

/// <summary>Reading vCard files.</summary>
public static class VCardDocument
{
    /// <summary>The VCARD components of a parsed document, as typed views.</summary>
    /// <param name="document">A parsed document.</param>
    /// <returns>The cards.</returns>
    public static IEnumerable<VCardView> Cards(ContentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Components.Where(c => c.IsNamed("VCARD")).Select(c => new VCardView(c));
    }
}
