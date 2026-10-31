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

/// <summary>Options for <see cref="MapiToIcsConverter"/>.</summary>
public sealed record OxcIcalExportOptions
{
    /// <summary>The PRODID written; default <c>-//Recursive AI//OneMail//EN</c>.</summary>
    public string ProductId { get; init; } = "-//Recursive AI//OneMail//EN";

    /// <summary>Write the X-MICROSOFT-CDO-* properties MS-OXCICAL lists (busy status, intended status, appointment sequence). Default true.</summary>
    public bool MicrosoftExtensions { get; init; } = true;

    /// <summary>Limits for the Outlook blobs; default <see cref="CalendaringLimits.Default"/>.</summary>
    public CalendaringLimits Limits { get; init; } = CalendaringLimits.Default;
}

/// <summary>
/// Appointment, meeting and task items to iCalendar (MS-OXCICAL 2.1.3). The result is a VCALENDAR document; what the iCalendar form cannot hold
/// is in the loss report. Appointments, meeting requests, responses and cancellations become a VEVENT with the METHOD of RFC 5546;
/// <c>IPM.Task</c> items become a VTODO. Exceptions of a recurring series become VEVENTs with a RECURRENCE-ID.
/// </summary>
public sealed class MapiToIcsConverter : IItemConversion<MapiMessage, Calendar.ContentDocument>
{
    private readonly OxcIcalExportOptions _options;

    /// <summary>Creates a converter.</summary>
    /// <param name="options">Options; null for the defaults.</param>
    public MapiToIcsConverter(OxcIcalExportOptions? options = null) => _options = options ?? new OxcIcalExportOptions();

    /// <summary>Converts one item.</summary>
    /// <param name="source">A calendar-family or task message.</param>
    /// <returns>A VCALENDAR document and the loss report. For any other message class the document holds no component and the report says <c>MessageClassNotCarried</c>. Never throws for malformed content.</returns>
    public ConversionResult<Calendar.ContentDocument> Convert(MapiMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        LossReport report = new();
        var doc = new Calendar.ContentDocument();
        Calendar.Component cal = new("VCALENDAR");
        doc.Root.Add(cal);
        cal.Add(new Calendar.ContentProperty("VERSION", "2.0"));
        cal.Add(new Calendar.ContentProperty("PRODID", _options.ProductId));
        if (OutlookAppointment.TryCreate(source, _options.Limits) is { } appt)
        {
            new Exporter(_options, report, cal, appt).Run();
        }
        else if (OutlookTask.TryCreate(source, _options.Limits) is { } task)
        {
            new TaskExporter(report, cal, task).Run();
        }
        else
        {
            report.Add("mapi", "PidTagMessageClass", LossKind.Dropped, "MessageClassNotCarried", $"'{source.MessageClass}' is not an appointment, meeting or task class; no iCalendar component was written.");
        }

        return new ConversionResult<Calendar.ContentDocument>(doc, report);
    }

    // The properties the exporter reads into iCalendar fields, and the ones it recomputes on import (MS-OXCICAL 2.1.3.1.1.20 and MS-OXOCAL 2.2.1).
    internal static readonly HashSet<MapiNamedProperty> Carried =
    [
        OutlookNames.StartWhole, OutlookNames.EndWhole, OutlookNames.SubType, OutlookNames.Location, OutlookNames.Where, OutlookNames.BusyStatus,
        OutlookNames.IntendedBusyStatus, OutlookNames.AppointmentRecur, OutlookNames.TimeZoneStruct, OutlookNames.TimeZoneDefinitionRecur,
        OutlookNames.TimeZoneDefinitionStartDisplay, OutlookNames.GlobalObjectId, OutlookNames.AppointmentSequence, OutlookNames.ReminderSet,
        OutlookNames.ReminderDelta, OutlookNames.ExceptionReplaceTime, OutlookNames.StateFlags,
        OutlookNames.TaskStatus, OutlookNames.PercentComplete, OutlookNames.TaskStartDate, OutlookNames.TaskDueDate, OutlookNames.TaskDateCompleted,
        OutlookNames.TaskRecurrence, OutlookNames.TaskComplete, OutlookNames.MeetingType,
    ];

