using System.Globalization;
using System.Text;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;

// OxocalTool itemdiff [--samples N] [--only substring] <root>...
//
// Outlook item -> iCalendar (MS-OXCICAL export) -> Outlook item (import), compared property by property: the second item against the
// first. Items come from the MSG, OFT, PST, OST and TNEF files under the roots. A difference is "reported" when the loss reports of the
// two conversions name the field, "unreported" otherwise. The recurrence is compared as the days it covers (the day walk of MS-OXOCAL
// 2.2.1.44.1.2 with its deleted and modified instances) and as decoded fields, so that two blobs that mean the same thing agree.
internal static class ItemSweep
{
    private sealed class Counter
    {
        public int Same;
        public int Reported;
        public int Unreported;
        public readonly Dictionary<string, int> ByClass = [];
        public readonly List<string> Samples = [];
    }

    public static int Run(IEnumerable<(string Origin, MapiMessage Message)> items, int samples, string? only)
    {
        Dictionary<string, Counter> fields = [];
        Dictionary<string, int> kinds = [];
        Dictionary<string, int> lossCodes = [];
        int n = 0, appts = 0, tasks = 0;
        foreach ((string origin, MapiMessage m) in items)
        {
            n++;
            string id = origin;
            if (only is not null && !origin.Contains(only, StringComparison.Ordinal))
            {
                continue;
            }

            OutlookAppointment? a1 = OutlookAppointment.TryCreate(m);
            if (a1 is null)
            {
                if (OutlookTask.TryCreate(m) is not null)
                {
                    tasks++;
                }

                continue;
            }

            appts++;
            kinds[a1.Kind.ToString()] = kinds.GetValueOrDefault(a1.Kind.ToString()) + 1;
            ConversionResult<ContentDocument> one = new MapiToIcsConverter().Convert(m);
            IcsComponent? cal = IcsDocument.Calendars(ContentDocument.Parse(one.Value.ToArray())).FirstOrDefault();
            if (cal is null)
            {
                Record(fields, "Item", "Unreported", id + " exported no calendar", samples);
                continue;
            }

            IReadOnlyList<ConversionResult<MapiMessage>> back = IcsToMapiConverter.ConvertAll(cal);
            if (back.Count == 0)
            {
                Record(fields, "Item", "Unreported", id + " imported no item", samples);
                continue;
            }

            ConversionResult<MapiMessage> two = back[0];
            OutlookAppointment? a2 = OutlookAppointment.TryCreate(two.Value);
            if (a2 is null)
            {
                Record(fields, "Item", "Unreported", id + " is no appointment after the round trip", samples);
                continue;
            }

            LossEntry[] losses = one.Report.Concat(two.Report).Where(x => x.IsContentLoss).ToArray();
            foreach (LossEntry le in losses)
            {
                lossCodes[le.Code] = lossCodes.GetValueOrDefault(le.Code) + 1;
            }

            string lossText = string.Join(",", losses.Select(x => x.Name + "/" + x.Code).Distinct());
            void Cmp(string field, string x, string y, params string[] names)
            {
                if (x == y)
                {
                    fields.TryAdd(field, new Counter());
                    fields[field].Same++;
                    return;
                }

                string[] explained = losses.Where(l => names.Any(nm => l.Name.Contains(nm, StringComparison.OrdinalIgnoreCase) || l.Code.Contains(nm, StringComparison.OrdinalIgnoreCase))).Select(l => l.Code).Distinct().ToArray();
                Record(fields, field, explained.Length > 0 ? "Reported:" + string.Join("+", explained) : "Unreported", $"{id} ({m.MessageClass})\n      item [{Cut(x)}]\n      back [{Cut(y)}]\n      loss {lossText}", samples);
            }

            Cmp("Kind", a1.Kind.ToString(), a2.Kind.ToString(), "MessageClass", "METHOD", "ForwardNotification");
            Cmp("Subject", a1.Subject ?? "", a2.Subject ?? "", "SUBJECT");
            Cmp("Location", a1.Location ?? "", a2.Location ?? "", "LOCATION");
            Cmp("AllDay", a1.IsAllDay.ToString(), a2.IsAllDay.ToString(), "SubType", "ALLDAY");
            Cmp("Start", Utc(a1.Start), Utc(a2.Start), "Time", "DTSTART", "Recurrence");
            Cmp("End", Utc(a1.End), Utc(a2.End), "Time", "DTEND", "Recurrence");
            Cmp("Busy", a1.BusyStatus.ToString() ?? "", a2.BusyStatus.ToString() ?? "", "BusyStatus", "TRANSP");
            Cmp("IntendedBusy", a1.IntendedBusyStatus.ToString() ?? "", a2.IntendedBusyStatus.ToString() ?? "", "Intended");
            Cmp("Sensitivity", (a1.Sensitivity ?? 0).ToString(CultureInfo.InvariantCulture), (a2.Sensitivity ?? 0).ToString(CultureInfo.InvariantCulture), "Sensitivity", "CLASS");
            Cmp("Importance", (a1.Importance ?? 1).ToString(CultureInfo.InvariantCulture), (a2.Importance ?? 1).ToString(CultureInfo.InvariantCulture), "Importance", "PRIORITY");
            Cmp("Sequence", (a1.Sequence ?? 0).ToString(CultureInfo.InvariantCulture), (a2.Sequence ?? 0).ToString(CultureInfo.InvariantCulture), "Sequence");
            Cmp("Reminder", Reminder(a1), Reminder(a2), "Reminder", "VALARM");
            Cmp("GlobalId", Hex(a1.GlobalObjectId), Hex(a2.GlobalObjectId), "GlobalObjectId", "UID");
            Cmp("Body", (m.Body ?? "").TrimEnd('\r', '\n'), (two.Value.Body ?? "").TrimEnd('\r', '\n'), "Body", "DESCRIPTION", "Rtf");
            Cmp("Organizer", Org(a1), Org(a2), "SentRepresenting", "ORGANIZER");
            Cmp("Attendees", Attendees(a1), Attendees(a2), "ATTENDEE", "Recipient");
            Cmp("Recurring", (a1.Recurrence is not null).ToString(), (a2.Recurrence is not null).ToString(), "Recur");
            if (a1.Recurrence is { } r1 && a2.Recurrence is { } r2)
            {
                Cmp("RecurPattern", Pattern(r1), Pattern(r2), "Recur", "RRULE", "ShortMonth", "FirstInstance");
                Cmp("RecurDays", Days(a1), Days(a2), "Recur", "RRULE", "ShortMonth", "FirstInstance", "Exception");
                Cmp("Exceptions", Exceptions(a1), Exceptions(a2), "Exception", "Attachment", "RECURRENCE-ID", "RDATE");
                Cmp("TimeZone", Zone(a1), Zone(a2), "TimeZone", "TZID");
            }
            else if (a1.TimeZone is not null || a2.TimeZone is not null)
            {
                Cmp("TimeZone", Zone(a1), Zone(a2), "TimeZone", "TZID");
            }
        }

        Console.WriteLine($"items {n}: appointments and meetings {appts}, tasks {tasks}");
        foreach (KeyValuePair<string, int> kv in kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  kind {kv.Key} {kv.Value}");
        }

        Console.WriteLine($"{"field",-14} {"same",7} {"reported",9} {"unreported",11}");
        foreach (KeyValuePair<string, Counter> kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{kv.Key,-14} {kv.Value.Same,7} {kv.Value.Reported,9} {kv.Value.Unreported,11}");
        }

        foreach (KeyValuePair<string, Counter> kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            foreach (KeyValuePair<string, int> c in kv.Value.ByClass.OrderByDescending(c => c.Value))
            {
                Console.WriteLine($"  {kv.Key} {c.Key} {c.Value}");
            }
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

    private static string Cut(string s) => s.Length > 400 ? s[..400] + "..." : s;

    private static void Record(Dictionary<string, Counter> fields, string field, string cls, string detail, int samples)
    {
        if (!fields.TryGetValue(field, out Counter? c))
        {
            fields[field] = c = new Counter();
        }

        if (cls.StartsWith("Reported", StringComparison.Ordinal))
        {
            c.Reported++;
        }
        else
        {
            c.Unreported++;
        }

        c.ByClass[cls] = c.ByClass.GetValueOrDefault(cls) + 1;
        if (!cls.StartsWith("Reported", StringComparison.Ordinal) && c.ByClass[cls] <= samples)
        {
            c.Samples.Add(cls + " " + detail);
        }
    }

    private static string Utc(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) : "";

    private static string Hex(byte[]? b) => b is null ? "" : System.Convert.ToHexString(b);

    private static string Reminder(OutlookAppointment a) => a.ReminderSet ? (a.ReminderMinutes is 0x5AE980E1 or null ? 15 : a.ReminderMinutes).Value.ToString(CultureInfo.InvariantCulture) : "";

    private static string Org(OutlookAppointment a) => a.Organizer is { } o ? (o.Address ?? "").ToLowerInvariant() + "|" + (o.Name ?? "") : "";

    // Recipients that are not the organizer's own row, by address: type, response, response time.
    private static string Attendees(OutlookAppointment a) => string.Join(";", a.Attendees.Where(x => !x.IsOrganizer && !x.IsDeleted)
        .Select(x => (x.Address ?? "").ToLowerInvariant() + "|" + x.Name + "|" + x.Role + "|" + x.Response + "|" + (x.ResponseTime is { } t ? Utc(t) : ""))
        .Order(StringComparer.Ordinal));

    // The pattern as what it says, not as how the blob spells it: fields that do not apply to the pattern type are left out.
    private static string Pattern(AppointmentRecurrencePattern r)
    {
        RecurrencePattern p = r.Pattern;
        StringBuilder sb = new();
        sb.Append(p.PatternType).Append('/').Append(p.Period);
        if (p.PatternType is 1 or 3)
        {
            sb.Append("/mask").Append(p.WeekdayMask);
        }

        if (p.PatternType == 1 && p.Period > 1)
        {
            sb.Append("/wk").Append(p.FirstDayOfWeek);
        }

        if (p.PatternType == 2)
        {
            sb.Append("/day").Append(p.DayOfMonth);
        }

        if (p.PatternType == 3)
        {
            sb.Append("/nth").Append(p.Nth);
        }

        sb.Append("/end").Append(p.EndType & 0xF);
        if (p.EndType == RecurrencePattern.EndAfterCount)
        {
            sb.Append('x').Append(p.OccurrenceCount);
        }

        if (p.EndType == RecurrencePattern.EndAfterDate)
        {
            sb.Append('@').Append(OutlookMinutes.ToDateTime(p.EndDate).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        sb.Append("/from").Append(OutlookMinutes.ToDateTime(p.StartDate).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        sb.Append("/t").Append(r.StartTimeOffset).Append('-').Append(r.EndTimeOffset);
        return sb.ToString();
    }

    private static string Days(OutlookAppointment a)
    {
        AppointmentRecurrencePattern r = a.Recurrence!;
        if (!OutlookRecurrence_CanWalk(r))
        {
            return "n/a";
        }

        SortedSet<DateTime> days = [];
        int guard = 0;
        foreach (DateTime d in OutlookRecurrence.Days(r.Pattern))
        {
            days.Add(d);
            if (++guard >= 5000)
            {
                break;
            }
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

        return days.Count + ":" + (days.Count > 0 ? days.Min.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".." + days.Max.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "") + ":"
            + System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", days.Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture))))).AsSpan(0, 5));
    }

    private static bool OutlookRecurrence_CanWalk(AppointmentRecurrencePattern r) => !r.Pattern.IsNonGregorian && r.Pattern.Period > 0;

    private static string Exceptions(OutlookAppointment a) => string.Join(";", a.Exceptions
        .Select(x => OutlookMinutes.ToDateTime(x.Info.OriginalStartTime).ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture) + ">" + x.Start.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture) + "-" + x.End.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture)
            + "|" + (x.Message?.Subject ?? x.Info.Subject) + "|" + (x.Message is null ? x.Info.Location : x.Message.Properties.Get(OutlookNames.Location)?.AsString(x.Message.String8Encoding) ?? x.Info.Location))
        .Order(StringComparer.Ordinal));

    private static string Zone(OutlookAppointment a)
    {
        if (a.TimeZone is not { } z)
        {
            return "none";
        }

        TzRule r = z.Rule;
        return $"{r.Bias}/{r.StandardBias}/{r.DaylightBias}/{Sys(r.StandardDate)}/{Sys(r.DaylightDate)} rules {z.Definition?.Rules.Count ?? 1}";
    }

    private static string Sys(SystemTime t) => t.IsUnset ? "-" : $"{t.Month}.{t.DayOfWeek}.{t.Day}.{t.Hour}:{t.Minute}";
}
