using System.Buffers.Text;
using System.Text;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>
/// Decodes the common single-line <c>n FETCH (...)</c> response straight into an <see cref="ImapFetchData"/> without building the
/// generic <see cref="ImapValue"/> tree first (RFC 9051 §7.5.2, §9). That tree costs several kilobytes per message and a summary
/// fetch of a large mailbox sends one such response per message.
/// </summary>
/// <remarks>
/// <para>It accepts only strictly well-formed lines made of items the typed model decodes itself: UID, FLAGS, INTERNALDATE,
/// RFC822.SIZE, MODSEQ, EMAILID, THREADID and ENVELOPE, with quoted strings or NIL. Everything else returns false and the caller
/// parses the line with <see cref="ImapResponseParser"/>, so odd input still gets its defects and the same decoded values.</para>
/// <para>The result is value-for-value what <c>ImapDecoders.Fetch</c> produces for the same line; a test compares the two.</para>
/// </remarks>
internal static class ImapFetchFast
{
    private static readonly ImapAddress[] NoAddresses = [];
    private static readonly string[] NoFlags = [];
    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    // The flags nearly every message carries, so decoding a message does not allocate a string for them (RFC 9051 §2.3.2, RFC 5788).
    private static readonly string[] KnownFlags =
    [
        "\\Seen", "\\Answered", "\\Flagged", "\\Deleted", "\\Draft", "\\Recent",
        "$Forwarded", "$MDNSent", "$Junk", "$NotJunk", "$Phishing", "$Label1", "$Label2", "$Label3", "$Label4", "$Label5",
    ];

    public static bool TryParse(ReadOnlySpan<byte> line, long number, StringCache cache, out ImapFetchData? data)
    {
        data = null;
        // "* " number SP "FETCH" SP "("
        int i = 2;
        while (i < line.Length && line[i] != (byte)' ')
        {
            i++;
        }

        i++;
        if (line.Length < i + 7 || !Ascii.EqualsIgnoreCase(line.Slice(i, 5), "FETCH"u8) || line[i + 5] != (byte)' ' || line[i + 6] != (byte)'(' || line[^1] != (byte)')')
        {
            return false;
        }

        i += 7;
        int end = line.Length - 1;
        var f = new ImapFetchData { SequenceNumber = number };
        bool first = true;
        while (i < end)
        {
            if (!first)
            {
                if (line[i] != (byte)' ')
                {
                    return false;
                }

                i++;
            }

            first = false;
            int nameStart = i;
            while (i < end && line[i] != (byte)' ')
            {
                i++;
            }

            if (i >= end)
            {
                return false;
            }

            ReadOnlySpan<byte> name = line[nameStart..i];
            i++;
            if (!Item(line[..end], name, ref i, f, cache))
            {
                return false;
            }
        }

        data = f;
        return i == end;
    }

    private static bool Item(ReadOnlySpan<byte> s, ReadOnlySpan<byte> name, ref int i, ImapFetchData f, StringCache cache)
    {
        if (Is(name, "UID"u8))
        {
            if (!Number(s, ref i, out long uid)) { return false; }
            f.Uid = uid;
            return true;
        }

        if (Is(name, "FLAGS"u8))
        {
            return Flags(s, ref i, f);
        }

        if (Is(name, "RFC822.SIZE"u8))
        {
            if (!Number(s, ref i, out long size)) { return false; }
            f.Rfc822Size = size;
            return true;
        }

        if (Is(name, "INTERNALDATE"u8))
        {
            if (!Quoted(s, ref i, out string? text) || text is null) { return false; }
            f.InternalDateText = text;
            f.InternalDate = ParseInternalDate(text);
            return true;
        }

        if (Is(name, "ENVELOPE"u8))
        {
            ImapEnvelope? e = Envelope(s, ref i, cache);
            if (e is null) { return false; }
            f.Envelope = e;
            return true;
        }

        if (Is(name, "MODSEQ"u8))
        {
            if (!Open(s, ref i) || !Number(s, ref i, out long ms) || !Close(s, ref i)) { return false; }
            f.ModSeq = ms;
            return true;
        }

        if (Is(name, "EMAILID"u8) || Is(name, "THREADID"u8))
        {
            if (!Open(s, ref i)) { return false; }
            int start = i;
            while (i < s.Length && IsAtomChar(s[i])) { i++; }
            if (i == start || !Close(s, ref i)) { return false; }
            string id = Encoding.ASCII.GetString(s[start..(i - 1)]);
            if (Is(name, "EMAILID"u8)) { f.EmailId = id; } else { f.ThreadId = id; }
            return true;
        }

        return false;
    }

