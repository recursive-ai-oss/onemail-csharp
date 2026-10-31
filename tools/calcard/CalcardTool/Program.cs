using System.Globalization;
using System.Text.Json;
using RecursiveAI.OneHtml;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Display;
using RecursiveAI.OneMail.Stores.Olm;
using RecursiveAI.OneMail.Tests.Display;

// CalcardTool sweep <root>...                        cards for every calendar object under the roots; counts per source format and method
// CalcardTool occ <cases.jsonl> <out.jsonl>          the card's occurrence list for each case of RecurrenceDump cases
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CalcardTool sweep <root>... | occ <cases.jsonl> <out.jsonl>");
    return 2;
}

return args[0] switch
{
    "sweep" => Sweep(args[1..]),
    "occ" => Occ(args[1], args[2]),
    _ => 2,
};

static IEnumerable<string> Files(string root) =>
    File.Exists(root) ? [root] : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal);

static IEnumerable<IcsComponent> CalendarsOf(IMailItem item)
{
    switch (item)
    {
        case MimeMailItem m:
            foreach (var part in m.CalendarParts)
            {
                foreach (var c in ContentDocument.Parse(part.GetDecodedContent()).Components.Where(c => c.IsNamed("VCALENDAR")))
                {
                    yield return new IcsComponent(c);
                }
            }

            break;
        case MapiMailItem a when OutlookAppointment.TryCreate(a.Message) is not null:
            foreach (var c in new MapiToIcsConverter().Convert(a.Message).Value.Components.Where(c => c.IsNamed("VCALENDAR")))
            {
                yield return new IcsComponent(c);
            }

            break;
        case OlmItem { OlmKind: OlmItemKind.Appointment } o:
            yield return new IcsComponent(new OlmToCalendarConverter().Convert(o).Value);
            break;
    }
}

