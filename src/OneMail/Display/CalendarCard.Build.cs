using System.Globalization;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.TimeZones;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Stores.Olm;

namespace RecursiveAI.OneMail.Display;

public sealed partial class CalendarCard
{
    private const int MaxLinks = 5;
    private const int MaxAttachments = 10;
    private const int MaxExceptionDates = 5;

    /// <summary>Builds one card per event series in a calendar object (a VCALENDAR, or a bare VEVENT/VTODO).</summary>
    /// <remarks>
    /// Components with the same UID are one series: the one without RECURRENCE-ID is the master, the others are its changed occurrences.
    /// To-dos get a card too (their due time stands in for the end). At most <see cref="CalendarCardOptions.MaxCards"/> cards are built;
    /// the first card then carries <see cref="CalendarCardDefectKind.EventsTruncated"/>. Never throws for malformed content.
    /// </remarks>
    /// <param name="calendar">The component.</param>
    /// <param name="options">Choices; null for the defaults.</param>
    /// <returns>The cards in document order; empty when there is no event.</returns>
    public static IReadOnlyList<CalendarCard> CreateAll(IcsComponent calendar, CalendarCardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        options ??= new CalendarCardOptions();
        List<IcsComponent> items = calendar.Component.IsNamed("VEVENT") || calendar.Component.IsNamed("VTODO")
            ? [calendar]
            : [.. calendar.Children.Where(c => c.Component.IsNamed("VEVENT") || c.Component.IsNamed("VTODO"))];
        List<(IcsComponent Master, List<IcsComponent> Overrides)> groups = [];
        Dictionary<string, int> byUid = new(StringComparer.Ordinal);
        foreach (IcsComponent c in items)
        {
            if (c.Uid is { } uid && byUid.TryGetValue(uid, out int gi))
            {
                // A master (no RECURRENCE-ID) found after an override takes the override's place as master.
                if (groups[gi].Master.RecurrenceId is not null && c.RecurrenceId is null)
                {
                    groups[gi].Overrides.Add(groups[gi].Master);
                    groups[gi] = (c, groups[gi].Overrides);
                }
                else
                {
                    groups[gi].Overrides.Add(c);
                }

                continue;
            }

            if (c.Uid is { } u)
            {
                byUid[u] = groups.Count;
            }

            groups.Add((c, []));
        }

        TimeZoneResolver zones = new(calendar);
        List<CalendarCard> cards = [];
        foreach ((IcsComponent master, List<IcsComponent> overrides) in groups)
        {
            if (cards.Count >= Math.Max(1, options.MaxCards))
            {
                cards[0].Defects = [.. cards[0].Defects, new CalendarCardDefect(CalendarCardDefectKind.EventsTruncated, (groups.Count - cards.Count).ToString(CultureInfo.InvariantCulture))];
                break;
            }

            cards.Add(new Builder(calendar, master, overrides, zones, options).Build());
        }

        return cards;
    }

    /// <summary>Builds the card of the first event in a calendar object.</summary>
    /// <param name="calendar">The component.</param>
    /// <param name="options">Choices; null for the defaults.</param>
    /// <returns>The card, or null when there is no event.</returns>
    public static CalendarCard? Create(IcsComponent calendar, CalendarCardOptions? options = null) => CreateAll(calendar, options) is { Count: > 0 } all ? all[0] : null;

    /// <summary>Parses iCalendar bytes (UTF-8, as RFC 5545 requires) and builds a card per event series of every VCALENDAR in them.</summary>
    /// <param name="ics">The file content.</param>
    /// <param name="options">Choices; null for the defaults.</param>
    /// <returns>The cards; empty when there is no event. Parse defects are not card defects: a damaged file gives what could be read.</returns>
    public static IReadOnlyList<CalendarCard> FromIcs(ReadOnlyMemory<byte> ics, CalendarCardOptions? options = null)
    {
        ContentDocument doc = ContentDocument.Parse(ics);
        return [.. doc.Components.Where(c => c.IsNamed("VCALENDAR")).SelectMany(c => CreateAll(new IcsComponent(c), options))];
    }

