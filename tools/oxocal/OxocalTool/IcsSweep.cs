using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;

// OxocalTool icsdiff [--samples N] [--out file] <root>...
//
// iCalendar -> Outlook item (MS-OXCICAL import) -> iCalendar (export), property by property. Every VEVENT group (the master and its
// overrides, by UID) of every .ics file under the roots is compared with what the round trip gives back, field by field:
//   When       first start and its length as instants (a floating time counts as UTC, as the import assumes it)
//   Series     every occurrence start (and length) over 2000-2030 from the same expansion engine on both sides
//   Method, Summary, Location, Description, Status, Transp, Class, Priority, Organizer, Attendees, Alarm, Categories, Sequence
// Each difference is "reported" when the loss reports of the two conversions name the field, "unreported" otherwise. Unreported
// differences are bugs or artefacts of this comparison. Items are also checked at the property level: the item's own recurrence
// (PidLidAppointmentRecur) is expanded by the day walk of MS-OXOCAL 2.2.1.44.1.2 and compared with the series of the iCalendar.
internal static class IcsSweep
{
    private static readonly OccurrenceWindow Window = new(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private sealed record Group(List<IcsComponent> Parts, IcsComponent Master, IcsComponent Calendar);

    private sealed class Counter
    {
        public int Same;
        public int Reported;
        public int Unreported;
        public readonly Dictionary<string, int> ByClass = [];
        public readonly List<string> Samples = [];
    }

    public static int Run(string[] args)
    {
        int samples = 3;
        string? outFile = null;
        string? only = null;
        string? writeBack = null;
        int written = 0;
        List<string> roots = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--samples")
            {
                samples = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--write-back")
            {
                writeBack = args[++i];
            }
            else if (args[i] == "--only")
            {
                only = args[++i];
            }
            else if (args[i] == "--out")
            {
                outFile = args[++i];
            }
            else
            {
                roots.Add(args[i]);
            }
        }

        Dictionary<string, Counter> fields = [];
        Dictionary<string, int> lossCodes = [];
        Dictionary<string, int> shapes = [];
        Dictionary<string, int> reasons = [];
        int files = 0, groups = 0, unreadable = 0, todos = 0;
        long occAgree = 0, occTotal = 0;
        int groupsAgree = 0, recGroups = 0, recLost = 0, recSame = 0, recOther = 0;
        long recOcc = 0;
        using StreamWriter? jl = outFile is null ? null : new StreamWriter(outFile);
        foreach (string root in roots)
        {
            IEnumerable<string> list = File.Exists(root)
                ? [root]
                : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ifb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);
            foreach (string file in list)
            {
                files++;
                ContentDocument doc;
                try
                {
                    doc = ContentDocument.Parse(File.ReadAllBytes(file));
                }
                catch (Exception e) when (e is IOException or InvalidDataException)
                {
                    unreadable++;
                    continue;
                }

                foreach (Component vc in doc.Components.Where(c => c.IsNamed("VCALENDAR")))
                {
                    IcsComponent cal = new(vc);
                    todos += cal.Todos.Count();
                    List<Group> gs = GroupsOf(cal);
                    foreach (IcsComponent todo in cal.Todos)
                    {
                        CompareTodo(fields, cal, todo, file, samples, jl, lossCodes);
                    }

                    if (gs.Count == 0)
                    {
                        continue;
                    }

                    IReadOnlyList<ConversionResult<RecursiveAI.OneMail.Mapi.MapiMessage>> items = IcsToMapiConverter.ConvertAll(cal);
                    for (int gi = 0; gi < gs.Count; gi++)
                    {
                        Group g = gs[gi];
                        if (only is not null && g.Master.Uid != only)
                        {
                            continue;
                        }

                        groups++;
                        ConversionResult<RecursiveAI.OneMail.Mapi.MapiMessage> item = items[gi];
                        foreach (LossEntry le in item.Report)
                        {
                            lossCodes["import " + le.Code] = lossCodes.GetValueOrDefault("import " + le.Code) + 1;
                            if (le.Code == "RecurrenceNotMapped" && g.Master.RecurrenceRules.Count > 0)
                            {
                                string shape = RuleShape(g.Master.RecurrenceRules[0]) + " :: " + le.Reason;
                                shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
                                reasons[le.Reason] = reasons.GetValueOrDefault(le.Reason) + 1;
                            }
                        }

                        ConversionResult<ContentDocument> back = new MapiToIcsConverter().Convert(item.Value);
                        foreach (LossEntry le in back.Report.Where(x => x.IsContentLoss))
                        {
                            lossCodes["export " + le.Code] = lossCodes.GetValueOrDefault("export " + le.Code) + 1;
                        }

                        if (only is not null)
                        {
                            Console.WriteLine("--- loss report");
                            foreach (LossEntry le in item.Report.Concat(back.Report.Where(x => x.IsContentLoss)))
                            {
                                Console.WriteLine($"  {le.Kind} {le.Name} {le.Code}: {le.Reason}");
                            }

                            Console.WriteLine($"--- item: attachments {item.Value.Attachments.Count}, parts {g.Parts.Count}, overrides {g.Parts.Count(x => x.RecurrenceId is not null)}");
                            Console.WriteLine("--- exported");
                            Console.WriteLine(Encoding.UTF8.GetString(back.Value.ToArray()));
                        }

                        if (writeBack is not null && g.Master.RecurrenceRules.Count > 0)
                        {
                            // The exported calendars, for the expansion oracles to read: one file per group, named after the source file.
                            Directory.CreateDirectory(writeBack);
                            File.WriteAllBytes(Path.Combine(writeBack, $"{Path.GetFileNameWithoutExtension(file)}.{written++:D5}.ics"), back.Value.ToArray());
                        }

                        IcsComponent? bcal = back.Value.Components.Where(c => c.IsNamed("VCALENDAR")).Select(c => new IcsComponent(c)).FirstOrDefault();
                        List<IcsComponent> bev = bcal is null ? [] : bcal.Events.ToList();
                        if (bcal is null || bev.Count == 0)
                        {
                            Record(fields, "Item", "Unreported", $"no VEVENT after the round trip: {file} {g.Master.Uid}", samples, jl);
                            continue;
                        }

                        IcsComponent bmaster = bev.Find(e => e.RecurrenceId is null) ?? bev[0];
                        List<Occ> oo = Expand(cal, g.Parts);
                        List<Occ> bo = Expand(bcal, bev);
                        string id = $"{Path.GetFileName(file)} uid={g.Master.Uid}";
                        string loss = string.Join(",", item.Report.Concat(back.Report.Where(x => x.IsContentLoss)).Select(x => x.Name + "/" + x.Code).Distinct());
                        void Cmp(string field, string a, string b, params string[] names)
                        {
                            if (a == b)
                            {
                                fields.TryAdd(field, new Counter());
                                fields[field].Same++;
                                return;
                            }

                            string[] explained = item.Report.Concat(back.Report.Where(x => x.IsContentLoss))
                                .Where(x => names.Any(n => Names(x, n)))
                                .Select(x => x.Code).Distinct().ToArray();
                            Record(fields, field, explained.Length > 0 ? "Reported:" + string.Join("+", explained) : "Unreported", $"{id}\n      orig [{Cut(a)}]\n      back [{Cut(b)}]\n      loss {loss}", samples, jl);
                        }

                        IcsComponent m = g.Master;
                        Cmp("When", When(oo, g), When(bo, null), "FirstInstance", "DTSTART", "TZID", "Floating", "TimeZone", "Recurrence", "Time");
                        if (oo.SequenceEqual(bo))
                        {
                            occAgree += oo.Count;
                            groupsAgree++;
                        }

                        occTotal += oo.Count;
                        if (g.Master.RecurrenceRules.Count > 0)
                        {
                            recGroups++;
                            bool lost = item.Report.Any(x => x.Code is "RecurrenceNotMapped" or "MultipleRRules");
                            if (lost)
                            {
                                recLost++;
                            }
                            else if (oo.SequenceEqual(bo))
                            {
                                recSame++;
                                recOcc += oo.Count;
                            }
                            else
                            {
                                recOther++;
                            }
                        }

                        Cmp("Series", Series(oo) + FirstDiff(oo, bo), Series(bo), "FirstInstance", "RRULE", "RDATE", "EXDATE", "Recurrence", "Override", "RECURRENCE-ID", "DatesWithout", "DTSTART", "TimeZone", "Time", "Duplicate");
                        Cmp("Method", MethodOf(cal.MethodText), MethodOf(bcal.MethodText), "METHOD");
                        Cmp("Summary", m.Summary ?? "", bmaster.Summary ?? "", "SUMMARY");
                        Cmp("Location", m.Location ?? "", bmaster.Location ?? "", "LOCATION");
                        Cmp("Description", Norm(m.Description), Norm(bmaster.Description), "DESCRIPTION");
                        Cmp("Status", StatusOf(m, cal), StatusOf(bmaster, bcal), "STATUS");
                        Cmp("Transp", Transp(m), Transp(bmaster), "TRANSP", "BUSYSTATUS");
                        Cmp("Class", (m.Classification ?? "").ToUpperInvariant(), (bmaster.Classification ?? "").ToUpperInvariant(), "CLASS");
                        Cmp("Priority", Prio(m.Priority), Prio(bmaster.Priority), "PRIORITY");
                        Cmp("Organizer", Person(m.Organizer), Person(bmaster.Organizer), "ORGANIZER");
                        foreach ((string fname, Func<IcsCalAddress, string> f, string[] names) in new (string, Func<IcsCalAddress, string>, string[])[]
                        {
                            ("AttendeeSet", _ => "", ["ATTENDEE"]),
                            ("AttendeeCn", Cn, ["ATTENDEE"]),
                            ("AttendeeRole", a => (a.Role ?? "REQ-PARTICIPANT").ToUpperInvariant(), ["ATTENDEE", "ROLE"]),
                            ("AttendeeCutype", a => (a.CalendarUserType ?? "INDIVIDUAL").ToUpperInvariant(), ["ATTENDEE", "CUTYPE"]),
                            ("AttendeePartstat", a => (a.ParticipationStatus ?? "NEEDS-ACTION").ToUpperInvariant(), ["ATTENDEE", "PARTSTAT"]),
                            ("AttendeeRsvp", a => a.Rsvp == true ? "R" : "", ["ATTENDEE", "RSVP"]),
                        })
                        {
                            Cmp(fname, AttendeeField(m, f), AttendeeField(bmaster, f), names);
                        }
                        Cmp("Alarm", Alarm(m), Alarm(bmaster), "VALARM");
                        Cmp("Categories", string.Join(",", m.Categories.Order()), string.Join(",", bmaster.Categories.Order()), "CATEGORIES");
                        Cmp("Sequence", (m.Sequence ?? 0).ToString(CultureInfo.InvariantCulture), (bmaster.Sequence ?? 0).ToString(CultureInfo.InvariantCulture), "SEQUENCE");
                        if (!string.IsNullOrEmpty(m.Uid))
                        {
                            Cmp("Uid", m.Uid, bmaster.Uid ?? "", "UID");
                        }

                        // The item itself: PidLidAppointmentRecur expanded by the day walk of MS-OXOCAL against the series' days.
                        Cmp("ItemDays", ItemDays(item.Value, oo, g), ItemDaysOrig(oo, g), "FirstInstance", "RRULE", "RDATE", "EXDATE", "Recurrence", "Override", "DatesWithout", "Time");
                    }
                }
            }
        }

        Console.WriteLine($"files {files} (unreadable {unreadable}), event groups {groups}, VTODOs seen {todos}");
        Console.WriteLine($"occurrences 2000-2030 in the iCalendar: {occTotal}; in groups whose series came back identical: {occAgree} ({groupsAgree} of {groups} groups)");
        Console.WriteLine($"recurring groups {recGroups}: series identical after the round trip {recSame} ({recOcc} occurrences compared), differing but reported {recOther}, recurrence not held (RecurrenceNotMapped or MultipleRRules, one event kept) {recLost}");
        Console.WriteLine($"{"field",-12} {"same",7} {"reported",9} {"unreported",11}");
        foreach (KeyValuePair<string, Counter> kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{kv.Key,-12} {kv.Value.Same,7} {kv.Value.Reported,9} {kv.Value.Unreported,11}");
        }

        foreach (KeyValuePair<string, Counter> kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            foreach (KeyValuePair<string, int> c in kv.Value.ByClass.OrderByDescending(c => c.Value))
            {
                Console.WriteLine($"  {kv.Key} {c.Key} {c.Value}");
            }
        }

        Console.WriteLine("unmapped rule reasons:");
        foreach (KeyValuePair<string, int> kv in reasons.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"  {kv.Value} {kv.Key}");
        }

