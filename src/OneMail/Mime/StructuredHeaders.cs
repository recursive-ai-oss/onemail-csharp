using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Mime;

/// <summary>A parsed <c>Date</c> header: an instant, the zone it was written in (when known), and the source text.</summary>
/// <param name="Value">The time with the written zone offset. When the zone is unknown it is read as UTC.</param>
/// <param name="ZoneKnown">False for <c>-0000</c>, military letters, an unrecognised zone or no zone (RFC 5322 §3.3, §4.3).</param>
/// <param name="Source">The header text as written (unfolded, comments removed).</param>
public readonly record struct MimeDate(DateTimeOffset Value, bool ZoneKnown, string Source)
{
    /// <summary>The instant in UTC.</summary>
    public DateTimeOffset Utc => Value.ToUniversalTime();

    /// <summary>The written offset from UTC in minutes, or null when the zone is unknown.</summary>
    public int? OffsetMinutes => ZoneKnown ? (int)Value.Offset.TotalMinutes : null;
}

/// <summary>
/// Parses <c>Date</c> values (RFC 5322 §3.3 with the obsolete forms of §4.3). It never throws; a value that is not a date
/// reads as absent with <see cref="MimeDefectKind.InvalidDate"/>. The resolutions are in <c>docs/ambiguities.md</c> (D1–D13).
/// </summary>
public static class DateParser
{
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>Parses a date.</summary>
    /// <param name="rawValue">The header value as written.</param>
    /// <param name="date">The date, when it could be read.</param>
    /// <param name="defects">Receives the defects; may be null.</param>
    /// <param name="baseOffset">Added to defect offsets.</param>
    /// <param name="depth">The entity depth recorded in defects.</param>
    /// <returns>True when a date was read.</returns>
    public static bool TryParse(ReadOnlySpan<byte> rawValue, out MimeDate date, List<MimeDefect>? defects = null, long baseOffset = 0, int depth = 0)
    {
        date = default;
        int rawLength = rawValue.Length;
        void D(MimeDefectKind k) => defects?.Add(new MimeDefect(k, baseOffset, rawLength, depth));

        // Tokens: runs of non-white-space, with comments removed and commas treated as white space.
        var tokens = new List<string>();
        var sb = new StringBuilder();
        int comment = 0;
        foreach (byte c in rawValue)
        {
            if (c == (byte)'(')
            {
                comment++;
            }
            else if (c == (byte)')' && comment > 0)
            {
                comment--;
            }
            else if (comment > 0)
            {
                continue;
            }
            else if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',')
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c < 128 ? (char)c : '?');
            }
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        string source = string.Join(' ', tokens);
        int i = 0;
        int weekday = -1;
        if (i < tokens.Count && tokens[i].Length >= 3 && char.IsLetter(tokens[i][0]))
        {
            weekday = Array.IndexOf(Days, tokens[i][..3].ToLowerInvariant());
            if (weekday >= 0)
            {
                i++;
            }
        }

        // day, month, year (the "5-Jan-2001" form is split first)
        if (i < tokens.Count && tokens[i].Contains('-', StringComparison.Ordinal) && tokens[i].Count(ch => ch == '-') == 2 && char.IsDigit(tokens[i][0]))
        {
            string[] split = tokens[i].Split('-');
            tokens.RemoveAt(i);
            tokens.InsertRange(i, split);
        }

        if (i + 3 > tokens.Count)
        {
            D(MimeDefectKind.InvalidDate);
            return false;
        }

        if (!TryDigits(tokens[i], 2, out int day) || day is < 1 or > 31)
        {
            D(MimeDefectKind.InvalidDate);
            return false;
        }

        string mon = tokens[i + 1];
        int month = mon.Length >= 3 ? Array.IndexOf(Months, mon[..3].ToLowerInvariant()) + 1 : 0;
        if (month == 0 || (mon.Length > 3 && !mon.All(char.IsLetter)))
        {
            D(MimeDefectKind.InvalidDate);
            return false;
        }

        string ys = tokens[i + 2];
        if (!TryDigits(ys, 4, out int year))
        {
            D(MimeDefectKind.InvalidDate);
            return false;
        }

        // RFC 5322 §4.3: a two-digit year is 19xx from 50 (RFC) -- the three reference libraries read 00-68 as 20xx and 69-99 as 19xx,
        // so that is the rule here (ambiguity D2). A three-digit year gets 1900 added (D3).
        if (year < 100)
        {
            year += year < 69 ? 2000 : 1900;
            D(MimeDefectKind.AmbiguousTwoDigitYear);
        }
        else if (ys.Length == 3)
        {
            year += 1900;
            D(MimeDefectKind.ThreeDigitYear);
        }

        i += 3;
        if (i >= tokens.Count)
        {
            D(MimeDefectKind.InvalidDate);
            return false;
        }

        // time
        string[] hms = tokens[i].Split(':');
        if (hms.Length is < 2 or > 3 || !TryDigits(hms[0], 2, out int hour) || !TryDigits(hms[1], 2, out int minute) || hour > 23 || minute > 59)
        {
            D(MimeDefectKind.InvalidDate);
            return false;
        }

        int second = 0;
        if (hms.Length == 3)
        {
            string sec = hms[2];
            int dot = sec.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                D(MimeDefectKind.FractionalSeconds);
                sec = sec[..dot];
            }

            if (!TryDigits(sec, 2, out second) || second > 60)
            {
                D(MimeDefectKind.InvalidDate);
                return false;
            }

            if (second == 60)
            {
                second = 59;
                D(MimeDefectKind.LeapSecond);
            }
        }

        i++;
        if (day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month))
        {
            D(MimeDefectKind.InvalidDate);
            return false;
        }

        // zone
        int? offset = null;
        string zone = i < tokens.Count ? tokens[i] : string.Empty;
        if (zone.Length > 0)
        {
            offset = ZoneOffset(zone, out bool invalid);
            if (invalid)
            {
                D(MimeDefectKind.InvalidTimeZone);
            }
            else if (offset is null)
            {
                D(MimeDefectKind.UnknownTimeZone);
            }
        }
        else
        {
            D(MimeDefectKind.UnknownTimeZone);
        }

        try
        {
            var local = new DateTime(Math.Clamp(year, 1, 9999), month, day, hour, minute, second, DateTimeKind.Unspecified);
            if (weekday >= 0 && (int)local.DayOfWeek != weekday)
            {
                D(MimeDefectKind.WeekdayMismatch);
            }

            var value = new DateTimeOffset(local, TimeSpan.FromMinutes(offset ?? 0));
            date = new MimeDate(value, offset is not null, source);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            D(MimeDefectKind.InvalidDate);
            return false;
        }
    }

    private static bool TryDigits(string s, int maxLength, out int value)
    {
        value = 0;
        if (s.Length == 0 || s.Length > maxLength || !s.All(char.IsAsciiDigit))
        {
            return false;
        }

        value = int.Parse(s, CultureInfo.InvariantCulture);
        return true;
    }

    // RFC 5322 §4.3: obsolete zone names. Military letters, -0000 and anything unrecognised are an unknown zone.
    private static int? ZoneOffset(string zone, out bool invalid)
    {
        invalid = false;
        if (zone.Length >= 5 && (zone[0] == '+' || zone[0] == '-') && zone[1..].Replace(":", string.Empty, StringComparison.Ordinal) is { Length: 4 } digits && digits.All(char.IsAsciiDigit))
        {
            int hh = int.Parse(digits[..2], CultureInfo.InvariantCulture);
            int mm = int.Parse(digits[2..], CultureInfo.InvariantCulture);
            if (hh > 23 || mm > 59)
            {
                invalid = true;
                return null;
            }

            int minutes = (hh * 60) + mm;
            if (zone[0] == '-')
            {
                return minutes == 0 ? null : -minutes;
            }

            return minutes;
        }

        return zone.ToUpperInvariant() switch
        {
            "UT" or "GMT" => 0,
            "EST" => -300,
            "EDT" => -240,
            "CST" => -360,
            "CDT" => -300,
            "MST" => -420,
            "MDT" => -360,
            "PST" => -480,
            "PDT" => -420,
            _ => null,
        };
    }
}

