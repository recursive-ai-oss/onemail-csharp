using System.Globalization;
using System.Text;
using System.Text.Json;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Models.Properties;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

// OracleFacts ical|vcards <root> <out.jsonl> : the released Ical.Net / FolkerKinzel.VCards packages, public API only.
// Facts follow the schema in tools/calendar/README.md, restricted to what each library's public model exposes:
// Ical.Net: component tree, text properties and date-time properties. FolkerKinzel.VCards: FN, EMAIL, TEL, NOTE.
string mode = args[0], root = args[1];
using StreamWriter w = new(args[2], false, new UTF8Encoding(false));
foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    bool ics = f.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ifb", StringComparison.OrdinalIgnoreCase);
    bool vcf = f.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase);
    if ((mode == "ical" && !ics) || (mode == "vcards" && !vcf))
    {
        continue;
    }

    string rel = Path.GetRelativePath(root, f);
    try
    {
        List<object[]> facts = [];
        string text = File.ReadAllText(f);
        if (mode == "ical")
        {
            Ical.Net.Calendar? cal = Ical.Net.Calendar.Load(text);
            if (cal is null)
            {
                throw new InvalidDataException("null calendar");
            }

            WalkIcal(cal, string.Empty, facts);
        }
        else
        {
            foreach (VCard card in Vcf.Parse(text))
            {
                facts.Add(["VCARD", string.Empty, Array.Empty<object>(), string.Empty, string.Empty]);
                Add(facts, "FN", card.DisplayNames);
                Add(facts, "EMAIL", card.EMails);
                Add(facts, "TEL", card.Phones);
                Add(facts, "NOTE", card.Notes);
            }
        }

        w.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["file"] = rel, ["facts"] = facts }));
    }
    catch (Exception e)
    {
        w.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["file"] = rel, ["error"] = e.GetType().Name, ["detail"] = e.StackTrace?.Split((char)10)[0] }));
    }
}

static void Add(List<object[]> facts, string name, IEnumerable<TextProperty> props)
{
    foreach (TextProperty? p in props ?? [])
    {
        if (p?.Value is { } v)
        {
            facts.Add(["VCARD", name, Array.Empty<object>(), "t:" + v, string.Empty]);
        }
    }
}

static void WalkIcal(Ical.Net.ICalendarObject o, string parent, List<object[]> facts)
{
    string path = parent.Length == 0 ? o.Name.ToUpperInvariant() : parent + "/" + o.Name.ToUpperInvariant();
    facts.Add([path, string.Empty, Array.Empty<object>(), string.Empty, string.Empty]);
    if (o is CalendarComponent c)
    {
        foreach (CalendarProperty p in c.Properties)
        {
            string n = p.Name.ToUpperInvariant();
            string? v = p.Value switch
            {
                CalDateTime d when n is "DTSTART" or "DTEND" or "DTSTAMP" or "CREATED" or "LAST-MODIFIED" or "DUE" or "COMPLETED" or "RECURRENCE-ID" =>
                    d.HasTime ? "dt:" + d.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (d.IsUtc ? "Z" : string.Empty)
                    : "d:" + d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                string s when n is "SUMMARY" or "DESCRIPTION" or "LOCATION" or "UID" or "COMMENT" or "CONTACT" or "PRODID" or "VERSION" or "METHOD" or "STATUS" or "CLASS" or "TZID" => "t:" + s,
                _ => null,
            };
            if (v is not null)
            {
                facts.Add([path, n, Array.Empty<object>(), v, string.Empty]);
            }
        }
    }

    foreach (Ical.Net.ICalendarObject child in o.Children)
    {
        if (child is CalendarComponent)
        {
            WalkIcal(child, path, facts);
        }
    }
}
