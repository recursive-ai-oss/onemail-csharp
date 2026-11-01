using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;

// OxocalTool scan <root> <out dir>       every calendar, meeting and task item of the MSG, OFT, PST, OST and TNEF files under <root>:
//                                        typed fields to <out>/items.jsonl, the converted iCalendar to <out>/ics/N.ics, counts on stdout
// OxocalTool roundtrip <root>            item -> iCalendar -> item -> iCalendar over the same items, and what differs
// OxocalTool ics <dir> <out dir>         iCalendar files -> items -> iCalendar, with the loss reports
// OxocalTool itemdiff <root>...          Outlook item -> iCalendar -> item compared property by property (see ItemSweep.cs)
// OxocalTool icsdiff <root>...           iCalendar -> item -> iCalendar compared field by field (see IcsSweep.cs)
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: OxocalTool scan <root> <out> | roundtrip <root> | ics <dir> <out>");
    return 2;
}

return args[0] switch
{
    "scan" => Scan(args[1], args[2]),
    "roundtrip" => RoundTrip(args[1]),
    "ics" => IcsFiles(args[1], args[2]),
    "icsdiff" => IcsSweep.Run(args[1..]),
    "itemdiff" => ItemDiff(args[1..]),
    _ => 2,
};

static int ItemDiff(string[] a)
{
    int samples = 3;
    string? only = null;
    List<string> roots = [];
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i] == "--samples")
        {
            samples = int.Parse(a[++i], CultureInfo.InvariantCulture);
        }
        else if (a[i] == "--only")
        {
            only = a[++i];
        }
        else
        {
            roots.Add(a[i]);
        }
    }

    return ItemSweep.Run(roots.SelectMany(Items), samples, only);
}

static IEnumerable<string> Files(string input)
{
    if (File.Exists(input))
    {
        yield return input;
        yield break;
    }

    foreach (string f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        if (Path.GetExtension(f).ToLowerInvariant() is ".msg" or ".oft" or ".pst" or ".ost" or ".dat" or ".tnef")
        {
            yield return f;
        }
    }
}

static IEnumerable<(string Origin, MapiMessage Message)> Items(string root)
{
    foreach (string file in Files(root))
    {
        MailContainer? c = null;
        try
        {
            c = Mail.Open(file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"skip {file}: {ex.GetType().Name}");
        }

        if (c is null)
        {
            continue;
        }

        using (c)
        {
            int k = 0;
            IEnumerator<IMailItem> e = c.Enumerate().GetEnumerator();
            while (true)
            {
                MapiMailItem? m = null;
                try
                {
                    if (!e.MoveNext())
                    {
                        break;
                    }

                    m = e.Current as MapiMailItem;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"stop {file}#{k}: {ex.GetType().Name}");
                    break;
                }

                if (m is not null && (OutlookAppointment.KindOf(m.Message.MessageClass) is not null || OutlookTask.TryCreate(m.Message) is not null))
                {
                    yield return (Path.GetFileName(file) + "#" + k, m.Message);
                }

                k++;
            }
        }
    }
}