/// <summary>
/// Parses message identifiers (RFC 5322 §3.6.4): <c>Message-ID</c>, <c>In-Reply-To</c>, <c>References</c> and <c>Content-ID</c>.
/// </summary>
public static class MessageIdParser
{
    /// <summary>Reads every <c>&lt;id&gt;</c> in a value, in order, without the angle brackets.</summary>
    /// <param name="rawValue">The header value as written.</param>
    /// <param name="defects">Receives the defects; may be null.</param>
    /// <param name="baseOffset">Added to defect offsets.</param>
    /// <param name="depth">The entity depth recorded in defects.</param>
    /// <returns>The identifiers.</returns>
    public static List<string> Parse(ReadOnlySpan<byte> rawValue, List<MimeDefect>? defects = null, long baseOffset = 0, int depth = 0)
    {
        var ids = new List<string>();
        int i = 0;
        while (i < rawValue.Length)
        {
            byte c = rawValue[i];
            if (c == (byte)'(')
            {
                int d = 0;
                while (i < rawValue.Length)
                {
                    byte x = rawValue[i++];
                    if (x == (byte)'\\' && i < rawValue.Length)
                    {
                        i++;
                    }
                    else if (x == (byte)'(')
                    {
                        d++;
                    }
                    else if (x == (byte)')' && --d == 0)
                    {
                        break;
                    }
                }
            }
            else if (c == (byte)'<')
            {
                int close = rawValue[(i + 1)..].IndexOf((byte)'>');
                int end = close < 0 ? rawValue.Length : i + 1 + close;
                if (close < 0)
                {
                    defects?.Add(new MimeDefect(MimeDefectKind.InvalidMessageId, baseOffset + i, rawValue.Length - i, depth));
                }

                string id = Decode(rawValue[(i + 1)..end]);
                if (id.Length > 0)
                {
                    ids.Add(id);
                }

                i = close < 0 ? rawValue.Length : end + 1;
            }
            else
            {
                if (c is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)','))
                {
                    // Not inside brackets: old mail writes bare identifiers; take the token as the identifier (and say so).
                    int s = i;
                    while (i < rawValue.Length && rawValue[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)'<' or (byte)'('))
                    {
                        i++;
                    }

                    defects?.Add(new MimeDefect(MimeDefectKind.InvalidMessageId, baseOffset + s, i - s, depth));
                    ids.Add(Decode(rawValue[s..i]));
                    continue;
                }

                i++;
            }
        }

        return ids;
    }

    private static string Decode(ReadOnlySpan<byte> id)
    {
        var bytes = new List<byte>(id.Length);
        foreach (byte b in id)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                bytes.Add(b);
            }
        }

        byte[] arr = [.. bytes];
        return System.Text.Unicode.Utf8.IsValid(arr) ? Encoding.UTF8.GetString(arr) : Text.MailEncoding.Windows1252.GetString(arr);
    }
}
