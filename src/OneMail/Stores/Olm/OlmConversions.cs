using System.Globalization;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Conversion;

namespace RecursiveAI.OneMail.Stores.Olm;

/// <summary>
/// Converts an OLM appointment, task or note to an iCalendar object (RFC 5545): <c>VEVENT</c>, <c>VTODO</c> or <c>VJOURNAL</c> inside a <c>VCALENDAR</c>.
/// Everything the calendar object has no place for is listed in the loss report: one entry per top-level element left behind, plus the
/// approximations (zone-less times, unverified recurrence kinds). The mapping is written down in <c>docs/olm.md</c>.
/// </summary>
public sealed class OlmToCalendarConverter : IItemConversion<OlmItem, Component>
{
    /// <inheritdoc />
    public ConversionResult<Component> Convert(OlmItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var report = new LossReport();
        var cal = new Component("VCALENDAR");
        Add(cal, "VERSION", "2.0");
        Add(cal, "PRODID", "-//RecursiveAI//OneMail//EN");
        switch (source.OlmKind)
        {
            case OlmItemKind.Appointment:
                cal.Add(Event(source, cal, report));
                break;
            case OlmItemKind.Task:
                cal.Add(Todo(source, report));
                break;
            case OlmItemKind.Note:
                cal.Add(Journal(source));
                break;
            default:
                report.Add(OlmMapping.Family, source.Element.Name, LossKind.Dropped, "NotACalendarItem", "The record is not an appointment, task or note; nothing was converted.");
                break;
        }

        foreach (OlmElement e in source.UnmappedElements())
        {
            report.Add(OlmMapping.Family, e.Name, LossKind.Dropped, "Unmapped", "The calendar object has no property for this element; it stays in the OLM record.");
        }

        return new ConversionResult<Component>(cal, report);
    }

    private static ContentProperty Add(Component c, string name, string value, params (string Name, string Value)[] parameters)
    {
        var p = new ContentProperty(name, value);
        foreach ((string n, string v) in parameters)
        {
            p.Parameters.Add(new ContentParameter(n, v));
        }

        c.Add(p);
        return p;
    }

    private static string E(string s) => ContentValues.Escape(s);

    private static string Uid(OlmItem item) => "olm-" + item.OlmKind.ToString().ToLowerInvariant() + "-" + item.Provenance.Index.ToString(CultureInfo.InvariantCulture) + "@onemail.invalid";

