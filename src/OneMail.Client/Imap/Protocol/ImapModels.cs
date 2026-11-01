using System.Globalization;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>An address of an ENVELOPE (RFC 9051 §7.5.2). A group starts with a host of null and a mailbox that is the group name, and ends with both null.</summary>
/// <param name="Name">The display name, still RFC 2047 encoded as the server sent it (null for NIL).</param>
/// <param name="Route">The obsolete source route (null for NIL).</param>
/// <param name="Mailbox">The local part (or group name).</param>
/// <param name="Host">The domain.</param>
public sealed record ImapAddress(string? Name, string? Route, string? Mailbox, string? Host)
{
    /// <summary>Whether this entry starts a group (host NIL, mailbox is the group name).</summary>
    public bool IsGroupStart => Host is null && Mailbox is not null;

    /// <summary>Whether this entry ends a group (mailbox and host NIL).</summary>
    public bool IsGroupEnd => Host is null && Mailbox is null;
}

/// <summary>The ENVELOPE structure (RFC 9051 §7.5.2). Strings are as the server sent them: header values still carry RFC 2047 encoded words.</summary>
public sealed class ImapEnvelope
{
    /// <summary>The Date header text.</summary>
    public string? Date { get; internal set; }

    /// <summary>The Subject header text.</summary>
    public string? Subject { get; internal set; }

    /// <summary>The From addresses.</summary>
    public IReadOnlyList<ImapAddress> From { get; internal set; } = [];

    /// <summary>The Sender addresses.</summary>
    public IReadOnlyList<ImapAddress> Sender { get; internal set; } = [];

    /// <summary>The Reply-To addresses.</summary>
    public IReadOnlyList<ImapAddress> ReplyTo { get; internal set; } = [];

    /// <summary>The To addresses.</summary>
    public IReadOnlyList<ImapAddress> To { get; internal set; } = [];

    /// <summary>The Cc addresses.</summary>
    public IReadOnlyList<ImapAddress> Cc { get; internal set; } = [];

    /// <summary>The Bcc addresses.</summary>
    public IReadOnlyList<ImapAddress> Bcc { get; internal set; } = [];

    /// <summary>The In-Reply-To header text.</summary>
    public string? InReplyTo { get; internal set; }

    /// <summary>The Message-ID header text.</summary>
    public string? MessageId { get; internal set; }
}

/// <summary>A MIME part as described by BODYSTRUCTURE (RFC 9051 §7.5.2): a multipart with children, or a single part.</summary>
public sealed class ImapBodyStructure
{
    /// <summary>The media type, lower case (<c>text</c>); <c>multipart</c> for a multipart.</summary>
    public string Type { get; internal set; } = "";

    /// <summary>The subtype, lower case.</summary>
    public string Subtype { get; internal set; } = "";

    /// <summary>The Content-Type parameters in order (names lower case).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; internal set; } = [];

    /// <summary>The children of a multipart; null for a single part.</summary>
    public IReadOnlyList<ImapBodyStructure>? Parts { get; internal set; }

    /// <summary>Content-ID (single part).</summary>
    public string? ContentId { get; internal set; }

    /// <summary>Content-Description (single part).</summary>
    public string? Description { get; internal set; }

    /// <summary>Content-Transfer-Encoding as written (single part).</summary>
    public string? Encoding { get; internal set; }

    /// <summary>The size in bytes of the encoded body (single part), or -1 when absent.</summary>
    public long Size { get; internal set; } = -1;

    /// <summary>The line count of a <c>text/*</c> or <c>message/rfc822</c> part, or -1.</summary>
    public long Lines { get; internal set; } = -1;

    /// <summary>The envelope of an embedded <c>message/rfc822</c> part.</summary>
    public ImapEnvelope? Envelope { get; internal set; }

    /// <summary>The structure of an embedded <c>message/rfc822</c> part.</summary>
    public ImapBodyStructure? EmbeddedBody { get; internal set; }

    /// <summary>The body MD5 (extension data).</summary>
    public string? Md5 { get; internal set; }

    /// <summary>The Content-Disposition type, lower case (extension data).</summary>
    public string? DispositionType { get; internal set; }

    /// <summary>The Content-Disposition parameters (extension data).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> DispositionParameters { get; internal set; } = [];

    /// <summary>The Content-Language tags (extension data).</summary>
    public IReadOnlyList<string> Languages { get; internal set; } = [];

    /// <summary>The Content-Location (extension data).</summary>
    public string? Location { get; internal set; }