    internal static readonly HashSet<MapiNamedProperty> Derived =
    [
        OutlookNames.Duration, OutlookNames.ClipStart, OutlookNames.ClipEnd, OutlookNames.CleanGlobalObjectId, OutlookNames.Recurring, OutlookNames.IsRecurring,
        OutlookNames.AllAttendeesString, OutlookNames.ToAttendeesString, OutlookNames.CcAttendeesString, OutlookNames.ReminderTime, OutlookNames.ReminderSignalTime,
        OutlookNames.TimeZoneDescription, OutlookNames.CommonStart, OutlookNames.CommonEnd, OutlookNames.TaskFRecurring, OutlookNames.TaskState,
    ];

    internal static readonly HashSet<ushort> CarriedTags =
    [
        MapiPid.Subject, MapiPid.NormalizedSubject, MapiPid.SubjectPrefix, MapiPid.MessageClass, MapiPid.Sensitivity, MapiPid.Importance, MapiPid.Body, MapiPid.Html,
        MapiPid.SentRepresentingName, MapiPid.SentRepresentingAddressType, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingSmtpAddress,
        MapiPid.SenderName, MapiPid.SenderAddressType, MapiPid.SenderEmailAddress, MapiPid.SenderSmtpAddress,
        MapiPid.CreationTime, MapiPid.LastModificationTime, OutlookNames.PidTagResponseRequested,
    ];

    internal static readonly HashSet<ushort> DerivedTags = [0x0060, 0x0061, 0x0062, 0x0C17];

    private sealed class Exporter(OxcIcalExportOptions o, LossReport report, Calendar.Component cal, OutlookAppointment a)
    {
        private OutlookTimeZone? _tz;
        private bool _allDay;
        private readonly HashSet<MapiNamedProperty> _seenNamed = [];

        public void Run()
        {
            MapiMessage m = a.Message;
            _tz = a.TimeZone;
            _allDay = a.IsAllDay;
            string? method = a.Kind switch
            {
                OutlookAppointmentKind.Appointment => "PUBLISH",
                OutlookAppointmentKind.MeetingRequest => "REQUEST",
                OutlookAppointmentKind.MeetingResponseAccepted or OutlookAppointmentKind.MeetingResponseTentative or OutlookAppointmentKind.MeetingResponseDeclined => "REPLY",
                OutlookAppointmentKind.MeetingCancellation => "CANCEL",
                _ => null,
            };
            if (method is not null)
            {
                cal.Add(new Calendar.ContentProperty("METHOD", method));
            }
            else if (a.Kind == OutlookAppointmentKind.MeetingForwardNotification)
            {
                report.Add("mapi", "PidTagMessageClass", LossKind.Approximated, "ForwardNotificationHasNoMethod", "RFC 5546 has no method for a forward notification; the event is written without METHOD.");
            }

            if (_tz is not null && !_allDay)
            {
                cal.Add(_tz.VTimeZone);
            }

            Calendar.Component ev = new("VEVENT");
            cal.Add(ev);
            IcsComponent e = new(ev);
            Series(e);
            Participants(e);
            Common(e, m);
            Alarm(ev);
            if (a.Recurrence is { } r)
            {
                foreach (OutlookModifiedInstance x in a.Exceptions)
                {
                    Exception(cal, x, r);
                }
            }

            foreach (CalendaringDefect d in a.Defects)
            {
                report.Add("mapi", "PidLidAppointmentRecur", LossKind.Approximated, d.Kind.ToString(), d.Detail);
            }

            Account(m, "", true);
            foreach (MapiAttachment att in m.Attachments)
            {
                if (a.Exceptions.Any(x => x.Attachment == att))
                {
                    continue;
                }

                report.Add("mapi", "Attachment", LossKind.Dropped, "AttachmentNotCarried", "iCalendar attachments by value are not written; the attachment is reported.", $"attachment[{m.Attachments.ToList().IndexOf(att)}]");
            }
        }