    private static Component Event(OlmItem it, Component cal, LossReport report)
    {
        var ev = new Component("VEVENT");
        OlmElement r = it.Element;
        Add(ev, "UID", r.ChildText("OPFCalendarEventCopyUUID") is { Length: > 0 } u ? u : Uid(it));
        Add(ev, "DTSTAMP", OlmValues.Date(r.ChildText("OPFCalendarEventCopyModDate")) is { } m ? OlmValues.Utc(m) : "19700101T000000Z");
        if (it.Subject is { } s)
        {
            Add(ev, "SUMMARY", E(s));
        }

        if (r.ChildText("OPFCalendarEventCopyLocation") is { Length: > 0 } loc)
        {
            Add(ev, "LOCATION", E(loc));
        }

        if (r.ChildText("OPFCalendarEventCopyDescriptionPlain") is { Length: > 0 } d)
        {
            Add(ev, "DESCRIPTION", E(d));
        }

        if (r.ChildText("OPFCalendarEventCopyDescription") is { Length: > 0 } h)
        {
            Add(ev, "X-ALT-DESC", E(h), ("FMTTYPE", "text/html"));
        }

        // The zone: the record carries a VTIMEZONE inside its own little calendar text. Times are wall-clock in that zone.
        string? tzid = ImportZone(r.ChildText("OPFCalendarEventGetStartTimeZoneICSData"), cal);
        DateTimeOffset? start = OlmValues.Date(r.ChildText("OPFCalendarEventCopyStartTime"));
        DateTimeOffset? end = OlmValues.Date(r.ChildText("OPFCalendarEventCopyEndTime"));
        bool allDay = it.Flag("OPFCalendarEventGetIsAllDayEvent");
        if (r.ChildText("OPFCalendarEventCopyStartTimeZone") != r.ChildText("OPFCalendarEventCopyEndTimeZone") && r.Child("OPFCalendarEventCopyEndTimeZone") is not null)
        {
            report.Add(OlmMapping.Family, "OPFCalendarEventCopyEndTimeZone", LossKind.Approximated, "EndZoneIgnored", "The end time is in a different zone from the start; both are written in the start zone.");
        }

        if (start is { } st)
        {
            if (allDay)
            {
                Add(ev, "DTSTART", OlmValues.DateOnly(st), ("VALUE", "DATE"));
                if (end is { } en)
                {
                    // DTEND of a date-valued event is exclusive; an end at midnight already is, anything later runs into that day.
                    DateTimeOffset exclusive = en.TimeOfDay == TimeSpan.Zero ? en : en.Date.AddDays(1);
                    Add(ev, "DTEND", OlmValues.DateOnly(exclusive), ("VALUE", "DATE"));
                    report.Add(OlmMapping.Family, "OPFCalendarEventCopyEndTime", LossKind.Approximated, "AllDayEnd", "An all-day event's end is made the exclusive date RFC 5545 asks for.");
                }
            }
            else
            {
                Time(ev, "DTSTART", st, tzid);
                if (end is { } en)
                {
                    Time(ev, "DTEND", en, tzid);
                }

                if (tzid is null)
                {
                    report.Add(OlmMapping.Family, "OPFCalendarEventCopyStartTime", LossKind.Approximated, "FloatingTime", "The record has no usable time zone; the times are written as floating local times.");
                }
            }
        }

        if (r.Child("OPFCalendarEventCopyRecurrence") is { } rec && Recurrence(rec, start, report, "OPFCalendarEventCopyRecurrence") is { } rule)
        {
            Add(ev, "RRULE", rule);
        }

        if (it.Flag("OPFCalendarEventGetIsPrivate"))
        {
            Add(ev, "CLASS", "PRIVATE");
        }

        if (r.Child("OPFCalendarEventCopyOrganizer") is { } org && org.Text.Trim() is { Length: > 0 } o)
        {
            if (o.Contains('@', StringComparison.Ordinal))
            {
                Add(ev, "ORGANIZER", "mailto:" + o);
            }
            else
            {
                report.Add(OlmMapping.Family, "OPFCalendarEventCopyOrganizer", LossKind.Dropped, "OrganizerNotAnAddress", "The organizer has no address; a calendar organizer must be an address.");
            }
        }

        foreach (MailAddress a in it.ToRecipients)
        {
            ContentProperty p = Add(ev, "ATTENDEE", a.Address.Length > 0 ? "mailto:" + a.Address : "invalid:nomail");
            if (a.Name is { Length: > 0 } cn)
            {
                p.Parameters.Add(new ContentParameter("CN", cn));
            }
        }

        if (r.Child("OPFCalendarEventCopyAttendeeList") is { Children.Count: > 0 })
        {
            report.Add(OlmMapping.Family, "OPFCalendarEventCopyAttendeeList", LossKind.Approximated, "AttendeeStatus", "Attendee status, type and response flags are numeric codes OneMail has not matched to a client; only names and addresses are written.");
        }

        if (it.Flag("OPFCalendarEventGetHasReminder") && OlmItem.TryParseNumber(r.ChildText("OPFCalendarEventCopyReminderDelta"), out double secs) && secs >= 0)
        {
            var alarm = new Component("VALARM");
            Add(alarm, "ACTION", "DISPLAY");
            Add(alarm, "DESCRIPTION", "Reminder");
            Add(alarm, "TRIGGER", "-PT" + ((long)secs).ToString(CultureInfo.InvariantCulture) + "S");
            ev.Add(alarm);
        }

        Categories(ev, it);
        if (it.Attachments.Count > 0)
        {
            report.Add(OlmMapping.Family, "OPFCalendarEventCopyAttachmentList", LossKind.Dropped, "CalendarAttachments", "Attachment content is not written into the calendar object; it stays in the archive.");
        }

        return ev;
    }

