using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.TimeZones;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Conversion.OxcIcal;

/// <summary>
/// iCalendar (RFC 5545, with the iTIP methods of RFC 5546) to Outlook items (MS-OXCICAL 2.1.3): a VEVENT becomes an appointment or a meeting
/// message by its METHOD, a VTODO a task. Overrides with a RECURRENCE-ID become exception attachments of the series. Whatever has no MAPI
/// form is in the loss report with family <c>ics</c>.
/// </summary>
public sealed class IcsToMapiConverter : IItemConversion<IcsComponent, MapiMessage>
{
    /// <summary>Converts the first event or task of a calendar (with its overrides).</summary>
    /// <param name="source">A VCALENDAR component.</param>
    /// <returns>The item, and the loss report. When the calendar holds no VEVENT or VTODO the item is an empty <c>IPM.Appointment</c> and the report says so. Never throws for malformed content.</returns>
    public ConversionResult<MapiMessage> Convert(IcsComponent source)
    {
        IReadOnlyList<ConversionResult<MapiMessage>> all = ConvertAll(source);
        if (all.Count > 0)
        {
            if (all.Count > 1)
            {
                all[0].Report.Add("ics", "VEVENT", LossKind.Dropped, "AdditionalItemsNotConverted", $"The calendar holds {all.Count} items; use ConvertAll to convert each.");
            }

            return all[0];
        }

        LossReport r = new();
        r.Add("ics", "VCALENDAR", LossKind.Dropped, "NoEventOrTask", "The calendar holds no VEVENT or VTODO.");
        MapiMessage m = new();
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.MessageClass, MapiType.UnicodeString), Encoding.Unicode.GetBytes("IPM.Appointment")));
        return new ConversionResult<MapiMessage>(m, r);
    }

    /// <summary>Converts every item of a calendar: one per UID of VEVENT, one per VTODO.</summary>
    /// <param name="source">A VCALENDAR component.</param>
    /// <returns>One result per item, in document order.</returns>
    public static IReadOnlyList<ConversionResult<MapiMessage>> ConvertAll(IcsComponent source)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<ConversionResult<MapiMessage>> list = [];
        TimeZoneResolver zones = new(source);
        string? method = source.MethodText?.Trim().ToUpperInvariant();
        List<(string Uid, List<IcsComponent> Parts)> groups = [];
        foreach (IcsComponent ev in source.Events)
        {
            string uid = ev.Uid ?? "";
            (string Uid, List<IcsComponent> Parts) g = groups.Find(x => x.Uid == uid && uid.Length > 0);
            if (g.Parts is null)
            {
                g = (uid, []);
                groups.Add(g);
            }

            g.Parts.Add(ev);
        }

        foreach ((string _, List<IcsComponent> parts) in groups)
        {
            IcsComponent master = parts.Find(p => p.RecurrenceId is null) ?? parts[0];
            List<IcsComponent> overrides = parts.Where(p => p != master && p.RecurrenceId is not null).ToList();
            LossReport report = new();
            MapiMessage m = new EventBuilder(master, overrides, zones, method, report).Build();
            list.Add(new ConversionResult<MapiMessage>(m, report));
        }

        foreach (IcsComponent todo in source.Todos)
        {
            LossReport report = new();
            list.Add(new ConversionResult<MapiMessage>(new TodoBuilder(todo, zones, report).Build(), report));
        }

        return list;
    }

    // ---- shared property construction ----

    internal sealed class Props
    {
        private readonly Dictionary<MapiNamedProperty, ushort> _ids = [];
        private ushort _next = 0x8000;

        public MapiMessage Target { get; } = new();

        public MapiMessage Message => Target;

        private ushort IdOf(MapiNamedProperty n)
        {
            if (!_ids.TryGetValue(n, out ushort id))
            {
                id = _next++;
                _ids[n] = id;
            }

            return id;
        }

        public void Str(ushort id, string v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v)));

        public void Int(ushort id, int v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(id, MapiType.Integer32), BitConverter.GetBytes(v)));

        public void Bool(ushort id, bool v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(id, MapiType.Boolean), [v ? (byte)1 : (byte)0]));

        public void Time(ushort id, DateTime utc) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(id, MapiType.Time), FileTime(utc)));

        public void Bin(ushort id, byte[] v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(id, MapiType.Binary), v));

        public void NStr(MapiNamedProperty n, string v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(IdOf(n), MapiType.UnicodeString), Encoding.Unicode.GetBytes(v), n));

        public void NInt(MapiNamedProperty n, int v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(IdOf(n), MapiType.Integer32), BitConverter.GetBytes(v), n));

        public void NBool(MapiNamedProperty n, bool v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(IdOf(n), MapiType.Boolean), [v ? (byte)1 : (byte)0], n));

        public void NTime(MapiNamedProperty n, DateTime utc) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(IdOf(n), MapiType.Time), FileTime(utc), n));

        public void NBin(MapiNamedProperty n, byte[] v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(IdOf(n), MapiType.Binary), v, n));

        public void NDouble(MapiNamedProperty n, double v) => Target.Properties.Add(MapiProperty.CreateOwned(new MapiTag(IdOf(n), MapiType.Floating64), BitConverter.GetBytes(v), n));

        public void NStrings(MapiNamedProperty n, IReadOnlyList<string> v) =>
            Target.Properties.Add(MapiProperty.CreateMulti(new MapiTag(IdOf(n), MapiType.MultipleString), v.Select(s => Encoding.Unicode.GetBytes(s)).ToList(), n));

        private static byte[] FileTime(DateTime utc)
        {
            long ticks = Math.Max(0, DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks - 504911232000000000L);
            byte[] b = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, ticks);
            return b;
        }
    }

    // The time zone an item is written in, and the tools to move between its local clock and UTC.
    internal sealed class ItemZone
    {
        public ItemZone(string id, IcsTimeZone zone, TzRule rule)
        {
            Id = id;
            Zone = zone;
            Rule = rule;
        }

        public string Id { get; }

        public IcsTimeZone Zone { get; }

        public TzRule Rule { get; }

        public DateTime ToUtc(DateTime local) => Zone.ToUtc(local, out _);

        public DateTime ToLocal(DateTime utc) => Zone.ToLocal(utc);

        public static ItemZone Utc(TimeZoneResolver zones) =>
            new("UTC", zones.Resolve("UTC") ?? throw new InvalidOperationException("UTC zone missing"), new TzRule(0x0003, 0, 0, 0, 0, default, default));

        // The rule is read off the zone in the year of the item: offsets by hour, then the two changes (MS-OXOCAL 2.2.1.39 describes the result).
        public static ItemZone From(string id, IcsTimeZone zone, int year)
        {
            DateTime t = new(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime end = t.AddYears(1);
            TimeSpan prev = zone.GetUtcOffset(t);
            TimeSpan lo = prev;
            TimeSpan hi = prev;
            List<(DateTime At, TimeSpan From, TimeSpan To)> changes = [];
            for (DateTime x = t.AddHours(1); x < end; x = x.AddHours(1))
            {
                TimeSpan o = zone.GetUtcOffset(x);
                if (o != prev)
                {
                    changes.Add((x, prev, o));
                    prev = o;
                }

                lo = o < lo ? o : lo;
                hi = o > hi ? o : hi;
            }

            if (changes.Count != 2 || lo == hi)
            {
                TimeSpan fixedOffset = changes.Count == 0 ? lo : zone.GetUtcOffset(t);
                return new ItemZone(id, zone, new TzRule(0x0003, 0, (int)-fixedOffset.TotalMinutes, 0, 0, default, default));
            }

            SystemTime ToStd = Rel(changes.Find(c => c.To == lo).At, hi);   // standard time begins: the wall clock read daylight time just before
            SystemTime ToDst = Rel(changes.Find(c => c.To == hi).At, lo);
            return new ItemZone(id, zone, new TzRule(0x0003, 0, (int)-lo.TotalMinutes, 0, (int)-(hi - lo).TotalMinutes, ToStd, ToDst));
        }

        private static SystemTime Rel(DateTime utc, TimeSpan offsetBefore)
        {
            DateTime w = utc + offsetBefore;
            int dim = DateTime.DaysInMonth(w.Year, w.Month);
            int n = w.Day + 7 > dim ? 5 : ((w.Day - 1) / 7) + 1;
            return new SystemTime(0, (ushort)w.Month, (ushort)w.DayOfWeek, (ushort)n, (ushort)w.Hour, (ushort)w.Minute, 0, 0);
        }
    }

    private static ItemZone ZoneFor(IcsTime t, TimeZoneResolver zones, LossReport report, string what)
    {
        if (t.Kind == IcsTimeKind.Zoned && t.TzId is { } id)
        {
            if (zones.Resolve(id) is { } z)
            {
                return ItemZone.From(id, z, t.Value.Year);
            }

            report.Add("ics", "TZID", LossKind.Approximated, "UnknownTimeZone", $"The time zone '{id}' of {what} is not defined or known; UTC was used.");
        }
        else if (t.Kind == IcsTimeKind.Floating || t.Kind == IcsTimeKind.Date)
        {
            report.Add("ics", what, LossKind.Approximated, "FloatingTimeAssumedUtc", "A floating time or date has no zone in MS-OXOCAL; UTC was assumed.");
        }

        return ItemZone.Utc(zones);
    }

    private static DateTime LocalOf(IcsTime t, ItemZone zone) => t.Kind == IcsTimeKind.Utc ? zone.ToLocal(t.Value) : t.Value;

    private static DateTime UtcOf(IcsTime t, ItemZone zone) => t.Kind == IcsTimeKind.Utc ? t.Value : zone.ToUtc(t.Value);

    private static readonly byte[] GoidHeader = [0x04, 0x00, 0x00, 0x00, 0x82, 0x00, 0xE0, 0x00, 0x74, 0xC5, 0xB7, 0x10, 0x1A, 0x82, 0xE0, 0x08];

    // MS-OXCICAL 2.1.3.1.1.20.26: a UID that is the hex of a GlobalObjectId goes back to its bytes; any other UID is carried inside a "vCal-Uid" payload.
    internal static (byte[] Goid, byte[] Clean) GlobalId(string uid, DateTime? instance)
    {
        byte[] g;
        if (uid.Length >= 82 && uid.Length % 2 == 0 && uid.AsSpan(0, 32).Equals("040000008200E00074C5B7101A82E008", StringComparison.OrdinalIgnoreCase) && IsHex(uid))
        {
            g = System.Convert.FromHexString(uid);
        }
        else
        {
            byte[] u = Encoding.UTF8.GetBytes(uid);
            g = new byte[16 + 4 + 8 + 8 + 4 + 12 + u.Length];
            GoidHeader.CopyTo(g, 0);
            BinaryPrimitives.WriteInt32LittleEndian(g.AsSpan(36), 12 + u.Length);
            "vCal-Uid\u0001\0\0\0"u8.CopyTo(g.AsSpan(40));
            u.CopyTo(g, 52);
        }

        byte[] clean = (byte[])g.Clone();
        clean[16] = clean[17] = clean[18] = clean[19] = 0;
        byte[] withInstance = (byte[])clean.Clone();
        if (instance is { } d)
        {
            withInstance[16] = (byte)(d.Year >> 8);
            withInstance[17] = (byte)d.Year;
            withInstance[18] = (byte)d.Month;
            withInstance[19] = (byte)d.Day;
        }

        return (withInstance, clean);
    }

    private static bool IsHex(string s) => s.All(char.IsAsciiHexDigit);

    private static string? Mailto(string address) => address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? address[7..] : address.Contains('@', StringComparison.Ordinal) ? address : null;

    // ---- VEVENT ----

    private sealed class EventBuilder(IcsComponent ev, List<IcsComponent> overrides, TimeZoneResolver zones, string? method, LossReport report)
    {
        private readonly Props _p = new();
        private ItemZone _zone = ItemZone.Utc(zones);
        private bool _allDay;
        private static readonly HashSet<string> Consumed = new(StringComparer.OrdinalIgnoreCase)
        {
            "UID", "DTSTART", "DTEND", "DURATION", "RRULE", "EXDATE", "RDATE", "RECURRENCE-ID", "SUMMARY", "DESCRIPTION", "LOCATION", "ORGANIZER", "ATTENDEE", "CLASS", "PRIORITY",
            "TRANSP", "STATUS", "SEQUENCE", "CATEGORIES", "CREATED", "LAST-MODIFIED", "X-MICROSOFT-CDO-BUSYSTATUS", "X-MICROSOFT-CDO-INTENDEDSTATUS", "X-MICROSOFT-CDO-ALLDAYEVENT",
            "X-MICROSOFT-CDO-APPT-SEQUENCE", "X-ALT-DESC",
        };

        public MapiMessage Build()
        {
            IcsTime? start = ev.Start;
            IcsTime? end = ev.End;
            if (start is not { } s)
            {
                report.Add("ics", "DTSTART", LossKind.Dropped, nameof(CalendaringDefectKind.MissingTime), "The VEVENT has no DTSTART.");
                s = new IcsTime(new DateTime(1970, 1, 1), IcsTimeKind.Utc);
            }

            _allDay = s.IsDate || (string.Equals(ev.GetText("X-MICROSOFT-CDO-ALLDAYEVENT"), "TRUE", StringComparison.OrdinalIgnoreCase) && s.Kind == IcsTimeKind.Floating && s.Value.TimeOfDay == TimeSpan.Zero);
            _zone = ZoneFor(s, zones, report, "DTSTART");
            DateTime startLocal = LocalOf(s, _zone);
            DateTime endLocal;
            if (end is { } en)
            {
                endLocal = LocalOf(en, _zone);
            }
            else if (ev.Duration is { } du)
            {
                endLocal = startLocal + du.ToTimeSpan();
            }
            else
            {
                endLocal = s.IsDate ? startLocal.AddDays(1) : startLocal;
            }

            string cls = method switch
            {
                "REQUEST" => "IPM.Schedule.Meeting.Request",
                "CANCEL" => "IPM.Schedule.Meeting.Canceled",
                "REPLY" => (ev.Attendees.Count > 0 ? ev.Attendees[0] : null)?.ParticipationStatus?.ToUpperInvariant() switch { "ACCEPTED" => "IPM.Schedule.Meeting.Resp.Pos", "TENTATIVE" => "IPM.Schedule.Meeting.Resp.Tent", _ => "IPM.Schedule.Meeting.Resp.Neg" },
                _ => "IPM.Appointment",
            };
            if (method is not (null or "PUBLISH" or "REQUEST" or "CANCEL" or "REPLY"))
            {
                report.Add("ics", "METHOD", LossKind.Approximated, "MethodNotMapped", $"METHOD:{method} has no Outlook message class; an appointment was made.");
            }

            _p.Str(MapiPid.MessageClass, cls);
            if (ev.Summary is { } sum)
            {
                _p.Str(MapiPid.Subject, sum);
                _p.Str(MapiPid.NormalizedSubject, sum);
            }

            _p.NBool(OutlookNames.SubType, _allDay);
            if (ev.Location is { } loc)
            {
                _p.NStr(OutlookNames.Location, loc);
            }

            if (ev.Description is { } desc)
            {
                _p.Str(MapiPid.Body, desc);
            }

            if (ev.Component.FindProperties("X-ALT-DESC").FirstOrDefault(p => string.Equals(p.GetParameter("FMTTYPE"), "text/html", StringComparison.OrdinalIgnoreCase)) is { } alt)
            {
                _p.Bin(MapiPid.Html, Encoding.UTF8.GetBytes(alt.GetText()));
                _p.Int(0x3FDE, 65001);
            }

            Recurrence(startLocal, endLocal, s);
            Meta();
            People();
            Alarm();
            Extras();
            return _p.Message;
        }

        private void Times(DateTime startLocal, DateTime endLocal)
        {
            DateTime su = _allDay ? startLocal : _zone.ToUtc(startLocal);
            DateTime eu = _allDay ? endLocal : _zone.ToUtc(endLocal);
            _p.NTime(OutlookNames.StartWhole, su);
            _p.NTime(OutlookNames.EndWhole, eu);
            _p.NInt(OutlookNames.Duration, (int)(endLocal - startLocal).TotalMinutes);
            _p.NTime(OutlookNames.CommonStart, su);
            _p.NTime(OutlookNames.CommonEnd, eu);
            _p.Time(0x0060, su);
            _p.Time(0x0061, eu);
        }

        private void Recurrence(DateTime startLocal, DateTime endLocal, IcsTime s)
        {
            IReadOnlyList<RecurrenceRule> rules = ev.RecurrenceRules;
            RecurrencePattern? pattern = null;
            if (rules.Count > 1)
            {
                report.Add("ics", "RRULE", LossKind.Dropped, "MultipleRRules", "More than one RRULE has no Outlook form; none was mapped.");
            }
            else if (rules.Count == 1)
            {
                pattern = MapRule(rules[0], startLocal, out string? why);
                if (pattern is null)
                {
                    report.Add("ics", "RRULE", LossKind.Dropped, "RecurrenceNotMapped", why ?? "The rule does not fit an Outlook recurrence pattern (MS-OXCICAL 2.1.3.2.2).");
                }
            }

            if (pattern is null)
            {
                Times(startLocal, endLocal);
                if (ev.Component.FindProperties("RDATE").Any() || ev.Component.FindProperties("EXDATE").Any())
                {
                    report.Add("ics", ev.Component.FindProperties("RDATE").Any() ? "RDATE" : "EXDATE", LossKind.Dropped, "DatesWithoutRecurrence", "RDATE and EXDATE only apply to a mapped recurring series.");
                }

                if (HasZone(s))
                {
                    ZoneProps(false);
                }

                foreach (IcsComponent o in overrides)
                {
                    report.Add("ics", "VEVENT", LossKind.Dropped, "OverrideWithoutSeries", "An override with RECURRENCE-ID needs a mapped recurring series; it was not converted.");
                }

                return;
            }

            // MS-OXOCAL 2.2.1.44: the dates of the series are local calendar days in the zone of PidLidTimeZoneStruct.
            pattern.StartDate = OutlookMinutes.FromDateTime(startLocal.Date);
            pattern.FirstDateTime = OutlookRecurrence.FirstDateTimeFor(pattern, startLocal.Date);
            Ends(pattern, rules[0]);
            if (OutlookRecurrence.FirstDay(pattern) is { } first && first != startLocal.Date)
            {
                report.Add("ics", "DTSTART", LossKind.Approximated, nameof(CalendaringDefectKind.AmbiguousFirstInstance), "DTSTART is not a day the rule allows; Outlook starts the series at the first allowed day.");
                pattern.StartDate = OutlookMinutes.FromDateTime(first);
                startLocal = first.Add(startLocal.TimeOfDay);
                endLocal = first.Add(endLocal.TimeOfDay).Add(endLocal.Date - startLocal.Date) ;
                Ends(pattern, rules[0]);
            }

            AppointmentRecurrencePattern r = new() { Pattern = pattern, StartTimeOffset = (uint)startLocal.TimeOfDay.TotalMinutes };
            r.EndTimeOffset = r.StartTimeOffset + (uint)Math.Max(0, (endLocal - startLocal).TotalMinutes);
            Dates(r, startLocal);
            Times(startLocal, endLocal);
            _p.NBool(OutlookNames.Recurring, true);
            _p.NBool(OutlookNames.IsRecurring, true);
            _p.NInt(new MapiNamedProperty(MapiPropertySets.Appointment, 0x8231), pattern.RecurFrequency switch { 0x200A => 1, 0x200B => 2, 0x200C => 3, _ => 4 });
            _p.NTime(OutlookNames.ClipStart, OutlookMinutes.ToDateTime(pattern.StartDate));
            _p.NTime(OutlookNames.ClipEnd, OutlookMinutes.ToDateTime(pattern.EndDate));
            ZoneProps(true);
            Exceptions(r, startLocal, endLocal);
            _p.NBin(OutlookNames.AppointmentRecur, r.ToArray());
        }

        private static bool HasZone(IcsTime s) => s.Kind == IcsTimeKind.Zoned;

        private void ZoneProps(bool recurring)
        {
            TzRule rule = _zone.Rule;
            _p.NBin(OutlookNames.TimeZoneStruct, TimeZoneStruct.ToArray(rule));
            _p.NStr(OutlookNames.TimeZoneDescription, _zone.Id);
            TimeZoneDefinition recur = new(_zone.Id, [rule with { Flags = 0x0003 }]);
            TimeZoneDefinition display = new(_zone.Id, [rule with { Flags = 0x0002 }]);
            if (recurring)
            {
                _p.NBin(OutlookNames.TimeZoneDefinitionRecur, recur.ToArray());
            }

            _p.NBin(OutlookNames.TimeZoneDefinitionStartDisplay, display.ToArray());
            _p.NBin(OutlookNames.TimeZoneDefinitionEndDisplay, display.ToArray());
        }

        private void Ends(RecurrencePattern p, RecurrenceRule rule)
        {
            if (rule.Count is { } c)
            {
                p.EndType = RecurrencePattern.EndAfterCount;
                p.OccurrenceCount = (uint)Math.Max(1, c);
                p.EndDate = OutlookMinutes.FromDateTime(OutlookRecurrence.Days(p).LastOrDefault(OutlookMinutes.ToDateTime(p.StartDate)));
            }
            else if (rule.Until is { } u)
            {
                DateTime until = u.Kind == IcsTimeKind.Utc ? _zone.ToLocal(u.Value) : u.Value;
                p.EndType = RecurrencePattern.EndAfterDate;
                p.EndDate = OutlookMinutes.FromDateTime(until.Date);
                List<DateTime> days = OutlookRecurrence.Days(p).ToList();
                p.OccurrenceCount = (uint)days.Count;
                p.EndDate = days.Count > 0 ? OutlookMinutes.FromDateTime(days[^1]) : p.StartDate;
            }
            else
            {
                p.EndType = RecurrencePattern.EndNever;
                p.OccurrenceCount = 10;
                p.EndDate = OutlookMinutes.NeverEnds;
            }
        }

        // The templates of MS-OXCICAL 2.1.3.2.2; a rule outside them has no Outlook form.
        private static RecurrencePattern? MapRule(RecurrenceRule r, DateTime start, out string? why)
        {
            why = null;
            if (r.BySecond.Count > 0 || r.ByYearDay.Count > 0 || r.ByWeekNo.Count > 0 || r.RScale is not null || r.ByMinute.Count > 1 || r.ByHour.Count > 1 || r.OtherParts.Count > 0)
            {
                why = "The rule uses parts outside the MS-OXCICAL templates.";
                return null;
            }

            RecurrencePattern p = new() { WriterVersion = 0x3004, ReaderVersion = 0x3004, FirstDayOfWeek = r.WeekStart is { } w ? (uint)w : 0 };
            int iv = Math.Max(1, r.Interval);
            switch (r.Frequency)
            {
                case Calendar.Ics.RecurrenceFrequency.Daily when r.ByDay.Count + r.ByMonthDay.Count + r.ByMonth.Count + r.BySetPos.Count == 0 && iv <= 999:
                    p.RecurFrequency = 0x200A;
                    p.PatternType = 0;
                    p.Period = (uint)iv * 1440;
                    return p;
                case Calendar.Ics.RecurrenceFrequency.Weekly when r.ByMonthDay.Count + r.ByMonth.Count + r.BySetPos.Count == 0 && iv <= 99 && r.ByDay.All(d => d.Ordinal is null):
                    p.RecurFrequency = 0x200B;
                    p.PatternType = 1;
                    p.Period = (uint)iv;
                    p.WeekdayMask = r.ByDay.Count == 0 ? 1u << (int)start.DayOfWeek : r.ByDay.Aggregate(0u, (m, d) => m | (1u << (int)d.Day));
                    return p;
                case Calendar.Ics.RecurrenceFrequency.Monthly when r.ByMonth.Count == 0 && iv <= 99:
                    p.RecurFrequency = 0x200C;
                    p.Period = (uint)iv;
                    return MonthlyOrYearly(r, p, start, ref why);
                case Calendar.Ics.RecurrenceFrequency.Yearly when r.ByMonth.Count <= 1 && iv <= 8:
                    p.RecurFrequency = 0x200D;
                    p.Period = 12u * (uint)iv;
                    int month = r.ByMonth.Count == 1 ? r.ByMonth[0] : start.Month;
                    start = new DateTime(start.Year, month, Math.Min(start.Day, DateTime.DaysInMonth(start.Year, month)));
                    return MonthlyOrYearly(r, p, start, ref why);
                default:
                    why = "The frequency or its parts have no Outlook pattern.";
                    return null;
            }
        }

        private static RecurrencePattern? MonthlyOrYearly(RecurrenceRule r, RecurrencePattern p, DateTime start, ref string? why)
        {
            if (r.ByDay.Count > 0)
            {
                if (r.ByMonthDay.Count > 0)
                {
                    why = "BYDAY with BYMONTHDAY has no Outlook pattern.";
                    return null;
                }

                p.PatternType = 3;
                p.WeekdayMask = r.ByDay.Aggregate(0u, (m, d) => m | (1u << (int)d.Day));
                int? n = r.ByDay.Count == 1 && r.ByDay[0].Ordinal is { } o ? o : r.BySetPos.Count == 1 ? r.BySetPos[0] : null;
                if (n is null || n == 0 || n > 4 || n < -1)
                {
                    why = "The weekday position is outside first to fourth and last.";
                    return null;
                }

                p.Nth = n == -1 ? 5u : (uint)n;
                return p;
            }

            if (r.ByMonthDay.Count > 1)
            {
                why = "Several BYMONTHDAY values have no Outlook pattern.";
                return null;
            }

            p.PatternType = 2;
            int day = r.ByMonthDay.Count == 1 ? r.ByMonthDay[0] : start.Day;
            if (day < -1 || day == 0 || day > 31)
            {
                why = "BYMONTHDAY is outside 1 to 31 and -1.";
                return null;
            }

            p.DayOfMonth = day == -1 ? 31u : (uint)day;
            return p;
        }

        // EXDATE and RDATE and the overrides become DeletedInstanceDates, ModifiedInstanceDates and exception infos (MS-OXCICAL 2.1.3.1.1.20.13, .18, .20).
        private void Dates(AppointmentRecurrencePattern r, DateTime startLocal)
        {
            SortedSet<uint> deleted = [];
            foreach (ContentProperty p in ev.Component.FindProperties("EXDATE"))
            {
                foreach (string v in p.Value.Split(','))
                {
                    if (IcsTime.TryParse(v.Trim(), p.GetParameter("TZID"), out IcsTime t))
                    {
                        DateTime d = LocalOf(t, _zone).Date;
                        if (OutlookRecurrence.IsValidDay(r.Pattern, d))
                        {
                            deleted.Add(OutlookMinutes.FromDateTime(d));
                        }
                        else
                        {
                            report.Add("ics", "EXDATE", LossKind.Dropped, "ExdateNotOnPattern", "An EXDATE names a day the series does not occupy; it was dropped (MS-OXCICAL 2.1.3.1.1.20.13).");
                        }
                    }
                }
            }

            if (ev.Component.FindProperties("RDATE").Any())
            {
                report.Add("ics", "RDATE", LossKind.Dropped, "RdateWithoutException", "An RDATE adds an instance that is not a moved one; Outlook can only hold moved instances (MS-OXCICAL 2.1.3.1.1.20.18).");
            }

            r.Pattern.DeletedInstanceDates.AddRange(deleted);
        }

        private void Exceptions(AppointmentRecurrencePattern r, DateTime startLocal, DateTime endLocal)
        {
            List<(IcsComponent O, DateTime Orig, DateTime S, DateTime E)> list = [];
            foreach (IcsComponent o in overrides)
            {
                if (o.RecurrenceId is not { } rid || o.Start is not { } os)
                {
                    report.Add("ics", "VEVENT", LossKind.Dropped, "OverrideWithoutTimes", "An override without RECURRENCE-ID or DTSTART was not converted.");
                    continue;
                }

                DateTime orig = LocalOf(rid, _zone);
                DateTime s = LocalOf(os, _zone);
                DateTime e = o.End is { } oe ? LocalOf(oe, _zone) : o.Duration is { } od ? s + od.ToTimeSpan() : s;
                if (!OutlookRecurrence.IsValidDay(r.Pattern, orig.Date))
                {
                    report.Add("ics", "RECURRENCE-ID", LossKind.Dropped, "OverrideNotOnPattern", "An override replaces a day the series does not occupy; it was not converted.");
                    continue;
                }

                list.Add((o, orig, s, e));
            }

            list.Sort((a, b) => a.S.CompareTo(b.S));
            foreach ((IcsComponent o, DateTime orig, DateTime s, DateTime e) in list)
            {
                uint origDay = OutlookMinutes.FromDateTime(orig.Date);
                if (!r.Pattern.DeletedInstanceDates.Contains(origDay))
                {
                    r.Pattern.DeletedInstanceDates.Add(origDay);
                }

                r.Pattern.ModifiedInstanceDates.Add(OutlookMinutes.FromDateTime(s.Date));
                ExceptionInfo info = new() { StartDateTime = OutlookMinutes.FromDateTime(s), EndDateTime = OutlookMinutes.FromDateTime(e), OriginalStartTime = OutlookMinutes.FromDateTime(orig) };
                if (o.Summary is { } subj)
                {
                    info.Overrides |= ExceptionOverrides.Subject;
                    info.Subject = subj;
                }

                if (o.Location is { } loc)
                {
                    info.Overrides |= ExceptionOverrides.Location;
                    info.Location = loc;
                }

                if (o.GetText("TRANSP") is { } tr)
                {
                    info.Overrides |= ExceptionOverrides.BusyStatus;
                    info.BusyStatus = string.Equals(tr, "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? 0u : 2u;
                }

                r.Exceptions.Add(info);
                _p.Message.AddAttachment(ExceptionAttachment(o, info, orig, s, e));
            }

            r.Pattern.DeletedInstanceDates.Sort();
            r.Pattern.ModifiedInstanceDates.Sort();
        }

        private MapiAttachment ExceptionAttachment(IcsComponent o, ExceptionInfo info, DateTime orig, DateTime s, DateTime e)
        {
            Props mp = new();
            mp.Str(MapiPid.MessageClass, OutlookAppointment.ExceptionMessageClass);
            if (info.Subject is { } subj)
            {
                mp.Str(MapiPid.Subject, subj);
                mp.Str(MapiPid.NormalizedSubject, subj);
            }

            if (info.Location is { } loc)
            {
                mp.NStr(OutlookNames.Location, loc);
            }

            mp.NTime(OutlookNames.StartWhole, _zone.ToUtc(s));
            mp.NTime(OutlookNames.EndWhole, _zone.ToUtc(e));
            mp.NTime(OutlookNames.ExceptionReplaceTime, _zone.ToUtc(orig));
            mp.NBool(OutlookNames.IsException, true);
            if (info.BusyStatus is { } b)
            {
                mp.NInt(OutlookNames.BusyStatus, (int)b);
            }

            if (o.Description is { } d)
            {
                mp.Str(MapiPid.Body, d);
            }

            MapiAttachment att = new(_p.Message);
            att.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.AttachMethod, MapiType.Integer32), BitConverter.GetBytes(5)));
            att.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.AttachmentFlags, MapiType.Integer32), BitConverter.GetBytes(2)));
            att.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x7FFE, MapiType.Boolean), [1]));
            att.Properties.Add(MapiProperty.CreateOwned(new MapiTag(OutlookNames.PidTagExceptionStartTime, MapiType.Time), TimeBytes(s)));
            att.Properties.Add(MapiProperty.CreateOwned(new MapiTag(OutlookNames.PidTagExceptionEndTime, MapiType.Time), TimeBytes(e)));
            att.Properties.Add(MapiProperty.CreateOwned(new MapiTag(OutlookNames.PidTagExceptionReplaceTime, MapiType.Time), TimeBytes(_zone.ToUtc(orig))));
            att.SetEmbeddedMessage(mp.Message);
            return att;
        }

        private static byte[] TimeBytes(DateTime utc)
        {
            byte[] b = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, Math.Max(0, DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks - 504911232000000000L));
            return b;
        }

        private void Meta()
        {
            string uid = ev.Uid ?? "";
            DateTime? inst = overrides.Count == 0 && ev.RecurrenceId is { } rid ? LocalOf(rid, _zone).Date : null;
            (byte[] goid, byte[] clean) = GlobalId(uid, inst);
            _p.NBin(OutlookNames.GlobalObjectId, goid);
            _p.NBin(OutlookNames.CleanGlobalObjectId, clean);
            if (ev.Sequence is { } seq)
            {
                _p.NInt(OutlookNames.AppointmentSequence, seq);
            }

            int? sens = ev.Classification?.ToUpperInvariant() switch { "PUBLIC" => 0, "PRIVATE" => 2, "CONFIDENTIAL" => 3, "X-PERSONAL" => 1, _ => null };
            if (sens is { } sv)
            {
                _p.Int(MapiPid.Sensitivity, sv);
            }
            else if (ev.Classification is not null)
            {
                report.Add("ics", "CLASS", LossKind.Dropped, "ClassNotMapped", $"CLASS:{ev.Classification} is not one of PUBLIC, PRIVATE, CONFIDENTIAL.");
            }

            if (ev.Priority is { } pr && pr != 0)
            {
                _p.Int(MapiPid.Importance, pr <= 4 ? 2 : pr == 5 ? 1 : 0);
            }

            string? busy = ev.GetText("X-MICROSOFT-CDO-BUSYSTATUS")?.ToUpperInvariant();
            int? bs = busy switch { "FREE" => 0, "TENTATIVE" => 1, "BUSY" => 2, "OOF" => 3, "WORKINGELSEWHERE" => 4, _ => null };
            bs ??= ev.Transparency?.ToUpperInvariant() switch { "TRANSPARENT" => 0, "OPAQUE" => 2, _ => null };
            bs ??= ev.Status?.ToUpperInvariant() switch { "CANCELLED" => 0, "TENTATIVE" => 1, "CONFIRMED" => 2, _ => null };
            _p.NInt(OutlookNames.BusyStatus, bs ?? 2);
            if (ev.GetText("X-MICROSOFT-CDO-INTENDEDSTATUS")?.ToUpperInvariant() is { } ints)
            {
                _p.NInt(OutlookNames.IntendedBusyStatus, ints switch { "FREE" => 0, "TENTATIVE" => 1, "OOF" => 3, "WORKINGELSEWHERE" => 4, _ => 2 });
            }

            if (ev.Categories.Count > 0)
            {
                _p.NStrings(MapiToIcsConverter_Keywords, ev.Categories);
            }

            if (ev.Created is { } cr)
            {
                _p.Time(MapiPid.CreationTime, UtcOf(cr, _zone));
            }

            if (ev.LastModified is { } lm)
            {
                _p.Time(MapiPid.LastModificationTime, UtcOf(lm, _zone));
            }
            else if (ev.Stamp is { } st)
            {
                _p.Time(MapiPid.LastModificationTime, UtcOf(st, _zone));
                report.Add("ics", "DTSTAMP", LossKind.Approximated, "DtstampAsLastModified", "DTSTAMP stands in for the last-modified time.");
            }
        }

        private static readonly MapiNamedProperty MapiToIcsConverter_Keywords = new(MapiPropertySets.PublicStrings, "Keywords");

        private void People()
        {
            IcsCalAddress? org = ev.Organizer;
            bool meeting = method is "REQUEST" or "CANCEL" or "REPLY" || ev.Attendees.Count > 0;
            int state = (meeting ? 1 : 0) | (method is "REQUEST" or "CANCEL" ? 2 : 0) | (method == "CANCEL" || string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase) ? 4 : 0);
            _p.NInt(OutlookNames.StateFlags, state);
            if (org is not null && Mailto(org.Address) is { } oa)
            {
                _p.Str(MapiPid.SentRepresentingName, org.CommonName ?? oa);
                _p.Str(MapiPid.SentRepresentingAddressType, "SMTP");
                _p.Str(MapiPid.SentRepresentingEmailAddress, oa);
                _p.Str(MapiPid.SentRepresentingSmtpAddress, oa);
            }
            else if (org is not null)
            {
                report.Add("ics", "ORGANIZER", LossKind.Dropped, "OrganizerNotMailto", "The ORGANIZER is not a mailto address.");
            }

            bool rsvp = ev.Attendees.Any(a => a.Rsvp == true) || (method is null or "PUBLISH" && !meeting);
            if (meeting)
            {
                _p.Message.Properties.Add(MapiProperty.CreateOwned(new MapiTag(OutlookNames.PidTagResponseRequested, MapiType.Boolean), [rsvp ? (byte)1 : (byte)0]));
                _p.Message.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x0C17, MapiType.Boolean), [rsvp ? (byte)1 : (byte)0]));
                _p.NInt(OutlookNames.ResponseStatus, method == "REQUEST" ? 5 : method == "REPLY" ? 0 : 1);
            }

            int row = 0;
            if (meeting && org is not null && Mailto(org.Address) is { } oaddr)
            {
                _p.Message.AddRecipient(Recipient(org.CommonName ?? oaddr, oaddr, 1, 3, 0, null, row++));
            }

            foreach (IcsCalAddress a in ev.Attendees)
            {
                string? addr = Mailto(a.Address);
                string name = a.CommonName ?? addr ?? "";
                if (addr is null || a.Address.StartsWith("invalid:nomail", StringComparison.OrdinalIgnoreCase))
                {
                    report.Add("ics", "ATTENDEE", LossKind.Approximated, "AttendeeWithoutAddress", "An attendee without a mailto address is kept by name only.");
                    addr = null;
                }

                bool resource = string.Equals(a.CalendarUserType, "RESOURCE", StringComparison.OrdinalIgnoreCase) || string.Equals(a.CalendarUserType, "ROOM", StringComparison.OrdinalIgnoreCase);
                int type = resource || string.Equals(a.Role, "NON-PARTICIPANT", StringComparison.OrdinalIgnoreCase) ? 3 : string.Equals(a.Role, "OPT-PARTICIPANT", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                int track = a.ParticipationStatus?.ToUpperInvariant() switch { "ACCEPTED" => 3, "DECLINED" => 4, "TENTATIVE" => 2, _ => 0 };
                string? rt = a.Property.GetParameter("X-MS-OLK-RESPTIME");
                DateTime? respTime = rt is not null && IcsTime.TryParse(rt, null, out IcsTime rtt) ? rtt.Value : null;
                foreach (ContentParameter prm in a.Property.Parameters)
                {
                    if (!prm.IsNamed("CN") && !prm.IsNamed("ROLE") && !prm.IsNamed("PARTSTAT") && !prm.IsNamed("RSVP") && !prm.IsNamed("CUTYPE") && !prm.IsNamed("X-MS-OLK-RESPTIME"))
                    {
                        report.Add("ics", "ATTENDEE;" + prm.Name, LossKind.Dropped, "ParameterNotMapped", "MS-OXCICAL maps no recipient field from this ATTENDEE parameter.");
                    }
                }

                _p.Message.AddRecipient(Recipient(name, addr, type, 1, track, respTime, row++));
            }
        }

        private static MapiRecipient Recipient(string name, string? addr, int type, int flags, int track, DateTime? respTime, int row)
        {
            MapiRecipient r = new(null);
            Props rp = new();
            rp.Int(MapiPid.RecipientType, type);
            rp.Str(MapiPid.DisplayName, name);
            if (addr is not null)
            {
                rp.Str(MapiPid.AddressType, "SMTP");
                rp.Str(MapiPid.EmailAddress, addr);
                rp.Str(MapiPid.SmtpAddress, addr);
            }

            rp.Int(MapiPid.RecipientFlags, flags);
            rp.Int(MapiPid.RecipientTrackStatus, track);
            if (respTime is { } t)
            {
                rp.Time(MapiPid.RecipientTrackStatusTime, t);
            }

            rp.Int(MapiPid.Rowid, row);
            foreach (MapiProperty p in rp.Message.Properties)
            {
                r.Properties.Add(p);
            }

            return r;
        }

        private void Alarm()
        {
            IcsComponent? al = ev.Alarms.FirstOrDefault(a => a.Trigger is not null);
            if (al?.Trigger is { } t)
            {
                int minutes = t.Offset is { } off ? (int)Math.Abs(off.ToTimeSpan().TotalMinutes) : 15;
                if (t.Offset is { Negative: false })
                {
                    report.Add("ics", "VALARM", LossKind.Approximated, "AlarmAfterStart", "An alarm after the start is stored as a reminder before it (Outlook reminders are only 'before').");
                }

                if (t.RelatedToEnd)
                {
                    report.Add("ics", "VALARM", LossKind.Approximated, "AlarmRelatedToEnd", "An alarm related to the end is stored relative to the start.");
                }

                _p.NBool(OutlookNames.ReminderSet, true);
                _p.NInt(OutlookNames.ReminderDelta, minutes);
            }
            else if (ev.Alarms.Any())
            {
                report.Add("ics", "VALARM", LossKind.Dropped, "AlarmWithoutTrigger", "A VALARM without a usable TRIGGER was not converted.");
            }

            if (ev.Alarms.Count() > 1)
            {
                report.Add("ics", "VALARM", LossKind.Dropped, "AdditionalAlarms", "Outlook keeps one reminder; the first alarm was converted.");
            }
        }

        // Every property not converted is reported by name; nothing is dropped silently.
        private void Extras()
        {
            foreach (ContentProperty p in ev.Component.Properties)
            {
                if (Consumed.Contains(p.Name))
                {
                    if (p.IsNamed("DTSTAMP"))
                    {
                        continue;
                    }

                    continue;
                }

                if (p.IsNamed("DTSTAMP"))
                {
                    continue;
                }

                report.Add("ics", p.Name, LossKind.Dropped, "NoMapiMapping", "MS-OXCICAL maps no property from this iCalendar field.");
            }

            foreach (IcsComponent child in ev.Children.Where(c => c.Name != "VALARM"))
            {
                report.Add("ics", child.Name, LossKind.Dropped, "NoMapiMapping", "MS-OXCICAL maps no property from this sub-component.");
            }
        }
    }

    // ---- VTODO ----

    private sealed class TodoBuilder(IcsComponent todo, TimeZoneResolver zones, LossReport report)
    {
        public MapiMessage Build()
        {
            Props p = new();
            p.Str(MapiPid.MessageClass, "IPM.Task");
            if (todo.Summary is { } s)
            {
                p.Str(MapiPid.Subject, s);
                p.Str(MapiPid.NormalizedSubject, s);
            }

            if (todo.Description is { } d)
            {
                p.Str(MapiPid.Body, d);
            }

            // MS-OXOTASK dates are calendar days stored as midnight.
            ItemZone z = ItemZone.Utc(zones);
            if (todo.Start is { } st)
            {
                p.NTime(OutlookNames.TaskStartDate, LocalOf(st, z).Date);
            }

            if (todo.Due is { } du)
            {
                p.NTime(OutlookNames.TaskDueDate, LocalOf(du, z).Date);
            }

            if (todo.Completed is { } co)
            {
                p.NTime(OutlookNames.TaskDateCompleted, UtcOf(co, z));
            }

            string? status = todo.Status?.ToUpperInvariant();
            int ts = status switch { "COMPLETED" => 2, "IN-PROCESS" => 1, "NEEDS-ACTION" => 0, "CANCELLED" => 4, _ => todo.Completed is not null ? 2 : 0 };
            if (status == "CANCELLED")
            {
                report.Add("ics", "STATUS", LossKind.Approximated, "TaskCancelledAsDeferred", "A cancelled VTODO is stored as deferred.");
            }

            p.NInt(OutlookNames.TaskStatus, ts);
            p.NBool(OutlookNames.TaskComplete, ts == 2);
            double pct = (todo.PercentComplete ?? (ts == 2 ? 100 : 0)) / 100.0;
            p.NDouble(OutlookNames.PercentComplete, Math.Clamp(pct, 0, 1));
            if (todo.Priority is { } pr && pr != 0)
            {
                p.Int(MapiPid.Importance, pr <= 4 ? 2 : pr == 5 ? 1 : 0);
            }

            int? sens = todo.Classification?.ToUpperInvariant() switch { "PUBLIC" => 0, "PRIVATE" => 2, "CONFIDENTIAL" => 3, "X-PERSONAL" => 1, _ => null };
            if (sens is { } sv)
            {
                p.Int(MapiPid.Sensitivity, sv);
            }

            if (todo.Uid is { } uid)
            {
                p.NBin(OutlookNames.TaskGlobalId, Encoding.UTF8.GetBytes(uid));
            }

            if (todo.RecurrenceRules.Count == 1 && todo.Start is { } rs)
            {
                RecurrencePattern? rp = MapSimple(todo.RecurrenceRules[0], rs.Value);
                if (rp is null)
                {
                    report.Add("ics", "RRULE", LossKind.Dropped, "RecurrenceNotMapped", "The rule does not fit an Outlook recurrence pattern.");
                }
                else
                {
                    p.NBin(OutlookNames.TaskRecurrence, rp.ToArray());
                    p.NBool(OutlookNames.TaskFRecurring, true);
                }
            }
            else if (todo.RecurrenceRules.Count > 0)
            {
                report.Add("ics", "RRULE", LossKind.Dropped, "RecurrenceNotMapped", "A task recurrence needs one rule and a DTSTART.");
            }

            HashSet<string> consumed = new(StringComparer.OrdinalIgnoreCase)
            {
                "UID", "DTSTAMP", "SUMMARY", "DESCRIPTION", "DTSTART", "DUE", "COMPLETED", "STATUS", "PERCENT-COMPLETE", "PRIORITY", "CLASS", "RRULE",
            };
            foreach (ContentProperty prop in todo.Component.Properties)
            {
                if (!consumed.Contains(prop.Name))
                {
                    report.Add("ics", prop.Name, LossKind.Dropped, "NoMapiMapping", "MS-OXOTASK has no property for this iCalendar field.");
                }
            }

            return p.Message;
        }

        private static RecurrencePattern? MapSimple(RecurrenceRule r, DateTime start)
        {
            int iv = Math.Max(1, r.Interval);
            if (r.Frequency == Calendar.Ics.RecurrenceFrequency.Daily && r.ByDay.Count + r.ByMonthDay.Count + r.ByMonth.Count == 0 && iv <= 999)
            {
                RecurrencePattern p = new() { RecurFrequency = 0x200A, PatternType = 0, Period = (uint)iv * 1440, StartDate = OutlookMinutes.FromDateTime(start.Date) };
                p.FirstDateTime = (uint)(((OutlookRecurrence.DayIndex(start) * 1440) % p.Period + p.Period) % p.Period);
                p.EndType = r.Count is { } c ? RecurrencePattern.EndAfterCount : RecurrencePattern.EndNever;
                p.OccurrenceCount = (uint)(r.Count ?? 10);
                p.EndDate = OutlookMinutes.NeverEnds;
                return r.Until is null ? p : null;
            }

            return null;
        }
    }
}
