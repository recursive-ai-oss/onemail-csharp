using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Client.Imap;

/// <summary>A mailbox as LIST reports it (RFC 9051 §6.3.9).</summary>
public sealed class ImapMailboxInfo
{
    internal ImapMailboxInfo(string name, string? delimiter, IReadOnlyList<string> attributes, ImapMailboxStatus? status)
    {
        Name = name;
        Delimiter = delimiter;
        Attributes = attributes;
        Status = status;
    }

    /// <summary>The full name, decoded from modified UTF-7 (or taken as UTF-8 under <c>UTF8=ACCEPT</c>).</summary>
    public string Name { get; }

    /// <summary>The hierarchy delimiter, or null for a flat namespace.</summary>
    public string? Delimiter { get; }

    /// <summary>Name attributes as sent: <c>\Noselect</c>, <c>\HasChildren</c>, <c>\Sent</c>, ...</summary>
    public IReadOnlyList<string> Attributes { get; }

    /// <summary>Counters returned with the listing when <see cref="ImapListOptions.Status"/> asked for them.</summary>
    public ImapMailboxStatus? Status { get; }

    /// <summary>Whether the attribute is present (ASCII case-insensitive).</summary>
    /// <param name="attribute">The attribute with its backslash, e.g. <c>\Sent</c>.</param>
    /// <returns>True if present.</returns>
    public bool HasAttribute(string attribute) => Attributes.Any(a => string.Equals(a, attribute, StringComparison.OrdinalIgnoreCase));

    /// <summary>The special use (RFC 6154) the server announced, or <see cref="ImapSpecialUse.None"/>.</summary>
    public ImapSpecialUse SpecialUse
    {
        get
        {
            foreach (string a in Attributes)
            {
                if (a.Length < 2 || a[0] != '\\')
                {
                    continue;
                }

                switch (a.ToUpperInvariant())
                {
                    case "\\ALL": return ImapSpecialUse.All;
                    case "\\ARCHIVE": return ImapSpecialUse.Archive;
                    case "\\DRAFTS": return ImapSpecialUse.Drafts;
                    case "\\FLAGGED": return ImapSpecialUse.Flagged;
                    case "\\JUNK": return ImapSpecialUse.Junk;
                    case "\\SENT": return ImapSpecialUse.Sent;
                    case "\\TRASH": return ImapSpecialUse.Trash;
                    case "\\IMPORTANT": return ImapSpecialUse.Important;
                    default: break;
                }
            }

            return ImapSpecialUse.None;
        }
    }

    /// <summary>The name.</summary>
    /// <returns><see cref="Name"/>.</returns>
    public override string ToString() => Name;
}

/// <summary>Mailbox roles from RFC 6154.</summary>
public enum ImapSpecialUse
{
    /// <summary>No special use announced.</summary>
    None,

    /// <summary><c>\All</c>.</summary>
    All,

    /// <summary><c>\Archive</c>.</summary>
    Archive,

    /// <summary><c>\Drafts</c>.</summary>
    Drafts,

    /// <summary><c>\Flagged</c>.</summary>
    Flagged,

    /// <summary><c>\Junk</c>.</summary>
    Junk,

    /// <summary><c>\Sent</c>.</summary>
    Sent,

    /// <summary><c>\Trash</c>.</summary>
    Trash,

    /// <summary><c>\Important</c> (RFC 8457).</summary>
    Important,
}

/// <summary>Options for <see cref="ImapClient.ListAsync"/>.</summary>
public sealed class ImapListOptions
{
    /// <summary>List only subscribed mailboxes (<c>LSUB</c>, or <c>LIST (SUBSCRIBED)</c> with LIST-EXTENDED). Default false.</summary>
    public bool SubscribedOnly { get; init; }

    /// <summary>Ask the server to report special-use attributes (<c>RETURN (SPECIAL-USE)</c>) when it offers LIST-EXTENDED and SPECIAL-USE. Default true; servers that send them anyway are read either way.</summary>
    public bool SpecialUse { get; init; } = true;

    /// <summary>Counters to return with each listing (<c>RETURN (STATUS (...))</c>, RFC 5819). Needs LIST-STATUS; ignored otherwise. Default none.</summary>
    public ImapStatusItems Status { get; init; }

    /// <summary>Ask for <c>CHILDREN</c> information. Default false.</summary>
    public bool Children { get; init; }
}

