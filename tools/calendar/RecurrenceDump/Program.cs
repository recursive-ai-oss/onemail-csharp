using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.TimeZones;

// RecurrenceDump cases <out.jsonl> <root>...     one line per recurring component: the case id, file, UID, window and cap
// RecurrenceDump expand <cases.jsonl> <out.jsonl> [--no-runtime-zones]  our occurrences for every case
// RecurrenceDump zones <root>...                  how every TZID in the corpus resolves, with counts
// RecurrenceDump rate <root>...                   expansion throughput (occurrences per second) and allocated bytes per occurrence
return args.Length > 0 ? args[0] switch
{
    "cases" => Cases(args[1], args[2..]),
    "expand" => Expand(args[1], args[2], args.Contains("--no-runtime-zones")),
    "zones" => Zones(args[1..]),
    "rate" => Rate(args[1..]),
    "offset" => Offset(args[1], args[2], args[3]),
    _ => Usage(),
} : Usage();

// RecurrenceDump offset <file> <tzid> <utc instant>: how the file's calendar resolves a zone at an instant
static int Offset(string file, string tzid, string utc)
{
    IcsComponent cal = Calendars(file).First();
    ExpansionReport report = new();
    IcsTimeZone? z = new TimeZoneResolver(cal, null, report).Resolve(tzid);
    DateTime t = DateTime.ParseExact(utc, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    Console.WriteLine(z is null ? "unresolved" : $"{z.Source} offset {z.GetUtcOffset(t)} local {z.ToLocal(t):s}");
    foreach (RecurrenceDefect d in report.Defects)
    {
        Console.WriteLine($"  {d.Kind} {d.Detail} x{d.Count}");
    }

    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: RecurrenceDump cases <out.jsonl> <root>... | expand <cases.jsonl> <out.jsonl> | zones <root>... | rate <root>...");
    return 2;
}

static IEnumerable<string> Files(string root) =>
    File.Exists(root) ? [root] :
    Directory.EnumerateFiles(root, "*.ics", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal);

static IEnumerable<IcsComponent> Calendars(string file)
{
    ContentDocument doc = ContentDocument.Parse(File.ReadAllBytes(file));
    return IcsDocument.Calendars(doc).ToList();
}

static bool IsItem(IcsComponent c) => c.Component.IsNamed("VEVENT") || c.Component.IsNamed("VTODO") || c.Component.IsNamed("VJOURNAL");

static bool Recurs(IcsComponent c) =>
    c.RecurrenceId is null && (c.Component.FindProperty("RRULE") is not null || c.Component.FindProperty("RDATE") is not null || c.Component.FindProperty("EXRULE") is not null);

static string Fmt(IcsTime t) => t.Kind switch
{
    IcsTimeKind.Date => t.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
    IcsTimeKind.Utc => t.Value.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
    _ => t.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
};

static string FmtUtc(DateTime t) => t.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

// One line per recurring component: the window is [DTSTART's day, five years later) and at most 400 occurrences are compared.
static int Cases(string output, string[] roots)
{
    int n = 0;
    using StreamWriter w = new(output);
    foreach (string root in roots)
    {
        string baseDir = File.Exists(root) ? Path.GetDirectoryName(Path.GetFullPath(root))! : Path.GetFullPath(root);
        foreach (string file in Files(root))
        {
            string rel = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            int ci = 0;
            foreach (IcsComponent cal in Calendars(file))
            {
                Dictionary<string, int> seen = [];
                foreach (IcsComponent c in cal.Children.Where(IsItem))
                {
                    if (!Recurs(c) || c.Start is not { } s || c.Uid is not { } uid)
                    {
                        continue;
                    }

                    seen[uid] = seen.GetValueOrDefault(uid) + 1;
                    string ics = Isolate(file, ci, uid);
                    DateTime ws = s.Value.Date;
                    DateTime we = ws.AddYears(5);
                    w.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["case"] = $"{Path.GetFileName(root.TrimEnd('/'))}/{rel}#{ci}#{uid}#{seen[uid] - 1}",
                        ["ics"] = ics,
                        ["uid"] = uid,
                        ["n"] = seen[uid] - 1,
                        ["start"] = ws.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
                        ["end"] = we.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
                        ["cap"] = 400,
                    }));
                    n++;
                }

                ci++;
            }
        }
    }

    Console.Error.WriteLine($"{n} recurring components");
    return 0;
}

// The calendar of a case with every other item removed: what each oracle is given, so none of them pays for (or trips on) its neighbours.
static string Isolate(string file, int calendarIndex, string uid)
{
    ContentDocument doc = ContentDocument.Parse(File.ReadAllBytes(file));
    int i = 0;
    foreach (Component root in doc.Components.ToList())
    {
        if (!root.IsNamed("VCALENDAR"))
        {
            doc.Root.Remove(root);
            continue;
        }

        if (i++ != calendarIndex)
        {
            doc.Root.Remove(root);
            continue;
        }

        foreach (Component child in root.Components.ToList())
        {
            if ((child.IsNamed("VEVENT") || child.IsNamed("VTODO") || child.IsNamed("VJOURNAL") || child.IsNamed("VFREEBUSY")) && new IcsComponent(child).Uid != uid)
            {
                root.Remove(child);
            }
        }
    }

    return System.Text.Encoding.UTF8.GetString(doc.ToArray());
}

