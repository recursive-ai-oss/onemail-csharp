using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMail.Display;

/// <summary>Date and text helpers for the card. Dates use the invariant culture and a fixed English layout, so output never depends on the machine.</summary>
internal static class CalendarCardFormat
{
    internal static string Date(DateTime d) =>
        d.Day.ToString(CultureInfo.InvariantCulture) + " " + CalendarCardStrings.ShortMonths[d.Month] + " " + d.Year.ToString("0000", CultureInfo.InvariantCulture);

    internal static string LongDate(DateTime d) => CalendarCardStrings.ShortDays[(int)d.DayOfWeek] + ", " + Date(d);

    internal static string Time(DateTime d) => d.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + d.Minute.ToString("00", CultureInfo.InvariantCulture);

    internal static string Offset(TimeSpan o)
    {
        bool neg = o < TimeSpan.Zero;
        TimeSpan a = o.Duration();
        return "UTC" + (neg ? "-" : "+") + a.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + a.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Makes text safe to show: control characters and bidirectional overrides and isolates (which can reorder what follows them) become spaces,
    /// runs of white space collapse, and the result is cut at <paramref name="max"/> characters without splitting a surrogate pair.
    /// </summary>
    internal static string Clean(string? s, int max, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        StringBuilder sb = new(Math.Min(s.Length, max + 1));
        bool space = false;
        foreach (char c in s)
        {
            if (sb.Length >= max)
            {
                truncated = true;
                break;
            }

            if (IsBlank(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
                if (sb.Length >= max)
                {
                    truncated = true;
                    break;
                }
            }

            sb.Append(c);
        }

        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1]))
        {
            sb.Length--;
        }

        if (truncated)
        {
            sb.Append('…');
        }

        return sb.ToString();
    }

    private static bool IsBlank(char c) =>
        c <= ' ' || (c >= '\u007f' && c <= '\u009f') || c == '\u00ad' || (c >= '\u200b' && c <= '\u200f') || (c >= '\u2028' && c <= '\u202e')
        || (c >= '\u2060' && c <= '\u206f') || c == '\ufeff' || c == '\ufffe' || c == '\uffff';

    /// <summary>Like <see cref="Clean"/> but keeps line breaks, for the description: at most <paramref name="maxLines"/> lines, <paramref name="max"/> characters in all.</summary>
    internal static string CleanMultiline(string? s, int max, int maxLines, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        StringBuilder sb = new();
        int lines = 0;
        int budget = max;
        foreach (string raw in s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (budget <= 0 || lines >= maxLines)
            {
                if (raw.Trim().Length > 0)
                {
                    truncated = true;
                    break;
                }

                continue;
            }

            string line = Clean(raw, budget, out bool cut);
            if (line.Length == 0 && sb.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
            lines++;
            budget -= line.Length + 1;
            if (cut)
            {
                truncated = true;
                break;
            }
        }

        string result = sb.ToString().TrimEnd('\n');
        return truncated && !result.EndsWith(CalendarCardStrings.Ellipsis, StringComparison.Ordinal) ? result + CalendarCardStrings.Ellipsis : result;
    }
}