/// <summary>STATUS data items (RFC 9051 §6.3.11, RFC 7162, RFC 8474, RFC 7889).</summary>
[Flags]
public enum ImapStatusItems
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>MESSAGES</c>.</summary>
    Messages = 1,

    /// <summary><c>RECENT</c>.</summary>
    Recent = 2,

    /// <summary><c>UIDNEXT</c>.</summary>
    UidNext = 4,

    /// <summary><c>UIDVALIDITY</c>.</summary>
    UidValidity = 8,

    /// <summary><c>UNSEEN</c>.</summary>
    Unseen = 16,

    /// <summary><c>HIGHESTMODSEQ</c> (CONDSTORE).</summary>
    HighestModSeq = 32,

    /// <summary><c>SIZE</c> (RFC 8438).</summary>
    Size = 64,

    /// <summary><c>APPENDLIMIT</c> (RFC 7889).</summary>
    AppendLimit = 128,

    /// <summary><c>DELETED</c> (RFC 9051).</summary>
    Deleted = 256,

    /// <summary>The five classic items: messages, recent, uidnext, uidvalidity, unseen.</summary>
    Standard = Messages | Recent | UidNext | UidValidity | Unseen,
}

/// <summary>STATUS counters; a value is null when the server did not report it.</summary>
public sealed class ImapMailboxStatus
{
    internal ImapMailboxStatus(IReadOnlyDictionary<string, long> items)
    {
        Items = items;
    }

    /// <summary>Everything the server reported, keyed by upper-case item name.</summary>
    public IReadOnlyDictionary<string, long> Items { get; }

    private long? Get(string k) => Items.TryGetValue(k, out long v) ? v : null;

    /// <summary>Number of messages.</summary>
    public long? Messages => Get("MESSAGES");

    /// <summary>Number of messages with <c>\Recent</c>.</summary>
    public long? Recent => Get("RECENT");

    /// <summary>The next UID.</summary>
    public long? UidNext => Get("UIDNEXT");

    /// <summary>The UID validity value.</summary>
    public long? UidValidity => Get("UIDVALIDITY");

    /// <summary>Messages without <c>\Seen</c>.</summary>
    public long? Unseen => Get("UNSEEN");

    /// <summary>The highest mod-sequence.</summary>
    public long? HighestModSeq => Get("HIGHESTMODSEQ");

    /// <summary>Total size in octets.</summary>
    public long? Size => Get("SIZE");

    /// <summary>The server's per-mailbox APPEND limit in octets.</summary>
    public long? AppendLimit => Get("APPENDLIMIT");

    /// <summary>Messages with <c>\Deleted</c>.</summary>
    public long? Deleted => Get("DELETED");
}

/// <summary>NAMESPACE result (RFC 2342): prefix and delimiter pairs.</summary>
/// <param name="Personal">The user's own mailboxes.</param>
/// <param name="OtherUsers">Other users' mailboxes.</param>
/// <param name="Shared">Shared mailboxes.</param>
public sealed record ImapNamespaces(
    IReadOnlyList<ImapNamespace> Personal,
    IReadOnlyList<ImapNamespace> OtherUsers,
    IReadOnlyList<ImapNamespace> Shared);

/// <summary>A namespace prefix and its hierarchy delimiter.</summary>
/// <param name="Prefix">The prefix, decoded from modified UTF-7.</param>
/// <param name="Delimiter">The delimiter, or null.</param>
public sealed record ImapNamespace(string Prefix, string? Delimiter);

/// <summary>How to open a mailbox with <see cref="ImapClient.SelectAsync"/>.</summary>
public sealed class ImapSelectOptions
{
    /// <summary>Open read-only with <c>EXAMINE</c>. Default false.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Send <c>(CONDSTORE)</c> so the mailbox reports mod-sequences (RFC 7162). Default false; ignored without the capability. QRESYNC implies it.</summary>
    public bool CondStore { get; init; }

    /// <summary>Resynchronise with a known state (RFC 7162 §3.2.5): the client enables QRESYNC and the server answers with the changes since. Default none.</summary>
    public ImapQResync? QResync { get; init; }
}

/// <summary>A previously known mailbox state for QRESYNC.</summary>
/// <param name="UidValidity">The UID validity the client's cache was built under.</param>
/// <param name="ModSeq">The highest mod-sequence the client has seen.</param>
/// <param name="KnownUids">UIDs the client still holds (narrows the VANISHED report), or empty.</param>
public sealed record ImapQResync(long UidValidity, long ModSeq, ImapSet KnownUids = default);