    private static Component Todo(OlmItem it, LossReport report)
    {
        var td = new Component("VTODO");
        OlmElement r = it.Element;
        Add(td, "UID", Uid(it));
        Add(td, "DTSTAMP", OlmValues.Date(r.ChildText("OPFTaskCopyModDate")) is { } m ? OlmValues.Utc(m) : "19700101T000000Z");
        if (it.Subject is { } s)
        {
            Add(td, "SUMMARY", E(s));
        }

        if (r.ChildText("OPFTaskCopyNotePlain") is { Length: > 0 } d)
        {
            Add(td, "DESCRIPTION", E(d));
        }

        if (r.ChildText("OPFTaskCopyNote") is { Length: > 0 } h)
        {
            Add(td, "X-ALT-DESC", E(h), ("FMTTYPE", "text/html"));
        }

        // The dates in a task carry no zone and sit at local midnight expressed in UTC, so they are written as the instants they are.
        if (OlmValues.Date(r.ChildText("OPFTaskCopyStartDateTime")) is { } st)
        {
            Add(td, "DTSTART", OlmValues.Utc(st));
        }

        if (OlmValues.Date(r.ChildText("OPFTaskCopyDueDateTime")) is { } due)
        {
            Add(td, "DUE", OlmValues.Utc(due));
        }

        if (OlmValues.Date(r.ChildText("OPFTaskCopyCompletedDateTime")) is { } done)
        {
            Add(td, "COMPLETED", OlmValues.Utc(done));
            Add(td, "STATUS", "COMPLETED");
        }

        if (OlmValues.Date(r.ChildText("OPFTaskCopyReminderTime")) is { } rem)
        {
            var alarm = new Component("VALARM");
            Add(alarm, "ACTION", "DISPLAY");
            Add(alarm, "DESCRIPTION", "Reminder");
            Add(alarm, "TRIGGER", OlmValues.Utc(rem), ("VALUE", "DATE-TIME"));
            td.Add(alarm);
        }

        if (r.Child("OPFTaskCopyRecurrence") is { } rec && Recurrence(rec, OlmValues.Date(r.ChildText("OPFTaskCopyStartDateTime")), report, "OPFTaskCopyRecurrence") is { } rule)
        {
            Add(td, "RRULE", rule);
        }

        Categories(td, it);
        return td;
    }

    private static Component Journal(OlmItem it)
    {
        var j = new Component("VJOURNAL");
        OlmElement r = it.Element;
        Add(j, "UID", Uid(it));
        Add(j, "DTSTAMP", OlmValues.Date(r.ChildText("OPFNoteCopyModDate")) is { } m ? OlmValues.Utc(m) : "19700101T000000Z");
        if (OlmValues.Date(r.ChildText("OPFNoteCopyCreationDate")) is { } c)
        {
            Add(j, "DTSTART", OlmValues.Utc(c));
        }

        if (it.Subject is { } s)
        {
            Add(j, "SUMMARY", E(s));
        }

        if (r.ChildText("OPFNoteCopyText") is { Length: > 0 } h)
        {
            Add(j, "X-ALT-DESC", E(h), ("FMTTYPE", "text/html"));
        }

        return j;
    }

    private static void Categories(Component c, OlmItem it)
    {
        IReadOnlyList<string> names = it.Categories;
        if (names.Count > 0)
        {
            Add(c, "CATEGORIES", string.Join(',', names.Select(E)));
        }
    }

