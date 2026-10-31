using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Fuzz;

// The Outlook calendar structures and their iCalendar conversion on hostile input. Byte 0 picks the input:
//   0: the rest is an AppointmentRecurrencePattern blob (MS-OXOCAL 2.2.1.44): parsed, written, read again, and expanded to days and an RRULE;
//   1: the rest is a TZDEFINITION, a TIME_ZONE_STRUCT or a bare TZRULE: parsed, written, and turned into a VTIMEZONE;
//   2: the rest is a patch list (16-bit offset, value) over a valid iCalendar series with an exception, an alarm and attendees:
//      converted to Outlook items and each item back to iCalendar;
//   3: the iCalendar is converted to an item whose property values are then damaged (4-byte records: property index, 16-bit offset, value),
//      read through the typed views and converted back.
// Nothing may throw, hang or grow without bound, whatever the input holds.
internal static class OxocalTarget
{
    private const string Series =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//x//EN\r\nMETHOD:REQUEST\r\n" +
        "BEGIN:VTIMEZONE\r\nTZID:Pacific Standard Time\r\nBEGIN:STANDARD\r\nDTSTART:20071104T020000\r\nTZOFFSETFROM:-0700\r\nTZOFFSETTO:-0800\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nEND:STANDARD\r\n" +
        "BEGIN:DAYLIGHT\r\nDTSTART:20070311T020000\r\nTZOFFSETFROM:-0800\r\nTZOFFSETTO:-0700\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n" +
        "BEGIN:VEVENT\r\nUID:a@example.com\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;TZID=Pacific Standard Time:20260105T100000\r\nDTEND;TZID=Pacific Standard Time:20260105T103000\r\n" +
        "RRULE:FREQ=WEEKLY;INTERVAL=2;COUNT=10;BYDAY=MO,TH;WKST=SU\r\nEXDATE;TZID=Pacific Standard Time:20260108T100000\r\nSUMMARY:Series\r\nLOCATION:Room\r\nSEQUENCE:1\r\n" +
        "ORGANIZER;CN=Org:mailto:org@example.com\r\nATTENDEE;CN=A;PARTSTAT=ACCEPTED;ROLE=REQ-PARTICIPANT;RSVP=TRUE:mailto:a@example.com\r\nATTENDEE;PARTSTAT=NEEDS-ACTION;ROLE=OPT-PARTICIPANT:mailto:b@example.com\r\n" +
        "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nDESCRIPTION:r\r\nEND:VALARM\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nUID:a@example.com\r\nDTSTAMP:20260101T000000Z\r\nRECURRENCE-ID;TZID=Pacific Standard Time:20260119T100000\r\nDTSTART;TZID=Pacific Standard Time:20260120T110000\r\nDTEND;TZID=Pacific Standard Time:20260120T113000\r\nSUMMARY:Moved\r\nEND:VEVENT\r\n" +
        "BEGIN:VTODO\r\nUID:t@example.com\r\nDTSTAMP:20260101T000000Z\r\nSUMMARY:Task\r\nDUE;VALUE=DATE:20260110\r\nSTATUS:IN-PROCESS\r\nPERCENT-COMPLETE:40\r\nEND:VTODO\r\n" +
        "END:VCALENDAR\r\n";

    private static readonly byte[] SeriesBytes = System.Text.Encoding.ASCII.GetBytes(Series);

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> rest = data[1..];
        switch (data[0] & 3)
        {
            case 0:
                Recurrence(rest);
                break;
            case 1:
                Zones(rest);
                break;
            case 2:
                byte[] ics = (byte[])SeriesBytes.Clone();
                for (int i = 0; i + 2 < rest.Length; i += 3)
                {
                    ics[(rest[i] | (rest[i + 1] << 8)) % ics.Length] = rest[i + 2];
                }

                Convert(ics, default);
                break;
            default:
                Convert(SeriesBytes, rest);
                break;
        }
    }

    private static void Recurrence(ReadOnlySpan<byte> data)
    {
        if (!AppointmentRecurrencePattern.TryParse(data, out AppointmentRecurrencePattern? r, new List<CalendaringDefect>()) || r is null)
        {
            return;
        }

        byte[] back = r.ToArray();
        _ = AppointmentRecurrencePattern.TryParse(back, out _);
        _ = OutlookRecurrence.Days(r.Pattern, 2000).Take(2000).Count();
        _ = OutlookRecurrence.ToRRule(r.Pattern, r.StartTimeOffset, d => d.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture), new List<CalendaringDefect>());
    }

    private static void Zones(ReadOnlySpan<byte> data)
    {
        if (TimeZoneDefinition.TryParse(data, out TimeZoneDefinition? d, new List<CalendaringDefect>()) && d is not null)
        {
            _ = TimeZoneDefinition.TryParse(d.ToArray(), out _);
            _ = d.ToVTimeZone("Fuzz");
        }

        if (TimeZoneStruct.TryParse(data, out TzRule? rule, new List<CalendaringDefect>()) && rule is not null)
        {
            _ = TimeZoneStruct.TryParse(TimeZoneStruct.ToArray(rule), out _);
            _ = new TimeZoneDefinition("Fuzz", [rule]).ToVTimeZone("Fuzz");
        }
    }

    private static void Convert(byte[] ics, ReadOnlySpan<byte> damage)
    {
        IcsComponent cal = IcsDocument.Calendars(ContentDocument.Parse(ics)).FirstOrDefault()!;
        if (cal is null)
        {
            return;
        }

        foreach (ConversionResult<MapiMessage> item in IcsToMapiConverter.ConvertAll(cal))
        {
            MapiMessage m = item.Value;
            Damage(m, damage);
            if (OutlookAppointment.TryCreate(m) is { } a)
            {
                _ = a.Recurrence;
                _ = a.Exceptions;
                _ = a.Attendees.Count;
                _ = a.DeletedInstances.Count;
            }

            _ = OutlookTask.TryCreate(m);
            _ = new MapiToIcsConverter().Convert(m);
        }
    }

    private static void Damage(MapiMessage m, ReadOnlySpan<byte> records)
    {
        for (int i = 0; i + 3 < records.Length; i += 4)
        {
            if (m.Properties.Count == 0)
            {
                return;
            }

            MapiProperty p = m.Properties[records[i] % m.Properties.Count];
            if (p.IsMultiValued || p.IsLazy)
            {
                continue;
            }

            byte[] bytes = p.GetBytes().ToArray();
            int offset = records[i + 1] | (records[i + 2] << 8);
            if (bytes.Length > 0)
            {
                bytes[offset % bytes.Length] = records[i + 3];
            }

            m.Properties.Set(MapiProperty.CreateOwned(p.Tag, bytes, p.Name));
        }
    }
}
