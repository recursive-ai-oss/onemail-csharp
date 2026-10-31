using System.Text;

namespace RecursiveAI.OneMail.Calendar.Forms;

// Date, time and UTC-offset text in the three spellings in play: iCalendar basic (RFC 5545 §3.3), vCard basic or extended
// (RFC 6350 §4.3, RFC 2426), and the ISO 8601 extended form jCal and jCard use (RFC 7265 §3.6, RFC 7095 §3.5).
internal static class DateForms
{
    // ---- iCalendar ----

    // 20110517 -> 2011-05-17
    internal static string? IcsDateToExtended(string s)
    {
        if (s.Length != 8 || !AllDigits(s))
        {
            return null;
        }

        return s[..4] + "-" + s[4..6] + "-" + s[6..8];
    }

    // 120000Z -> 12:00:00Z
    internal static string? IcsTimeToExtended(string s)
    {
        bool z = s.EndsWith('Z');
        string t = z ? s[..^1] : s;
        if (t.Length != 6 || !AllDigits(t))
        {
            return null;
        }

        return t[..2] + ":" + t[2..4] + ":" + t[4..6] + (z ? "Z" : string.Empty);
    }

    // 20110517T120000Z -> 2011-05-17T12:00:00Z
    internal static string? IcsDateTimeToExtended(string s)
    {
        int t = s.IndexOf('T', StringComparison.Ordinal);
        if (t != 8)
        {
            return null;
        }

        string? d = IcsDateToExtended(s[..8]);
        string? tm = IcsTimeToExtended(s[9..]);
        return d is null || tm is null ? null : d + "T" + tm;
    }

    // Extended back to basic by dropping the separators: 2011-05-17T12:00:00Z -> 20110517T120000Z. iCalendar date-times carry
    // no offsets (RFC 5545 §3.3.5), so every hyphen and colon is a separator.
    internal static string ExtendedToBasic(string s) => s.Replace("-", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal);

    internal static string? IcsOffsetToExtended(string s)
    {
        // +HHMM or +HHMMSS (RFC 5545 §3.3.14) -> +HH:MM[:SS]
        if (s.Length is not (5 or 7) || s[0] is not ('+' or '-') || !AllDigits(s.AsSpan(1)))
        {
            return null;
        }

        return s.Length == 5 ? s[..3] + ":" + s[3..5] : s[..3] + ":" + s[3..5] + ":" + s[5..7];
    }

    internal static string OffsetToBasic(string s) => s.Replace(":", string.Empty, StringComparison.Ordinal);

    internal static bool IsDuration(string s)
    {
        string t = s.TrimStart('+', '-');
        return t.Length > 1 && t[0] == 'P';
    }

    internal static bool IsVCardOffset(string s) =>
        s.Length >= 3 && s[0] is '+' or '-' && AllDigits(s.AsSpan(1, 2)) && (s.Length == 3 || (s.Length == 5 && AllDigits(s.AsSpan(3, 2))) || (s.Length == 6 && s[3] == ':' && AllDigits(s.AsSpan(4, 2))));

    internal static string? VCardOffsetToExtended(string s)
    {
        if (!IsVCardOffset(s))
        {
            return null;
        }

        return s.Length == 5 ? s[..3] + ":" + s[3..5] : s;
    }

