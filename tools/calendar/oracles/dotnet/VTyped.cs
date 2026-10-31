using System.Globalization;
using System.Text;
using System.Text.Json;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Models;
using FolkerKinzel.VCards.Models.Properties;

// OracleFacts vtyped <root> <out.jsonl>: the typed values FolkerKinzel.VCards' public model returns for every .vcf, in the fact schema of
// tools/contacts/compare_vcard.py (the same forms the typed view writes: s: structured, l: list, t: text, d: date, b: byte count, g: geo).
internal static class VTyped
{
    public static int Run(string root, string outFile)
    {
        using StreamWriter w = new(outFile, false, new UTF8Encoding(false));
        foreach (string f in Directory.EnumerateFiles(root, "*.vcf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, f);
            try
            {
                var facts = new List<object[]>();
                foreach (VCard c in Vcf.Parse(File.ReadAllText(f)))
                {
                    Card(c, facts);
                }

                w.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["file"] = rel, ["facts"] = facts }));
            }
            catch (Exception e)
            {
                w.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["file"] = rel, ["error"] = e.GetType().Name }));
            }
        }

        return 0;
    }

    private static string J(IEnumerable<string>? l) => string.Join(",", l ?? []);

    private static void Add(List<object[]> facts, string name, string value) => facts.Add(["VCARD", name, Array.Empty<object>(), value, string.Empty]);

    private static void Texts(List<object[]> facts, string name, IEnumerable<TextProperty?>? props)
    {
        foreach (TextProperty? p in props ?? [])
        {
            if (p?.Value is { } v)
            {
                Add(facts, name, "t:" + v);
            }
        }
    }

    private static void Media(List<object[]> facts, string name, IEnumerable<DataProperty?>? props)
    {
        foreach (DataProperty? p in props ?? [])
        {
            if (p?.Value?.Bytes is { } b)
            {
                Add(facts, name, "b:" + b.Length.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static void Dates(List<object[]> facts, string name, IEnumerable<DateAndOrTimeProperty?>? props)
    {
        foreach (DateAndOrTimeProperty? p in props ?? [])
        {
            if (p?.Value is { } v && v.DateOnly is { } d)
            {
                Add(facts, name, "d:" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }
    }

    private static void Card(VCard c, List<object[]> facts)
    {
        facts.Add(["VCARD", string.Empty, Array.Empty<object>(), string.Empty, string.Empty]);
        Texts(facts, "FN", c.DisplayNames);
        foreach (NameProperty? p in c.NameViews ?? [])
        {
            if (p?.Value is { } n)
            {
                Add(facts, "N", "s:" + string.Join(";", new[] { n.Surnames, n.Given, n.Given2, n.Prefixes, n.Suffixes }.Select(J)));
            }
        }

        foreach (AddressProperty? p in c.Addresses ?? [])
        {
            if (p?.Value is { } a)
            {
                Add(facts, "ADR", "s:" + string.Join(";", new[] { a.POBox, a.Extended, a.Street, a.Locality, a.Region, a.PostalCode, a.Country }.Select(J)));
            }
        }

        Texts(facts, "TEL", c.Phones);
        Texts(facts, "EMAIL", c.EMails);
        Texts(facts, "URL", c.Urls);
        Texts(facts, "NOTE", c.Notes);
        Texts(facts, "TITLE", c.Titles);
        Texts(facts, "ROLE", c.Roles);
        Texts(facts, "IMPP", c.Messengers);
        foreach (OrgProperty? p in c.Organizations ?? [])
        {
            if (p?.Value is { } o)
            {
                Add(facts, "ORG", "s:" + o.Name + (o.Units is { Count: > 0 } u ? ";" + string.Join(";", u) : string.Empty));
            }
        }

        foreach (StringCollectionProperty? p in c.NickNames ?? [])
        {
            if (p?.Value is { } v)
            {
                Add(facts, "NICKNAME", "l:" + J(v));
            }
        }

        foreach (StringCollectionProperty? p in c.Categories ?? [])
        {
            if (p?.Value is { } v)
            {
                Add(facts, "CATEGORIES", "l:" + J(v));
            }
        }

        Dates(facts, "BDAY", c.BirthDayViews);
        Dates(facts, "ANNIVERSARY", c.AnniversaryViews);
        Media(facts, "PHOTO", c.Photos);
        Media(facts, "LOGO", c.Logos);
        Media(facts, "SOUND", c.Sounds);
        Media(facts, "KEY", c.Keys);
        foreach (GenderProperty? p in c.GenderViews ?? [])
        {
            if (p?.Value is { } g)
            {
                Add(facts, "GENDER", "s:" + (g.Sex is { } sex ? sex.ToString() switch { "Male" => "M", "Female" => "F", "NonOrNotApplicable" => "N", "Other" => "O", "Unknown" => "U", var x => x } : string.Empty) + ";" + (g.Identity ?? string.Empty));
            }
        }

        foreach (GeoProperty? p in c.GeoCoordinates ?? [])
        {
            if (p?.Value is { } g)
            {
                Add(facts, "GEO", "g:" + g.Latitude.ToString("0.######", CultureInfo.InvariantCulture) + ";" + g.Longitude.ToString("0.######", CultureInfo.InvariantCulture));
            }
        }

        foreach (TimeZoneProperty? p in c.TimeZones ?? [])
        {
            if (p?.Value?.Value is { } z)
            {
                Add(facts, "TZ", "t:" + z);
            }
        }

        if (c.Language?.Value is { } l)
        {
            Add(facts, "LANG", "t:" + l);
        }
    }
}
