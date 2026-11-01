using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
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
            foreach (IcsComponent extra in parts.Where(p => p != master && p.RecurrenceId is null))
            {
                report.Add("ics", "VEVENT", LossKind.Dropped, "DuplicateUidNotConverted", "A second VEVENT with the same UID and no readable RECURRENCE-ID is neither a series nor an override; it was not converted.");
            }

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
        public ItemZone(string id, IcsTimeZone zone, TzRule rule, IReadOnlyList<TzRule>? rules = null)
        {
            Id = id;
            Zone = zone;
            Rule = rule;
            Rules = rules ?? [rule];
        }

        /// <summary>How many years past a series' start its zone's rule changes are followed.</summary>
        private const int MaxRuleYears = 100;

        public string Id { get; }

        public IcsTimeZone Zone { get; }

        /// <summary>The rule in force when the item starts: PidLidTimeZoneStruct and the effective rule of the definitions.</summary>
        public TzRule Rule { get; }

        /// <summary>The rules of the definition (MS-OXOCAL 2.2.1.41), the first of them <see cref="Rule"/>, in ascending order of the year they take effect.</summary>
        public IReadOnlyList<TzRule> Rules { get; }

        public DateTime ToUtc(DateTime local) => Zone.ToUtc(local, out _);

        public DateTime ToLocal(DateTime utc) => Zone.ToLocal(utc);

        public static ItemZone Utc(TimeZoneResolver zones) =>
            new("UTC", zones.Resolve("UTC") ?? throw new InvalidOperationException("UTC zone missing"), new TzRule(0x0003, 0, 0, 0, 0, default, default));

        // The rule is read off the zone in the year of the item: offsets by hour, then the two changes (MS-OXOCAL 2.2.1.39 describes the result).
        public static ItemZone From(string id, IcsTimeZone zone, int year) => new(id, zone, RuleOf(zone, year));

        /// <summary>
        /// The same zone with a definition that covers every year up to <paramref name="lastYear"/>: a new rule wherever the zone's daylight saving
        /// rule or offset changes (the United States in 2007, say), so that the series converts to UTC as the iCalendar says in every year.
        /// </summary>
        public ItemZone Through(int startYear, int lastYear)
        {
            List<TzRule> rules = [Rule];
            lastYear = Math.Min(Math.Max(lastYear, startYear), Math.Min(startYear + MaxRuleYears, 9990));   // a hostile end date must not cost a scan of eight thousand years
            for (int y = startYear + 1; y <= lastYear && rules.Count < 1024; y++)
            {
                TzRule r = RuleOf(Zone, y);
                if (!r.SameAs(rules[^1]))
                {
                    rules.Add(r with { Flags = 0, Year = (ushort)y });
                }
            }

            return rules.Count == 1 ? this : new ItemZone(Id, Zone, Rule, rules);
        }

        private static TzRule RuleOf(IcsTimeZone zone, int year)
        {
            DateTime t = new(Math.Clamp(year, 1, 9998), 1, 1, 0, 0, 0, DateTimeKind.Utc);   // the year after must exist
            DateTime end = t.AddYears(1);
            TimeSpan prev = zone.GetUtcOffset(t);
            TimeSpan lo = prev;
            TimeSpan hi = prev;
            List<(DateTime At, TimeSpan From, TimeSpan To)> changes = [];
            // Day by day, then hour by hour inside the day on which the offset changed.
            for (DateTime day = t; day < end; day = day.AddDays(1))
            {
                DateTime next = day.AddDays(1) < end ? day.AddDays(1) : end;
                TimeSpan atNext = next == end ? zone.GetUtcOffset(end.AddTicks(-1)) : zone.GetUtcOffset(next);
                TimeSpan atDay = zone.GetUtcOffset(day);
                if (atNext == atDay && next != end)
                {
                    lo = atDay < lo ? atDay : lo;
                    hi = atDay > hi ? atDay : hi;
                    continue;
                }

                for (DateTime x = day.AddHours(1); x <= next && x < end; x = x.AddHours(1))
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
            }

            if (changes.Count != 2 || lo == hi)
            {
                TimeSpan fixedOffset = changes.Count == 0 ? lo : zone.GetUtcOffset(t);
                return new TzRule(0x0003, 0, (int)-fixedOffset.TotalMinutes, 0, 0, default, default);
            }

            SystemTime ToStd = Rel(changes.Find(c => c.To == lo).At, hi);   // standard time begins: the wall clock read daylight time just before
            SystemTime ToDst = Rel(changes.Find(c => c.To == hi).At, lo);
            return new TzRule(0x0003, 0, (int)-lo.TotalMinutes, 0, (int)-(hi - lo).TotalMinutes, ToStd, ToDst);
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
                endLocal = OutlookMinutes.Add(startLocal, du.ToTimeSpan());
            }
            else
            {
                endLocal = s.IsDate ? OutlookMinutes.AddDays(startLocal, 1) : startLocal;
            }

            string cls = method switch
            {
                "REQUEST" => "IPM.Schedule.Meeting.Request",
                "CANCEL" => "IPM.Schedule.Meeting.Canceled",
                // MS-OXCICAL 2.1.3.1.1.1: a COUNTER is always a tentative response that carries PidLidAppointmentCounterProposal.
                "COUNTER" => "IPM.Schedule.Meeting.Resp.Tent",
                "REPLY" => (ev.Attendees.Count > 0 ? ev.Attendees[0] : null)?.ParticipationStatus?.ToUpperInvariant() switch { "ACCEPTED" => "IPM.Schedule.Meeting.Resp.Pos", "TENTATIVE" => "IPM.Schedule.Meeting.Resp.Tent", _ => "IPM.Schedule.Meeting.Resp.Neg" },
                _ => "IPM.Appointment",
            };
            if (method is not (null or "PUBLISH" or "REQUEST" or "CANCEL" or "REPLY" or "COUNTER"))
            {
                report.Add("ics", "METHOD", LossKind.Approximated, "MethodNotMapped", $"METHOD:{method} has no Outlook message class; an appointment was made.");
            }

            _p.Str(MapiPid.MessageClass, cls);
            if (method == "COUNTER")
            {
                _p.NBool(OutlookNames.CounterProposal, true);
            }

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
            // MS-OXCICAL 2.1.3.2.1.3 and .4: BYHOUR and BYMINUTE are the time of day of every instance (StartTimeOffset). A DTSTART at another
            // time is not an instance of the series, which an Outlook pattern cannot say.
            if (rules[0].ByHour.Count == 1 || rules[0].ByMinute.Count == 1)
            {
                TimeSpan tod = new(rules[0].ByHour.Count == 1 ? rules[0].ByHour[0] : startLocal.Hour, rules[0].ByMinute.Count == 1 ? rules[0].ByMinute[0] : startLocal.Minute, 0);
                if (tod != new TimeSpan(startLocal.Hour, startLocal.Minute, 0))
                {
                    report.Add("ics", "DTSTART", LossKind.Approximated, nameof(CalendaringDefectKind.AmbiguousFirstInstance), "DTSTART is at another time than BYHOUR and BYMINUTE give; the series uses the rule's time (MS-OXCICAL 2.1.3.2.1.4).");
                    TimeSpan len = endLocal - startLocal;
                    startLocal = startLocal.Date + tod;
                    endLocal = startLocal + len;
                }
            }

            // MS-OXOCAL times in a series are minutes (StartTimeOffset, EndTimeOffset).
            if (startLocal.Second != 0 || endLocal.Second != 0)
            {
                report.Add("ics", "DTSTART", LossKind.Approximated, "SubMinuteTimeDropped", "A recurring series keeps its times in whole minutes (MS-OXOCAL 2.2.1.44.5); the seconds were dropped.");
                startLocal = new DateTime(startLocal.Year, startLocal.Month, startLocal.Day, startLocal.Hour, startLocal.Minute, 0);
                endLocal = new DateTime(endLocal.Year, endLocal.Month, endLocal.Day, endLocal.Hour, endLocal.Minute, 0);
            }

            pattern.StartDate = OutlookMinutes.FromDateTime(startLocal.Date);
            // A yearly series is anchored in the month of its rule, which BYMONTH may set apart from DTSTART's month.
            DateTime anchor = pattern.RecurFrequency == 0x200D ? new DateTime(startLocal.Year, rules[0].ByMonth.Count == 1 ? rules[0].ByMonth[0] : startLocal.Month, 1) : startLocal.Date;
            pattern.FirstDateTime = OutlookRecurrence.FirstDateTimeFor(pattern, anchor);
            // MS-OXCICAL 2.1.3.2.3: a day that a shorter month lacks is the month's last day in Outlook and no instance at all in RFC 5545.
            if (pattern.PatternType == 2 && rules[0].ByMonthDay is not [-1]
                && (pattern.RecurFrequency == 0x200C ? pattern.DayOfMonth >= 29 : pattern.DayOfMonth > DateTime.DaysInMonth(2001, anchor.Month)))
            {
                report.Add("ics", "RRULE", LossKind.Approximated, nameof(CalendaringDefectKind.AmbiguousShortMonth), "BYMONTHDAY 29 to 31: Outlook puts the instance on the last day of a shorter month, RFC 5545 skips that month (MS-OXCICAL 2.1.3.2.3).");
            }
            Ends(pattern, rules[0], startLocal.TimeOfDay);
            if (OutlookRecurrence.FirstDay(pattern) is { } first && first != startLocal.Date)
            {
                report.Add("ics", "DTSTART", LossKind.Approximated, nameof(CalendaringDefectKind.AmbiguousFirstInstance), "DTSTART is not a day the rule allows; Outlook starts the series at the first allowed day.");
                pattern.StartDate = OutlookMinutes.FromDateTime(first);
                TimeSpan length = endLocal - startLocal;
                startLocal = first.Add(startLocal.TimeOfDay);
                endLocal = startLocal + length;
                Ends(pattern, rules[0], startLocal.TimeOfDay);
            }

            AppointmentRecurrencePattern r = new() { Pattern = pattern, StartTimeOffset = (uint)startLocal.TimeOfDay.TotalMinutes };
            r.EndTimeOffset = r.StartTimeOffset + (uint)Math.Max(0, (endLocal - startLocal).TotalMinutes);
            Dates(r, startLocal);
            Times(startLocal, endLocal);
            _p.NBool(OutlookNames.Recurring, true);
            _p.NBool(OutlookNames.IsRecurring, true);
            _p.NInt(OutlookNames.RecurrenceType, pattern.RecurFrequency switch { 0x200A => 1, 0x200B => 2, 0x200C => 3, _ => 4 });
            _p.NTime(OutlookNames.ClipStart, OutlookMinutes.ToDateTime(pattern.StartDate));
            _p.NTime(OutlookNames.ClipEnd, OutlookMinutes.ToDateTime(pattern.EndDate));
            // A series that runs on past a change of the zone's rules needs each rule: through its last day (at most 100 years), or for 60 years when it never ends.
            _zone = _zone.Through(startLocal.Year, pattern.EndType == RecurrencePattern.EndNever ? startLocal.Year + 60 : OutlookMinutes.ToDateTime(pattern.EndDate).Year);
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
            // The series' definition holds a rule for every year the zone's rule changes in (MS-OXOCAL 2.2.1.41); the first one is the effective rule.
            TimeZoneDefinition recur = new(_zone.Id, _zone.Rules.Select((r, i) => r with { Flags = (ushort)(i == 0 ? 0x0003 : 0) }).ToList());
            TimeZoneDefinition display = new(_zone.Id, [rule with { Flags = 0x0002 }]);
            if (recurring)
            {
                _p.NBin(OutlookNames.TimeZoneDefinitionRecur, recur.ToArray());
            }

            _p.NBin(OutlookNames.TimeZoneDefinitionStartDisplay, display.ToArray());
            _p.NBin(OutlookNames.TimeZoneDefinitionEndDisplay, display.ToArray());
        }

        private void Ends(RecurrencePattern p, RecurrenceRule rule, TimeSpan timeOfDay)
        {
            if (rule.Count is { } c)
            {
                p.EndType = RecurrencePattern.EndAfterCount;
                p.OccurrenceCount = (uint)Math.Max(1, c);
                p.EndDate = OutlookMinutes.FromDateTime(OutlookRecurrence.Days(p).LastOrDefault(OutlookMinutes.ToDateTime(p.StartDate)));
            }
            else if (rule.Until is { } u)
            {
                // RFC 5545 3.3.10: UNTIL bounds the instance's start. A DATE value (a sloppy writer's) is taken to include its whole day.
                DateTime until = u.Kind == IcsTimeKind.Utc ? _zone.ToLocal(u.Value) : u.IsDate ? u.Value.Date.AddDays(1).AddTicks(-1) : u.Value;
                p.EndType = RecurrencePattern.EndAfterDate;
                p.EndDate = OutlookMinutes.FromDateTime(until.Date);
                List<DateTime> days = OutlookRecurrence.Days(p).ToList();
                // MS-OXCICAL 2.1.3.2.2.1: EndDate is the start date of the last instance, so a last day whose start is after UNTIL is not one.
                while (days.Count > 0 && days[^1] + timeOfDay > until)
                {
                    days.RemoveAt(days.Count - 1);
                }

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

        // The templates of MS-OXCICAL 2.1.3.2.2; a rule outside them has no Outlook form. Two rewrites that mean exactly the same thing are
        // accepted too: a daily rule that only filters weekdays (every day, INTERVAL=1) is the weekly template, and BYDAY=1MO (the ordinal
        // inside BYDAY) is BYDAY=MO;BYSETPOS=1.
        internal static RecurrencePattern? MapRule(RecurrenceRule r, DateTime start, out string? why)
        {
            why = null;
            if (r.Frequency is Calendar.Ics.RecurrenceFrequency.Hourly or Calendar.Ics.RecurrenceFrequency.Minutely or Calendar.Ics.RecurrenceFrequency.Secondly)
            {
                why = $"FREQ={r.Frequency.ToString().ToUpperInvariant()} repeats within a day; Outlook patterns step by days at the finest (MS-OXCICAL 2.1.3.2.1.1).";
                return null;
            }

            if (r.RScale is not null)
            {
                why = "RSCALE (RFC 7529) names another calendar; MS-OXCICAL maps it only through X-MICROSOFT-CALSCALE.";
                return null;
            }

            if (r.BySecond.Count > 0 || r.ByYearDay.Count > 0 || r.ByWeekNo.Count > 0 || r.OtherParts.Count > 0)
            {
                why = "BYSECOND, BYYEARDAY and BYWEEKNO are outside the MS-OXCICAL templates (2.1.3.2.2).";
                return null;
            }

            if (r.ByMinute.Count > 1 || r.ByHour.Count > 1)
            {
                why = "Several BYHOUR or BYMINUTE values give several instances a day, which an Outlook pattern cannot hold (MS-OXCICAL 2.1.3.2.1.3, .4).";
                return null;
            }

            RecurrencePattern p = new() { WriterVersion = 0x3004, ReaderVersion = 0x3004, FirstDayOfWeek = r.WeekStart is { } w ? (uint)w : 0 };
            int iv = Math.Max(1, r.Interval);
            bool plainDays = r.ByDay.Count > 0 && r.ByDay.All(d => d.Ordinal is null);
            switch (r.Frequency)
            {
                case Calendar.Ics.RecurrenceFrequency.Daily when r.ByDay.Count + r.ByMonthDay.Count + r.ByMonth.Count + r.BySetPos.Count == 0 && iv <= 999:
                    p.RecurFrequency = 0x200A;
                    p.PatternType = 0;
                    p.Period = (uint)iv * 1440;
                    return p;
                case Calendar.Ics.RecurrenceFrequency.Daily when plainDays && iv == 1 && r.ByMonthDay.Count + r.ByMonth.Count + r.BySetPos.Count == 0:
                    p.RecurFrequency = 0x200B;
                    p.PatternType = 1;
                    p.Period = 1;
                    p.WeekdayMask = r.ByDay.Aggregate(0u, (m, d) => m | (1u << (int)d.Day));
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
                // RFC 5545 3.3.10: without BYMONTH a yearly BYMONTHDAY applies to every month and a yearly BYDAY counts weekdays over the whole year.
                // Neither is "the month of DTSTART" (MS-OXCICAL 2.1.3.2.1.7 describes how Outlook reads the part when it is absent from its own output).
                case Calendar.Ics.RecurrenceFrequency.Yearly when r.ByMonth.Count == 0 && r.ByDay.Count > 0:
                    why = "A yearly rule with BYDAY and no BYMONTH counts weekdays across the whole year (RFC 5545 3.3.10); an Outlook pattern needs a month.";
                    return null;
                case Calendar.Ics.RecurrenceFrequency.Yearly when r.ByMonth.Count == 0 && r.ByMonthDay.Count > 0:
                    if (r.ByMonthDay.Count == 1 && iv == 1 && r.BySetPos.Count == 0)
                    {
                        // Every month on that day is the monthly template.
                        p.RecurFrequency = 0x200C;
                        p.Period = 1;
                        return MonthlyOrYearly(r, p, start, ref why);
                    }

                    why = "A yearly rule with BYMONTHDAY and no BYMONTH recurs in every month of every year in the interval (RFC 5545 3.3.10); only INTERVAL=1 with one day is the monthly template.";
                    return null;
                case Calendar.Ics.RecurrenceFrequency.Yearly when r.ByMonth.Count <= 1 && iv <= 8:
                    p.RecurFrequency = 0x200D;
                    p.Period = 12u * (uint)iv;
                    int month = r.ByMonth.Count == 1 ? r.ByMonth[0] : start.Month;
                    start = new DateTime(start.Year, month, Math.Min(start.Day, DateTime.DaysInMonth(start.Year, month)));
                    return MonthlyOrYearly(r, p, start, ref why);
                default:
                    why = r.Frequency switch
                    {
                        Calendar.Ics.RecurrenceFrequency.Daily => "A daily rule with BYMONTHDAY, BYMONTH or BYSETPOS, or with BYDAY and an INTERVAL above 1, has no Outlook pattern; the interval is outside 1 to 999 otherwise (MS-OXCICAL 2.1.3.2.2.1).",
                        Calendar.Ics.RecurrenceFrequency.Weekly => "A weekly rule with BYMONTHDAY, BYMONTH, BYSETPOS or ordinal weekdays has no Outlook pattern; the interval is outside 1 to 99 otherwise (MS-OXCICAL 2.1.3.2.2.2).",
                        Calendar.Ics.RecurrenceFrequency.Monthly => "A monthly rule with BYMONTH has no Outlook pattern; the interval is outside 1 to 99 otherwise (MS-OXCICAL 2.1.3.2.2.3, .4).",
                        _ => "A yearly rule with several BYMONTH values has no Outlook pattern; the interval is outside 1 to 8 otherwise (MS-OXCICAL 2.1.3.2.2.5, .6).",
                    };
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

            PairRdates(r, startLocal, deleted);
            r.Pattern.DeletedInstanceDates.AddRange(deleted);
        }

        private const int MaxPairedDates = 1000;

        // RDATEs that move an EXDATEd instance (start, end and the instance's original start, all local).
        private readonly List<(DateTime Orig, DateTime Start, DateTime End)> _moves = [];

        // MS-OXCICAL 2.1.3.1.1.20.18: an RDATE is only carried as the new place of an instance that an EXDATE removes. The moved instance must stay
        // between its neighbours, must not overlap them and must not share a calendar day with another instance. Whatever cannot be paired is reported.
        private void PairRdates(AppointmentRecurrencePattern r, DateTime startLocal, SortedSet<uint> deleted)
        {
            List<DateTime> rdates = [];
            foreach (ContentProperty p in ev.Component.FindProperties("RDATE"))
            {
                foreach (string v in p.Value.Split(','))
                {
                    if (v.Contains('/', StringComparison.Ordinal))
                    {
                        report.Add("ics", "RDATE", LossKind.Dropped, "RdatePeriodNotCarried", "An RDATE with a period has no Outlook form and is ignored (MS-OXCICAL 2.1.3.1.1.20.18).");
                    }
                    else if (IcsTime.TryParse(v.Trim(), p.GetParameter("TZID"), out IcsTime t))
                    {
                        rdates.Add(LocalOf(t, _zone));
                    }
                }
            }

            if (rdates.Count == 0)
            {
                return;
            }

            // Pairing looks at every EXDATE for every RDATE; a hostile file gets no more than this many of either.
            if (rdates.Count > MaxPairedDates || deleted.Count > MaxPairedDates)
            {
                report.Add("ics", "RDATE", LossKind.Dropped, "RdateWithoutException", $"More than {MaxPairedDates} RDATEs or EXDATEs; none were paired with a moved instance.");
                return;
            }

            TimeSpan offset = TimeSpan.FromMinutes(r.StartTimeOffset);
            TimeSpan length = TimeSpan.FromMinutes(r.EndTimeOffset - r.StartTimeOffset);
            List<DateTime> free = deleted.Select(d => OutlookMinutes.ToDateTime(d) + offset).ToList();   // EXDATEs still without an RDATE
            HashSet<DateTime> usedDays = [];
            bool Live(DateTime day) => day >= OutlookMinutes.ToDateTime(r.Pattern.StartDate) && OutlookRecurrence.IsValidDay(r.Pattern, day) && !deleted.Contains(OutlookMinutes.FromDateTime(day))
                && (r.Pattern.EndType == RecurrencePattern.EndNever || day <= OutlookMinutes.ToDateTime(r.Pattern.EndDate));
            foreach (DateTime rd in rdates.Distinct().Order())
            {
                if (Live(rd.Date) && rd - rd.Date == offset)
                {
                    continue;   // already an instance of the series: a repeated date, nothing to carry
                }

                DateTime? best = null;
                foreach (DateTime ex in free)
                {
                    DateTime prev = ex.Date, next = ex.Date;
                    for (int i = 0; i < 400 && !Live(prev = prev.AddDays(-1)) && prev.Year > 1601; i++)
                    {
                    }

                    for (int i = 0; i < 400 && !Live(next = next.AddDays(1)) && next.Year < 9000; i++)
                    {
                    }

                    DateTime prevStart = prev + offset, nextStart = next + offset;
                    bool ok = Live(prev) ? rd >= prevStart + length && rd.Date != prev : true;
                    ok &= !Live(next) || (rd + length <= nextStart && rd.Date != next);
                    ok &= !usedDays.Contains(rd.Date);
                    if (ok && (best is null || Math.Abs((rd - ex).Ticks) < Math.Abs((rd - best.Value).Ticks)))
                    {
                        best = ex;
                    }
                }

                if (best is not { } pair)
                {
                    report.Add("ics", "RDATE", LossKind.Dropped, "RdateWithoutException", "An RDATE that is not the new place of an EXDATEd instance cannot be held; Outlook has only moved instances (MS-OXCICAL 2.1.3.1.1.20.18).");
                    continue;
                }

                free.Remove(pair);
                usedDays.Add(rd.Date);
                _moves.Add((pair, rd, rd + length));
            }
        }

        private void Exceptions(AppointmentRecurrencePattern r, DateTime startLocal, DateTime endLocal)
        {
            List<(IcsComponent? O, DateTime Orig, DateTime S, DateTime E)> list = [];
            foreach ((DateTime mo, DateTime ms, DateTime me) in _moves)
            {
                list.Add((null, mo, ms, me));
            }

            foreach (IcsComponent o in overrides)
            {
                if (string.Equals(o.Component.FindProperty("RECURRENCE-ID")?.GetParameter("RANGE"), "THISANDFUTURE", StringComparison.OrdinalIgnoreCase))
                {
                    report.Add("ics", "RECURRENCE-ID", LossKind.Approximated, "RangeThisAndFutureNotCarried", "RANGE=THISANDFUTURE changes every later instance; an Outlook exception changes one, so only the named instance was changed.");
                }

                if (o.RecurrenceId is not { } rid || o.Start is not { } os)
                {
                    report.Add("ics", "VEVENT", LossKind.Dropped, "OverrideWithoutTimes", "An override without RECURRENCE-ID or DTSTART was not converted.");
                    continue;
                }

                DateTime orig = LocalOf(rid, _zone);
                DateTime s = LocalOf(os, _zone);
                DateTime e = o.End is { } oe ? LocalOf(oe, _zone) : o.Duration is { } od ? OutlookMinutes.Add(s, od.ToTimeSpan()) : s;
                if (!OutlookRecurrence.IsValidDay(r.Pattern, orig.Date))
                {
                    report.Add("ics", "RECURRENCE-ID", LossKind.Dropped, "OverrideNotOnPattern", "An override replaces a day the series does not occupy; it was not converted.");
                    continue;
                }

                list.Add((o, orig, s, e));
            }

            list.Sort((a, b) => a.S.CompareTo(b.S));
            foreach ((IcsComponent? o, DateTime orig, DateTime s, DateTime e) in list)
            {
                uint origDay = OutlookMinutes.FromDateTime(orig.Date);
                if (!r.Pattern.DeletedInstanceDates.Contains(origDay))
                {
                    r.Pattern.DeletedInstanceDates.Add(origDay);
                }

                r.Pattern.ModifiedInstanceDates.Add(OutlookMinutes.FromDateTime(s.Date));
                ExceptionInfo info = new() { StartDateTime = OutlookMinutes.FromDateTime(s), EndDateTime = OutlookMinutes.FromDateTime(e), OriginalStartTime = OutlookMinutes.FromDateTime(orig) };
                if (o?.Summary is { } subj)
                {
                    info.Overrides |= ExceptionOverrides.Subject;
                    info.Subject = subj;
                }

                if (o?.Location is { } loc)
                {
                    info.Overrides |= ExceptionOverrides.Location;
                    info.Location = loc;
                }

                if (o?.GetText("TRANSP") is { } tr)
                {
                    info.Overrides |= ExceptionOverrides.BusyStatus;
                    info.BusyStatus = string.Equals(tr, "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? 0u : 2u;
                }

                r.Exceptions.Add(info);
                if (o is not null)
                {
                    _p.Message.AddAttachment(ExceptionAttachment(o, info, orig, s, e));
                }
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
            // MS-OXCICAL 2.1.3.1.1.20.23: STATUS only stands in for a missing busy status and is never written back. A cancelled event is
            // kept by the cancelled bit of PidLidAppointmentStateFlags; CONFIRMED, TENTATIVE and any other value are lost with the export.
            if (ev.Status is { } statusText && !string.Equals(statusText.Trim(), "CANCELLED", StringComparison.OrdinalIgnoreCase))
            {
                report.Add("ics", "STATUS", LossKind.Dropped, "StatusNotExported", "MS-OXCICAL 2.1.3.1.1.20.23: STATUS (other than CANCELLED) only feeds the busy status on import and is not exported.");
            }
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
            // RFC 5546 3.2.3 and 3.2.7: in a REPLY or COUNTER the sender is the attendee who answers and the organizer is the addressee, which is
            // how Outlook holds a meeting response (the sender is the responder, the recipient the organizer).
            bool reply = method is "REPLY" or "COUNTER";
            IcsCalAddress? org = ev.Organizer;
            IcsCalAddress? sender = reply ? ev.Attendees.Count > 0 ? ev.Attendees[0] : null : org;
            bool meeting = method is "REQUEST" or "CANCEL" or "REPLY" or "COUNTER" || ev.Attendees.Count > 0;
            int state = (meeting ? 1 : 0) | (method is "REQUEST" or "CANCEL" ? 2 : 0) | (method == "CANCEL" || string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase) ? 4 : 0);
            _p.NInt(OutlookNames.StateFlags, state);
            if (sender is not null && Mailto(sender.Address) is { } oa)
            {
                _p.Str(MapiPid.SentRepresentingName, sender.CommonName ?? oa);
                _p.Str(MapiPid.SentRepresentingAddressType, "SMTP");
                _p.Str(MapiPid.SentRepresentingEmailAddress, oa);
                _p.Str(MapiPid.SentRepresentingSmtpAddress, oa);
            }
            else if (sender is not null)
            {
                report.Add("ics", reply ? "ATTENDEE" : "ORGANIZER", LossKind.Dropped, "OrganizerNotMailto", $"The {(reply ? "replying ATTENDEE" : "ORGANIZER")} is not a mailto address.");
            }

            // MS-OXCICAL 2.1.3.1.1.20.2.5: PidTagResponseRequested is one flag for the whole message. It is true when any ATTENDEE asks for a
            // response or the item is an appointment (IPM.Appointment), false otherwise; the export writes that one value on every ATTENDEE.
            bool appointmentClass = method is not ("REQUEST" or "CANCEL" or "REPLY" or "COUNTER");
            bool rsvp = ev.Attendees.Any(a => a.Rsvp == true) || appointmentClass;
            _p.Message.Properties.Add(MapiProperty.CreateOwned(new MapiTag(OutlookNames.PidTagResponseRequested, MapiType.Boolean), [rsvp ? (byte)1 : (byte)0]));
            _p.Message.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x0C17, MapiType.Boolean), [rsvp ? (byte)1 : (byte)0]));
            if (meeting)
            {
                _p.NInt(OutlookNames.ResponseStatus, method == "REQUEST" ? 5 : method is "REPLY" or "COUNTER" ? 0 : 1);
            }

            int row = 0;
            if (reply)
            {
                if (org is not null && Mailto(org.Address) is { } toAddr)
                {
                    _p.Message.AddRecipient(Recipient(org.CommonName ?? toAddr, toAddr, 1, 1, 0, null, row++));
                }
                else if (org is not null)
                {
                    report.Add("ics", "ORGANIZER", LossKind.Dropped, "OrganizerNotMailto", "The ORGANIZER is not a mailto address.");
                }

                if (sender is not null)
                {
                    // The reply's answer is the message class (2.1.3.1.1.1); the other ATTENDEE parameters have no place in a response.
                    string? psr = sender.ParticipationStatus?.ToUpperInvariant();
                    if (psr is not ("ACCEPTED" or "TENTATIVE" or "DECLINED") && method == "REPLY")
                    {
                        report.Add("ics", "ATTENDEE;PARTSTAT", LossKind.Approximated, "PartstatNotCarried", $"A reply with PARTSTAT={sender.ParticipationStatus ?? "(none)"} has no message class of its own; it is a decline (MS-OXCICAL 2.1.3.1.1.1).");
                    }

                    if (sender.Role is { } rr && !rr.Equals("REQ-PARTICIPANT", StringComparison.OrdinalIgnoreCase))
                    {
                        report.Add("ics", "ATTENDEE;ROLE", LossKind.Dropped, "AttendeeRoleNotCarried", "A response message has no recipient role for its sender.");
                    }

                    if (sender.CalendarUserType is { } cu && !cu.Equals("INDIVIDUAL", StringComparison.OrdinalIgnoreCase))
                    {
                        report.Add("ics", "ATTENDEE;CUTYPE", LossKind.Dropped, "AttendeeTypeApproximated", "A response message has no user type for its sender.");
                    }

                    if (sender.Rsvp == true)
                    {
                        report.Add("ics", "ATTENDEE;RSVP", LossKind.Dropped, "RsvpIsPerMessage", "A response message asks for no response.");
                    }
                }

                if (ev.Attendees.Count > 1)
                {
                    report.Add("ics", "ATTENDEE", LossKind.Dropped, "AdditionalAttendeesInReply", "A reply holds one ATTENDEE, the one who answers (MS-OXCICAL 2.1.3.1.1.1); the others were not converted.");
                }

                return;
            }

            if (meeting && org is not null && Mailto(org.Address) is { } oaddr)
            {
                _p.Message.AddRecipient(Recipient(org.CommonName ?? oaddr, oaddr, 1, 3, 0, null, row++));
            }

            bool rsvpReported = false;
            foreach (IcsCalAddress a in ev.Attendees)
            {
                string? addr = Mailto(a.Address);
                string name = a.CommonName ?? addr ?? "";
                if (addr is null || a.Address.StartsWith("invalid:nomail", StringComparison.OrdinalIgnoreCase))
                {
                    report.Add("ics", "ATTENDEE", LossKind.Approximated, "AttendeeWithoutAddress", "An attendee without a mailto address is kept by name only.");
                    addr = null;
                }

                string? cutype = a.CalendarUserType?.ToUpperInvariant();
                bool resource = cutype is "RESOURCE" or "ROOM";
                string? role = a.Role?.ToUpperInvariant();
                // MS-OXCICAL 2.1.3.1.1.20.2: RESOURCE, ROOM and NON-PARTICIPANT are type 3 (Bcc), OPT-PARTICIPANT type 2, everything else type 1.
                int type = resource || role == "NON-PARTICIPANT" ? 3 : role == "OPT-PARTICIPANT" ? 2 : 1;
                // The export gives ROLE back only as OPT-PARTICIPANT or NON-PARTICIPANT and CUTYPE only as RESOURCE (2.1.3.1.1.20.2.2, .4).
                if (role == "CHAIR")
                {
                    report.Add("ics", "ATTENDEE;ROLE", LossKind.Approximated, "AttendeeRoleNotCarried", "ROLE=CHAIR has no recipient form; the attendee is an ordinary required recipient (MS-OXCICAL 2.1.3.1.1.20.2.4).");
                }
                else if (role is not (null or "REQ-PARTICIPANT" or "OPT-PARTICIPANT" or "NON-PARTICIPANT"))
                {
                    report.Add("ics", "ATTENDEE;ROLE", LossKind.Approximated, "AttendeeRoleNotCarried", $"ROLE={a.Role} has no recipient form; the attendee is an ordinary required recipient.");
                }

                if (cutype == "ROOM")
                {
                    report.Add("ics", "ATTENDEE;CUTYPE", LossKind.Approximated, "AttendeeTypeApproximated", "CUTYPE=ROOM is carried as a resource (recipient type 3); the export writes CUTYPE=RESOURCE.");
                }
                else if (cutype is not (null or "INDIVIDUAL" or "RESOURCE"))
                {
                    report.Add("ics", "ATTENDEE;CUTYPE", LossKind.Dropped, "AttendeeTypeApproximated", $"CUTYPE={a.CalendarUserType} has no recipient form; the attendee is an individual (MS-OXCICAL 2.1.3.1.1.20.2.2).");
                }
                else if (role == "NON-PARTICIPANT" && !resource)
                {
                    report.Add("ics", "ATTENDEE;CUTYPE", LossKind.Approximated, "AttendeeTypeApproximated", "A non-participant is recipient type 3 (Bcc), which the export writes as CUTYPE=RESOURCE.");
                }

                if (!rsvpReported && (a.Rsvp == true) != rsvp)
                {
                    rsvpReported = true;
                    report.Add("ics", "ATTENDEE;RSVP", LossKind.Approximated, "RsvpIsPerMessage", "Outlook keeps one response-requested flag for the item; every ATTENDEE gets that value back (MS-OXCICAL 2.1.3.1.1.20.2.5).");
                }

                string? ps = a.ParticipationStatus?.ToUpperInvariant();
                if (ps is not (null or "NEEDS-ACTION" or "ACCEPTED" or "DECLINED" or "TENTATIVE"))
                {
                    report.Add("ics", "ATTENDEE;PARTSTAT", LossKind.Approximated, "PartstatNotCarried", $"PARTSTAT={a.ParticipationStatus} has no recipient track status; it is carried as no response (MS-OXCICAL 2.1.3.1.1.20.2).");
                }

                int track = ps switch { "ACCEPTED" => 3, "DECLINED" => 4, "TENTATIVE" => 2, _ => 0 };
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

            if (status is not (null or "COMPLETED" or "IN-PROCESS" or "NEEDS-ACTION" or "CANCELLED"))
            {
                report.Add("ics", "STATUS", LossKind.Approximated, "StatusNotCarried", $"STATUS:{todo.Status} is not a VTODO status (RFC 5545 3.8.1.11); the task is {(ts == 2 ? "complete" : "not started")}.");
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
                // MS-OXOTASK 2.2.2.2.32: the task's global ID is a 16-byte GUID. A UID we wrote ourselves (32 hex digits, "@onemail") maps back to
                // the same GUID; any other UID has no GUID form, so a GUID is derived from it and the replacement is reported.
                string hex = uid.EndsWith("@onemail", StringComparison.Ordinal) ? uid[..^8] : uid;
                if (hex.Length == 32 && hex.All(char.IsAsciiHexDigit))
                {
                    p.NBin(OutlookNames.TaskGlobalId, System.Convert.FromHexString(hex));
                }
                else
                {
                    p.NBin(OutlookNames.TaskGlobalId, SHA256.HashData(Encoding.UTF8.GetBytes(uid)).AsSpan(0, 16).ToArray());
                    report.Add("ics", "UID", LossKind.Approximated, "UidReplacedByGuid", "A task's UID is a 16-byte GUID in Outlook; the UID text was replaced by a GUID derived from it.");
                }
            }

            if (todo.RecurrenceRules.Count == 1 && todo.Start is { } rs)
            {
                RecurrencePattern? rp = MapSimple(todo.RecurrenceRules[0], rs.Value);
                if (rp is not null)
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

        // A task's series is days only (MS-OXOTASK): the same templates as an appointment's, dates without a zone.
        private RecurrencePattern? MapSimple(RecurrenceRule r, DateTime start)
        {
            RecurrencePattern? p = EventBuilder.MapRule(r, start, out string? why);
            if (p is null)
            {
                report.Add("ics", "RRULE", LossKind.Dropped, "RecurrenceNotMapped", why ?? "The rule does not fit an Outlook recurrence pattern.");
                return null;
            }

            DateTime day = start.Date;
            p.StartDate = OutlookMinutes.FromDateTime(day);
            DateTime anchor = p.RecurFrequency == 0x200D ? new DateTime(day.Year, r.ByMonth.Count == 1 ? r.ByMonth[0] : day.Month, 1) : day;
            p.FirstDateTime = OutlookRecurrence.FirstDateTimeFor(p, anchor);
            if (r.Count is { } c)
            {
                p.EndType = RecurrencePattern.EndAfterCount;
                p.OccurrenceCount = (uint)Math.Max(1, c);
                p.EndDate = OutlookMinutes.FromDateTime(OutlookRecurrence.Days(p).LastOrDefault(day));
            }
            else if (r.Until is { } u)
            {
                p.EndType = RecurrencePattern.EndAfterDate;
                p.EndDate = OutlookMinutes.FromDateTime(u.Value.Date);
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

            if (OutlookRecurrence.FirstDay(p) is { } first && first != day)
            {
                report.Add("ics", "DTSTART", LossKind.Approximated, nameof(CalendaringDefectKind.AmbiguousFirstInstance), "DTSTART is not a day the rule allows; the series starts at the first allowed day.");
                p.StartDate = OutlookMinutes.FromDateTime(first);
            }

            return p;
        }
    }
}