        private static string Fmt(DateTime local) => local.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

        private IcsTime AsIcs(DateTime local)
        {
            if (_allDay)
            {
                return new IcsTime(local.Date, IcsTimeKind.Date);
            }

            return _tz is not null ? new IcsTime(local, IcsTimeKind.Zoned, _tz.Id) : new IcsTime(DateTime.SpecifyKind(local, DateTimeKind.Utc), IcsTimeKind.Utc);
        }

        private string UntilText(DateTime local)
        {
            if (_allDay)
            {
                return local.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            }

            DateTime utc = _tz is not null ? _tz.Zone.ToUtc(local, out _) : local;
            return Fmt(utc) + "Z";
        }

        private void Series(IcsComponent e)
        {
            MapiMessage m = a.Message;
            e.SetText("UID", Uid());
            e.SetTime("DTSTAMP", new IcsTime(DateTime.SpecifyKind((m.Properties.Get(MapiPid.LastModificationTime, MapiType.Time)?.AsTime() ?? m.Properties.Get(MapiPid.CreationTime, MapiType.Time)?.AsTime() ?? a.Start ?? new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).UtcDateTime, DateTimeKind.Utc), IcsTimeKind.Utc));
            DateTime? start = a.StartLocal;
            DateTime? end = a.EndLocal;
            AppointmentRecurrencePattern? r = a.Recurrence;
            if (r is not null && OutlookRecurrence.CanMap(r.Pattern))
            {
                // The series' own clock: first valid day plus the start offset (MS-OXOCAL 2.2.1.44.5), not the UTC instants, which may sit on a skipped day.
                DateTime? first = OutlookRecurrence.FirstDay(r.Pattern);
                DateTime sd = OutlookMinutes.ToDateTime(r.Pattern.StartDate).Date;
                if (first is { } f && f != sd)
                {
                    report.Add("mapi", "PidLidAppointmentRecur", LossKind.Approximated, nameof(CalendaringDefectKind.AmbiguousFirstInstance),
                        "StartDate is not a day the pattern allows; DTSTART is the first allowed day.");
                }

                DateTime day = first ?? sd;
                start = day.AddMinutes(r.StartTimeOffset);
                end = day.AddMinutes(r.EndTimeOffset);
                List<CalendaringDefect> ds = [];
                string? rule = OutlookRecurrence.ToRRule(r.Pattern, r.StartTimeOffset, UntilText, ds);
                foreach (CalendaringDefect d in ds)
                {
                    report.Add("mapi", "PidLidAppointmentRecur", LossKind.Approximated, d.Kind.ToString(), d.Detail);
                }

                if (rule is not null)
                {
                    e.Component.Add(new Calendar.ContentProperty("RRULE", rule));
                }

                foreach (DateTime d in a.DeletedInstances)
                {
                    DateTime t = d.Date.AddMinutes(r.StartTimeOffset);
                    AddTimeList(e, "EXDATE", t);
                }
            }
            else if (r is not null)
            {
                report.Add("mapi", "PidLidAppointmentRecur", LossKind.Dropped, nameof(CalendaringDefectKind.UnsupportedCalendar), "The recurrence uses a calendar or pattern with no iCalendar form; the item is written as a single event.");
            }

            if (start is { } s)
            {
                e.SetTime("DTSTART", AsIcs(s));
                if (end is { } en)
                {
                    DateTime endv = _allDay && en.Date <= s.Date ? s.Date.AddDays(1) : en;
                    e.SetTime("DTEND", AsIcs(endv));
                }
            }
            else
            {
                report.Add("mapi", "PidLidAppointmentStartWhole", LossKind.Dropped, nameof(CalendaringDefectKind.MissingTime), "The item has no start time.");
            }
        }

        private void AddTimeList(IcsComponent e, string name, DateTime local)
        {
            Calendar.ContentProperty p = new(name, _allDay ? local.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : Fmt(local));
            if (_allDay)
            {
                p.Parameters.Set("VALUE", "DATE");
            }
            else if (_tz is not null)
            {
                p.Parameters.Set("TZID", _tz.Id);
            }
            else
            {
                p.Value += "Z";
            }

            e.Component.Add(p);
        }