    private static bool Is(ReadOnlySpan<byte> name, ReadOnlySpan<byte> expected) => Ascii.EqualsIgnoreCase(name, expected);

    private static bool Open(ReadOnlySpan<byte> s, ref int i)
    {
        if (i < s.Length && s[i] == (byte)'(') { i++; return true; }
        return false;
    }

    private static bool Close(ReadOnlySpan<byte> s, ref int i)
    {
        if (i < s.Length && s[i] == (byte)')') { i++; return true; }
        return false;
    }

    // Atom characters kept deliberately narrower than RFC 9051's atom-specials rule: anything unusual takes the general parser.
    private static bool IsAtomChar(byte c) => c is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')
        or (byte)'\\' or (byte)'$' or (byte)'_' or (byte)'.' or (byte)'+' or (byte)'-' or (byte)':';

    private static bool Number(ReadOnlySpan<byte> s, ref int i, out long value)
    {
        value = 0;
        int start = i;
        while (i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9') { i++; }
        int len = i - start;
        return len is > 0 and <= 18 && Utf8Parser.TryParse(s.Slice(start, len), out value, out int used) && used == len;
    }

    private static bool Flags(ReadOnlySpan<byte> s, ref int i, ImapFetchData f)
    {
        if (!Open(s, ref i)) { return false; }
        if (Close(s, ref i)) { f.Flags = NoFlags; return true; }
        int count = 1;
        for (int k = i; k < s.Length && s[k] != (byte)')'; k++)
        {
            if (s[k] == (byte)' ') { count++; }
        }

        var flags = new string[count];
        for (int n = 0; n < count; n++)
        {
            if (n > 0)
            {
                if (i >= s.Length || s[i] != (byte)' ') { return false; }
                i++;
            }

            int start = i;
            while (i < s.Length && IsAtomChar(s[i])) { i++; }
            // The general parser reads an atom NIL as no text (an empty flag), so leave that to it.
            if (i == start || (i - start == 3 && Ascii.EqualsIgnoreCase(s[start..i], "NIL"u8))) { return false; }
            flags[n] = FlagText(s[start..i]);
        }

        f.Flags = flags;
        return Close(s, ref i);
    }

    private static string FlagText(ReadOnlySpan<byte> bytes)
    {
        foreach (string known in KnownFlags)
        {
            if (known.Length == bytes.Length && Ascii.EqualsIgnoreCase(bytes, known) && Ascii.Equals(bytes, known))
            {
                return known;
            }
        }

        return Encoding.ASCII.GetString(bytes);
    }

    // A quoted string (NIL gives null). Only the escapes RFC 9051 defines (\" and \\) are taken; any other byte the general parser may treat differently is refused.
    private static bool Quoted(ReadOnlySpan<byte> s, ref int i, out string? text, StringCache? cache = null)
    {
        text = null;
        if (i + 3 <= s.Length && s[i] == (byte)'N' && s[i + 1] == (byte)'I' && s[i + 2] == (byte)'L' && (i + 3 == s.Length || s[i + 3] is (byte)' ' or (byte)')'))
        {
            i += 3;
            return true;
        }

        if (i >= s.Length || s[i] != (byte)'"') { return false; }
        int start = ++i;
        bool escaped = false;
        while (i < s.Length && s[i] != (byte)'"')
        {
            byte c = s[i];
            if (c < 0x20 || c == 0x7f) { return false; }
            if (c == (byte)'\\')
            {
                if (i + 1 >= s.Length || s[i + 1] is not ((byte)'"' or (byte)'\\')) { return false; }
                escaped = true;
                i++;
            }

            i++;
        }

        if (i >= s.Length) { return false; }
        ReadOnlySpan<byte> raw = s[start..i];
        i++;
        if (!escaped)
        {
            text = raw.Length == 0 ? "" : cache is null ? Encoding.UTF8.GetString(raw) : cache.Get(raw);
            return true;
        }

        byte[] tmp = new byte[raw.Length];
        int n = 0;
        for (int k = 0; k < raw.Length; k++)
        {
            if (raw[k] == (byte)'\\') { k++; }
            tmp[n++] = raw[k];
        }

        text = Encoding.UTF8.GetString(tmp, 0, n);
        return true;
    }

    private static ImapEnvelope? Envelope(ReadOnlySpan<byte> s, ref int i, StringCache cache)
    {
        if (!Open(s, ref i)) { return null; }
        var e = new ImapEnvelope();
        if (!Quoted(s, ref i, out string? date) || !Space(s, ref i)) { return null; }
        e.Date = date;
        if (!Quoted(s, ref i, out string? subject) || !Space(s, ref i)) { return null; }
        e.Subject = subject;
        if (!Addresses(s, ref i, cache, out IReadOnlyList<ImapAddress> from) || !Space(s, ref i)) { return null; }
        e.From = from;
        if (!Addresses(s, ref i, cache, out IReadOnlyList<ImapAddress> sender) || !Space(s, ref i)) { return null; }
        e.Sender = sender;
        if (!Addresses(s, ref i, cache, out IReadOnlyList<ImapAddress> replyTo) || !Space(s, ref i)) { return null; }
        e.ReplyTo = replyTo;
        if (!Addresses(s, ref i, cache, out IReadOnlyList<ImapAddress> to) || !Space(s, ref i)) { return null; }
        e.To = to;
        if (!Addresses(s, ref i, cache, out IReadOnlyList<ImapAddress> cc) || !Space(s, ref i)) { return null; }
        e.Cc = cc;
        if (!Addresses(s, ref i, cache, out IReadOnlyList<ImapAddress> bcc) || !Space(s, ref i)) { return null; }
        e.Bcc = bcc;
        if (!Quoted(s, ref i, out string? inReplyTo) || !Space(s, ref i)) { return null; }
        e.InReplyTo = inReplyTo;
        if (!Quoted(s, ref i, out string? messageId) || !Close(s, ref i)) { return null; }
        e.MessageId = messageId;
        return e;
    }

    private static bool Space(ReadOnlySpan<byte> s, ref int i)
    {
        if (i < s.Length && s[i] == (byte)' ') { i++; return true; }
        return false;
    }

    // address-list = "(" 1*address ")" / nil; address = "(" name SP route SP mailbox SP host ")" (RFC 9051 §9)
    private static bool Addresses(ReadOnlySpan<byte> s, ref int i, StringCache cache, out IReadOnlyList<ImapAddress> list)
    {
        list = NoAddresses;
        if (i + 3 <= s.Length && s[i] == (byte)'N' && s[i + 1] == (byte)'I' && s[i + 2] == (byte)'L')
        {
            i += 3;
            return true;
        }

        if (!Open(s, ref i)) { return false; }
        // Count the addresses first so the array is exact: each starts with "(" outside quotes at depth one.
        int count = 0;
        bool quoted = false;
        int depth = 1;
        for (int k = i; k < s.Length && depth > 0; k++)
        {
            byte c = s[k];
            if (quoted)
            {
                if (c == (byte)'\\') { k++; }
                else if (c == (byte)'"') { quoted = false; }
            }
            else if (c == (byte)'"') { quoted = true; }
            else if (c == (byte)'(') { if (depth == 1) { count++; } depth++; }
            else if (c == (byte)')') { depth--; }
        }

        if (count == 0) { return false; }
        var result = new ImapAddress[count];
        for (int n = 0; n < count; n++)
        {
            // Servers write the addresses back to back; one space between them is tolerated as the general parser does.
            if (n > 0 && i < s.Length && s[i] == (byte)' ')
            {
                i++;
            }

            if (!Open(s, ref i)
                || !Quoted(s, ref i, out string? name, cache) || !Space(s, ref i)
                || !Quoted(s, ref i, out string? route, cache) || !Space(s, ref i)
                || !Quoted(s, ref i, out string? mailbox, cache) || !Space(s, ref i)
                || !Quoted(s, ref i, out string? host, cache) || !Close(s, ref i))
            {
                return false;
            }

            result[n] = new ImapAddress(name, route, mailbox, host);
        }

        list = result;
        return Close(s, ref i);
    }

    // date-time = DQUOTE date-day-fixed "-" date-month "-" date-year SP time SP zone DQUOTE (RFC 9051 §9). The exact 26-character form is read
    // here; anything else (space-padded day, odd zone) goes to the general decoder.
    private static DateTimeOffset? ParseInternalDate(string t)
    {
        if (t.Length == 26 && t[2] == '-' && t[6] == '-' && t[11] == ' ' && t[14] == ':' && t[17] == ':' && t[20] == ' ' && (t[21] is '+' or '-')
            && Digits(t, 0, 2, out int day) && Digits(t, 7, 4, out int year) && Digits(t, 12, 2, out int hour) && Digits(t, 15, 2, out int minute)
            && Digits(t, 18, 2, out int second) && Digits(t, 22, 2, out int zh) && Digits(t, 24, 2, out int zm)
            && Month(t.AsSpan(3, 3)) is int month and > 0 && year >= 1 && day is >= 1 and <= 31 && hour < 24 && minute < 60 && second < 60 && zh < 14 && zm < 60
            && day <= DateTime.DaysInMonth(year, month))
        {
            var offset = new TimeSpan(zh, zm, 0);
            return new DateTimeOffset(year, month, day, hour, minute, second, t[21] == '-' ? -offset : offset);
        }

        return ImapDecoders.ParseInternalDate(t);
    }

    private static bool Digits(string t, int start, int length, out int value)
    {
        value = 0;
        for (int k = start; k < start + length; k++)
        {
            if (t[k] is < '0' or > '9') { return false; }
            value = (value * 10) + (t[k] - '0');
        }

        return true;
    }

    private static int Month(ReadOnlySpan<char> m)
    {
        for (int k = 0; k < 12; k++)
        {
            if (m.Equals(MonthNames[k], StringComparison.OrdinalIgnoreCase)) { return k + 1; }
        }

        return 0;
    }

    /// <summary>
    /// Remembers recently decoded short ASCII strings (host names and display names repeat from message to message), so a mailbox of
    /// thousands of messages from a few hundred senders allocates each of them once. Direct-mapped by a hash of the bytes; a miss
    /// just decodes again. One per connection reader; not thread-safe.
    /// </summary>
    internal sealed class StringCache
    {
        private const int Slots = 512;
        private const int MaxLength = 64;
        private readonly string?[] _entries = new string?[Slots];

        public string Get(ReadOnlySpan<byte> utf8)
        {
            if (utf8.Length > MaxLength || !Ascii.IsValid(utf8))
            {
                return Encoding.UTF8.GetString(utf8);
            }

            uint h = 2166136261;
            foreach (byte b in utf8)
            {
                h = (h ^ b) * 16777619;
            }

            ref string? slot = ref _entries[h % Slots];
            string? s = slot;
            if (s is not null && Ascii.Equals(utf8, s))
            {
                return s;
            }

            return slot = Encoding.ASCII.GetString(utf8);
        }
    }
}