    /// <summary>Builds the cards of a <c>text/calendar</c> part (RFC 6047 iMIP) or an attached .ics.</summary>
    /// <param name="part">The part.</param>
    /// <param name="options">Choices; null for the defaults.</param>
    /// <returns>The cards.</returns>
    public static IReadOnlyList<CalendarCard> FromMime(MimePart part, CalendarCardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        return FromIcs(part.GetDecodedContent(), options);
    }

    /// <summary>
    /// Builds the card of an Outlook appointment or meeting item: the item goes through the MS-OXCICAL export, plus the online-meeting
    /// URL (<c>PidLidNetShowUrl</c>) that the export leaves out. A message that is not a calendar item gives no card.
    /// </summary>
    /// <param name="message">The message from an MSG, PST, OST, TNEF or OLM source.</param>
    /// <param name="options">Choices; null for the defaults.</param>
    /// <returns>The cards (one for a calendar item).</returns>
    public static IReadOnlyList<CalendarCard> FromMapi(MapiMessage message, CalendarCardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (OutlookAppointment.TryCreate(message) is null)
        {
            return [];
        }

        ContentDocument doc = new MapiToIcsConverter().Convert(message).Value;
        string? netShow = message.Properties.Get(OutlookNames.NetShowUrl)?.AsString(message.String8Encoding);
        List<CalendarCard> cards = [];
        foreach (Component cal in doc.Components.Where(c => c.IsNamed("VCALENDAR")))
        {
            if (!string.IsNullOrWhiteSpace(netShow))
            {
                foreach (Component ev in cal.FindComponents("VEVENT").Where(e => e.FindProperty("X-MS-OLK-NETSHOWURL") is null))
                {
                    ev.Add(new ContentProperty("X-MS-OLK-NETSHOWURL", netShow));
                }
            }

            cards.AddRange(CreateAll(new IcsComponent(cal), options));
        }

        return cards;
    }