        // MS-OXCICAL 2.1.3.1.1.20.26: a vCal-Uid payload is the UID itself, anything else is the hex of the GlobalObjectId with the instance date zeroed.
        private string Uid()
        {
            byte[]? g = a.GlobalObjectId;
            if (g is { Length: > 52 } && g.AsSpan(40).StartsWith("vCal-Uid\u0001\0\0\0"u8))
            {
                return Encoding.UTF8.GetString(g.AsSpan(52));
            }

            if (g is { Length: >= 20 })
            {
                byte[] c = (byte[])g.Clone();
                c[16] = c[17] = c[18] = c[19] = 0;
                return System.Convert.ToHexString(c);
            }

            report.Add("mapi", "PidLidGlobalObjectId", LossKind.Approximated, "UidSynthesized", "The item has no usable PidLidGlobalObjectId; a UID was made from its subject and times.");
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes((a.Subject ?? "") + "|" + a.Start?.UtcTicks + "|" + a.End?.UtcTicks));
            return System.Convert.ToHexString(h.AsSpan(0, 16)) + "@onemail";
        }

        private void Participants(IcsComponent e)
        {
            MapiMessage m = a.Message;
            var org = a.Organizer;
            bool response = a.Kind is OutlookAppointmentKind.MeetingResponseAccepted or OutlookAppointmentKind.MeetingResponseTentative or OutlookAppointmentKind.MeetingResponseDeclined;
            if (response)
            {
                // RFC 5546 §3.2.3: a REPLY names the organizer and the one attendee who answers.
                OutlookAttendee? to = a.Attendees.FirstOrDefault(x => x.Role == OutlookAttendeeRole.Required && x.Address is not null);
                if (to is not null)
                {
                    e.Component.Add(Person("ORGANIZER", to.Name, to.Address!));
                }

                if (org is { Address: { } ad })
                {
                    string stat = a.Kind switch { OutlookAppointmentKind.MeetingResponseAccepted => "ACCEPTED", OutlookAppointmentKind.MeetingResponseTentative => "TENTATIVE", _ => "DECLINED" };
                    Calendar.ContentProperty p = Person("ATTENDEE", org.Value.Name, ad);
                    p.Parameters.Set("PARTSTAT", stat);
                    e.Component.Add(p);
                }

                return;
            }

            if (org is { Address: { } oa })
            {
                e.Component.Add(Person("ORGANIZER", org.Value.Name, oa));
            }
            else if (org is not null)
            {
                report.Add("mapi", "PidTagSentRepresentingEmailAddress", LossKind.Dropped, "OrganizerWithoutSmtpAddress", "The organizer has no SMTP address, so no ORGANIZER was written.");
            }

            if (!a.IsMeeting)
            {
                return;
            }

            bool rsvp = m.Properties.Get(OutlookNames.PidTagResponseRequested, MapiType.Boolean)?.AsBoolean() == true;
            foreach (OutlookAttendee at in a.Attendees)
            {
                if (at.IsOrganizer || at.IsDeleted)
                {
                    continue;
                }

                Calendar.ContentProperty p = at.Address is null ? new("ATTENDEE", "invalid:nomail") : Person("ATTENDEE", at.Name, at.Address);
                if (at.Address is null && at.Name is not null)
                {
                    p.Parameters.Set("CN", at.Name);
                }

                switch (at.Role)
                {
                    case OutlookAttendeeRole.Optional:
                        p.Parameters.Set("ROLE", "OPT-PARTICIPANT");
                        break;
                    case OutlookAttendeeRole.Resource:
                        p.Parameters.Set("CUTYPE", "RESOURCE");
                        p.Parameters.Set("ROLE", "NON-PARTICIPANT");
                        break;
                }

                string? ps = at.Response switch { OutlookResponse.Accepted => "ACCEPTED", OutlookResponse.Declined => "DECLINED", OutlookResponse.Tentative => "TENTATIVE", _ => "NEEDS-ACTION" };
                p.Parameters.Set("PARTSTAT", ps);
                p.Parameters.Set("RSVP", rsvp ? "TRUE" : "FALSE");
                if (at.ResponseTime is { } rt)
                {
                    p.Parameters.Set("X-MS-OLK-RESPTIME", rt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
                }

                e.Component.Add(p);
            }
        }

        private static Calendar.ContentProperty Person(string name, string? cn, string address)
        {
            Calendar.ContentProperty p = new(name, "mailto:" + address);
            if (!string.IsNullOrEmpty(cn))
            {
                p.Parameters.Set("CN", cn);
            }

            return p;
        }

        private void Common(IcsComponent e, MapiMessage m)
        {
            if (a.Subject is { } subj)
            {
                e.SetText("SUMMARY", subj);
            }

            if (a.Location is { Length: > 0 } loc)
            {
                e.SetText("LOCATION", loc);
            }

            if (m.Body is { Length: > 0 } body)
            {
                e.SetText("DESCRIPTION", body.TrimEnd('\r', '\n'));
            }

            if (m.Properties.Get(MapiPid.Html) is { } html && HtmlText(html) is { Length: > 0 } ht)
            {
                Calendar.ContentProperty p = new("X-ALT-DESC", ContentValues.Escape(ht));
                p.Parameters.Set("FMTTYPE", "text/html");
                e.Component.Add(p);
            }
            else if (m.Properties.Get(MapiPid.RtfCompressed) is not null)
            {
                report.Add("mapi", "PidTagRtfCompressed", LossKind.Dropped, "RtfBodyNotCarried", "The body exists only as RTF; iCalendar carries plain text or HTML.");
            }

            if (a.Sensitivity is { } sens)
            {
                string? cls = sens switch { 0 => "PUBLIC", 2 => "PRIVATE", 3 => "CONFIDENTIAL", 1 => "X-PERSONAL", _ => null };
                if (cls is not null)
                {
                    e.SetText("CLASS", cls);
                }
            }

            if (a.Importance is { } imp)
            {
                e.Component.Add(new Calendar.ContentProperty("PRIORITY", imp switch { 2 => "1", 1 => "5", _ => "9" }));
            }

            if (a.BusyStatus is { } bs)
            {
                e.Component.Add(new Calendar.ContentProperty("TRANSP", bs is OutlookBusyStatus.Free or OutlookBusyStatus.WorkingElsewhere ? "TRANSPARENT" : "OPAQUE"));
                if (o.MicrosoftExtensions)
                {
                    e.Component.Add(new Calendar.ContentProperty("X-MICROSOFT-CDO-BUSYSTATUS", bs switch { OutlookBusyStatus.Free => "FREE", OutlookBusyStatus.Tentative => "TENTATIVE", OutlookBusyStatus.OutOfOffice => "OOF", OutlookBusyStatus.WorkingElsewhere => "WORKINGELSEWHERE", _ => "BUSY" }));
                }
            }

            if (o.MicrosoftExtensions && a.IntendedBusyStatus is { } ib)
            {
                e.Component.Add(new Calendar.ContentProperty("X-MICROSOFT-CDO-INTENDEDSTATUS", ib switch { OutlookBusyStatus.Free => "FREE", OutlookBusyStatus.Tentative => "TENTATIVE", OutlookBusyStatus.OutOfOffice => "OOF", OutlookBusyStatus.WorkingElsewhere => "WORKINGELSEWHERE", _ => "BUSY" }));
            }

            if (a.Sequence is { } seq)
            {
                e.Component.Add(new Calendar.ContentProperty("SEQUENCE", seq.ToString(CultureInfo.InvariantCulture)));
            }

            if (a.IsCanceled)
            {
                e.SetText("STATUS", "CANCELLED");   // RFC 5546 §3.2.5
            }

            if (m.Properties.Get(Keywords) is { } kw && kw.ValueCount > 0)
            {
                List<string> words = [];
                for (int i = 0; i < kw.ValueCount; i++)
                {
                    if (kw.AsString(m.String8Encoding, null, i) is { Length: > 0 } w)
                    {
                        words.Add(ContentValues.Escape(w));
                    }
                }

                if (words.Count > 0)
                {
                    e.Component.Add(new Calendar.ContentProperty("CATEGORIES", string.Join(',', words)));
                }
            }

            if (m.Properties.Get(MapiPid.CreationTime, MapiType.Time)?.AsTime() is { } ct)
            {
                e.SetTime("CREATED", new IcsTime(DateTime.SpecifyKind(ct.UtcDateTime, DateTimeKind.Utc), IcsTimeKind.Utc));
            }

            if (m.Properties.Get(MapiPid.LastModificationTime, MapiType.Time)?.AsTime() is { } lm)
            {
                e.SetTime("LAST-MODIFIED", new IcsTime(DateTime.SpecifyKind(lm.UtcDateTime, DateTimeKind.Utc), IcsTimeKind.Utc));
            }
        }

        internal static readonly MapiNamedProperty Keywords = new(MapiPropertySets.PublicStrings, "Keywords");

        // PidTagHtml is binary; the code page is not stored with it here, so UTF-8 is read (the importer writes UTF-8).
        private static string? HtmlText(MapiProperty p) => p.Type == MapiType.Binary ? Encoding.UTF8.GetString(p.GetBytes().Span) : p.AsString();

        private void Alarm(Calendar.Component ev)
        {
            if (!a.ReminderSet)
            {
                return;
            }

            // MS-OXCICAL 2.1.3.1.1.20.62: 0x5AE980E1 stands for the 15-minute default.
            int minutes = a.ReminderMinutes is { } d and not 0x5AE980E1 ? d : 15;
            Calendar.Component al = new("VALARM");
            al.Add(new Calendar.ContentProperty("TRIGGER", "-PT" + minutes.ToString(CultureInfo.InvariantCulture) + "M"));
            al.Add(new Calendar.ContentProperty("ACTION", "DISPLAY"));
            al.Add(new Calendar.ContentProperty("DESCRIPTION", "Reminder"));
            ev.Add(al);
        }

        private void Exception(Calendar.Component calendar, OutlookModifiedInstance x, AppointmentRecurrencePattern r)
        {
            Calendar.Component ev = new("VEVENT");
            calendar.Add(ev);
            IcsComponent e = new(ev);
            e.SetText("UID", Uid());
            e.SetTime("DTSTAMP", new IcsTime(DateTime.SpecifyKind((a.Start ?? new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).UtcDateTime, DateTimeKind.Utc), IcsTimeKind.Utc));
            e.SetTime("RECURRENCE-ID", AsIcs(x.OriginalStart));
            e.SetTime("DTSTART", AsIcs(x.Start));
            e.SetTime("DTEND", AsIcs(_allDay && x.End.Date <= x.Start.Date ? x.Start.Date.AddDays(1) : x.End));
            MapiMessage? em = x.Message;
            string? subject = em?.Subject ?? x.Info.Subject;
            string? location = em is null ? x.Info.Location : em.Properties.Get(OutlookNames.Location)?.AsString(em.String8Encoding) ?? x.Info.Location;
            if (subject is not null)
            {
                e.SetText("SUMMARY", subject);
            }

            if (location is { Length: > 0 })
            {
                e.SetText("LOCATION", location);
            }

            if (em?.Body is { Length: > 0 } body)
            {
                e.SetText("DESCRIPTION", body.TrimEnd('\r', '\n'));
            }

            uint? busy = em?.Properties.Get(OutlookNames.BusyStatus, MapiType.Integer32)?.AsInt32() is { } bi ? (uint)bi : x.Info.BusyStatus;
            if (busy is { } b)
            {
                e.Component.Add(new Calendar.ContentProperty("TRANSP", b is 0 or 4 ? "TRANSPARENT" : "OPAQUE"));
            }

            if (x.Info.ReminderSet == true && x.Info.ReminderDelta is { } rd)
            {
                Calendar.Component al = new("VALARM");
                al.Add(new Calendar.ContentProperty("TRIGGER", "-PT" + rd.ToString(CultureInfo.InvariantCulture) + "M"));
                al.Add(new Calendar.ContentProperty("ACTION", "DISPLAY"));
                al.Add(new Calendar.ContentProperty("DESCRIPTION", "Reminder"));
                ev.Add(al);
            }

            if (em is not null)
            {
                string path = $"attachment[{a.Message.Attachments.ToList().IndexOf(x.Attachment!)}]/message";
                Account(em, path, false);
            }
        }

        // Every property of the message is carried, derived or reported (MS-OXCICAL has no field for the rest).
        private void Account(MapiMessage m, string path, bool top)
        {
            foreach (MapiProperty p in m.Properties)
            {
                if (p.Name is { } n)
                {
                    if (Carried.Contains(n) || n == Keywords)
                    {
                        continue;
                    }

                    if (Derived.Contains(n))
                    {
                        report.Add("mapi", n.ToString(), LossKind.Internal, "DerivedProperty", "Recomputed from the iCalendar fields on import.", path);
                        continue;
                    }

                    report.Add("mapi", n.ToString(), LossKind.Dropped, "NoIcalMapping", "MS-OXCICAL maps no iCalendar field from this property.", path);
                    continue;
                }

                if (CarriedTags.Contains(p.Id))
                {
                    continue;
                }

                string name = MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();
                if (DerivedTags.Contains(p.Id))
                {
                    report.Add("mapi", name, LossKind.Internal, "DerivedProperty", "Recomputed from the iCalendar fields on import.", path);
                }
                else if (OxcMail.MapiToMimeConverter.Bookkeeping.Contains(p.Id))
                {
                    report.Add("mapi", name, LossKind.Internal, "StoreBookkeeping", "A store-internal property with no meaning outside the store.", path);
                }
                else
                {
                    report.Add("mapi", name, LossKind.Dropped, "NoIcalMapping", "MS-OXCICAL maps no iCalendar field from this property.", path);
                }
            }

            string rp = path.Length == 0 ? "recipient" : path + "/recipient";
            HashSet<MapiTag> seen = [];
            foreach (MapiRecipient rec in m.Recipients)
            {
                foreach (MapiProperty p in rec.Properties)
                {
                    if (Consumed.Contains(p.Id) || !seen.Add(p.Tag))
                    {
                        continue;
                    }

                    string name = MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();
                    if (OxcMail.MapiToMimeConverter.Bookkeeping.Contains(p.Id))
                    {
                        report.Add("mapi", name, LossKind.Internal, "StoreBookkeeping", "A store-internal recipient property.", rp);
                    }
                    else
                    {
                        report.Add("mapi", name, LossKind.Dropped, "NoIcalMapping", "MS-OXCICAL maps no ATTENDEE field from this recipient property.", rp);
                    }
                }
            }
        }

        private static readonly HashSet<ushort> Consumed =
        [
            MapiPid.DisplayName, MapiPid.AddressType, MapiPid.EmailAddress, MapiPid.SmtpAddress, MapiPid.RecipientType, MapiPid.RecipientFlags,
            MapiPid.RecipientTrackStatus, MapiPid.RecipientTrackStatusTime, MapiPid.RecipientDisplayName,
        ];
    }