    /// <summary>Any extension fields beyond the ones above, unparsed.</summary>
    public IReadOnlyList<ImapValue> Extensions { get; internal set; } = [];

    /// <summary>Whether this is a multipart.</summary>
    public bool IsMultipart => Parts is not null;

    /// <summary>The part specifier for <c>BODY[...]</c> (<c>1.2</c>); <c>"1"</c> for the only part of a non-multipart message. Empty for the root.</summary>
    public string Section { get; internal set; } = "";

    /// <summary>The structure's text as a MIME type, <c>type/subtype</c>.</summary>
    public string MediaType => Type + "/" + Subtype;
}

/// <summary>One data item of a FETCH response that carries a section: <c>BODY[1.2]&lt;0&gt;</c>, <c>BINARY[1]</c>, <c>RFC822</c>.</summary>
/// <param name="Name">The item as the server wrote it, upper-cased keyword part first (<c>BODY[HEADER.FIELDS (FROM)]&lt;0&gt;</c>).</param>
/// <param name="Data">The value: a literal (buffered, streamed or skipped), a string, or NIL.</param>
public sealed record ImapFetchSection(string Name, ImapValue Data);

/// <summary>A FETCH response (RFC 9051 §7.5.2) with the common items decoded; <see cref="Other"/> keeps every item not known here.</summary>
public sealed class ImapFetchData
{
    /// <summary>The message sequence number.</summary>
    public long SequenceNumber { get; internal set; }

    /// <summary>UID, or null when not in the response.</summary>
    public long? Uid { get; internal set; }

    /// <summary>The flags (<c>\Seen</c>, keywords), or null when FLAGS was not in the response.</summary>
    public IReadOnlyList<string>? Flags { get; internal set; }

    /// <summary>INTERNALDATE as the server wrote it (<c>17-Jul-1996 02:44:25 -0700</c>), or null.</summary>
    public string? InternalDateText { get; internal set; }

    /// <summary>INTERNALDATE parsed, or null when absent or unparsable.</summary>
    public DateTimeOffset? InternalDate { get; internal set; }

    /// <summary>RFC822.SIZE, or null.</summary>
    public long? Rfc822Size { get; internal set; }

    /// <summary>MODSEQ (RFC 7162), or null.</summary>
    public long? ModSeq { get; internal set; }

    /// <summary>EMAILID (RFC 8474), or null.</summary>
    public string? EmailId { get; internal set; }

    /// <summary>THREADID (RFC 8474), or null.</summary>
    public string? ThreadId { get; internal set; }

    /// <summary>ENVELOPE, or null.</summary>
    public ImapEnvelope? Envelope { get; internal set; }

    /// <summary>BODYSTRUCTURE (or BODY with a list), or null.</summary>
    public ImapBodyStructure? BodyStructure { get; internal set; }

    /// <summary>The section items (<c>BODY[...]</c>, <c>BINARY[...]</c>, <c>RFC822</c>, <c>RFC822.HEADER</c>, <c>RFC822.TEXT</c>) in order.</summary>
    public IReadOnlyList<ImapFetchSection> Sections { get; internal set; } = [];

    /// <summary>Every item this decoder does not know (<c>X-GM-LABELS</c>, <c>PREVIEW</c>, ...), name and value, preserved.</summary>
    public IReadOnlyList<KeyValuePair<string, ImapValue>> Other { get; internal set; } = [];

    /// <summary>Items that did not have the shape the grammar requires.</summary>
    public IReadOnlyList<ProtocolDefect> Defects { get; internal set; } = [];
}

/// <summary>A LIST or LSUB response (RFC 9051 §7.3.1, LIST-EXTENDED RFC 5258).</summary>
public sealed class ImapListEntry
{
    /// <summary>The name attributes (<c>\Noselect</c>, <c>\HasChildren</c>, special-use <c>\Sent</c>, ...), as written.</summary>
    public IReadOnlyList<string> Attributes { get; internal set; } = [];

    /// <summary>The hierarchy delimiter, or null for NIL.</summary>
    public string? Delimiter { get; internal set; }

    /// <summary>The mailbox name exactly as sent (modified UTF-7 unless UTF8=ACCEPT is enabled).</summary>
    public ReadOnlyMemory<byte> RawName { get; internal set; }