/// <summary>
/// The selected mailbox as the client has tracked it: what SELECT reported, kept current by the unsolicited responses (EXISTS, EXPUNGE,
/// VANISHED, FETCH flags) of every later command and of IDLE. It holds counters only, never a per-message table.
/// </summary>
public sealed class ImapMailbox
{
    internal ImapMailbox(string name)
    {
        Name = name;
    }

    /// <summary>The mailbox name as selected.</summary>
    public string Name { get; }

    /// <summary>Whether it was opened read-only (<c>[READ-ONLY]</c> or EXAMINE).</summary>
    public bool ReadOnly { get; internal set; }

    /// <summary>The UID validity value; UIDs from an earlier value must be discarded.</summary>
    public long UidValidity { get; internal set; }

    /// <summary>The predicted next UID, when reported.</summary>
    public long UidNext { get; internal set; }

    /// <summary>Message count; moves with EXISTS and EXPUNGE.</summary>
    public long Exists { get; internal set; }

    /// <summary>Number of recent messages (IMAP4rev1 servers).</summary>
    public long Recent { get; internal set; }

    /// <summary>Flags defined in the mailbox.</summary>
    public IReadOnlyList<string> Flags { get; internal set; } = [];

    /// <summary>Flags the client may change permanently; <c>\*</c> means new keywords may be created.</summary>
    public IReadOnlyList<string> PermanentFlags { get; internal set; } = [];

    /// <summary>The highest mod-sequence (CONDSTORE), or 0 when the mailbox does not support them.</summary>
    public long HighestModSeq { get; internal set; }

    /// <summary>Whether the server said the mailbox has no mod-sequences (<c>[NOMODSEQ]</c>).</summary>
    public bool NoModSeq { get; internal set; }

    /// <summary>The object id (OBJECTID, RFC 8474), when reported.</summary>
    public string? MailboxId { get; internal set; }

    /// <summary>The first unseen message number, when reported.</summary>
    public long? FirstUnseen { get; internal set; }

    /// <summary>For a QRESYNC select: the changes the server reported while opening (VANISHED EARLIER and changed FETCH data). Empty otherwise.</summary>
    public IReadOnlyList<ImapEvent> ResyncEvents { get; internal set; } = [];
}

/// <summary>What kind of change an <see cref="ImapEvent"/> reports.</summary>
public enum ImapEventKind
{
    /// <summary>The message count changed (<see cref="ImapEvent.Count"/>).</summary>
    Exists,

    /// <summary>The recent count changed.</summary>
    Recent,

    /// <summary>A message was expunged (<see cref="ImapEvent.Number"/> is its sequence number).</summary>
    Expunge,

    /// <summary>Messages were expunged and reported by UID (<see cref="ImapEvent.Uids"/>); <see cref="ImapEvent.Earlier"/> for a resync.</summary>
    Vanished,

    /// <summary>A message's flags (or other fetched data) changed; see <see cref="ImapEvent.Fetch"/>.</summary>
    Fetch,

    /// <summary>The server is closing the connection (<c>BYE</c>).</summary>
    Bye,

    /// <summary>The mailbox flags list changed.</summary>
    MailboxFlags,
}

/// <summary>An unsolicited change reported by the server.</summary>
public sealed class ImapEvent
{
    internal ImapEvent(ImapEventKind kind)
    {
        Kind = kind;
    }

    /// <summary>The kind of change.</summary>
    public ImapEventKind Kind { get; }

    /// <summary>The message count for <see cref="ImapEventKind.Exists"/> and <see cref="ImapEventKind.Recent"/>.</summary>
    public long Count { get; internal set; }

    /// <summary>The sequence number for <see cref="ImapEventKind.Expunge"/> and <see cref="ImapEventKind.Fetch"/>.</summary>
    public long Number { get; internal set; }

    /// <summary>The expunged UIDs for <see cref="ImapEventKind.Vanished"/>.</summary>
    public ImapSet Uids { get; internal set; }

    /// <summary>For <see cref="ImapEventKind.Vanished"/>: true when the report is a resync (<c>EARLIER</c>), not an expunge that just happened.</summary>
    public bool Earlier { get; internal set; }

    /// <summary>The data of a <see cref="ImapEventKind.Fetch"/> event (UID, FLAGS, MODSEQ ...).</summary>
    public ImapFetchData? Fetch { get; internal set; }

    /// <summary>The server's text for <see cref="ImapEventKind.Bye"/>.</summary>
    public string? Text { get; internal set; }
}