    private sealed class TaskExporter(LossReport report, Calendar.Component cal, OutlookTask t)
    {
        public void Run()
        {
            Calendar.Component todo = new("VTODO");
            cal.Add(todo);
            IcsComponent e = new(todo);
            MapiMessage m = t.Message;
            byte[] g = m.Properties.Get(OutlookNames.TaskGlobalId, MapiType.Binary)?.GetBytes().ToArray() ?? [];
            e.SetText("UID", g.Length == 16 ? System.Convert.ToHexString(g) + "@onemail" : g.Length > 0 ? System.Convert.ToHexString(g) : System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t.Subject ?? "")).AsSpan(0, 16)) + "@onemail");
            DateTimeOffset stamp = m.Properties.Get(MapiPid.LastModificationTime, MapiType.Time)?.AsTime() ?? new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
            e.SetTime("DTSTAMP", new IcsTime(DateTime.SpecifyKind(stamp.UtcDateTime, DateTimeKind.Utc), IcsTimeKind.Utc));
            if (t.Subject is { } s)
            {
                e.SetText("SUMMARY", s);
            }

            if (m.Body is { Length: > 0 } body)
            {
                e.SetText("DESCRIPTION", body.TrimEnd('\r', '\n'));
            }

            // MS-OXOTASK dates are calendar days: they are written as DATE values.
            if (t.StartDate is { } sd)
            {
                e.SetTime("DTSTART", new IcsTime(sd.Date, IcsTimeKind.Date));
            }

            if (t.DueDate is { } dd)
            {
                e.SetTime("DUE", new IcsTime(dd.Date, IcsTimeKind.Date));
            }

            if (t.DateCompleted is { } dc)
            {
                e.SetTime("COMPLETED", new IcsTime(DateTime.SpecifyKind(dc, DateTimeKind.Utc), IcsTimeKind.Utc));
            }

            string status = t.Status switch
            {
                OutlookTaskStatus.Complete => "COMPLETED",
                OutlookTaskStatus.InProgress => "IN-PROCESS",
                OutlookTaskStatus.NotStarted => "NEEDS-ACTION",
                _ => "IN-PROCESS",
            };
            e.SetText("STATUS", status);
            if (t.Status is OutlookTaskStatus.Waiting or OutlookTaskStatus.Deferred)
            {
                report.Add("mapi", "PidLidTaskStatus", LossKind.Approximated, "TaskStatusWeakened", "VTODO has no waiting or deferred status; IN-PROCESS was written.");
            }

            if (t.PercentComplete is { } pc)
            {
                e.Component.Add(new Calendar.ContentProperty("PERCENT-COMPLETE", ((int)Math.Round(pc * 100)).ToString(CultureInfo.InvariantCulture)));
            }

            if (t.Importance is { } imp)
            {
                e.Component.Add(new Calendar.ContentProperty("PRIORITY", imp switch { 2 => "1", 1 => "5", _ => "9" }));
            }

            if (t.Sensitivity is { } sens && sens != 0)
            {
                e.SetText("CLASS", sens switch { 2 => "PRIVATE", 3 => "CONFIDENTIAL", _ => "X-PERSONAL" });
            }

            if (t.Recurrence is { } r)
            {
                string? rule = OutlookRecurrence.ToRRule(r, 0, d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                if (rule is not null && t.StartDate is not null)
                {
                    Calendar.ContentProperty rp = new("RRULE", rule);
                    todo.Add(rp);
                }
                else
                {
                    report.Add("mapi", "PidLidTaskRecurrence", LossKind.Dropped, "RecurrenceNotCarried", "The task recurrence has no iCalendar form or the task has no start date.");
                }
            }

            foreach (MapiProperty p in m.Properties)
            {
                if (p.Name is { } n)
                {
                    if (Carried.Contains(n) || n == Exporter.Keywords)
                    {
                        continue;
                    }

                    report.Add("mapi", n.ToString(), Derived.Contains(n) ? LossKind.Internal : LossKind.Dropped, Derived.Contains(n) ? "DerivedProperty" : "NoIcalMapping",
                        Derived.Contains(n) ? "Recomputed from the iCalendar fields on import." : "MS-OXCICAL maps no VTODO field from this property.");
                    continue;
                }

                if (CarriedTags.Contains(p.Id) || p.Id == MapiPid.MessageClass)
                {
                    continue;
                }

                string name = MapiPropertyNames.GetName(p.Id) ?? p.Tag.ToString();
                bool book = OxcMail.MapiToMimeConverter.Bookkeeping.Contains(p.Id);
                report.Add("mapi", name, book ? LossKind.Internal : LossKind.Dropped, book ? "StoreBookkeeping" : "NoIcalMapping", book ? "A store-internal property." : "MS-OXCICAL maps no VTODO field from this property.");
            }
        }
    }
}
