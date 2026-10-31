namespace RecursiveAI.OneMail.Stores;

/// <summary>
/// What a store reader repaired or guessed. Store readers never throw on malformed content: they keep going, set the
/// matching bit on the entry and report the byte offset through the reader's defect handler. Members whose name starts
/// with <c>Ambiguous</c> mark places where real programs disagree and OneMail took one documented reading
/// (<c>docs/mailboxes.md</c>).
/// </summary>
[Flags]
public enum StoreDefects
{
    /// <summary>No defect.</summary>
    None = 0,

    /// <summary>mbox: bytes before the first <c>From </c> line. They are kept as the store's preamble, not as a message.</summary>
    PreambleBeforeFirstMessage = 1 << 0,

    /// <summary>mbox: a message is not followed by an empty line before the next <c>From </c> line. The next line still starts a message.</summary>
    MissingSeparatorLine = 1 << 1,

    /// <summary>
    /// mbox: a line starts with <c>From </c> but does not look like an envelope line (no sender and date). In the default
    /// strict mode it is message text, not a delimiter (ambiguity: other readers split there).
    /// </summary>
    AmbiguousFromLine = 1 << 2,

    /// <summary>
    /// mbox: a <c>Content-Length</c> header is present but the message does not end where it says, so it is ignored and the
    /// message ends at the next envelope line (ambiguity).
    /// </summary>
    ContentLengthMismatch = 1 << 3,

    /// <summary>mbox: two <c>Content-Length</c> headers disagree. The first one is used (ambiguity).</summary>
    AmbiguousContentLength = 1 << 4,

    /// <summary>mbox: an envelope line longer than the reader's limit. It is kept whole, as the envelope.</summary>
    OverlongEnvelopeLine = 1 << 5,

    /// <summary>Maildir: a file name that does not follow the <c>unique:2,flags</c> shape. The whole name is the unique part.</summary>
    UnrecognisedFileName = 1 << 6,

    /// <summary>Maildir: flag letters in the info section are not in ASCII order or repeat. They are read as written.</summary>
    UnsortedFlags = 1 << 7,

    /// <summary>Maildir: a keyword letter (a to z) has no entry in the folder's keyword file. The letter is surfaced as the keyword name.</summary>
    UnknownKeywordLetter = 1 << 8,

    /// <summary>A control file (keyword list, UID list, plist) that could not be parsed. Its entries are skipped.</summary>
    MalformedControlFile = 1 << 9,

    /// <summary>EMLX: the byte-count line is missing or not a number. The entry has no message.</summary>
    BadByteCount = 1 << 10,

    /// <summary>EMLX: the byte count runs past the end of the file. The message is cut at the end of the file.</summary>
    TruncatedMessage = 1 << 11,

    /// <summary>EMLX: the bytes after the message are not a property list. Kept as unparsed trailer bytes.</summary>
    BadTrailer = 1 << 12,

    /// <summary>EMLX: a partial message whose attachments are stored beside it (<c>.partial.emlx</c>).</summary>
    PartialMessage = 1 << 13,
}

/// <summary>One defect with the byte offset it was found at, as delivered to a reader's defect handler.</summary>
/// <param name="Kind">The defect (a single bit).</param>
/// <param name="Offset">Absolute offset in the file the defect refers to; for directory stores, 0.</param>
public readonly record struct StoreDefect(StoreDefects Kind, long Offset);

/// <summary>Message flags shared by the stores. Each store maps its own spelling onto these.</summary>
[Flags]
public enum MessageMarks
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The message has been read (Maildir <c>S</c>, mbox <c>Status: R</c>, IMAP <c>\Seen</c>).</summary>
    Seen = 1 << 0,

    /// <summary>The message has been answered (Maildir <c>R</c>, mbox <c>X-Status: A</c>).</summary>
    Answered = 1 << 1,

    /// <summary>The message is flagged (Maildir <c>F</c>, mbox <c>X-Status: F</c>).</summary>
    Flagged = 1 << 2,

    /// <summary>The message is marked deleted or trashed (Maildir <c>T</c>, mbox <c>X-Status: D</c>).</summary>
    Deleted = 1 << 3,

    /// <summary>The message is a draft (Maildir <c>D</c>, mbox <c>X-Status: T</c>).</summary>
    Draft = 1 << 4,

    /// <summary>The message was forwarded or resent (Maildir <c>P</c>, "passed").</summary>
    Forwarded = 1 << 5,

    /// <summary>The message has arrived since the last session (Maildir: it is in <c>new</c>).</summary>
    Recent = 1 << 6,
}

/// <summary>The kinds of mail store that content sniffing can name.</summary>
public enum StoreFormat
{
    /// <summary>Nothing recognised.</summary>
    Unknown = 0,

    /// <summary>A single mbox file (any variant).</summary>
    Mbox,

    /// <summary>A single <c>.emlx</c> or <c>.partial.emlx</c> file.</summary>
    Emlx,

    /// <summary>A directory with <c>cur</c>, <c>new</c> and <c>tmp</c> subdirectories.</summary>
    MaildirFolder,

    /// <summary>A Maildir++ root: a Maildir folder with dot-prefixed Maildir subfolders, or a directory holding only such subfolders.</summary>
    MaildirRoot,

    /// <summary>An Apple Mail mailbox bundle (a directory holding <c>Messages</c> directories of <c>.emlx</c> files).</summary>
    AppleMailBundle,
}