    /// <summary>The mailbox name decoded from modified UTF-7 (RFC 3501 §5.1.3); a name with no <c>&amp;</c> is the same text.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The extended data items after the name, e.g. <c>("CHILDINFO" ("SUBSCRIBED"))</c>, unparsed.</summary>
    public IReadOnlyList<ImapValue> Extended { get; internal set; } = [];
}

/// <summary>A STATUS response (RFC 9051 §7.2.4).</summary>
public sealed class ImapStatusData
{
    /// <summary>The mailbox name, decoded.</summary>
    public string Mailbox { get; internal set; } = "";

    /// <summary>The items by upper-case name (<c>MESSAGES</c>, <c>UIDNEXT</c>, <c>HIGHESTMODSEQ</c>, <c>SIZE</c>, ...). Items whose value is not a number are left out.</summary>
    public IReadOnlyDictionary<string, long> Items { get; internal set; } = new Dictionary<string, long>();
}

/// <summary>An ESEARCH response (RFC 4731, RFC 9051 §7.3.4).</summary>
public sealed class ImapEsearchData
{
    /// <summary>The correlator tag of the command it answers, if present.</summary>
    public string? Tag { get; internal set; }

    /// <summary>Whether the numbers are UIDs.</summary>
    public bool IsUid { get; internal set; }

    /// <summary>The result items in order: <c>COUNT</c>, <c>MIN</c>, <c>MAX</c>, <c>ALL</c> (a sequence set), <c>MODSEQ</c>, ... with their values.</summary>
    public IReadOnlyList<KeyValuePair<string, ImapValue>> Items { get; internal set; } = [];

    /// <summary>The value of the first item called <paramref name="name"/>, or null.</summary>
    /// <param name="name">The item name, case-insensitive.</param>
    /// <returns>The value, or null.</returns>
    public ImapValue? Get(string name)
    {
        foreach (KeyValuePair<string, ImapValue> kv in Items)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }

        return null;
    }
}

/// <summary>A NAMESPACE response (RFC 2342): personal, other users', shared; each a list of prefix and delimiter.</summary>
public sealed class ImapNamespaceData
{
    /// <summary>The personal namespaces (prefix, delimiter).</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Personal { get; internal set; } = [];

    /// <summary>The other users' namespaces.</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> OtherUsers { get; internal set; } = [];

    /// <summary>The shared namespaces.</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Shared { get; internal set; } = [];
}

/// <summary>A VANISHED response (RFC 7162 §3.2.10).</summary>
public sealed class ImapVanishedData
{
    /// <summary>Whether it was the <c>(EARLIER)</c> form, sent in answer to a FETCH/SELECT with VANISHED rather than unsolicited.</summary>
    public bool Earlier { get; internal set; }

    /// <summary>The UIDs as a sequence set (<c>41,43:116,118</c>); expand with <see cref="ImapSequenceSet"/>.</summary>
    public string UidSet { get; internal set; } = "";
}

/// <summary>Helpers for IMAP sequence sets (<c>1,3:7,10:*</c>).</summary>
public static class ImapSequenceSet
{
    /// <summary>Expands a set of numbers and ranges. <c>*</c> in a range endpoint is read as <paramref name="starValue"/>.</summary>
    /// <param name="set">The set text.</param>
    /// <param name="starValue">What <c>*</c> stands for. Default <see cref="uint.MaxValue"/>; pass the highest UID or message count.</param>
    /// <param name="maxCount">The most numbers to produce. Default 10,000,000; a longer set stops there (it is hostile or you want ranges).</param>
    /// <returns>The numbers in the order written; a malformed element is skipped.</returns>
    public static IEnumerable<long> Expand(string set, long starValue = uint.MaxValue, long maxCount = 10_000_000)
    {
        ArgumentNullException.ThrowIfNull(set);
        long produced = 0;
        foreach (string part in set.Split(','))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);
            string a = colon < 0 ? part : part[..colon];
            string? b = colon < 0 ? null : part[(colon + 1)..];
            if (!TryNumber(a, starValue, out long lo) || (b is not null && !TryNumber(b, starValue, out _)))
            {
                continue;
            }

            long hi = lo;
            if (b is not null)
            {
                TryNumber(b, starValue, out hi);
            }

            if (hi < lo)
            {
                (lo, hi) = (hi, lo);
            }

            for (long n = lo; n <= hi; n++)
            {
                if (produced++ >= maxCount)
                {
                    yield break;
                }

                yield return n;
            }
        }
    }

    private static bool TryNumber(string s, long star, out long n)
    {
        if (s == "*")
        {
            n = star;
            return true;
        }

        return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
    }
}