    private static void Time(Component c, string name, DateTimeOffset t, string? tzid)
    {
        if (tzid is null)
        {
            Add(c, name, OlmValues.Local(t));
        }
        else
        {
            Add(c, name, OlmValues.Local(t), ("TZID", tzid));
        }
    }

    // The record keeps its zone as a VCALENDAR text with one VTIMEZONE. Copy that component into the output; return its TZID.
    private static string? ImportZone(string? ics, Component into)
    {
        if (string.IsNullOrWhiteSpace(ics))
        {
            return null;
        }

        ContentDocument doc = ContentDocument.Parse(ics);
        foreach (Component root in doc.Components)
        {
            foreach (Component tz in root.FindComponents("VTIMEZONE"))
            {
                if (tz.FindProperty("TZID")?.Value is { Length: > 0 } id)
                {
                    into.Add(tz);
                    return id;
                }
            }
        }

        return null;
    }

    private static readonly string[] Days = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    // Maps OPFRecurrencePattern* to an RRULE. Kinds seen in real archives: Daily, Weekly, AbsoluteMonthly, AbsoluteYearly.
    // Relative kinds are mapped by name and cannot be checked against a client yet; they are reported as approximations.
    private static string? Recurrence(OlmElement rec, DateTimeOffset? start, LossReport report, string path)
    {
        OlmElement? pat = rec.Child("OPFRecurrencePattern");
        string type = pat?.ChildText("OPFRecurrencePatternType") ?? string.Empty;
        string interval = pat?.ChildText("OPFRecurrencePatternInterval") is { } iv && OlmItem.TryParseNumber(iv, out double n) && n >= 1 ? ((long)n).ToString(CultureInfo.InvariantCulture) : "1";
        string rule;
        switch (type)
        {
            case "OPFRecurrencePatternDaily":
                rule = "FREQ=DAILY;INTERVAL=" + interval;
                break;
            case "OPFRecurrencePatternWeekly":
                rule = "FREQ=WEEKLY;INTERVAL=" + interval;
                string by = WeekDays(pat!.Child("OPFRecurrencePatternDaysOfWeek"));
                if (by.Length == 0 && start is { } s)
                {
                    by = Days[(int)s.DayOfWeek];
                }

                if (by.Length > 0)
                {
                    rule += ";BYDAY=" + by;
                }

                break;
            case "OPFRecurrencePatternAbsoluteMonthly":
                rule = "FREQ=MONTHLY;INTERVAL=" + interval + Num(pat!, "OPFRecurrencePatternDayOfMonth", ";BYMONTHDAY=");
                break;
            case "OPFRecurrencePatternAbsoluteYearly":
                rule = "FREQ=YEARLY;INTERVAL=" + interval + Num(pat!, "OPFRecurrencePatternMonth", ";BYMONTH=") + Num(pat!, "OPFRecurrencePatternDayOfMonth", ";BYMONTHDAY=");
                break;
            case "OPFRecurrencePatternRelativeMonthly" or "OPFRecurrencePatternRelativeYearly":
                bool yearly = type.EndsWith("Yearly", StringComparison.Ordinal);
                string days = WeekDays(pat!.Child("OPFRecurrencePatternDaysOfWeek"));
                string nth = OlmItem.TryParseNumber(pat.ChildText("OPFRecurrencePatternWeek"), out double wk) ? (wk >= 5 ? "-1" : ((long)wk).ToString(CultureInfo.InvariantCulture)) : "1";
                rule = (yearly ? "FREQ=YEARLY" : "FREQ=MONTHLY") + ";INTERVAL=" + interval + (yearly ? Num(pat, "OPFRecurrencePatternMonth", ";BYMONTH=") : string.Empty)
                    + (days.Length > 0 ? ";BYDAY=" + string.Join(',', days.Split(',').Select(d => nth + d)) : string.Empty);
                report.Add(OlmMapping.Family, path, LossKind.Approximated, "RelativeRecurrence", "Relative monthly and yearly patterns are mapped by name; their week numbering (5 meaning last) is not checked against a client.");
                break;
            default:
                report.Add(OlmMapping.Family, path, LossKind.Dropped, "RecurrenceKind", "The recurrence pattern '" + type + "' is not one OneMail maps.");
                return null;
        }

        bool numbered = rec.ChildText("OPFRecurrenceIsNumbered") is { } nb && OlmItem.TryParseNumber(nb, out double nbv) && nbv != 0;
        bool noEnd = rec.ChildText("OPFRecurrenceIsNoEnd") is { } ne && OlmItem.TryParseNumber(ne, out double nev) && nev != 0;
        bool hasEnd = rec.ChildText("OPFRecurrenceHasEndDate") is { } he && OlmItem.TryParseNumber(he, out double hev) && hev != 0;
        if (numbered && OlmItem.TryParseNumber(rec.ChildText("OPFRecurrenceGetOccurenceCount"), out double cnt) && cnt >= 1)
        {
            rule += ";COUNT=" + ((long)cnt).ToString(CultureInfo.InvariantCulture);
        }
        else if (hasEnd && !noEnd && OlmValues.Date(rec.ChildText("OPFRecurrenceCopyEndDate")) is { } until)
        {
            rule += ";UNTIL=" + OlmValues.Utc(until);
            report.Add(OlmMapping.Family, path, LossKind.Approximated, "RecurrenceEnd", "The end date is written as the UTC instant the record holds; the record carries no zone for it.");
        }

        return rule;
    }

