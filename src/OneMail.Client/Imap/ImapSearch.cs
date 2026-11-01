using System.Globalization;
using RecursiveAI.OneMail.Client.Imap.Protocol;

namespace RecursiveAI.OneMail.Client.Imap;

/// <summary>
/// A SEARCH, SORT or THREAD criterion (RFC 9051 §6.4.4), built from the static members and combined with <see cref="And"/>,
/// <see cref="Or"/> and <see cref="Not"/>. String arguments are sent as quoted strings or literals, so they cannot break the command.
/// </summary>
public sealed class ImapSearch
{
    private readonly List<Action<ImapCommand>> _terms = [];

    private ImapSearch() { }

    /// <summary>Whether a string argument is not ASCII, so the command needs a <c>CHARSET UTF-8</c> (or <c>UTF8=ACCEPT</c>) to carry it.</summary>
    public bool HasNonAscii { get; private set; }

    private int TermCount => _terms.Count;

    private static ImapSearch Key(string key)
    {
        var s = new ImapSearch();
        s._terms.Add(c => c.Atom(key));
        return s;
    }

    private static ImapSearch KeyText(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var s = new ImapSearch();
        s._terms.Add(c => c.Atom(key).Astring(value));
        s.HasNonAscii = value.Any(ch => ch >= 0x80);
        return s;
    }

    private static ImapSearch KeyDate(string key, DateTimeOffset date)
    {
        var s = new ImapSearch();
        // RFC 9051 §6.4.4: dates are day-month-year only; the time and zone are ignored by the server.
        s._terms.Add(c => c.Atom(key).Atom(date.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture)));
        return s;
    }

    /// <summary>Every message.</summary>
    public static ImapSearch All => Key("ALL");

    /// <summary>Messages with <c>\Seen</c>.</summary>
    public static ImapSearch Seen => Key("SEEN");

    /// <summary>Messages without <c>\Seen</c>.</summary>
    public static ImapSearch Unseen => Key("UNSEEN");

    /// <summary>Messages with <c>\Answered</c>.</summary>
    public static ImapSearch Answered => Key("ANSWERED");

    /// <summary>Messages with <c>\Flagged</c>.</summary>
    public static ImapSearch Flagged => Key("FLAGGED");

    /// <summary>Messages with <c>\Deleted</c>.</summary>
    public static ImapSearch Deleted => Key("DELETED");

    /// <summary>Messages with <c>\Draft</c>.</summary>
    public static ImapSearch Draft => Key("DRAFT");

    /// <summary>Messages the server marks as new (RFC 9051 removed <c>RECENT</c>; servers still answer it).</summary>
    public static ImapSearch New => Key("NEW");

    /// <summary>Messages carrying a keyword.</summary>
    /// <param name="keyword">The keyword atom.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Keyword(string keyword) => KeyText("KEYWORD", keyword);

    /// <summary>Messages not carrying a keyword.</summary>
    /// <param name="keyword">The keyword atom.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Unkeyword(string keyword) => KeyText("UNKEYWORD", keyword);

    /// <summary>Header <c>From</c> contains the text (case-insensitive substring).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch From(string text) => KeyText("FROM", text);

    /// <summary>Header <c>To</c> contains the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch To(string text) => KeyText("TO", text);

    /// <summary>Header <c>Cc</c> contains the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Cc(string text) => KeyText("CC", text);

    /// <summary>Header <c>Bcc</c> contains the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Bcc(string text) => KeyText("BCC", text);

    /// <summary>Header <c>Subject</c> contains the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Subject(string text) => KeyText("SUBJECT", text);

    /// <summary>The body contains the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Body(string text) => KeyText("BODY", text);

    /// <summary>The header or body contains the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Text(string text) => KeyText("TEXT", text);

    /// <summary>A header field contains the text (an empty text matches any message with the field).</summary>
    /// <param name="field">The header field name.</param>
    /// <param name="text">The text.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Header(string field, string text)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(text);
        var s = new ImapSearch();
        s._terms.Add(c => c.Atom("HEADER").Astring(field).Astring(text));
        s.HasNonAscii = text.Any(ch => ch >= 0x80) || field.Any(ch => ch >= 0x80);
        return s;
    }

    /// <summary>Messages larger than <paramref name="octets"/>.</summary>
    /// <param name="octets">Size in octets.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Larger(long octets) { var s = new ImapSearch(); s._terms.Add(c => c.Atom("LARGER").Number(octets)); return s; }

    /// <summary>Messages smaller than <paramref name="octets"/>.</summary>
    /// <param name="octets">Size in octets.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Smaller(long octets) { var s = new ImapSearch(); s._terms.Add(c => c.Atom("SMALLER").Number(octets)); return s; }

    /// <summary>Internal date before the day of <paramref name="date"/>.</summary>
    /// <param name="date">The date; only the day counts.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Before(DateTimeOffset date) => KeyDate("BEFORE", date);

    /// <summary>Internal date on or after the day of <paramref name="date"/>.</summary>
    /// <param name="date">The date; only the day counts.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Since(DateTimeOffset date) => KeyDate("SINCE", date);

    /// <summary>Internal date on the day of <paramref name="date"/>.</summary>
    /// <param name="date">The date; only the day counts.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch On(DateTimeOffset date) => KeyDate("ON", date);

    /// <summary>Date header before the day of <paramref name="date"/>.</summary>
    /// <param name="date">The date; only the day counts.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch SentBefore(DateTimeOffset date) => KeyDate("SENTBEFORE", date);

    /// <summary>Date header on or after the day of <paramref name="date"/>.</summary>
    /// <param name="date">The date; only the day counts.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch SentSince(DateTimeOffset date) => KeyDate("SENTSINCE", date);

    /// <summary>Messages whose UID is in the set.</summary>
    /// <param name="uids">The UIDs.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Uid(ImapSet uids) { var s = new ImapSearch(); s._terms.Add(c => c.Atom("UID").SequenceSet(uids.Text)); return s; }

    /// <summary>Messages whose number is in the set.</summary>
    /// <param name="numbers">The message numbers.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch Numbers(ImapSet numbers) { var s = new ImapSearch(); s._terms.Add(c => c.SequenceSet(numbers.Text)); return s; }

    /// <summary>Messages changed since a mod-sequence (CONDSTORE, RFC 7162).</summary>
    /// <param name="modSeq">The mod-sequence.</param>
    /// <returns>The criterion.</returns>
    public static ImapSearch ModSeq(long modSeq) { var s = new ImapSearch(); s._terms.Add(c => c.Atom("MODSEQ").Number(modSeq)); return s; }

    /// <summary>Both this and <paramref name="other"/> must match.</summary>
    /// <param name="other">The other criterion.</param>
    /// <returns>A combined criterion; the operands are not changed.</returns>
    public ImapSearch And(ImapSearch other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var s = new ImapSearch { HasNonAscii = HasNonAscii || other.HasNonAscii };
        s._terms.AddRange(_terms);
        s._terms.AddRange(other._terms);
        return s;
    }

    /// <summary>Either this or <paramref name="other"/> must match.</summary>
    /// <param name="other">The other criterion.</param>
    /// <returns>A combined criterion.</returns>
    public ImapSearch Or(ImapSearch other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var s = new ImapSearch { HasNonAscii = HasNonAscii || other.HasNonAscii };
        s._terms.Add(c =>
        {
            c.Atom("OR");
            Group(c, this);
            Group(c, other);
        });
        return s;
    }

    /// <summary>This must not match.</summary>
    /// <returns>The negated criterion.</returns>
    public ImapSearch Not()
    {
        var s = new ImapSearch { HasNonAscii = HasNonAscii };
        s._terms.Add(c =>
        {
            c.Atom("NOT");
            Group(c, this);
        });
        return s;
    }

    private static void Group(ImapCommand c, ImapSearch s)
    {
        if (s.TermCount == 1)
        {
            s._terms[0](c);
            return;
        }

        c.Open();
        s.Apply(c);
        c.Close();
    }

    /// <summary>Appends the criterion to a command.</summary>
    /// <param name="command">The command being built.</param>
    public void Apply(ImapCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (Action<ImapCommand> t in _terms)
        {
            t(command);
        }
    }
}

