using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.VCard;

namespace ContactTool;

// vfacts: the typed view's values for every card, in the fact schema tools/calendar/oracles/compare.py reads, so that the same
// comparison runs against the Python vobject and FolkerKinzel.VCards drivers. Only values the typed accessors return are written,
// never the raw tree (the raw tree is CalendarDump's job).
internal static class VFacts
{
    public static int Run(string root, string outFile)
    {
        using var w = new StreamWriter(outFile, false, new UTF8Encoding(false));
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, f);
            try
            {
                var facts = new List<object[]>();
                ContentDocument doc = ContentDocument.Parse(File.ReadAllBytes(f));
                foreach (VCardView v in VCardDocument.Cards(doc))
                {
                    Card(v, facts);
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

    private static object[] P(IEnumerable<string> types)
    {
        string[] t = types.Select(x => x.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();
        return t.Length == 0 ? [] : [new object[] { "TYPE", t }];
    }

    private static void Add(List<object[]> facts, string name, object[] ps, string value) => facts.Add(["VCARD", name, ps, value, string.Empty]);

    private static string J(IReadOnlyList<string> l) => string.Join(",", l);

    private static void Card(VCardView v, List<object[]> facts)
    {
        facts.Add(["VCARD", string.Empty, Array.Empty<object>(), string.Empty, string.Empty]);
        Add(facts, "VERSION", [], "t:" + (v.VersionText ?? string.Empty));
        foreach (string s in v.FormattedNames)
        {
            Add(facts, "FN", [], "t:" + s);
        }

        foreach (VCardName n in v.Names)
        {
            Add(facts, "N", [], "s:" + string.Join(";", new[] { n.Family, n.Given, n.Additional, n.Prefixes, n.Suffixes }.Select(J)));
        }

        foreach (VCardAddress a in v.Addresses)
        {
            Add(facts, "ADR", P(a.Types), "s:" + string.Join(";", new[] { a.PoBox, a.Extended, a.Street, a.Locality, a.Region, a.PostalCode, a.Country }.Select(J)));
        }

        foreach (VCardTyped t in v.Phones)
        {
            Add(facts, "TEL", P(t.Types), "t:" + t.Value);
        }

        foreach (VCardTyped t in v.Emails)
        {
            Add(facts, "EMAIL", P(t.Types), "t:" + t.Value);
        }

        foreach (VCardTyped t in v.Urls)
        {
            Add(facts, "URL", P(t.Types), "t:" + t.Value);
        }

        foreach (IReadOnlyList<string> org in v.Organizations)
        {
            Add(facts, "ORG", [], "s:" + string.Join(";", org));
        }

        foreach (string t in v.Titles)
        {
            Add(facts, "TITLE", [], "t:" + t);
        }

        foreach (string t in v.Roles)
        {
            Add(facts, "ROLE", [], "t:" + t);
        }

        foreach (string t in v.Notes)
        {
            Add(facts, "NOTE", [], "t:" + t);
        }

        if (v.Nicknames.Count > 0)
        {
            Add(facts, "NICKNAME", [], "l:" + J(v.Nicknames));
        }

        if (v.Categories.Count > 0)
        {
            Add(facts, "CATEGORIES", [], "l:" + J(v.Categories));
        }

        if (v.Uid is { } uid)
        {
            Add(facts, "UID", [], "t:" + uid);
        }

        if (v.Kind is { } kind)
        {
            Add(facts, "KIND", [], "t:" + kind);
        }

        foreach (string m in v.Members)
        {
            Add(facts, "MEMBER", [], "t:" + m);
        }

        foreach (VCardDate b in v.Birthdays)
        {
            Add(facts, "BDAY", [], "d:" + Date(b));
        }

        foreach (VCardDate an in v.Anniversaries)
        {
            Add(facts, "ANNIVERSARY", [], "d:" + Date(an));
        }

        foreach ((string name, IReadOnlyList<VCardMedia> list) in new[] { ("PHOTO", v.Photos), ("LOGO", v.Logos), ("SOUND", v.Sounds), ("KEY", v.Keys) })
        {
            foreach (VCardMedia mm in list)
            {
                if (mm.Data is { } d)
                {
                    Add(facts, name, [], "b:" + d.Length.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        if (v.Gender is { } g)
        {
            Add(facts, "GENDER", [], "s:" + (g.Sex ?? string.Empty) + ";" + (g.Identity ?? string.Empty));
        }

        foreach (VCardGeo geo in v.Geos)
        {
            Add(facts, "GEO", [], "g:" + geo.Latitude.ToString("0.######", CultureInfo.InvariantCulture) + ";" + geo.Longitude.ToString("0.######", CultureInfo.InvariantCulture));
        }

        foreach (VCardTyped t in v.TimeZones)
        {
            Add(facts, "TZ", [], "t:" + t.Value);
        }

        foreach (VCardTyped t in v.Languages)
        {
            Add(facts, "LANG", [], "t:" + t.Value);
        }

        foreach (VCardTyped t in v.Impps)
        {
            Add(facts, "IMPP", P(t.Types), "t:" + t.Value);
        }

        foreach (VCardTyped t in v.Related)
        {
            Add(facts, "RELATED", P(t.Types), "t:" + t.Value);
        }
    }

    // A date in the form a library would print for the same card: partial dates keep their dashes (--MM-DD) and time zones are not folded in.
    private static string Date(VCardDate d)
    {
        string date = d.Year is null && d.Month is null && d.Day is null ? string.Empty
            : d.Year is { } y ? y.ToString("D4", CultureInfo.InvariantCulture) + (d.Month is { } m ? "-" + m.ToString("D2", CultureInfo.InvariantCulture) + (d.Day is { } dd ? "-" + dd.ToString("D2", CultureInfo.InvariantCulture) : string.Empty) : string.Empty)
            : "--" + (d.Month is { } m2 ? m2.ToString("D2", CultureInfo.InvariantCulture) : "-") + (d.Day is { } d2 ? "-" + d2.ToString("D2", CultureInfo.InvariantCulture) : string.Empty);
        return d.Hour is null ? date : date + "T" + d.Hour.Value.ToString("D2", CultureInfo.InvariantCulture) + ":" + (d.Minute ?? 0).ToString("D2", CultureInfo.InvariantCulture) + ":" + (d.Second ?? 0).ToString("D2", CultureInfo.InvariantCulture) + (d.Offset ?? string.Empty);
    }
}