static int Expand(string casesFile, string output, bool noRuntimeZones)
{
    using StreamWriter w = new(output);
    int n = 0;
    foreach (string line in File.ReadLines(casesFile))
    {
        JsonElement c = JsonSerializer.Deserialize<JsonElement>(line);
        string ics = c.GetProperty("ics").GetString()!;
        string uid = c.GetProperty("uid").GetString()!;
        int nth = c.GetProperty("n").GetInt32();
        int cap = c.GetProperty("cap").GetInt32();
        DateTime ws = DateTime.ParseExact(c.GetProperty("start").GetString()!, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        DateTime we = DateTime.ParseExact(c.GetProperty("end").GetString()!, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        IcsComponent cal = IcsDocument.Calendars(ContentDocument.Parse(System.Text.Encoding.UTF8.GetBytes(ics))).First();
        IcsComponent master = cal.Children.Where(x => IsItem(x) && Recurs(x) && x.Uid == uid).ElementAt(nth);
        List<IcsComponent> overrides = [.. cal.Children.Where(x => IsItem(x) && x.RecurrenceId is not null && x.Uid == uid)];
        ExpansionReport report = new();
        TimeZoneResolver zones = new(cal, new TimeZoneResolverOptions { UseRuntimeDatabase = !noRuntimeZones }, report);
        ExpansionOptions options = new() { WindowMode = WindowMode.StartingIn, MaxOccurrences = cap };
        RecurrenceSet set = new(master, overrides, zones, options, report);
        string kind = "F";
        List<string> starts = [];
        foreach (Occurrence o in set.Expand(new OccurrenceWindow(ws, we)))
        {
            if (o.Start.IsDate)
            {
                kind = "D";
                starts.Add(Fmt(o.Start));
            }
            else if (o.StartUtc is { } u)
            {
                kind = "Z";
                starts.Add(FmtUtc(u));
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
            ["defects"] = report.Defects.Select(d => d.Kind + (d.Detail is null ? "" : ":" + d.Detail)).ToList(),
        }));
        n++;
    }

    Console.Error.WriteLine($"{n} cases expanded");
    return 0;
}

static int Zones(string[] roots)
{
    Dictionary<string, (int Uses, string Source)> table = new(StringComparer.Ordinal);
    int files = 0;
    foreach (string root in roots)
    {
        foreach (string file in Files(root))
        {
            files++;
            foreach (IcsComponent cal in Calendars(file))
            {
                ExpansionReport report = new();
                TimeZoneResolver resolver = new(cal, null, report);
                HashSet<string> ids = [];
                foreach (IcsComponent tz in cal.TimeZones)
                {
                    if (tz.TimeZoneId is { } defined)
                    {
                        ids.Add(defined);
                    }
                }

                foreach (IcsComponent item in cal.Children)
                {
                    foreach (ContentProperty p in item.Component.Properties)
                    {
                        if (p.GetParameter("TZID") is { } tz)
                        {
                            ids.Add(tz);
                        }
                    }

                    foreach (IcsComponent child in item.Children)
                    {
                        foreach (ContentProperty p in child.Component.Properties)
                        {
                            if (p.GetParameter("TZID") is { } tz)
                            {
                                ids.Add(tz);
                            }
                        }
                    }
                }

                foreach (string id in ids)
                {
                    IcsTimeZone? z = resolver.Resolve(id);
                    string src = z is null ? "UNKNOWN" : z.Source.ToString();
                    table[id] = (table.GetValueOrDefault(id).Uses + 1, src);
                }
            }
        }
    }

    foreach (IGrouping<string, KeyValuePair<string, (int Uses, string Source)>> g in table.GroupBy(kv => kv.Value.Source).OrderBy(g => g.Key))
    {
        Console.WriteLine($"{g.Key}: {g.Count()} distinct TZIDs, {g.Sum(kv => kv.Value.Uses)} calendars");
    }

    foreach (KeyValuePair<string, (int Uses, string Source)> kv in table.Where(kv => kv.Value.Source == "UNKNOWN").OrderByDescending(kv => kv.Value.Uses))
    {
        Console.WriteLine($"  unknown: {kv.Key} ({kv.Value.Uses})");
    }

    Console.WriteLine($"{files} files");
    return 0;
}

static int Rate(string[] roots)
{
    long occurrences = 0;
    long alloc0 = GC.GetAllocatedBytesForCurrentThread();
    Stopwatch sw = Stopwatch.StartNew();
    foreach (string root in roots)
    {
        foreach (string file in Files(root))
        {
            foreach (IcsComponent cal in Calendars(file))
            {
                ExpansionOptions options = new() { MaxOccurrences = 100_000 };
                foreach (Occurrence _ in CalendarRecurrence.Expand(cal, new OccurrenceWindow(new DateTime(1990, 1, 1), new DateTime(2035, 1, 1)), options))
                {
                    occurrences++;
                }
            }
        }
    }

    sw.Stop();
    long bytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
    Console.WriteLine($"{occurrences} occurrences in {sw.Elapsed.TotalSeconds:F2}s = {occurrences / sw.Elapsed.TotalSeconds:F0}/s, {bytes} bytes allocated (including parsing)");
    return 0;
}