    private static bool AllDigits(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    // ---- vCard ----

    // A vCard date and/or time split into its parts, so that basic, extended and truncated spellings all convert.
    internal sealed class VCardDt
    {
        internal string? Year;
        internal string? Month;
        internal string? Day;
        internal bool HasT;
        internal string? Hour;
        internal string? Minute;
        internal string? Second;
        internal string? Zone;

        internal bool HasDate => Year is not null || Month is not null || Day is not null;

        internal bool HasTime => Hour is not null || Minute is not null || Second is not null;

        // Parses basic or extended. Returns null when the text is not a date and/or time.
        internal static VCardDt? Parse(string s)
        {
            VCardDt dt = new();
            string date = s;
            string time = string.Empty;
            int t = s.IndexOf('T', StringComparison.Ordinal);
            if (t >= 0)
            {
                dt.HasT = true;
                date = s[..t];
                time = s[(t + 1)..];
            }

            if (date.Length > 0 && !dt.ParseDate(date))
            {
                return null;
            }

            if (dt.HasT || (date.Length == 0 && time.Length == 0))
            {
                if (!dt.ParseTime(time))
                {
                    return null;
                }
            }
            else if (!dt.HasDate)
            {
                return null;
            }

            return dt.HasDate || dt.HasTime ? dt : null;
        }

        // Parses a time (no date) as in VALUE=time, which has no designator: 102200Z, 10:22, -2200, --00.
        internal static VCardDt? ParseTimeOnly(string s)
        {
            VCardDt dt = new();
            return dt.ParseTime(s) && dt.HasTime ? dt : null;
        }

        private bool ParseDate(string d)
        {
            // Zone-less. Forms: YYYY, YYYYMMDD, YYYY-MM, YYYY-MM-DD, --MM, --MMDD, --MM-DD, ---DD.
            if (d.StartsWith("---", StringComparison.Ordinal))
            {
                string r = d[3..];
                if (r.Length != 2 || !AllDigits(r))
                {
                    return false;
                }

                Day = r;
                return true;
            }

            if (d.StartsWith("--", StringComparison.Ordinal))
            {
                string r = d[2..].Replace("-", string.Empty, StringComparison.Ordinal);
                if (r.Length is not (2 or 4) || !AllDigits(r))
                {
                    return false;
                }

                Month = r[..2];
                Day = r.Length == 4 ? r[2..] : null;
                return true;
            }

            string[] parts = d.Split('-');
            if (parts.Length == 1)
            {
                string r = parts[0];
                if (r.Length is not (4 or 8) || !AllDigits(r))
                {
                    return false;
                }

                Year = r[..4];
                if (r.Length == 8)
                {
                    Month = r[4..6];
                    Day = r[6..];
                }

                return true;
            }

            if (parts.Length is 2 or 3 && parts[0].Length == 4 && parts[1].Length == 2 && AllDigits(parts[0]) && AllDigits(parts[1]))
            {
                Year = parts[0];
                Month = parts[1];
                if (parts.Length == 3)
                {
                    if (parts[2].Length != 2 || !AllDigits(parts[2]))
                    {
                        return false;
                    }

                    Day = parts[2];
                }

                return true;
            }

            return false;
        }

        private bool ParseTime(string t)
        {
            // Zone first: Z, +hh, +hhmm, +hh:mm, or - when it follows digits.
            int z = -1;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] is 'Z' or 'z' or '+')
                {
                    z = i;
                    break;
                }

                if (t[i] == '-' && i > 0 && char.IsAsciiDigit(t[i - 1]))
                {
                    z = i;
                    break;
                }
            }

            if (z >= 0)
            {
                Zone = t[z..];
                if (Zone is "z")
                {
                    Zone = "Z";
                }

                t = t[..z];
                if (Zone != "Z" && !IsVCardOffset(Zone))
                {
                    return false;
                }
            }

            if (t.Length == 0)
            {
                return Zone is not null;
            }

            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                string r = t[2..];
                if (r.Length != 2 || !AllDigits(r))
                {
                    return false;
                }

                Second = r;
                return true;
            }

            if (t[0] == '-')
            {
                string r = t[1..].Replace(":", string.Empty, StringComparison.Ordinal);
                if (r.Length is not (2 or 4) || !AllDigits(r))
                {
                    return false;
                }

                Minute = r[..2];
                Second = r.Length == 4 ? r[2..] : null;
                return true;
            }

            string c = t.Replace(":", string.Empty, StringComparison.Ordinal);
            if (c.Length is not (2 or 4 or 6) || !AllDigits(c))
            {
                return false;
            }

            Hour = c[..2];
            Minute = c.Length >= 4 ? c[2..4] : null;
            Second = c.Length == 6 ? c[4..] : null;
            return true;
        }

        internal string ToExtended() => Format(true);

        internal string ToBasic() => Format(false);

        private string Format(bool ext)
        {
            StringBuilder sb = new();
            if (Year is not null)
            {
                sb.Append(Year);
                if (Month is not null)
                {
                    sb.Append(ext || Day is null ? "-" : string.Empty).Append(Month);
                    if (Day is not null)
                    {
                        sb.Append(ext ? "-" : string.Empty).Append(Day);
                    }
                }
            }
            else if (Month is not null)
            {
                sb.Append("--").Append(Month);
                if (Day is not null)
                {
                    sb.Append(ext ? "-" : string.Empty).Append(Day);
                }
            }
            else if (Day is not null)
            {
                sb.Append("---").Append(Day);
            }

            if (HasT)
            {
                sb.Append('T');
            }

            string sep = ext ? ":" : string.Empty;
            if (Hour is not null)
            {
                sb.Append(Hour);
                if (Minute is not null)
                {
                    sb.Append(sep).Append(Minute);
                    if (Second is not null)
                    {
                        sb.Append(sep).Append(Second);
                    }
                }
            }
            else if (Minute is not null)
            {
                sb.Append('-').Append(Minute);
                if (Second is not null)
                {
                    sb.Append(sep).Append(Second);
                }
            }
            else if (Second is not null)
            {
                sb.Append("--").Append(Second);
            }

            if (Zone is not null)
            {
                sb.Append(ext ? (Zone.Length == 5 ? Zone[..3] + ":" + Zone[3..5] : Zone) : OffsetToBasic(Zone));
            }

            return sb.ToString();
        }
    }
}