    private static string Num(OlmElement e, string child, string prefix) =>
        OlmItem.TryParseNumber(e.ChildText(child), out double d) ? prefix + ((long)d).ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string WeekDays(OlmElement? d)
    {
        if (d is null)
        {
            return string.Empty;
        }

        bool On(string n) => d.ChildText(n) is { } t && OlmItem.TryParseNumber(t, out double v) && v != 0;
        var list = new List<string>();
        string[] names = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
        for (int i = 0; i < 7; i++)
        {
            bool weekdayFlag = On("weekdays") && i is >= 1 and <= 5;
            bool weekendFlag = On("weekenddays") && i is 0 or 6;
            if (On(names[i]) || On("allDays") || weekdayFlag || weekendFlag)
            {
                list.Add(Days[i]);
            }
        }

        return string.Join(',', list);
    }
}

/// <summary>
/// Converts an OLM contact or group to a vCard (RFC 6350): a contact to vCard 3.0, a group to a vCard 4.0 with <c>KIND:group</c> and one
/// <c>MEMBER</c> per address. Elements with no vCard property are listed in the loss report and stay in the OLM record.
/// </summary>
public sealed class OlmToVCardConverter : IItemConversion<OlmItem, Component>
{
    /// <inheritdoc />
    public ConversionResult<Component> Convert(OlmItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var report = new LossReport();
        var card = new Component("VCARD");
        OlmElement r = source.Element;
        bool group = source.OlmKind == OlmItemKind.Group;
        if (source.OlmKind is not (OlmItemKind.Contact or OlmItemKind.Group))
        {
            report.Add(OlmMapping.Family, r.Name, LossKind.Dropped, "NotAContact", "The record is not a contact or group; nothing was converted.");
            return new ConversionResult<Component>(card, report);
        }

        Add(card, "VERSION", group ? "4.0" : "3.0");
        Add(card, "PRODID", "-//RecursiveAI//OneMail//EN");
        string fn = source.Subject ?? Company(r) ?? FirstEmail(r) ?? string.Empty;
        Add(card, "FN", E(fn));
        if (group)
        {
            Add(card, "KIND", "group");
            if (r.Child("OPFGroupCopyMemberList") is { } members)
            {
                foreach (OlmElement a in members.Children)
                {
                    if (a.Attribute("OPFContactEmailAddressAddress") is { Length: > 0 } addr)
                    {
                        ContentProperty p = Add(card, "MEMBER", "mailto:" + addr);
                        if (a.Attribute("OPFContactEmailAddressName") is { Length: > 0 } name)
                        {
                            report.Add(OlmMapping.Family, "OPFContactEmailAddressName", LossKind.Dropped, "MemberName", "A group member is written as an address; the member's display name has no vCard property.", "member");
                        }

                        _ = p;
                    }
                }
            }
        }
        else
        {
            Contact(card, r, report);
        }

        if (OlmValues.Date(r.ChildText(group ? "OPFGroupCopyModDate" : "OPFContactCopyModDate")) is { } rev)
        {
            Add(card, "REV", OlmValues.Utc(rev));
        }

        foreach (OlmElement e in source.UnmappedElements())
        {
            report.Add(OlmMapping.Family, e.Name, LossKind.Dropped, "Unmapped", "The vCard has no property for this element; it stays in the OLM record.");
        }

        return new ConversionResult<Component>(card, report);
    }