/// <summary>Result of COPY or MOVE (UIDPLUS, RFC 4315).</summary>
/// <param name="UidValidity">UID validity of the destination, or 0 when the server did not report <c>COPYUID</c>.</param>
/// <param name="SourceUids">The source UIDs, in order.</param>
/// <param name="DestinationUids">The UIDs assigned in the destination, matching <paramref name="SourceUids"/> by position.</param>
public sealed record ImapCopyResult(long UidValidity, IReadOnlyList<long> SourceUids, IReadOnlyList<long> DestinationUids);

/// <summary>Result of APPEND (UIDPLUS).</summary>
/// <param name="UidValidity">UID validity of the destination, or 0 when the server did not report <c>APPENDUID</c>.</param>
/// <param name="Uids">The UIDs assigned to the appended messages, in order.</param>
public sealed record ImapAppendResult(long UidValidity, IReadOnlyList<long> Uids);

/// <summary>A message to upload with <see cref="ImapClient.AppendAsync(string, IReadOnlyList{ImapAppendItem}, CancellationToken)"/>.</summary>
public sealed class ImapAppendItem
{
    /// <summary>Creates an item from bytes held in memory.</summary>
    /// <param name="message">The RFC 5322 message.</param>
    public ImapAppendItem(ReadOnlyMemory<byte> message)
    {
        Bytes = message;
        Length = message.Length;
    }

    /// <summary>Creates an item from a stream.</summary>
    /// <param name="stream">The stream, positioned at the message start.</param>
    /// <param name="length">Exact number of bytes to send.</param>
    public ImapAppendItem(Stream stream, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Stream = stream;
        Length = length;
    }

    internal ReadOnlyMemory<byte> Bytes { get; }

    internal Stream? Stream { get; }

    /// <summary>The message length in octets.</summary>
    public long Length { get; }

    /// <summary>Flags to set. Default none.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>The internal date to give the message. Default: the server's clock.</summary>
    public DateTimeOffset? InternalDate { get; init; }
}

/// <summary>How <see cref="ImapClient.StoreAsync"/> changes flags.</summary>
public enum ImapStoreMode
{
    /// <summary>Replace the flags (<c>FLAGS</c>).</summary>
    Set,

    /// <summary>Add flags (<c>+FLAGS</c>).</summary>
    Add,

    /// <summary>Remove flags (<c>-FLAGS</c>).</summary>
    Remove,
}

/// <summary>What <see cref="ImapClient.FetchAsync"/> asks the server for. Everything defaults to off; set what you need.</summary>
public sealed class ImapFetchQuery
{
    /// <summary>Include the UID. Default false (a UID fetch always returns it).</summary>
    public bool Uid { get; init; }

    /// <summary>Include <c>FLAGS</c>.</summary>
    public bool Flags { get; init; }

    /// <summary>Include <c>INTERNALDATE</c>.</summary>
    public bool InternalDate { get; init; }

    /// <summary>Include <c>RFC822.SIZE</c>.</summary>
    public bool Size { get; init; }

    /// <summary>Include <c>ENVELOPE</c>.</summary>
    public bool Envelope { get; init; }

    /// <summary>Include <c>BODYSTRUCTURE</c>.</summary>
    public bool BodyStructure { get; init; }

    /// <summary>Include <c>MODSEQ</c> (needs CONDSTORE).</summary>
    public bool ModSeq { get; init; }

    /// <summary>Include <c>EMAILID</c> and <c>THREADID</c> (OBJECTID).</summary>
    public bool ObjectIds { get; init; }

    /// <summary>Fetch the whole message as <c>BODY[]</c>; see <see cref="ImapMessage.Source"/>.</summary>
    public bool Source { get; init; }

    /// <summary>Fetch only the header of the whole message: <c>BODY[HEADER]</c>.</summary>
    public bool Header { get; init; }

    /// <summary>Fetch these header fields only (<c>BODY[HEADER.FIELDS (...)]</c>); see <see cref="ImapMessage.GetHeaderFields"/>.</summary>
    public IReadOnlyList<string> HeaderFields { get; init; } = [];

    /// <summary>Other sections to fetch, e.g. <c>1.2</c>, <c>TEXT</c>, <c>1.MIME</c>.</summary>
    public IReadOnlyList<ImapSectionRequest> Sections { get; init; } = [];

    /// <summary>Use <c>BODY[...]</c> instead of <c>BODY.PEEK[...]</c>, which sets <c>\Seen</c>. Default false.</summary>
    public bool MarkSeen { get; init; }

    /// <summary>Only messages changed since this mod-sequence (CHANGEDSINCE, RFC 7162). Default none.</summary>
    public long? ChangedSince { get; init; }

