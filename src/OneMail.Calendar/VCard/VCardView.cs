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
public sealed record VCardTyped(string Value, IReadOnlyList<string> Types, int? Preference, ContentProperty Property)
{
    /// <summary>The ALTID parameter (RFC 6350 section 5.4): properties sharing one are alternative representations of the same thing.</summary>
    public string? AltId => Property.GetParameter("ALTID");

    /// <summary>The PID parameter values (RFC 6350 section 5.5), for example <c>1.1</c>, used to match a property across synchronised copies of a card.</summary>
    public IReadOnlyList<string> Pid => Property.Parameters.Find("PID")?.Values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList() ?? [];

    /// <summary>The LANGUAGE parameter, or null.</summary>
    public string? Language => Property.GetParameter("LANGUAGE");

    /// <summary>The VALUE parameter (<c>uri</c>, <c>text</c>, <c>utc-offset</c> ...) lower-cased, or null.</summary>
    public string? ValueType => Property.GetParameter("VALUE")?.ToLowerInvariant();

    /// <summary>The LABEL parameter (vCard 4.0 ADR and similar), or null.</summary>
    public string? Label => Property.GetParameter("LABEL");

    /// <summary>Whether <see cref="Types"/> holds <paramref name="type"/> (ASCII case-insensitive).</summary>
    /// <param name="type">The type, for example <c>work</c>.</param>
    /// <returns>True when present.</returns>
    public bool HasType(string type) => Types.Any(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A PHOTO, LOGO, SOUND or KEY: inline data or a URI. Inline data is decoded on first use of <see cref="Data"/>, so a card with a large photo
/// costs nothing until the photo is wanted.
/// </summary>
public sealed class VCardMedia
{
    private readonly Func<byte[]?>? _decode;
    private byte[]? _data;
    private bool _decoded;

    internal VCardMedia(Func<byte[]?>? decode, string? uri, string? mediaType, ContentProperty property)
    {
        _decode = decode;
        Uri = uri;
        MediaType = mediaType;
        Property = property;
    }

    /// <summary>The decoded bytes of inline data, or null for a linked resource or data that does not decode (the text stays in <see cref="Property"/>).</summary>
    public byte[]? Data
    {
        get
        {
            if (!_decoded)
            {
                _data = _decode?.Invoke();
                _decoded = true;
            }

            return _data;
        }
    }

    /// <summary>Whether the value is inline data (it may still fail to decode, in which case <see cref="Data"/> is null).</summary>
    public bool IsInline => _decode is not null;

    /// <summary>The URI text of a linked resource, or null.</summary>
    public string? Uri { get; }

    /// <summary>The media type, from MEDIATYPE, a <c>data:</c> URI or the TYPE parameter; null if unknown.</summary>
    public string? MediaType { get; }

    /// <summary>The property itself.</summary>
    public ContentProperty Property { get; }
}

/// <summary>A GENDER value (RFC 6350 section 6.2.7): a sex component and optional free text.</summary>
/// <param name="Sex">One of <c>M</c>, <c>F</c>, <c>O</c>, <c>N</c>, <c>U</c> as written (upper-cased), or null when empty.</param>
/// <param name="Identity">The free-text identity, or null.</param>
public sealed record VCardGender(string? Sex, string? Identity);

/// <summary>A GEO value (RFC 6350 section 6.5.2, RFC 2426 section 3.4.2).</summary>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="Property">The property itself.</param>
public sealed record VCardGeo(double Latitude, double Longitude, ContentProperty Property);

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

    /// <summary>The text of a property of this card: transfer encoding removed, then escapes resolved by the card's dialect (see <see cref="Unescape"/>).</summary>
    /// <param name="property">A property of the card.</param>
    /// <returns>The text.</returns>
    public string GetText(ContentProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Unescape(property.GetDecodedValue());
    }

    /// <summary>
    /// Resolves the text escapes of one value (or one component of a structured value) the way this card's version defines them. vCard 2.1 knows
    /// <c>\\</c> and <c>\;</c> only. vCard 3.0 and 4.0 (RFC 2426 section 5, RFC 6350 section 3.4) know <c>\\ \, \; \n \N</c>; a backslash before any other
    /// character is dropped and the character kept, because Apple Contacts writes <c>http\://</c> and every reader checked reads it as <c>http://</c>.
    /// </summary>
    /// <param name="value">The value with its escapes.</param>
    /// <returns>The text.</returns>
    public string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Version == VCardVersion.V21 || !value.Contains('\\', StringComparison.Ordinal))
        {
            return ContentValues.Unescape(value, Style);
        }

        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            char n = value[++i];
            sb.Append(n is 'n' or 'N' ? '\n' : n);
        }

        return sb.ToString();
    }

    private string? Text(string name) => Component.FindProperty(name) is { } tp ? GetText(tp) : null;

    /// <summary>The first FN.</summary>
    public string? FormattedName => Text("FN");

    /// <summary>Every FN (vCard 4.0 allows several).</summary>
    public IReadOnlyList<string> FormattedNames => Component.FindProperties("FN").Select(p => GetText(p)).ToList();

    /// <summary>The first N property.</summary>
    public VCardName? Name => Component.FindProperty("N") is { } p ? NameOf(p) : null;

    /// <summary>Every N property (vCard 4.0 allows several, and a card from a merge tool often has more than one).</summary>
    public IReadOnlyList<VCardName> Names => Component.FindProperties("N").Select(NameOf).ToList();

    private VCardName NameOf(ContentProperty p)
    {
        List<IReadOnlyList<string>> c = Structured(p, 5);
        return new VCardName(c[0], c[1], c[2], c[3], c[4]);
    }

    // Splits at unescaped semicolons into components, then each at unescaped commas (RFC 6350 §6.2.2). vCard 2.1
    // has no comma lists, so a 2.1 component is one value.
    private List<IReadOnlyList<string>> Structured(ContentProperty p, int count)
    {
        string decoded = p.GetDecodedValue();
        List<string> comps = ContentValues.SplitUnescaped(decoded, ';');
        List<IReadOnlyList<string>> result = new(count);
        bool v21 = Version == VCardVersion.V21;
        for (int i = 0; i < count; i++)
        {
            string raw = i < comps.Count ? comps[i] : string.Empty;
            if (raw.Length == 0)
            {
                result.Add([]);
            }
            else if (v21)
            {
                result.Add([Unescape(raw)]);
            }
            else if (raw.AsSpan().IndexOfAny(',', '\\') < 0)
            {
                // No list separator and no escape: the component is the one value.
                result.Add([raw]);
            }
            else
            {
                // An empty list element ("5,") carries nothing and is dropped.
                List<string> items = [];
                foreach (string v in ContentValues.SplitUnescaped(raw, ','))
                {
                    if (v.Length > 0)
                    {
                        items.Add(Unescape(v));
                    }
                }

                result.Add(items);
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
                foreach (string v in c.Values)
                {
                    // The usual value is one bare word: keep it as it is rather than splitting.
                    if (v.Length > 0 && !v.Contains(',', StringComparison.Ordinal) && !char.IsWhiteSpace(v[0]) && !char.IsWhiteSpace(v[^1]))
                    {
                        types.Add(v);
                    }
                    else
                    {
                        types.AddRange(v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                }
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

    private List<VCardTyped> Typed(string name)
    {
        List<VCardTyped> result = [];
        foreach (ContentProperty p in Component.FindProperties(name))
        {
            IReadOnlyList<string> t = GetTypes(p);
            result.Add(new VCardTyped(GetText(p), t, Preference(p, t), p));
        }

        return result;
    }

    /// <summary>Every EMAIL.</summary>
    public IReadOnlyList<VCardTyped> Emails => Typed("EMAIL");

    /// <summary>Every TEL.</summary>
    public IReadOnlyList<VCardTyped> Phones => Typed("TEL");

    /// <summary>Every URL.</summary>
    public IReadOnlyList<VCardTyped> Urls => Typed("URL");

    /// <summary>Every ADR.</summary>
    public IReadOnlyList<VCardAddress> Addresses
    {
        get
        {
            List<VCardAddress> result = [];
            foreach (ContentProperty p in Component.FindProperties("ADR"))
            {
                List<IReadOnlyList<string>> c = Structured(p, 7);
                result.Add(new VCardAddress(c[0], c[1], c[2], c[3], c[4], c[5], c[6], GetTypes(p), p));
            }

            return result;
        }
    }

    /// <summary>The first ORG as its organization name followed by organizational units.</summary>
    public IReadOnlyList<string> Organization
    {
        get
        {
            ContentProperty? p = Component.FindProperty("ORG");
            return p is null ? [] : ContentValues.SplitUnescaped(p.GetDecodedValue(), ';').Select(v => Unescape(v)).ToList();
        }
    }

    /// <summary>TITLE (the first).</summary>
    public string? Title => Text("TITLE");

    /// <summary>Every TITLE (vCard 4.0 allows several, and older cards often have more than one).</summary>
    public IReadOnlyList<string> Titles => Component.FindProperties("TITLE").Select(p => GetText(p)).ToList();

    /// <summary>ROLE (the first).</summary>
    public string? Role => Text("ROLE");

    /// <summary>Every ROLE.</summary>
    public IReadOnlyList<string> Roles => Component.FindProperties("ROLE").Select(p => GetText(p)).ToList();

    /// <summary>NOTE (the first).</summary>
    public string? Note => Text("NOTE");

    /// <summary>Every NOTE.</summary>
    public IReadOnlyList<string> Notes => Component.FindProperties("NOTE").Select(p => GetText(p)).ToList();

    /// <summary>Every ORG, each as the organization name followed by its units.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Organizations => Component.FindProperties("ORG")
        .Select(p => (IReadOnlyList<string>)ContentValues.SplitUnescaped(p.GetDecodedValue(), ';').Select(v => Unescape(v)).ToList()).ToList();

    /// <summary>NICKNAME values, split at commas.</summary>
    public IReadOnlyList<string> Nicknames => Component.FindProperties("NICKNAME")
        .SelectMany(p => ContentValues.SplitUnescaped(p.GetDecodedValue(), ',').Select(v => Unescape(v))).ToList();

    /// <summary>CATEGORIES values, split at commas.</summary>
    public IReadOnlyList<string> Categories => Component.FindProperties("CATEGORIES")
        .SelectMany(p => ContentValues.SplitUnescaped(p.GetDecodedValue(), ',').Select(v => Unescape(v))).ToList();

    /// <summary>UID.</summary>
    public string? Uid => Text("UID");

    /// <summary>REV as written.</summary>
    public string? Revision => Text("REV");

    /// <summary>KIND (vCard 4.0, RFC 6350 §6.1.4) as written; null when absent (the RFC default is <c>individual</c>).</summary>
    public string? Kind => Text("KIND");

    /// <summary>MEMBER URIs (vCard 4.0 groups).</summary>
    public IReadOnlyList<string> Members => Component.FindProperties("MEMBER").Select(p => GetText(p)).ToList();

    /// <summary>BDAY parsed; null when absent or not a date (the property stays readable through the tree).</summary>
    public VCardDate? Birthday => VCardDate.TryParse(Component.FindProperty("BDAY")?.Value);

    /// <summary>Every BDAY that parses as a date (a card may carry several; vCard 4.0 allows one).</summary>
    public IReadOnlyList<VCardDate> Birthdays => Component.FindProperties("BDAY").Select(p => VCardDate.TryParse(p.Value)).Where(d => d is not null).Select(d => d!.Value).ToList();

    /// <summary>Every ANNIVERSARY that parses as a date.</summary>
    public IReadOnlyList<VCardDate> Anniversaries => Component.FindProperties("ANNIVERSARY").Select(p => VCardDate.TryParse(p.Value)).Where(d => d is not null).Select(d => d!.Value).ToList();

    /// <summary>ANNIVERSARY parsed.</summary>
    public VCardDate? Anniversary => VCardDate.TryParse(Component.FindProperty("ANNIVERSARY")?.Value);

    /// <summary>Every PHOTO.</summary>
    public IReadOnlyList<VCardMedia> Photos => Media("PHOTO");

    /// <summary>Every LOGO.</summary>
    public IReadOnlyList<VCardMedia> Logos => Media("LOGO");

    /// <summary>Every SOUND.</summary>
    public IReadOnlyList<VCardMedia> Sounds => Media("SOUND");

    /// <summary>Every KEY (RFC 6350 section 6.8.1). Inline keys have <see cref="VCardMedia.Data"/>; the TYPE of a 2.1/3.0 key (<c>PGP</c>, <c>X509</c>) is the media type as written.</summary>
    public IReadOnlyList<VCardMedia> Keys => Media("KEY");

    private List<VCardMedia> Media(string name) => Component.FindProperties(name).Select(p => MediaOf(p, name)).ToList();

    // Photos in the wild carry a surplus or missing '=' or stray white space; the bytes are still all there. Anything else in the text is an error.
    private static byte[]? DecodeBase64(string text)
    {
        var clean = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/')
            {
                clean.Append(ch);
            }
            else if (!(ch is '=' || char.IsWhiteSpace(ch)))
            {
                return null;
            }
        }

        if (clean.Length % 4 == 1)
        {
            return null;
        }

        clean.Append('=', (4 - clean.Length % 4) % 4);
        return ContentValues.TryDecodeBase64(clean.ToString(), out byte[] bytes) ? bytes : null;
    }

    private static VCardMedia MediaOf(ContentProperty p, string owner)
    {
        string v = p.Value.Trim();
        string? mt = p.GetParameter("MEDIATYPE");
        if (mt is null)
        {
            string? t = GetTypes(p).FirstOrDefault(x => !x.Equals("pref", StringComparison.OrdinalIgnoreCase));
            string prefix = owner switch { "SOUND" => "audio/", "KEY" => string.Empty, _ => "image/" };
            mt = t is null ? null : t.Contains('/', StringComparison.Ordinal) ? t : prefix + (prefix.Length == 0 ? t : t.ToLowerInvariant());
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

                string payload = v[(comma + 1)..];
                byte[]? Decode()
                {
                    if (b64)
                    {
                        return DecodeBase64(payload);
                    }

                    return System.Text.Encoding.UTF8.GetBytes(System.Uri.UnescapeDataString(payload));
                }

                return new VCardMedia(Decode, null, mt, p);
            }
        }

        // vCard 2.1 and 3.0 mark inline data with ENCODING=BASE64 or b; otherwise the value is a URI.
        if (p.Encoding is "BASE64" or "B")
        {
            return new VCardMedia(() => DecodeBase64(p.Value), null, mt, p);
        }

        return new VCardMedia(null, v, mt, p);
    }

    /// <summary>Every IMPP (instant messaging URI, RFC 6350 section 6.4.3).</summary>
    public IReadOnlyList<VCardTyped> Impps => Typed("IMPP");

    /// <summary>Every RELATED (RFC 6350 section 6.6.6); the relation is in <see cref="VCardTyped.Types"/>.</summary>
    public IReadOnlyList<VCardTyped> Related => Typed("RELATED");

    /// <summary>Every LANG (RFC 6350 section 6.4.4) language tag.</summary>
    public IReadOnlyList<VCardTyped> Languages => Typed("LANG");

    /// <summary>Every TZ (RFC 6350 section 6.5.1): a zone name, a UTC offset (<c>-0500</c>, or <c>-05:00</c> in 3.0) or a URI, as written.</summary>
    public IReadOnlyList<VCardTyped> TimeZones => Typed("TZ");

    /// <summary>The first GENDER (vCard 4.0).</summary>
    public VCardGender? Gender
    {
        get
        {
            ContentProperty? p = Component.FindProperty("GENDER");
            if (p is null)
            {
                return null;
            }

            List<string> parts = ContentValues.SplitUnescaped(p.GetDecodedValue(), ';');
            string sex = parts.Count > 0 ? parts[0].Trim() : string.Empty;
            string id = parts.Count > 1 ? Unescape(parts[1]) : string.Empty;
            return new VCardGender(sex.Length == 0 ? null : sex.ToUpperInvariant(), id.Length == 0 ? null : id);
        }
    }

    /// <summary>Every GEO that parses: <c>lat;lon</c> in 3.0 and 2.1, <c>geo:lat,lon</c> in 4.0. A value that is neither stays in the tree.</summary>
    public IReadOnlyList<VCardGeo> Geos => Component.FindProperties("GEO").Select(p => ParseGeo(p)).Where(g => g is not null).Select(g => g!).ToList();

    private static VCardGeo? ParseGeo(ContentProperty p)
    {
        // A comma in a geo: URI is sometimes escaped as if it were text; the escape means nothing to a URI.
        string v = p.Value.Trim().Replace("\\", string.Empty, StringComparison.Ordinal);
        if (v.StartsWith("geo:", StringComparison.OrdinalIgnoreCase))
        {
            // RFC 5870: geo:lat,lon[,alt][;crs=...;u=...]
            v = v[4..];
            int semi = v.IndexOf(';', StringComparison.Ordinal);
            if (semi >= 0)
            {
                v = v[..semi];
            }

            string[] c = v.Split(',');
            return c.Length >= 2 && Dbl(c[0], out double la) && Dbl(c[1], out double lo) ? new VCardGeo(la, lo, p) : null;
        }

        string[] parts = v.Split(';');
        return parts.Length == 2 && Dbl(parts[0], out double lat) && Dbl(parts[1], out double lon) ? new VCardGeo(lat, lon, p) : null;
    }

    private static bool Dbl(string s, out double d) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);

    /// <summary>REV parsed as an instant (<c>19951031T222710Z</c>, <c>1995-10-31T22:27:10Z</c>, or a date alone, taken as UTC); null when absent or unparseable.</summary>
    public DateTimeOffset? RevisionTime
    {
        get
        {
            if (VCardDate.TryParse(Component.FindProperty("REV")?.Value) is not { Year: int y, Month: int mo, Day: int d } rev)
            {
                return null;
            }

            try
            {
                TimeSpan off = ParseOffset(rev.Offset);
                return new DateTimeOffset(y, mo, d, rev.Hour ?? 0, rev.Minute ?? 0, Math.Min(rev.Second ?? 0, 59), off);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    private static TimeSpan ParseOffset(string? o)
    {
        if (string.IsNullOrEmpty(o) || o.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.Zero;
        }

        string d = o[1..].Replace(":", string.Empty, StringComparison.Ordinal);
        if (d.Length is 2 or 4 && d.All(char.IsAsciiDigit))
        {
            int h = int.Parse(d.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
            int m = d.Length == 4 ? int.Parse(d.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture) : 0;
            TimeSpan t = new(h, m, 0);
            return o[0] == '-' ? -t : t;
        }

        return TimeSpan.Zero;
    }

    /// <summary>BDAY as text when it is not a date (vCard 4.0 allows <c>VALUE=text</c>, such as <c>circa 1800</c>); null when absent or a date.</summary>
    public string? BirthdayText => Birthday is null ? Text("BDAY") : null;

    /// <summary>ANNIVERSARY as text when it is not a date.</summary>
    public string? AnniversaryText => Anniversary is null ? Text("ANNIVERSARY") : null;

    /// <summary>The extension properties (names beginning <c>X-</c>), in order, groups included.</summary>
    public IReadOnlyList<ContentProperty> Extensions => Component.Properties.Where(p => p.Name.StartsWith("X-", StringComparison.OrdinalIgnoreCase)).ToList();

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