static int Sweep(string[] roots)
{
    bool viaMapi = roots.Contains("--via-mapi");
    roots = [.. roots.Where(r => r != "--via-mapi")];
    Dictionary<string, int> cardsBy = [];
    Dictionary<string, int> defects = [];
    int files = 0, openErrors = 0, exceptions = 0, htmlErrors = 0, htmlChecked = 0, described = 0, custom = 0, parseBackBad = 0, rules = 0, noCard = 0;
    Dictionary<string, int> sentenceForms = [];
    void Count(string key) => cardsBy[key] = cardsBy.GetValueOrDefault(key) + 1;

    void CheckCards(string source, IReadOnlyList<CalendarCard> cards, IEnumerable<IcsComponent> calendars)
    {
        foreach (CalendarCard c in cards)
        {
            Count(source + "/" + c.Method);
            foreach (var d in c.Defects)
            {
                defects[d.Kind.ToString()] = defects.GetValueOrDefault(d.Kind.ToString()) + 1;
            }

            foreach (string html in new[] { c.ToHtml(), c.ToHtml(linkUrls: true) })
            {
                Document doc = Document.Parse("<!DOCTYPE html><title>t</title>" + html, new ParseOptions { RecordErrors = true });
                htmlChecked++;
                htmlErrors += doc.ParseErrors.Count;
            }

            _ = c.ToText();
        }

        foreach (IcsComponent cal in calendars)
        {
            foreach (IcsComponent ev in cal.Children.Where(e => e.Component.IsNamed("VEVENT") || e.Component.IsNamed("VTODO")))
            {
                if ((ev.Start ?? ev.Due) is not { } start)
                {
                    continue;
                }

                foreach (RecurrenceRule r in ev.RecurrenceRules)
                {
                    rules++;
                    string? s = CalendarCardRecurrence.Describe(r, start);
                    if (s is null)
                    {
                        custom++;
                        continue;
                    }

                    described++;
                    string form = System.Text.RegularExpressions.Regex.Replace(s, @"\d+|January|February|March|April|May|June|July|August|September|October|November|December|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Oct|Nov|Dec", "#");
                    sentenceForms[form] = sentenceForms.GetValueOrDefault(form) + 1;
                    if (SentenceParser.KeyOfRule(r, start) != SentenceParser.Parse(s))
                    {
                        parseBackBad++;
                        Console.WriteLine("PARSE-BACK MISMATCH: " + r + " -> " + s);
                    }
                }
            }
        }
    }

    // Each calendar object of an .ics file becomes an Outlook item (MS-OXCICAL import) and is shown from the item: the generated Outlook meeting
    // items. Method, title, location, status, organizer and attendee count are compared with the card of the original.
    Dictionary<string, int> mapiDiff = [];
    int mapiCards = 0, mapiCompared = 0;
    void ViaMapi(List<IcsComponent> cals, IReadOnlyList<CalendarCard> original)
    {
        foreach (IcsComponent cal in cals)
        {
            foreach (var r in IcsToMapiConverter.ConvertAll(cal))
            {
                foreach (CalendarCard m in CalendarCard.FromMapi(r.Value))
                {
                    mapiCards++;
                    Count("mapi-from-ics/" + m.Method);
                    CalendarCard? o = original.FirstOrDefault(x => x.Uid is not null && x.Summary == m.Summary) ?? original.FirstOrDefault(x => x.Summary == m.Summary);
                    if (o is null)
                    {
                        continue;
                    }

                    mapiCompared++;
                    void Diff(string field, object? a, object? b)
                    {
                        if (!Equals(a, b))
                        {
                            mapiDiff[field] = mapiDiff.GetValueOrDefault(field) + 1;
                            if (mapiDiff[field] <= 3)
                            {
                                Console.WriteLine($"MAPI DIFF {field}: ics [{a}] item [{b}] ({o.Summary})");
                            }
                        }
                    }

                    Diff("Method", o.Method == CalendarCardMethod.None ? CalendarCardMethod.Publish : o.Method, m.Method);
                    Diff("Location", o.Location, m.Location);
                    Diff("Status", o.Status, m.Status);
                    Diff("AttendeeCount", o.AttendeeCount, m.AttendeeCount);
                    // The zone label is not compared (a Windows name for an IANA one, UTC for a floating time); the wall-clock text is.
                    static string NoZone(string t) => t.Contains(" (", StringComparison.Ordinal) ? t[..t.LastIndexOf(" (", StringComparison.Ordinal)] : t;
                    if (o.Recurrence != m.Recurrence)
                    {
                        string kind = (o.RecurrenceDescribed, m.RecurrenceDescribed, o.Recurrence is null, m.Recurrence is null) switch
                        {
                            (_, _, true, false) => "item-has-recurrence-ics-has-none",
                            (_, _, false, true) => "ics-has-recurrence-item-has-none",
                            (true, false, _, _) => "ics-described-item-custom",
                            (false, true, _, _) => "ics-custom-item-described",
                            _ => "both-described-differ",
                        };
                        Diff("Recurrence/" + kind, o.Recurrence, m.Recurrence);
                    }

                    Diff("When", NoZone(o.When), NoZone(m.When));
                    if (NoZone(o.When) != NoZone(m.When) && mapiDiff.GetValueOrDefault("When") <= 3)
                    {
                        Console.WriteLine(string.Join(" | ", System.Text.Encoding.UTF8.GetString(new MapiToIcsConverter().Convert(r.Value).Value.ToArray()).Split("\r\n").Where(l => l.StartsWith("DTSTART") || l.StartsWith("DTEND") || l.StartsWith("RRULE") || l.StartsWith("EXDATE"))));
                    }

                }
            }
        }
    }

    foreach (string root in roots)
    {
        foreach (string file in Files(root))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".ics" or ".ifb" or ".eml" or ".msg" or ".pst" or ".ost" or ".olm" or ".dat" or ".tnef" or ".mbox" or ".oft" or ".emlx"))
            {
                continue;
            }

            files++;
            try
            {
                if (ext is ".ics" or ".ifb")
                {
                    byte[] data = File.ReadAllBytes(file);
                    var cards = CalendarCard.FromIcs(data);
                    var cals = ContentDocument.Parse(data).Components.Where(c => c.IsNamed("VCALENDAR")).Select(c => new IcsComponent(c)).ToList();
                    if (cards.Count == 0 && cals.Any(c => c.Events.Any()))
                    {
                        noCard++;
                    }

                    CheckCards("ics", cards, cals);
                    if (viaMapi)
                    {
                        ViaMapi(cals, cards);
                    }

                    continue;
                }

                MailContainer container;
                try
                {
                    container = Mail.Open(file);
                }
                catch (Exception e) when (e is NotSupportedException or IOException or InvalidDataException)
                {
                    openErrors++;
                    continue;
                }

                using (container)
                {
                    string fmt = container.Format.ToString().ToLowerInvariant();
                    IEnumerator<IMailItem> en = container.Enumerate().GetEnumerator();
                    while (true)
                    {
                        IMailItem item;
                        try
                        {
                            if (!en.MoveNext())
                            {
                                break;
                            }

                            item = en.Current;
                        }
                        catch (Exception e) when (e is NotSupportedException or IOException or InvalidDataException)
                        {
                            openErrors++;
                            break;
                        }

                        CheckCards(fmt, CalendarCard.FromItem(item), CalendarsOf(item).ToList());
                    }
                }
            }
            catch (Exception e)
            {
                exceptions++;
                Console.WriteLine("EXCEPTION " + file + ": " + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    Console.WriteLine($"files {files}, open errors (unreadable or unsupported) {openErrors}, exceptions {exceptions}, ics files with events but no card {noCard}");
    foreach (var kv in cardsBy.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"cards {kv.Key} {kv.Value}");
    }

    if (viaMapi)
    {
        Console.WriteLine($"cards from generated Outlook items {mapiCards}, compared with the card of the original {mapiCompared}");
        foreach (var kv in mapiDiff.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"mapi-vs-ics difference {kv.Key} {kv.Value}");
        }
    }

    Console.WriteLine($"cards total {cardsBy.Values.Sum()}, html documents {htmlChecked}, html parse errors {htmlErrors}");
    Console.WriteLine($"rrules {rules}: described {described} (parse-back mismatches {parseBackBad}), custom-rule fallback {custom}, distinct sentence forms {sentenceForms.Count}");
    foreach (var kv in sentenceForms.OrderByDescending(k => k.Value).Take(40))
    {
        Console.WriteLine($"  form {kv.Value} {kv.Key}");
    }

    foreach (var kv in defects.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"defect {kv.Key} {kv.Value}");
    }

    return exceptions + parseBackBad + htmlErrors == 0 ? 0 : 1;
}