    private static ContentProperty Add(Component c, string name, string value, params (string Name, string Value)[] parameters)
    {
        var p = new ContentProperty(name, value);
        foreach ((string n, string v) in parameters)
        {
            // TYPE=work,voice is two values, not one value with a comma in it.
            p.Parameters.Add(new ContentParameter(n, v.Split(',')));
        }

        c.Add(p);
        return p;
    }

    private static string E(string s) => ContentValues.Escape(s);

    private static string? Company(OlmElement r) => Get(r, "OPFContactCopyBusinessCompany");

    private static string? FirstEmail(OlmElement r) =>
        r.Child("OPFContactCopyEmailAddressList")?.Children.Select(c => c.Attribute("OPFContactEmailAddressAddress")).FirstOrDefault(a => !string.IsNullOrEmpty(a));

    private static string? Get(OlmElement r, string name) => r.ChildText(name) is { Length: > 0 } t ? t : null;

    private static void Contact(Component card, OlmElement r, LossReport report)
    {
        string[] n = [Get(r, "OPFContactCopyLastName") ?? string.Empty, Get(r, "OPFContactCopyFirstName") ?? string.Empty, Get(r, "OPFContactCopyMiddleName") ?? string.Empty,
            Get(r, "OPFContactCopyTitle") ?? string.Empty, Get(r, "OPFContactCopySetNameSuffix") ?? string.Empty];
        Add(card, "N", string.Join(';', n.Select(E)));
        if (Get(r, "OPFContactCopyNickName") is { } nick)
        {
            Add(card, "NICKNAME", E(nick));
        }

        string? org = Get(r, "OPFContactCopyBusinessCompany");
        string? dept = Get(r, "OPFContactCopyBusinessDepartment");
        if (org is not null || dept is not null)
        {
            Add(card, "ORG", E(org ?? string.Empty) + (dept is null ? string.Empty : ";" + E(dept)));
        }

        if (Get(r, "OPFContactCopyBusinessTitle") is { } title)
        {
            Add(card, "TITLE", E(title));
        }

        Tel(card, r, "OPFContactCopyBusinessPhone", "work,voice");
        Tel(card, r, "OPFContactCopyBusinessPhone2", "work,voice");
        Tel(card, r, "OPFContactCopyBusinessFax", "work,fax");
        Tel(card, r, "OPFContactCopyHomePhone", "home,voice");
        Tel(card, r, "OPFContactCopyHomePhone2", "home,voice");
        Tel(card, r, "OPFContactCopyHomeFax", "home,fax");
        Tel(card, r, "OPFContactCopyCellPhone", "cell");
        Tel(card, r, "OPFContactCopyPager", "pager");
        Tel(card, r, "OPFContactCopyOtherPhone", "voice");
        Tel(card, r, "OPFContactCopyOtherFax", "fax");
        Tel(card, r, "OPFContactCopyAssistantPhone", "x-assistant");

        // The numbered lists repeat entries of the main list, split by kind; an address already written is not written twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string list in new[] { "OPFContactCopyEmailAddressList", "OPFContactCopyEmailAddressList1", "OPFContactCopyEmailAddressList2", "OPFContactCopyDefaultEmailAddress" })
        {
            if (r.Child(list) is not { } l)
            {
                continue;
            }

            foreach (OlmElement a in l.Children)
            {
                string? addr = a.Attribute("OPFContactEmailAddressAddress");
                if (string.IsNullOrEmpty(addr) || !seen.Add(addr))
                {
                    continue;
                }

                // Slot numbers are matched to work, home and other from archives whose addresses were named after their slot.
                string type = a.Attribute("OPFContactEmailAddressType") switch { "1" => "home", "2" => "other", _ => "work" };
                bool pref = a.Attribute("OPFContactEmailAddressIsDefault") is "1" or "1E0";
                Add(card, "EMAIL", E(addr), ("TYPE", pref ? type + ",pref" : type));
            }
        }

        Adr(card, r, "Home", "home");
        Adr(card, r, "Business", "work");
        Adr(card, r, "Other", "postal");
        if (Get(r, "OPFContactCopyBusinessHomePage") is { } bw)
        {
            Add(card, "URL", E(bw), ("TYPE", "work"));
        }

        if (Get(r, "OPFContactCopyHomeWebPage") is { } hw)
        {
            Add(card, "URL", E(hw), ("TYPE", "home"));
        }

        if (OlmValues.Date(r.ChildText("OPFContactCopyBirthday")) is { } bd)
        {
            Add(card, "BDAY", bd.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            report.Add(OlmMapping.Family, "OPFContactCopyBirthday", LossKind.Approximated, "DateOnlyZone", "The record holds an instant (local midnight in UTC); the UTC date is written.");
        }

        if (OlmValues.Date(r.ChildText("OPFContactCopyAnniversary")) is { } an)
        {
            Add(card, "X-ANNIVERSARY", an.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            report.Add(OlmMapping.Family, "OPFContactCopyAnniversary", LossKind.Approximated, "DateOnlyZone", "The record holds an instant (local midnight in UTC); the UTC date is written, in an extension property because vCard 3.0 has none.");
        }

        if (Get(r, "OPFContactCopySpousesName") is { } sp)
        {
            Add(card, "X-SPOUSE", E(sp));
        }

        if (Get(r, "OPFContactCopyNotesPlain") is { } note)
        {
            Add(card, "NOTE", E(note));
        }

        if (r.Child("OPFContactCopyCategoryList") is { } cl)
        {
            string[] names = cl.Children.Select(c => c.Attribute("OPFCategoryCopyName")).Where(x => !string.IsNullOrEmpty(x)).Select(x => E(x!)).ToArray();
            if (names.Length > 0)
            {
                Add(card, "CATEGORIES", string.Join(',', names));
            }
        }
    }

    private static void Tel(Component card, OlmElement r, string element, string types)
    {
        if (Get(r, element) is { } v)
        {
            Add(card, "TEL", E(v), ("TYPE", types));
        }
    }

    private static readonly string[] AddressParts = ["StreetAddress", "City", "State", "Zip", "Country"];

    private static void Adr(Component card, OlmElement r, string prefix, string type)
    {
        string[] parts = AddressParts.Select(p => Get(r, "OPFContactCopy" + prefix + p) ?? string.Empty).ToArray();
        if (parts.All(p => p.Length == 0))
        {
            return;
        }

        // ADR: post-office box; extended; street; locality; region; postal code; country (RFC 6350 6.3.1).
        Add(card, "ADR", ";;" + E(parts[0]) + ";" + E(parts[1]) + ";" + E(parts[2]) + ";" + E(parts[3]) + ";" + E(parts[4]), ("TYPE", type));
    }
}