        Console.WriteLine("unmapped rule shapes:");
        foreach (KeyValuePair<string, int> kv in shapes.OrderByDescending(k => k.Value).Take(200))
        {
            Console.WriteLine($"  {kv.Value} {kv.Key}");
        }

        Console.WriteLine("loss codes:");
        foreach (KeyValuePair<string, int> kv in lossCodes.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {kv.Key} {kv.Value}");
        }

        foreach (KeyValuePair<string, Counter> kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            foreach (string s in kv.Value.Samples)
            {
                Console.WriteLine($"SAMPLE {kv.Key}: {s}");
            }
        }

        return 0;
    }

    private static string RuleShape(RecurrenceRule r)
    {
        List<string> p = [r.Frequency.ToString().ToUpperInvariant()];
        if (r.Interval > 1)
        {
            p.Add("INTERVAL");
        }

        if (r.BySecond.Count > 0) p.Add("BYSECOND");
        if (r.ByMinute.Count > 0) p.Add("BYMINUTE");
        if (r.ByHour.Count > 0) p.Add("BYHOUR");
        if (r.ByDay.Count > 0) p.Add(r.ByDay.Any(d => d.Ordinal is not null) ? "BYDAY(n)" : "BYDAY");
        if (r.ByMonthDay.Count > 0) p.Add(r.ByMonthDay.Count > 1 ? "BYMONTHDAY*" : "BYMONTHDAY");
        if (r.ByYearDay.Count > 0) p.Add("BYYEARDAY");
        if (r.ByWeekNo.Count > 0) p.Add("BYWEEKNO");
        if (r.ByMonth.Count > 0) p.Add(r.ByMonth.Count > 1 ? "BYMONTH*" : "BYMONTH");
        if (r.BySetPos.Count > 0) p.Add("BYSETPOS");
        if (r.RScale is not null) p.Add("RSCALE");
        if (r.Until is not null) p.Add("UNTIL");
        if (r.Count is not null) p.Add("COUNT");
        return string.Join(";", p);
    }

    // VTODO to task item and back: the fields MS-OXOTASK carries, and the series as the days it covers.
    private static void CompareTodo(Dictionary<string, Counter> fields, IcsComponent cal, IcsComponent todo, string file, int samples, StreamWriter? jl, Dictionary<string, int> lossCodes)
    {
        Component tmp = new("VCALENDAR");
        tmp.Add(new ContentProperty("VERSION", "2.0"));
        tmp.Add(todo.Component);
        IcsComponent one = new(tmp);
        ConversionResult<RecursiveAI.OneMail.Mapi.MapiMessage> item = IcsToMapiConverter.ConvertAll(one).First(r => r.Value.MessageClass == "IPM.Task");
        ConversionResult<ContentDocument> back = new MapiToIcsConverter().Convert(item.Value);
        foreach (LossEntry le in item.Report.Concat(back.Report.Where(x => x.IsContentLoss)))
        {
            lossCodes["todo " + le.Code] = lossCodes.GetValueOrDefault("todo " + le.Code) + 1;
        }

        IcsComponent? bt = back.Value.Components.Where(c => c.IsNamed("VCALENDAR")).Select(c => new IcsComponent(c)).FirstOrDefault()?.Todos.FirstOrDefault();
        string id = $"{Path.GetFileName(file)} uid={todo.Uid} (VTODO)";
        if (bt is null)
        {
            Record(fields, "Todo", "Unreported", "no VTODO after the round trip: " + id, samples, jl);
            return;
        }

        LossEntry[] losses = item.Report.Concat(back.Report.Where(x => x.IsContentLoss)).ToArray();
        void Cmp(string field, string a, string b, params string[] names)
        {
            if (a == b)
            {
                fields.TryAdd(field, new Counter());
                fields[field].Same++;
                return;
            }

            string[] explained = losses.Where(l => names.Any(n => Names(l, n))).Select(l => l.Code).Distinct().ToArray();
            Record(fields, field, explained.Length > 0 ? "Reported:" + string.Join("+", explained) : "Unreported", $"{id}\n      orig [{Cut(a)}]\n      back [{Cut(b)}]", samples, jl);
        }

        static string T(IcsTime? t) => t is { } v ? (v.IsDate ? v.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : v.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)) : "";
        static string Day(IcsTime? t) => t is { } v ? v.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "";
        Cmp("TodoSummary", todo.Summary ?? "", bt.Summary ?? "", "SUMMARY");
        Cmp("TodoDescription", Norm(todo.Description), Norm(bt.Description), "DESCRIPTION");
        Cmp("TodoStart", Day(todo.Start), Day(bt.Start), "DTSTART");
        Cmp("TodoDue", Day(todo.Due), Day(bt.Due), "DUE");
        Cmp("TodoCompleted", T(todo.Completed), T(bt.Completed), "COMPLETED");
        // A VTODO without STATUS is a task not started.
        Cmp("TodoStatus", (todo.Status ?? "NEEDS-ACTION").ToUpperInvariant(), (bt.Status ?? "NEEDS-ACTION").ToUpperInvariant(), "STATUS");
        Cmp("TodoPercent", (todo.PercentComplete ?? 0).ToString(CultureInfo.InvariantCulture), (bt.PercentComplete ?? 0).ToString(CultureInfo.InvariantCulture), "PERCENT");
        Cmp("TodoPriority", Prio(todo.Priority), Prio(bt.Priority), "PRIORITY");
        Cmp("TodoClass", (todo.Classification ?? "").ToUpperInvariant(), (bt.Classification ?? "").ToUpperInvariant(), "CLASS");
