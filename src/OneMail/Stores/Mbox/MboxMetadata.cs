using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Stores.Mbox;

/// <summary>The parsed envelope (<c>From_</c>) line of an mbox message.</summary>
/// <param name="Sender">The text after <c>From </c> up to the next space; empty if the line has none.</param>
/// <param name="DateText">Everything after the sender, trimmed, as written.</param>
/// <param name="Date">
/// <paramref name="DateText"/> read as a date (asctime style with an optional numeric zone before the year, or the
/// RFC 5322 day-month-year order), or null if it does not read as one. With no zone the time is taken as UTC.
/// </param>
public readonly record struct MboxEnvelope(string Sender, string DateText, DateTimeOffset? Date)
{
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>Parses an envelope line, with or without its line ending.</summary>
    /// <param name="line">The line's bytes.</param>
    /// <returns>The envelope. A line that does not start with <c>From </c> gives an empty sender and the whole line as date text.</returns>
    public static MboxEnvelope Parse(ReadOnlySpan<byte> line)
    {
        string text = Encoding.Latin1.GetString(line).TrimEnd('\r', '\n');
        if (text.StartsWith("From ", StringComparison.Ordinal))
        {
            text = text[5..];
        }

        text = text.TrimStart(' ', '\t');
        int sp = text.IndexOfAny([' ', '\t']);
        string sender = sp < 0 ? text : text[..sp];
        string rest = sp < 0 ? string.Empty : text[(sp + 1)..].Trim(' ', '\t');
        return new MboxEnvelope(sender, rest, ParseDate(rest));
    }

    private static DateTimeOffset? ParseDate(string text)
    {
        string[] t = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int month = -1;
        int j = -1;
        for (int i = 0; i < t.Length && month < 0; i++)
        {
            int m = Array.IndexOf(MonthNames, t[i].ToLowerInvariant());
            if (m >= 0)
            {
                month = m + 1;
                j = i;
            }
        }

        if (month < 0)
        {
            return null;
        }

        int day = -1;
        int dayIndex = -1;
        if (j + 1 < t.Length && int.TryParse(t[j + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int d1) && t[j + 1].Length <= 2)
        {
            day = d1;
            dayIndex = j + 1;
        }
        else if (j > 0 && int.TryParse(t[j - 1], NumberStyles.None, CultureInfo.InvariantCulture, out int d2) && t[j - 1].Length <= 2)
        {
            day = d2;
            dayIndex = j - 1;
        }

        if (day < 0)
        {
            return null;
        }

        int timeIndex = Array.FindIndex(t, s => s.Length >= 3 && char.IsAsciiDigit(s[0]) && s.Contains(':', StringComparison.Ordinal));
        if (timeIndex < 0)
        {
            return null;
        }

        string[] hms = t[timeIndex].Split(':');
        if (hms.Length is < 2 or > 3
            || !int.TryParse(hms[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hh)
            || !int.TryParse(hms[1], NumberStyles.None, CultureInfo.InvariantCulture, out int mi))
        {
            return null;
        }

        int ss = 0;
        if (hms.Length == 3 && !int.TryParse(hms[2], NumberStyles.None, CultureInfo.InvariantCulture, out ss))
        {
            return null;
        }

        int year = -1;
        TimeSpan offset = TimeSpan.Zero;
        for (int i = 0; i < t.Length; i++)
        {
            if (i == j || i == dayIndex || i == timeIndex)
            {
                continue;
            }

            string s = t[i];
            if ((s[0] == '+' || s[0] == '-') && s.Length == 5
                && int.TryParse(s.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int z))
            {
                offset = new TimeSpan(z / 100, z % 100, 0);
                if (s[0] == '-')
                {
                    offset = -offset;
                }
            }
            else if (s.Length == 4 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int y) && year < 0)
            {
                year = y;
            }
        }

        if (year < 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(year, month, day, hh, mi, ss, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Message properties that mbox files keep in headers: the <c>Status</c> and <c>X-Status</c> flag letters, Gmail Takeout's
/// <c>X-Gmail-Labels</c> and <c>X-GM-THRID</c>, and <c>X-Keywords</c>.
/// </summary>
/// <remarks>
/// Only the header block is read (at most 64 KiB), nothing is changed, and headers are matched case-insensitively with
/// folding undone. Unrecognised content is ignored: this reads hints, the headers themselves stay in the message.
/// </remarks>
public sealed class MboxMetadata
{
    private MboxMetadata()
    {
    }

    /// <summary>
    /// Flags from <c>Status</c> (<c>R</c> seen) and <c>X-Status</c> (<c>A</c> answered, <c>F</c> flagged, <c>D</c> deleted,
    /// <c>T</c> draft), the letters mutt, Pine and Thunderbird write.
    /// </summary>
    public MessageMarks Flags { get; private set; }

    /// <summary>The Gmail labels from <c>X-Gmail-Labels</c>, in order, with encoded words decoded. Empty when absent.</summary>
    public IReadOnlyList<string> GmailLabels { get; private set; } = [];

    /// <summary>The Gmail conversation id from <c>X-GM-THRID</c>, or null.</summary>
    public string? GmailThreadId { get; private set; }

    /// <summary>The keywords listed in <c>X-Keywords</c> (comma or space separated). Empty when absent.</summary>
    public IReadOnlyList<string> Keywords { get; private set; } = [];

    /// <summary>The <c>Content-Length</c> header value, or null if absent or not a number.</summary>
    public long? ContentLength { get; private set; }

    /// <summary>Reads the metadata of an entry.</summary>
    /// <param name="source">The mbox bytes.</param>
    /// <param name="entry">An entry of that file.</param>
    /// <returns>The metadata.</returns>
    public static MboxMetadata Read(IRandomAccessSource source, in MboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] b = new byte[(int)Math.Min(entry.MessageLength, 65536)];
        int n = source.ReadFully(entry.MessageOffset, b);
        return Parse(b.AsSpan(0, n));
    }

    /// <summary>Asynchronous twin of <see cref="Read"/>.</summary>
    /// <param name="source">The mbox bytes.</param>
    /// <param name="entry">An entry of that file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The metadata.</returns>
    public static async ValueTask<MboxMetadata> ReadAsync(IRandomAccessSource source, MboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] b = new byte[(int)Math.Min(entry.MessageLength, 65536)];
        int n = await source.ReadFullyAsync(entry.MessageOffset, b, cancellationToken).ConfigureAwait(false);
        return Parse(b.AsSpan(0, n));
    }

    /// <summary>Reads the metadata from the start of a message held in memory.</summary>
    /// <param name="message">The message bytes (the header block is enough).</param>
    /// <returns>The metadata.</returns>
    public static MboxMetadata Parse(ReadOnlySpan<byte> message)
    {
        MboxMetadata m = new();
        List<string> labels = [];
        List<string> keywords = [];
        string? status = null;
        string? xstatus = null;

        int pos = 0;
        StringBuilder? field = null;
        while (pos <= message.Length)
        {
            int nl = pos < message.Length ? message[pos..].IndexOf((byte)'\n') : -1;
            ReadOnlySpan<byte> line = nl >= 0 ? message.Slice(pos, nl) : message[Math.Min(pos, message.Length)..];
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            bool continuation = line.Length > 0 && (line[0] == (byte)' ' || line[0] == (byte)'\t');
            if (continuation && field is not null)
            {
                field.Append(' ').Append(Encoding.Latin1.GetString(line).Trim());
            }
            else
            {
                if (field is not null)
                {
                    Apply(m, field.ToString(), labels, keywords, ref status, ref xstatus);
                    field = null;
                }

                if (line.Length == 0)
                {
                    break;
                }

                field = new StringBuilder(Encoding.Latin1.GetString(line));
            }

            if (nl < 0)
            {
                break;
            }

            pos += nl + 1;
        }

        if (field is not null)
        {
            Apply(m, field.ToString(), labels, keywords, ref status, ref xstatus);
        }

        MessageMarks f = MessageMarks.None;
        if (status is not null && status.Contains('R', StringComparison.Ordinal))
        {
            f |= MessageMarks.Seen;
        }

        if (xstatus is not null)
        {
            if (xstatus.Contains('A', StringComparison.Ordinal))
            {
                f |= MessageMarks.Answered;
            }

            if (xstatus.Contains('F', StringComparison.Ordinal))
            {
                f |= MessageMarks.Flagged;
            }

            if (xstatus.Contains('D', StringComparison.Ordinal))
            {
                f |= MessageMarks.Deleted;
            }

            if (xstatus.Contains('T', StringComparison.Ordinal))
            {
                f |= MessageMarks.Draft;
            }
        }

        m.Flags = f;
        m.GmailLabels = labels;
        m.Keywords = keywords;
        return m;
    }

    private static void Apply(MboxMetadata m, string header, List<string> labels, List<string> keywords, ref string? status, ref string? xstatus)
    {
        int colon = header.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return;
        }

        string name = header[..colon].Trim();
        string value = header[(colon + 1)..].Trim();
        if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
        {
            status ??= value;
        }
        else if (name.Equals("X-Status", StringComparison.OrdinalIgnoreCase))
        {
            xstatus ??= value;
        }
        else if (name.Equals("X-GM-THRID", StringComparison.OrdinalIgnoreCase))
        {
            m.GmailThreadId ??= value;
        }
        else if (name.Equals("X-Gmail-Labels", StringComparison.OrdinalIgnoreCase) && labels.Count == 0)
        {
            foreach (string part in SplitLabels(value))
            {
                labels.Add(Rfc2047.DecodeText(part));
            }
        }
        else if (name.Equals("X-Keywords", StringComparison.OrdinalIgnoreCase) && keywords.Count == 0)
        {
            keywords.AddRange(value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && m.ContentLength is null
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long cl))
        {
            m.ContentLength = cl;
        }
    }

    // Comma separated; a label containing a comma is double-quoted.
    private static List<string> SplitLabels(string value)
    {
        List<string> parts = [];
        StringBuilder sb = new();
        bool quoted = false;
        foreach (char c in value)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                if (sb.Length > 0)
                {
                    parts.Add(sb.ToString().Trim());
                }

                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            parts.Add(sb.ToString().Trim());
        }

        parts.RemoveAll(p => p.Length == 0);
        return parts;
    }
}
