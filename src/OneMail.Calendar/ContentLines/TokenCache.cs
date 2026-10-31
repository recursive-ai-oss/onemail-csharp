using System.Text;

namespace RecursiveAI.OneMail.Calendar.ContentLines;

/// <summary>
/// Shared strings for the property, parameter and component names (and the parameter values) that nearly every file repeats, so
/// reading a 10,000-card address book allocates the text of <c>EMAIL</c> or <c>TYPE</c> once rather than once per line. Only an
/// exact, upper-case, ASCII match is shared; anything else (a lower-case name, an unknown one) is decoded as before, so what
/// the tree stores never changes.
/// </summary>
internal static class TokenCache
{
    private const int Slots = 1024;
    private const int MaxLength = 24;
    // RFC 5545 §3.6–3.8, RFC 6350 §6, RFC 2426 and vCard 2.1: the names and values clients write most.
    private static readonly string[] s_tokens =
    [
        "BEGIN", "END", "VCALENDAR", "VEVENT", "VTODO", "VJOURNAL", "VFREEBUSY", "VTIMEZONE", "STANDARD", "DAYLIGHT", "VALARM", "VCARD",
        "VERSION", "PRODID", "CALSCALE", "METHOD", "UID", "DTSTAMP", "DTSTART", "DTEND", "DUE", "DURATION", "SUMMARY", "DESCRIPTION", "LOCATION",
        "ORGANIZER", "ATTENDEE", "CATEGORIES", "STATUS", "SEQUENCE", "RRULE", "EXDATE", "RDATE", "EXRULE", "RECURRENCE-ID", "CREATED",
        "LAST-MODIFIED", "CLASS", "TRANSP", "PRIORITY", "URL", "ACTION", "TRIGGER", "REPEAT", "TZID", "TZNAME", "TZOFFSETFROM", "TZOFFSETTO",
        "COMMENT", "CONTACT", "GEO", "ATTACH", "RELATED-TO", "COMPLETED", "PERCENT-COMPLETE", "RESOURCES", "X-WR-CALNAME", "X-WR-TIMEZONE",
        "X-WR-CALDESC", "N", "FN", "NICKNAME", "PHOTO", "BDAY", "ADR", "LABEL", "TEL", "EMAIL", "TITLE", "ROLE", "ORG", "NOTE", "REV", "KIND",
        "GENDER", "ANNIVERSARY", "LANG", "IMPP", "SOURCE", "SORT-STRING", "MEMBER", "LOGO", "SOUND", "KEY", "MAILER", "TZ", "AGENT",
        "TYPE", "CN", "PARTSTAT", "RSVP", "VALUE", "ENCODING", "CHARSET", "LANGUAGE", "CUTYPE", "DELEGATED-TO", "DELEGATED-FROM", "SENT-BY",
        "DIR", "ALTID", "PREF", "RELATED", "RANGE", "FMTTYPE", "FBTYPE", "RELTYPE", "PID", "MEDIATYPE", "LABEL", "GEO", "INDEX", "LEVEL",
        "INTERNET", "HOME", "WORK", "CELL", "VOICE", "FAX", "PAGER", "MSG", "VIDEO", "TEXT", "X400", "DOM", "INTL", "POSTAL", "PARCEL",
        "TRUE", "FALSE", "ACCEPTED", "DECLINED", "TENTATIVE", "NEEDS-ACTION", "COMPLETED", "IN-PROCESS", "DELEGATED", "CANCELLED", "CONFIRMED",
        "REQ-PARTICIPANT", "OPT-PARTICIPANT", "NON-PARTICIPANT", "CHAIR", "INDIVIDUAL", "GROUP", "RESOURCE", "ROOM", "UNKNOWN",
        "DATE", "DATE-TIME", "TIME", "DURATION", "BINARY", "URI", "BASE64", "B", "QUOTED-PRINTABLE", "8BIT", "7BIT", "UTF-8", "PUBLIC",
        "PRIVATE", "CONFIDENTIAL", "OPAQUE", "TRANSPARENT", "BUSY", "FREE", "DISPLAY", "AUDIO", "REQUEST", "REPLY", "CANCEL", "PUBLISH",
        "GREGORIAN", "2.0", "3.0", "4.0", "2.1", "0", "1", "2", "3", "5", "9", "START", "END", "DAILY", "WEEKLY", "MONTHLY", "YEARLY",
    ];

    private static readonly string?[] s_table = Build();

    private static string?[] Build()
    {
        string?[] table = new string?[Slots];
        foreach (string t in s_tokens)
        {
            int slot = Slot(Encoding.ASCII.GetBytes(t));
            table[slot] ??= t;
        }

        return table;
    }

    private static int Slot(ReadOnlySpan<byte> s)
    {
        uint h = (uint)s.Length;
        foreach (byte b in s)
        {
            h = (h * 31) + b;
        }

        return (int)(h & (Slots - 1));
    }

    /// <summary>The shared string for these ASCII bytes when they are a known token written exactly so; otherwise a new string.</summary>
    /// <param name="bytes">The UTF-8 bytes.</param>
    /// <returns>The text.</returns>
    public static string Get(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is > 0 and <= MaxLength)
        {
            string? t = s_table[Slot(bytes)];
            if (t is not null && t.Length == bytes.Length)
            {
                bool same = true;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] != t[i])
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    return t;
                }
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