// The series as the days it covers (BYDAY=1FR and BYDAY=FR;BYSETPOS=1 are one rule).
        IcsComponent? backCal = back.Value.Components.Where(c => c.IsNamed("VCALENDAR")).Select(c => new IcsComponent(c)).FirstOrDefault();
        Cmp("TodoSeries", Series(ExpandDays(one, todo)), Series(ExpandDays(backCal!, bt)), "RRULE", "Recurrence", "FirstInstance");
    }

    // A loss entry explains a field when it is named for it (the field itself or one of its parameters) or its code says so.
    private static bool Names(LossEntry e, string n) =>
        e.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith(n + ";", StringComparison.OrdinalIgnoreCase) || e.Code.Contains(n, StringComparison.OrdinalIgnoreCase);

    private static string Cut(string s) => s.Length > 300 ? s[..300] + "..." : s;

    private static void Record(Dictionary<string, Counter> fields, string field, string cls, string detail, int samples, StreamWriter? jl)
    {
        if (!fields.TryGetValue(field, out Counter? c))
        {
            fields[field] = c = new Counter();
        }

        bool reported = cls.StartsWith("Reported", StringComparison.Ordinal);
        if (reported)
        {
            c.Reported++;
        }
        else
        {
            c.Unreported++;
        }

        c.ByClass[cls] = c.ByClass.GetValueOrDefault(cls) + 1;
        if (!reported && c.Samples.Count < samples * 4 && c.ByClass[cls] <= samples)
        {
            c.Samples.Add(cls + " " + detail);
        }

        jl?.WriteLine(field + "\t" + cls + "\t" + detail.Replace('\n', ' '));
    }

    private static string Norm(string? s) => (s ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n').Trim();

    private static string MethodOf(string? m) => string.IsNullOrWhiteSpace(m) || m.Trim().Equals("PUBLISH", StringComparison.OrdinalIgnoreCase) ? "PUBLISH" : m.Trim().ToUpperInvariant();

    // METHOD:CANCEL says the event is cancelled (RFC 5546 3.2.5) whether or not STATUS does.
    private static string StatusOf(IcsComponent e, IcsComponent cal) => MethodOf(cal.MethodText) == "CANCEL" ? "CANCELLED" : (e.Status ?? "").Trim().ToUpperInvariant();

    private static string Prio(int? p) => p is null or 0 ? "" : (p <= 4 ? "high" : p == 5 ? "normal" : "low");

    // The busy status MS-OXCICAL 2.1.3.1.1.20.25 / .31 / .23 derive: X-MICROSOFT-CDO-BUSYSTATUS, else TRANSP, else STATUS, else busy.
    private static string Transp(IcsComponent e)
    {
        string? b = e.GetText("X-MICROSOFT-CDO-BUSYSTATUS")?.Trim().ToUpperInvariant();
        string? t = e.Transparency?.Trim().ToUpperInvariant();
        string? st = e.Status?.Trim().ToUpperInvariant();
        return b is "FREE" or "TENTATIVE" or "BUSY" or "OOF" or "WORKINGELSEWHERE" ? b
            : t == "TRANSPARENT" ? "FREE" : t == "OPAQUE" ? "BUSY"
            : st == "CANCELLED" ? "FREE" : st == "TENTATIVE" ? "TENTATIVE" : "BUSY";
    }

    // A missing CN is read back as the address: Outlook recipients always have a display name.
    private static string Cn(IcsCalAddress a) => a.CommonName is null || string.Equals(a.CommonName, a.Email, StringComparison.OrdinalIgnoreCase) ? "" : a.CommonName;

    private static string Person(IcsCalAddress? a) => a is null ? "" : a.Email.ToLowerInvariant() + "|" + Cn(a);

    private static string AttendeeField(IcsComponent e, Func<IcsCalAddress, string> f) => string.Join(";", e.Attendees.Select(a => a.Email.ToLowerInvariant() + "=" + f(a)).Order(StringComparer.Ordinal));

    private static string Alarm(IcsComponent e)
    {
        IcsComponent? al = e.Alarms.FirstOrDefault(a => a.Trigger is not null);
        return al?.Trigger is { Offset: { } off } ? ((int)off.ToTimeSpan().TotalMinutes).ToString(CultureInfo.InvariantCulture) : al is null ? "" : "abs";
    }

    private static List<Group> GroupsOf(IcsComponent cal)
    {
        List<Group> groups = [];
        Dictionary<string, Group> byUid = [];
        foreach (IcsComponent ev in cal.Events)
        {
            string uid = ev.Uid ?? "";
            if (uid.Length > 0 && byUid.TryGetValue(uid, out Group? g))
            {
                g.Parts.Add(ev);
                continue;
            }

            Group ng = new([ev], ev, cal);
            groups.Add(ng);
            if (uid.Length > 0)
            {
                byUid[uid] = ng;
            }
        }

        // The converter's master is the part without RECURRENCE-ID, else the first.
        return groups.Select(g => g with { Master = g.Parts.Find(p => p.RecurrenceId is null) ?? g.Parts[0] }).ToList();
    }

    private readonly record struct Occ(long Key, long Length, string Kind);

    // The occurrences of one group of components (a series and its overrides) over the window, from a calendar made of the original's time
    // zones and just those components, so that one busy series cannot use up the cap of the others.
    private static List<Occ> Expand(IcsComponent cal, IEnumerable<IcsComponent> parts)
    {
        List<Occ> r = [];
        Component tmp = new("VCALENDAR");
        foreach (Component tz in cal.Component.FindComponents("VTIMEZONE"))
        {
            tmp.Add(tz);
        }

        foreach (IcsComponent p in parts)
        {
            tmp.Add(p.Component);
        }

        try
        {
            foreach (Occurrence o in CalendarRecurrence.Expand(new IcsComponent(tmp), Window, new ExpansionOptions { MaxOccurrences = 100_000 }))
            {
                // A floating time stands as UTC (the import assumes it), a date as midnight UTC.
                long s = o.StartUtc?.Ticks ?? DateTime.SpecifyKind(o.Start.Value, DateTimeKind.Utc).Ticks;
                long e = o.EndUtc?.Ticks ?? DateTime.SpecifyKind(o.End.Value, DateTimeKind.Utc).Ticks;
                r.Add(new Occ(s, e - s, o.Start.IsDate ? "D" : "T"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("expand failed: " + ex.GetType().Name + " " + ex.Message);
        }

        return r.OrderBy(o => o.Key).ToList();
    }

    // A task has dates, not times: each occurrence is its day.
    private static List<Occ> ExpandDays(IcsComponent cal, IcsComponent todo) =>
        [.. Expand(cal, [todo]).Select(o => new Occ(new DateTime(o.Key, DateTimeKind.Utc).Date.Ticks, 0, "D")).DistinctBy(o => o.Key)];

    private static string FirstDiff(List<Occ> a, List<Occ> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (a[i] != b[i])
            {
                return $" first difference at {i}: {Stamp(a[i].Key)}+{TimeSpan.FromTicks(a[i].Length).TotalMinutes} vs {Stamp(b[i].Key)}+{TimeSpan.FromTicks(b[i].Length).TotalMinutes}";
            }
        }

        return a.Count == b.Count ? "" : $" counts {a.Count} vs {b.Count}";
    }

    private static string Stamp(long ticks) => new DateTime(ticks, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture);

    private static string When(List<Occ> occs, Group? _) => occs.Count == 0 ? "none" : Stamp(occs[0].Key) + "+" + TimeSpan.FromTicks(occs[0].Length).TotalMinutes.ToString(CultureInfo.InvariantCulture) + occs[0].Kind;

    private static string Series(List<Occ> occs) => occs.Count + ":" + string.Join(",", occs.Take(6).Select(o => Stamp(o.Key))) + (occs.Count > 6 ? ".." + Stamp(occs[^1].Key) : "")
        + ":" + Hash(string.Join(",", occs.Select(o => o.Key + "/" + o.Length)));

    private static string Hash(string s) => System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)).AsSpan(0, 6));

    // The local calendar days the item's recurrence covers, from the day walk of MS-OXOCAL, with the deleted instances taken out and the
    // modified instances put at their new days: compared with the days of the occurrences of the iCalendar in the item's own zone.
    private static string ItemDays(RecursiveAI.OneMail.Mapi.MapiMessage m, List<Occ> orig, Group g)
    {
        OutlookAppointment? a = OutlookAppointment.TryCreate(m);
        if (a?.Recurrence is not { } r || !OutlookRecurrence_CanWalk(r))
        {
            return "n/a";
        }

        HashSet<DateTime> days = [];
        foreach (DateTime d in OutlookRecurrence.Days(r.Pattern))
        {
            if (!InBounds(d))
            {
                continue;
            }

            days.Add(d);
        }

        foreach (DateTime d in a.DeletedInstances)
        {
            days.Remove(d.Date);
        }

        foreach (OutlookModifiedInstance x in a.Exceptions)
        {
            days.Remove(x.OriginalStart.Date);
            days.Add(x.Start.Date);
        }

        return days.Count + ":" + Hash(string.Join(",", days.Order().Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture))));
    }

    // Days near the window's edges are left out: the window is in UTC and the days are local.
    private static bool InBounds(DateTime d) => d >= Window.Start.AddDays(2) && d < Window.End.AddDays(-2);

    private static bool OutlookRecurrence_CanWalk(AppointmentRecurrencePattern r) => !r.Pattern.IsNonGregorian && r.Pattern.Period > 0;

    // The days of the iCalendar's occurrences, in the zone the series is written in (the item's local clock).
    private static string ItemDaysOrig(List<Occ> orig, Group g)
    {
        if (g.Master.RecurrenceRules.Count == 0)
        {
            return "n/a";
        }

        IcsTime? s = g.Master.Start;
        HashSet<DateTime> days = [];
        foreach (Occ o in orig)
        {
            DateTime u = new(o.Key, DateTimeKind.Utc);
            DateTime ld = LocalDay(u, s, g);
            if (InBounds(ld))
            {
                days.Add(ld);
            }
        }

        return days.Count + ":" + Hash(string.Join(",", days.Order().Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture))));
    }

    private static DateTime LocalDay(DateTime utc, IcsTime? s, Group g)
    {
        if (s is { Kind: IcsTimeKind.Zoned, TzId: { } id } && new RecursiveAI.OneMail.Calendar.TimeZones.TimeZoneResolver(g.Calendar).Resolve(id) is { } z)
        {
            return z.ToLocal(utc).Date;
        }

        return utc.Date;
    }
}