/// <summary>What SEARCH should report beyond the matching numbers (ESEARCH, RFC 4731).</summary>
[Flags]
public enum ImapSearchReturn
{
    /// <summary>No ESEARCH return options: the classic SEARCH response.</summary>
    None = 0,

    /// <summary>The smallest match.</summary>
    Min = 1,

    /// <summary>The largest match.</summary>
    Max = 2,

    /// <summary>The number of matches.</summary>
    Count = 4,

    /// <summary>Every match.</summary>
    All = 8,
}

/// <summary>The result of a SEARCH.</summary>
public sealed class ImapSearchResult
{
    internal ImapSearchResult(IReadOnlyList<long> numbers, long? min, long? max, long? count, long? modSeq, bool isUid)
    {
        Numbers = numbers;
        Min = min;
        Max = max;
        Count = count ?? numbers.Count;
        ModSeq = modSeq;
        IsUid = isUid;
    }

    /// <summary>The matching numbers in server order (UIDs when the search was a UID search). Empty when only Min, Max or Count were requested.</summary>
    public IReadOnlyList<long> Numbers { get; }

    /// <summary>Smallest match, when the server reported it.</summary>
    public long? Min { get; }

    /// <summary>Largest match, when the server reported it.</summary>
    public long? Max { get; }

    /// <summary>Number of matches (the length of <see cref="Numbers"/> when not reported separately).</summary>
    public long Count { get; }

    /// <summary>Highest mod-sequence among the matches (CONDSTORE), when reported.</summary>
    public long? ModSeq { get; }

    /// <summary>Whether <see cref="Numbers"/> are UIDs.</summary>
    public bool IsUid { get; }
}

/// <summary>A message thread from THREAD (RFC 5256): a message and the replies below it.</summary>
public sealed class ImapThread
{
    internal ImapThread(long? number, List<ImapThread> children)
    {
        Number = number;
        Children = children;
    }

    /// <summary>The message number or UID, or null for a dummy parent the server invented for messages that share a missing ancestor.</summary>
    public long? Number { get; }

    /// <summary>The replies, in server order.</summary>
    public IReadOnlyList<ImapThread> Children { get; }
}

/// <summary>Sort keys for SORT (RFC 5256).</summary>
public enum ImapSortKey
{
    /// <summary>Internal date.</summary>
    Arrival,

    /// <summary>First <c>Cc</c> address.</summary>
    Cc,

    /// <summary>Sent date.</summary>
    Date,

    /// <summary>First <c>From</c> address.</summary>
    From,

    /// <summary>Size in octets.</summary>
    Size,

    /// <summary>Base subject.</summary>
    Subject,

    /// <summary>First <c>To</c> address.</summary>
    To,
}

/// <summary>One SORT key and its direction.</summary>
/// <param name="Key">The key.</param>
/// <param name="Reverse">True for descending order.</param>
public readonly record struct ImapSortCriterion(ImapSortKey Key, bool Reverse = false);

/// <summary>Threading algorithms for THREAD (RFC 5256).</summary>
public enum ImapThreadAlgorithm
{
    /// <summary>Subject-only grouping.</summary>
    OrderedSubject,

    /// <summary>The References-based algorithm.</summary>
    References,
}