    /// <summary>With <see cref="ChangedSince"/> on a UID fetch and QRESYNC enabled: also report expunged UIDs (<c>VANISHED</c>). Default false.</summary>
    public bool Vanished { get; init; }
}

/// <summary>One body section to fetch.</summary>
/// <param name="Section">The section specifier without brackets (<c>1.2</c>, <c>TEXT</c>, <c>HEADER</c>, <c>2.MIME</c>); empty for the whole message.</param>
/// <param name="Offset">First octet for a partial fetch (<c>&lt;offset.count&gt;</c>), or null for all.</param>
/// <param name="Count">Number of octets for a partial fetch; used with <paramref name="Offset"/>.</param>
/// <param name="Binary">Use <c>BINARY[...]</c> (RFC 3516), which decodes the transfer encoding on the server. Needs the BINARY capability.</param>
public readonly record struct ImapSectionRequest(string Section, long? Offset = null, long? Count = null, bool Binary = false);

/// <summary>A fetched message: the typed data plus any body sections that were requested and fit the in-memory limit.</summary>
public sealed class ImapMessage
{
    private readonly ImapFetchData _data;
    private readonly Dictionary<string, ReadOnlyMemory<byte>>? _sections;

    internal ImapMessage(ImapFetchData data, Dictionary<string, ReadOnlyMemory<byte>>? sections)
    {
        _data = data;
        _sections = sections;
    }

    /// <summary>The fetch data as the raw layer decoded it, including items without a typed property here.</summary>
    public ImapFetchData Data => _data;

    /// <summary>The message sequence number at the time of the response.</summary>
    public long SequenceNumber => _data.SequenceNumber;

    /// <summary>The UID, if fetched.</summary>
    public long? Uid => _data.Uid;

    /// <summary>The flags, if fetched.</summary>
    public IReadOnlyList<string>? Flags => _data.Flags;

    /// <summary>The internal date, if fetched and parseable.</summary>
    public DateTimeOffset? InternalDate => _data.InternalDate;

    /// <summary>The size in octets, if fetched.</summary>
    public long? Size => _data.Rfc822Size;

    /// <summary>The mod-sequence, if fetched.</summary>
    public long? ModSeq => _data.ModSeq;

    /// <summary>The object id (OBJECTID), if fetched.</summary>
    public string? EmailId => _data.EmailId;

    /// <summary>The thread id (OBJECTID), if fetched.</summary>
    public string? ThreadId => _data.ThreadId;

    /// <summary>The envelope, if fetched.</summary>
    public ImapEnvelope? Envelope => _data.Envelope;

    /// <summary>The body structure, if fetched.</summary>
    public ImapBodyStructure? BodyStructure => _data.BodyStructure;

    /// <summary>The whole message (<c>BODY[]</c>) when <see cref="ImapFetchQuery.Source"/> was set and the literal was within the in-memory limit; otherwise empty. Use <see cref="ImapClient.DownloadAsync(long, string, Stream, long?, long?, CancellationToken)"/> for big ones.</summary>
    public ReadOnlyMemory<byte> Source => GetSection("");

    /// <summary>Returns a fetched section's bytes by specifier (<c>""</c> for the whole message, <c>HEADER</c>, <c>1.2</c> ...; header-field fetches by <see cref="GetHeaderFields"/>).</summary>
    /// <param name="section">The specifier as requested, case-insensitive.</param>
    /// <returns>The bytes, or empty when the section was not fetched or was too large to buffer.</returns>
    public ReadOnlyMemory<byte> GetSection(string section) =>
        _sections is not null && _sections.TryGetValue(section.ToUpperInvariant(), out ReadOnlyMemory<byte> m) ? m : default;

    /// <summary>The bytes of a <c>HEADER.FIELDS</c> fetch (the fields requested, in header syntax, ending with a blank line).</summary>
    /// <returns>The bytes, or empty when not fetched.</returns>
    public ReadOnlyMemory<byte> GetHeaderFields() => GetSection("HEADER.FIELDS");

    /// <summary>Parses <see cref="Source"/> into the MIME entity tree.</summary>
    /// <param name="options">Reader options, or null for the defaults.</param>
    /// <returns>The tree, or null when the message body was not fetched.</returns>
    public MimeEntity? ParseSource(MimeReaderOptions? options = null) =>
        Source.IsEmpty ? null : MimeEntity.Parse(Source, options);

    /// <summary>The UID, or the sequence number when no UID was fetched.</summary>
    /// <returns>A short description.</returns>
    public override string ToString() => Uid is { } u ? "UID " + u : "#" + SequenceNumber;
}