static string Fmt(IcsTime t) => t.Kind switch
{
    IcsTimeKind.Date => t.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
    IcsTimeKind.Utc => t.Value.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
    _ => t.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
};

static int Occ(string casesFile, string output)
{
    using StreamWriter w = new(output);
    int n = 0;
    foreach (string line in File.ReadLines(casesFile))
    {
        JsonElement c = JsonSerializer.Deserialize<JsonElement>(line);
        string uid = c.GetProperty("uid").GetString()!;
        IcsComponent cal = new(ContentDocument.Parse(System.Text.Encoding.UTF8.GetBytes(c.GetProperty("ics").GetString()!)).Components.First(x => x.IsNamed("VCALENDAR")));
        CalendarCard? card = CalendarCard.CreateAll(cal, new CalendarCardOptions { NextOccurrences = 50 }).FirstOrDefault(x => x.Uid == uid);
        List<string> starts = [];
        string kind = "F";
        foreach (Occurrence o in card?.OccurrenceData ?? [])
        {
            if (o.Start.IsDate)
            {
                kind = "D";
                starts.Add(Fmt(o.Start));
            }
            else if (o.StartUtc is { } u)
            {
                kind = "Z";
                starts.Add(u.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }
            else
            {
                starts.Add(Fmt(o.Start));
            }
        }

        w.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["case"] = c.GetProperty("case").GetString()!,
            ["kind"] = kind,
            ["starts"] = starts,
            ["n"] = c.GetProperty("n").GetInt32(),
            ["defects"] = (card?.Defects ?? []).Select(d => d.Kind.ToString()).ToList(),
        }));
        n++;
    }

    Console.Error.WriteLine($"{n} cases");
    return 0;
}