    /// <summary>The cards of whatever calendar objects a message item carries: the <c>text/calendar</c> parts of a MIME message, or the Outlook item itself.</summary>
    /// <param name="item">The item.</param>
    /// <param name="options">Choices; null for the defaults.</param>
    /// <returns>The cards; empty for an item with no calendar object.</returns>
    public static IReadOnlyList<CalendarCard> FromItem(IMailItem item, CalendarCardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            MimeMailItem m => [.. m.CalendarParts.SelectMany(p => FromMime(p, options))],
            MapiMailItem a => FromMapi(a.Message, options),
            OlmItem { OlmKind: OlmItemKind.Appointment } olm => CreateAll(new IcsComponent(new OlmToCalendarConverter().Convert(olm).Value), options),
            _ => [],
        };
    }

    private readonly record struct Moment(DateTime Wall, DateTime? Utc, string? Label, IcsTimeZone? Zone, bool IsDate);

    private sealed class Builder(IcsComponent calendar, IcsComponent master, List<IcsComponent> overrides, TimeZoneResolver zones, CalendarCardOptions o)
    {
        private readonly List<CalendarCardDefect> _defects = [];
        private readonly HashSet<string> _zoneDefects = [];
        private List<Occurrence> _data = [];
        private IcsTimeZone? _display;
        private string? _displayLabel;

        private string One(string? s, int? max = null)
        {
            string r = CalendarCardFormat.Clean(s, max ?? o.MaxFieldLength, out bool cut);
            if (cut)
            {
                _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.FieldTruncated, null));
            }

            return r;
        }

        internal CalendarCard Build()
        {
            CalendarCard c = new() { Uid = master.Uid is null ? null : One(master.Uid) };
            if (o.DisplayTimeZone is { Length: > 0 } dz)
            {
                _display = zones.Resolve(dz);
                _displayLabel = One(dz);
                if (_display is null)
                {
                    _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.DisplayZoneUnresolved, _displayLabel));
                }
            }

            string? methodText = calendar.MethodText?.Trim();
            c.Method = Map(calendar.Method, methodText);
            string? status = master.Status?.Trim().ToUpperInvariant();
            bool cancelled = c.Method == CalendarCardMethod.Cancel || status == "CANCELLED";
            c.Status = cancelled ? CalendarCardStrings.StatusCancelled : status == "TENTATIVE" ? CalendarCardStrings.StatusTentative : null;
            c.Summary = master.Summary is { } s && One(s).Length > 0 ? One(s) : CalendarCardStrings.NoSummary;
            c.Action = ActionText(c.Method, cancelled);

            Time(c, cancelled);
            Where(c);
            People(c);
            Extras(c);
            c.Defects = [.. _defects];
            return c;
        }

        private static CalendarCardMethod Map(IcsMethod m, string? text) => m switch
        {
            IcsMethod.Publish => CalendarCardMethod.Publish,
            IcsMethod.Request => CalendarCardMethod.Request,
            IcsMethod.Reply => CalendarCardMethod.Reply,
            IcsMethod.Add => CalendarCardMethod.Add,
            IcsMethod.Cancel => CalendarCardMethod.Cancel,
            IcsMethod.Refresh => CalendarCardMethod.Refresh,
            IcsMethod.Counter => CalendarCardMethod.Counter,
            IcsMethod.DeclineCounter => CalendarCardMethod.DeclineCounter,
            _ => string.IsNullOrEmpty(text) ? CalendarCardMethod.None : CalendarCardMethod.Other,
        };

        private string ActionText(CalendarCardMethod m, bool cancelled)
        {
            switch (m)
            {
                case CalendarCardMethod.Request:
                    return master.Sequence is > 0 ? CalendarCardStrings.UpdatedInvitation : CalendarCardStrings.Invitation;
                case CalendarCardMethod.Cancel:
                    return CalendarCardStrings.Cancelled;
                case CalendarCardMethod.Reply:
                    IcsCalAddress? who = master.Attendees.FirstOrDefault(a => a.ParticipationStatus is not null) ?? (master.Attendees.Count > 0 ? master.Attendees[0] : null);
                    string name = who is null ? CalendarCardStrings.Someone : One(who.CommonName is { Length: > 0 } cn ? cn : who.Email);
                    string verb = who?.ParticipationStatus?.ToUpperInvariant() switch
                    {
                        "ACCEPTED" => CalendarCardStrings.Accepted,
                        "DECLINED" => CalendarCardStrings.Declined,
                        "TENTATIVE" => CalendarCardStrings.TentativelyAccepted,
                        _ => CalendarCardStrings.RepliedTo,
                    };
                    return name + " " + verb;
                case CalendarCardMethod.Counter:
                    return CalendarCardStrings.CounterProposal;
                case CalendarCardMethod.DeclineCounter:
                    return CalendarCardStrings.CounterDeclined;
                case CalendarCardMethod.Add:
                    return CalendarCardStrings.AddedInstance;
                case CalendarCardMethod.Refresh:
                    return CalendarCardStrings.RefreshRequest;
                case CalendarCardMethod.Publish:
                case CalendarCardMethod.None:
                    return cancelled ? CalendarCardStrings.Cancelled : CalendarCardStrings.Published;
                default:
                    return CalendarCardStrings.UnknownMethod;
            }
        }

        private Moment Make(IcsTime t)
        {
            switch (t.Kind)
            {
                case IcsTimeKind.Date:
                    return new Moment(t.Value, null, null, null, true);
                case IcsTimeKind.Utc:
                    return new Moment(t.Value, t.Value, "UTC", null, false);
                case IcsTimeKind.Zoned:
                    IcsTimeZone? z = zones.Resolve(t.TzId);
                    string label = One(t.TzId);
                    if (z is null)
                    {
                        if (_zoneDefects.Add(label))
                        {
                            _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.UnresolvedZone, label));
                        }

                        return new Moment(t.Value, null, label, null, false);
                    }

                    return new Moment(t.Value, z.ToUtc(t.Value, out _), label, z, false);
                default:
                    return new Moment(t.Value, null, null, null, false);
            }
        }

        private Moment Display(Moment m) =>
            _display is not null && m.Utc is { } utc && !m.IsDate ? new Moment(_display.ToLocal(utc), utc, _displayLabel, _display, false) : m;

        private static string Zone(Moment m)
        {
            if (m.Label is null)
            {
                return string.Empty;
            }

            if (m.Zone is null)
            {
                return " (" + m.Label + ")";
            }

            return " (" + m.Label + ", " + CalendarCardFormat.Offset(m.Zone.GetUtcOffset(m.Utc ?? m.Wall)) + ")";
        }

        private static string Range(Moment s, Moment? e)
        {
            if (s.IsDate)
            {
                DateTime last = e is { IsDate: true } ed ? ed.Wall.AddDays(-1) : s.Wall;
                return CalendarCardStrings.AllDay + ", " + CalendarCardFormat.LongDate(s.Wall)
                    + (last.Date > s.Wall.Date ? " " + CalendarCardStrings.To + " " + CalendarCardFormat.LongDate(last) : string.Empty);
            }

            string text = CalendarCardFormat.LongDate(s.Wall) + " " + CalendarCardFormat.Time(s.Wall);
            if (e is { } end && end.Wall >= s.Wall)
            {
                text = end.Wall.Date == s.Wall.Date
                    ? text + "–" + CalendarCardFormat.Time(end.Wall)
                    : text + " " + CalendarCardStrings.To + " " + CalendarCardFormat.LongDate(end.Wall) + " " + CalendarCardFormat.Time(end.Wall);
            }

            return text + Zone(s);
        }

        private string Format(IcsTime start, IcsTime? end, bool original = false)
        {
            Moment s = Make(start);
            Moment? e = end is { } et ? Make(et) : null;
            if (!original)
            {
                s = Display(s);
                e = e is { } e2 ? Display(e2) : null;
            }

            return Range(s, e);
        }

        private static IcsTime? EndOf(IcsComponent c, IcsTime start)
        {
            if ((c.End ?? c.Due) is { } end)
            {
                return end;
            }

            if (c.Duration is { } d)
            {
                long ticks = start.Value.Ticks + d.ToTimeSpan().Ticks;
                if (ticks > 0 && ticks < DateTime.MaxValue.Ticks)
                {
                    return start with { Value = new DateTime(ticks, DateTimeKind.Unspecified) };
                }
            }

            return null;
        }

        private void Time(CalendarCard c, bool cancelled)
        {
            if ((master.Start ?? master.Due) is not { } start)
            {
                return;
            }

            IcsTime? end = EndOf(master, start);
            c.When = Format(start, end);
            if (_display is not null)
            {
                Moment m = Make(start);
                if (m.Utc is not null && !string.Equals(m.Label, _displayLabel, StringComparison.OrdinalIgnoreCase))
                {
                    c.OriginalTime = Format(start, end, original: true);
                }
            }

            List<RecurrenceRule> rules = [.. master.RecurrenceRules];
            IReadOnlyList<IcsTime> rdates = master.RecurrenceDates;
            if (rules.Count == 0 && rdates.Count == 0)
            {
                return;
            }

            List<string> sentences = [];
            foreach (RecurrenceRule r in rules)
            {
                string? sentence = CalendarCardRecurrence.Describe(r, start);
                if (sentence is null)
                {
                    sentences.Clear();
                    break;
                }

                sentences.Add(sentence);
            }

            if (rules.Count > 0 && sentences.Count == 0)
            {
                c.Recurrence = CalendarCardStrings.CustomRule;
                _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.RecurrenceNotDescribed, null));
            }
            else
            {
                c.Recurrence = rules.Count == 0 ? CalendarCardStrings.SelectedDates : string.Join("; ", sentences);
                c.RecurrenceDescribed = true;
                IReadOnlyList<IcsTime> ex = master.ExceptionDates;
                if (ex.Count > 0)
                {
                    c.Recurrence += " (" + CalendarCardStrings.Except + " " + CalendarCardRecurrence.JoinList(ex.Take(MaxExceptionDates).Select(x => CalendarCardFormat.Date(x.Value)))
                        + (ex.Count > MaxExceptionDates ? " " + CalendarCardStrings.More(ex.Count - MaxExceptionDates) : string.Empty) + ")";
                }
            }

            if (overrides.Count > 0)
            {
                c.Recurrence += " (" + CalendarCardStrings.Changed(overrides.Count) + ")";
            }

            int n = Math.Clamp(o.NextOccurrences, 0, 50);
            if (n > 0 && !cancelled)
            {
                c.Occurrences = Next(start, n);
                c.OccurrenceData = _data;
            }
        }

        private List<string> Next(IcsTime start, int n)
        {
            ExpansionReport report = new();
            ExpansionOptions eo = new() { MaxInstancesScanned = 200_000, MaxIterationsWithoutHit = 50_000, MaxOccurrences = 10_000 };
            List<string> result = [];
            _data = [];
            OccurrenceWindow window = o.After is { } after ? new OccurrenceWindow(after, DateTime.MaxValue) : OccurrenceWindow.All;
            try
            {
                RecurrenceSet set = new(master, overrides, zones, eo, report);
                foreach (Occurrence occ in set.Expand(window))
                {
                    result.Add(Format(occ.Start, occ.End.Equals(occ.Start) ? null : occ.End));
                    _data.Add(occ);
                    if (result.Count >= n)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException)
            {
                // The expansion is lenient and should not throw; a failure here only costs the occurrence list.
                _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.ExpansionLimited, null));
            }

            if (result.Count < n && report.Defects.Any(d => d.IsLimit))
            {
                _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.ExpansionLimited, null));
            }

            return result;
        }

        private void Where(CalendarCard c)
        {
            if (master.Location is { } loc && One(loc) is { Length: > 0 } l)
            {
                c.Location = l;
            }

            List<CalendarCardLink> links = [];
            void AddUrl(string label, string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw) || links.Count >= MaxLinks)
                {
                    return;
                }

                string t = raw.Trim();
                if (t.Length <= o.MaxFieldLength && Uri.TryCreate(t, UriKind.Absolute, out Uri? u) && u.Scheme is "http" or "https" or "mailto" && u.AbsoluteUri.Length <= o.MaxFieldLength * 3)
                {
                    if (!links.Any(x => x.Url == u.AbsoluteUri))
                    {
                        links.Add(new CalendarCardLink(label, u.AbsoluteUri));
                    }
                }
                else
                {
                    _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.UrlRejected, CalendarCardFormat.Clean(t, 60, out _)));
                }
            }

            foreach (ContentProperty p in master.Component.FindProperties("CONFERENCE").Take(MaxLinks))
            {
                AddUrl(CalendarCardStrings.Join, p.Value.Length > 4096 ? null : p.GetText());
            }

            foreach (string name in ConferenceProperties)
            {
                if (master.Component.FindProperty(name) is { } p)
                {
                    AddUrl(CalendarCardStrings.Join, p.Value.Length > 4096 ? null : p.GetText());
                }
            }

            if (master.Component.FindProperty("URL") is { } up)
            {
                AddUrl("URL", up.Value.Length > 4096 ? null : up.GetText());
            }

            c.Links = links;
        }

        // Names Outlook and other clients write the online-meeting URL under: MS-OXCICAL 2.1.3.1.1.20.56 for NetShow, the rest as observed in the wild.
        private static readonly string[] ConferenceProperties =
        [
            "X-MS-OLK-NETSHOWURL", "X-MICROSOFT-ONLINEMEETINGCONFLINK", "X-MICROSOFT-SKYPETEAMSMEETINGURL", "X-GOOGLE-CONFERENCE",
        ];

        private void People(CalendarCard c)
        {
            if (master.Component.FindProperty("ORGANIZER") is { } org && master.Organizer is { } oa)
            {
                string name = One(oa.CommonName);
                string addr = One(oa.Email);
                c.Organizer = name.Length > 0 ? (addr.Length > 0 ? name + " (" + addr + ")" : name) : addr;
                _ = org;
            }

            List<CalendarCardAttendee> list = [];
            int total = 0;
            foreach (ContentProperty p in master.Component.FindProperties("ATTENDEE"))
            {
                total++;
                if (list.Count >= o.MaxAttendees)
                {
                    continue;
                }

                string value = p.Value.Length > 1024 ? p.Value[..1024] : p.Value;
                string email = value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
                string? cn = p.GetParameter("CN");
                list.Add(new CalendarCardAttendee(
                    cn is { Length: > 0 } ? One(cn) : null,
                    One(email),
                    p.GetParameter("ROLE")?.ToUpperInvariant() switch
                    {
                        "CHAIR" => CalendarCardStrings.Chair,
                        "OPT-PARTICIPANT" => CalendarCardStrings.Optional,
                        "NON-PARTICIPANT" => CalendarCardStrings.NonParticipant,
                        "REQ-PARTICIPANT" => CalendarCardStrings.Required,
                        _ => null,
                    },
                    p.GetParameter("PARTSTAT")?.ToUpperInvariant() switch
                    {
                        "ACCEPTED" => CalendarCardStrings.StatusAccepted,
                        "DECLINED" => CalendarCardStrings.StatusDeclined,
                        "TENTATIVE" => CalendarCardStrings.StatusTentativeReply,
                        "DELEGATED" => CalendarCardStrings.StatusDelegated,
                        "NEEDS-ACTION" => CalendarCardStrings.NeedsAction,
                        _ => null,
                    },
                    p.GetParameter("CUTYPE")?.ToUpperInvariant() switch
                    {
                        "ROOM" => CalendarCardStrings.Room,
                        "RESOURCE" => CalendarCardStrings.Resource,
                        "GROUP" => CalendarCardStrings.Group,
                        _ => null,
                    }));
            }

            c.Attendees = list;
            c.AttendeeCount = total;
            if (total > list.Count)
            {
                _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.AttendeesTruncated, (total - list.Count).ToString(CultureInfo.InvariantCulture)));
            }
        }

        private void Extras(CalendarCard c)
        {
            List<string> files = [];
            int attachCount = 0;
            foreach (ContentProperty p in master.Component.FindProperties("ATTACH"))
            {
                attachCount++;
                if (files.Count >= MaxAttachments)
                {
                    continue;
                }

                string? fn = p.GetParameter("FILENAME");
                bool binary = string.Equals(p.GetParameter("VALUE"), "BINARY", StringComparison.OrdinalIgnoreCase) || p.Encoding is not null;
                string text = fn is { Length: > 0 } ? One(fn) : binary ? CalendarCardStrings.Embedded : One(p.Value.Length > 2048 ? p.Value[..2048] : p.GetText());
                if (text.Length > 0)
                {
                    files.Add(text);
                }
            }

            if (attachCount > MaxAttachments)
            {
                _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.AttachmentsTruncated, (attachCount - MaxAttachments).ToString(CultureInfo.InvariantCulture)));
            }

            c.Attachments = files;
            if (master.Component.FindProperty("DESCRIPTION") is { } dp)
            {
                // Only the part that can be shown is unescaped; the rest of a huge value is never copied.
                string raw = dp.GetDecodedValue();
                int cap = Math.Max(0, o.MaxDescriptionLength) * 4 + 64;
                if (raw.Length > cap)
                {
                    raw = raw[..cap];
                }

                string d = CalendarCardFormat.CleanMultiline(ContentValues.Unescape(raw, TextEscapeStyle.Rfc5545), Math.Max(0, o.MaxDescriptionLength), 30, out bool cut);
                if (cut || dp.Value.Length > cap)
                {
                    if (!d.EndsWith(CalendarCardStrings.Ellipsis, StringComparison.Ordinal) && d.Length > 0)
                    {
                        d += CalendarCardStrings.Ellipsis;
                    }

                    _defects.Add(new CalendarCardDefect(CalendarCardDefectKind.DescriptionTruncated, null));
                }

                c.Description = d.Length > 0 ? d : null;
            }
        }
    }
}