static string Iso(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "";

static int Scan(string root, string outDir)
{
    Directory.CreateDirectory(Path.Combine(outDir, "ics"));
    using StreamWriter w = new(Path.Combine(outDir, "items.jsonl"), false, new UTF8Encoding(false));
    int n = 0, appts = 0, meetings = 0, tasks = 0, recurring = 0, withDefects = 0, withExceptions = 0, lossy = 0;
    Dictionary<string, int> classes = [];
    foreach ((string origin, MapiMessage m) in Items(root))
    {
        n++;
        string cls = m.MessageClass ?? "";
        classes[cls] = classes.GetValueOrDefault(cls) + 1;
        Dictionary<string, object?> row = new() { ["origin"] = origin, ["class"] = cls, ["subject"] = m.Subject };
        int defects = 0;
        if (OutlookAppointment.TryCreate(m) is { } a)
        {
            if (a.IsMeetingMessage)
            {
                meetings++;
            }
            else
            {
                appts++;
            }

            row["start"] = Iso(a.Start);
            row["end"] = Iso(a.End);
            row["allDay"] = a.IsAllDay;
            row["location"] = a.Location;
            row["busy"] = a.BusyStatus?.ToString();
            row["sequence"] = a.Sequence;
            row["sensitivity"] = a.Sensitivity;
            row["reminder"] = a.ReminderSet ? a.ReminderMinutes : null;
            row["organizer"] = a.Organizer?.Address;
            row["attendees"] = a.Attendees.Select(x => new { x.Name, x.Address, role = x.Role.ToString(), resp = x.Response.ToString() }).ToList();
            row["zone"] = a.TimeZone?.Id;
            row["recurring"] = a.Recurrence is not null;
            if (a.Recurrence is { } r)
            {
                recurring++;
                row["recur"] = new { freq = r.Pattern.RecurFrequency, type = r.Pattern.PatternType, period = r.Pattern.Period, end = r.Pattern.EndType, count = r.Pattern.OccurrenceCount, deleted = r.Pattern.DeletedInstanceDates.Count, modified = r.Pattern.ModifiedInstanceDates.Count, exceptions = r.Exceptions.Count };
                if (r.Exceptions.Count > 0)
                {
                    withExceptions++;
                }
            }

            _ = a.Exceptions;
            _ = a.TimeZone;
            defects = a.Defects.Count;
            row["defects"] = a.Defects.Select(d => d.Kind.ToString()).ToList();
        }
        else if (OutlookTask.TryCreate(m) is { } t)
        {
            tasks++;
            row["status"] = t.Status?.ToString();
            row["percent"] = t.PercentComplete;
            row["start"] = t.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            row["due"] = t.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            row["owner"] = t.Owner;
            row["recurring"] = t.Recurrence is not null;
            defects = t.Defects.Count;
        }

        if (defects > 0)
        {
            withDefects++;
        }

        ConversionResult<ContentDocument> ics = new MapiToIcsConverter().Convert(m);
        if (ics.Report.HasContentLoss)
        {
            lossy++;
        }

        row["loss"] = ics.Report.GroupBy(e => e.Code + (e.Kind == LossKind.Internal ? "(i)" : "")).ToDictionary(g => g.Key, g => g.Count());
        File.WriteAllBytes(Path.Combine(outDir, "ics", n.ToString("D5", CultureInfo.InvariantCulture) + ".ics"), ics.Value.ToArray());
        w.WriteLine(JsonSerializer.Serialize(row));
    }

    Console.WriteLine($"items {n}: appointments {appts}, meeting messages {meetings}, tasks {tasks}; recurring {recurring} (with exceptions {withExceptions}); with typed defects {withDefects}; with content loss in the iCalendar {lossy}");
    foreach (KeyValuePair<string, int> kv in classes.OrderByDescending(x => x.Value))
    {
        Console.WriteLine($"  {kv.Key}: {kv.Value}");
    }

    return 0;
}

// Item -> ICS -> item -> ICS: the two iCalendar documents must agree on every field MS-OXCICAL maps.
static int RoundTrip(string root)
{
    int n = 0, same = 0, differ = 0, unreportedLoss = 0;
    foreach ((string origin, MapiMessage m) in Items(root))
    {
        n++;
        ConversionResult<ContentDocument> one = new MapiToIcsConverter().Convert(m);
        IcsComponent c1 = IcsDocument.Calendars(ContentDocument.Parse(one.Value.ToArray())).FirstOrDefault()!;
        if (c1 is null)
        {
            continue;
        }

        ConversionResult<MapiMessage> back = new IcsToMapiConverter().Convert(c1);
        ConversionResult<ContentDocument> two = new MapiToIcsConverter().Convert(back.Value);
        IcsComponent c2 = IcsDocument.Calendars(ContentDocument.Parse(two.Value.ToArray())).First();
        string a = Canon(c1), b = Canon(c2);
        if (a == b)
        {
            same++;
        }
        else
        {
            differ++;
            Console.WriteLine($"DIFF {origin} ({m.MessageClass})");
            string[] la = a.Split('\n'), lb = b.Split('\n');
            foreach (string l in la.Except(lb).Take(4))
            {
                Console.WriteLine("  - " + l);
            }

            foreach (string l in lb.Except(la).Take(4))
            {
                Console.WriteLine("  + " + l);
            }
        }

        if (back.Report.Any(e => e.Kind == LossKind.Dropped && e.Code == "NoMapiMapping") && !back.Report.HasContentLoss)
        {
            unreportedLoss++;
        }
    }

    Console.WriteLine($"items {n}: ICS stable through item and back {same}, differ {differ}, silent {unreportedLoss}");
    return differ == 0 ? 0 : 1;
}

// The iCalendar fields that MS-OXCICAL maps, normalised, one per line; DTSTAMP and the PRODID are not compared.
static string Canon(IcsComponent cal)
{
    List<string> lines = [];
    lines.Add("METHOD=" + cal.MethodText);
    foreach (IcsComponent c in cal.Children.Where(x => x.Name is "VEVENT" or "VTODO"))
    {
        string tag = c.Name + "/" + (c.RecurrenceId is null ? "" : "ovr:" + c.RecurrenceId.Value.Value.ToString("s", CultureInfo.InvariantCulture)) + "/";
        foreach (ContentProperty p in c.Component.Properties)
        {
            if (p.Name.Equals("DTSTAMP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(tag + p.Name.ToUpperInvariant() + ";" + string.Join(";", p.Parameters.OrderBy(q => q.Name, StringComparer.Ordinal).Select(q => q.ToString())) + ":" + p.Value);
        }

        foreach (IcsComponent al in c.Alarms)
        {
            lines.Add(tag + "VALARM:" + al.GetText("TRIGGER"));
        }
    }

    lines.Sort(StringComparer.Ordinal);
    return string.Join("\n", lines);
}

static int IcsFiles(string dir, string outDir)
{
    Directory.CreateDirectory(outDir);
    int n = 0, items = 0, stable = 0, lossEntries = 0;
    foreach (string f in Directory.EnumerateFiles(dir, "*.ics", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        n++;
        foreach (IcsComponent cal in IcsDocument.Calendars(ContentDocument.Parse(File.ReadAllBytes(f))))
        {
            foreach (ConversionResult<MapiMessage> r in IcsToMapiConverter.ConvertAll(cal))
            {
                items++;
                lossEntries += r.Report.Count;
                ConversionResult<ContentDocument> back = new MapiToIcsConverter().Convert(r.Value);
                IcsComponent c2 = IcsDocument.Calendars(ContentDocument.Parse(back.Value.ToArray())).First();
                ConversionResult<MapiMessage> again = new IcsToMapiConverter().Convert(c2);
                ConversionResult<ContentDocument> back2 = new MapiToIcsConverter().Convert(again.Value);
                if (Canon(IcsDocument.Calendars(ContentDocument.Parse(back2.Value.ToArray())).First()) == Canon(c2))
                {
                    stable++;
                }
                else
                {
                    Console.WriteLine("UNSTABLE " + f);
                }
            }
        }
    }

    Console.WriteLine($"files {n}: items {items}, stable on the second pass {stable}, loss entries {lossEntries}");
    return 0;
}
