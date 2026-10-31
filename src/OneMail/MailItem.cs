namespace RecursiveAI.OneMail;

/// <summary>What kind of thing an item is. MIME messages are always <see cref="Message"/>; the other kinds come with the Outlook item classes.</summary>
public enum MailItemKind
{
    /// <summary>An email message (also reports, receipts and meeting messages while they are read as mail).</summary>
    Message = 0,

    /// <summary>A calendar appointment.</summary>
    Appointment,

    /// <summary>A contact.</summary>
    Contact,

    /// <summary>A task.</summary>
    Task,

    /// <summary>A sticky note.</summary>
    Note,

    /// <summary>A journal entry.</summary>
    Journal,

    /// <summary>A distribution list.</summary>
    DistributionList,

    /// <summary>Something the item's format holds that is none of the above.</summary>
    Other,
}

/// <summary>Where a date of an item comes from. An item may carry several; they are different facts, not different spellings of one.</summary>
public enum MailDateSource
{
    /// <summary>The message's own <c>Date</c> header (RFC 5322 §3.6.1), as the sender's client wrote it.</summary>
    DateHeader = 0,

    /// <summary>The time the client submitted the message (MAPI later; never present for MIME).</summary>
    ClientSubmit,

    /// <summary>The time the message arrived at the store: a mbox envelope line, a Maildir file's modification time, an Apple Mail <c>date-received</c>, MAPI delivery time later.</summary>
    Delivery,

    /// <summary>The time the item was created (MAPI later; never present for MIME).</summary>
    Creation,
}

/// <summary>One date with the place it was read from.</summary>
/// <param name="Value">The instant; its offset is the source's zone when <paramref name="ZoneKnown"/> is true, otherwise UTC.</param>
/// <param name="Source">Which fact this date is.</param>
/// <param name="ZoneKnown">False when the source stated no usable time zone (a <c>-0000</c> zone, or a store date that has none). Then the offset in <paramref name="Value"/> is not a claim.</param>
public readonly record struct MailDate(DateTimeOffset Value, MailDateSource Source, bool ZoneKnown);

/// <summary>An email address with its display name. The view flattens groups to their members.</summary>
/// <param name="Name">The decoded display name, or null when there is none.</param>
/// <param name="Address">The address as written (<c>local@domain</c>); possibly not valid, since the reader is lenient.</param>
public readonly record struct MailAddress(string? Name, string Address)
{
    /// <summary>The address in the form <c>Name &lt;address&gt;</c>, or just the address when there is no name. Not quoted for use in a header.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => string.IsNullOrEmpty(Name) ? Address : Name + " <" + Address + ">";
}

/// <summary>One defect, as the family that raised it reports it, in one shape.</summary>
/// <param name="Family">Which reader raised it: <c>mime</c>, <c>mailbox</c> (mbox, Maildir, EMLX), later <c>mapi</c> and others.</param>
/// <param name="Code">The defect's catalogue name, for example <c>MissingFinalBoundary</c>.</param>
/// <param name="Offset">The byte offset it was found at in the item's own bytes (a message's offsets start at its first byte), or -1 when none is known.</param>
/// <param name="IsAmbiguity">True when the defect marks a choice between valid readings and OneMail took the documented one.</param>
public readonly record struct MailDefect(string Family, string Code, long Offset, bool IsAmbiguity);

/// <summary>Where an item was read from.</summary>
/// <param name="Format">The format of the container the bytes came from.</param>
/// <param name="Location">The file or directory path, or null for memory and streams.</param>
/// <param name="Folder">The folder inside the container (a Maildir++ folder name), or null.</param>
/// <param name="Offset">The offset of the item's bytes inside the container file, or -1 when it is not a range of one file.</param>
/// <param name="Length">The length of that range, or -1.</param>
/// <param name="Index">The item's position in its container, from 0, or -1 when it is not part of one.</param>
public readonly record struct MailProvenance(MailFormat Format, string? Location, string? Folder, long Offset, long Length, int Index);

/// <summary>
/// A body of an item in one representation. It is a projection: nothing is read until the caller asks.
/// </summary>
public abstract class MailBody
{
    internal MailBody()
    {
    }

    /// <summary>The media type of the body, lower case: <c>text/plain</c>, <c>text/html</c> or <c>application/rtf</c>.</summary>
    public abstract string ContentType { get; }

    /// <summary>The charset the item states for the body, lower case as written, or null when it states none.</summary>
    public abstract string? Charset { get; }

    /// <summary>Opens the body bytes with any transfer encoding (base64, quoted-printable) removed, as a new stream.</summary>
    /// <returns>A readable stream; the caller disposes it.</returns>
    public abstract Stream Open();

    /// <summary>
    /// Reads the whole body as text. The charset is the stated one; when it is absent or unknown the bytes are read as UTF-8 if
    /// they are valid UTF-8 and as Windows-1252 otherwise. Undecodable bytes become U+FFFD.
    /// </summary>
    /// <returns>The text.</returns>
    public abstract string ReadText();
}

/// <summary>An attachment of an item. An attachment is an item in its own right: it has a name, a type, content, and, when it is a message, an embedded item with the full view.</summary>
public interface IMailAttachment
{
    /// <summary>The file name as written (the disposition's <c>filename</c>, else the type's <c>name</c>), decoded; null when it has none. Not made safe for a file system.</summary>
    string? FileName { get; }

    /// <summary>The media type, lower case.</summary>
    string ContentType { get; }

    /// <summary>The <c>Content-ID</c> without angle brackets, or null. Inline content is addressed from HTML as <c>cid:</c> + this value.</summary>
    string? ContentId { get; }

    /// <summary>True when the attachment is meant to be shown inside the body: it is not marked <c>attachment</c> and has a content ID.</summary>
    bool IsInline { get; }

    /// <summary>The size of the decoded content in bytes. Computed on first use by decoding the content once, so it costs a pass over large attachments.</summary>
    long Size { get; }

    /// <summary>Opens the decoded content as a new stream.</summary>
    /// <returns>A readable stream; the caller disposes it.</returns>
    Stream Open();

    /// <summary>The embedded message when the attachment is a message (<c>message/rfc822</c>), else null.</summary>
    IMailItem? Embedded { get; }
}

/// <summary>
/// The one read view every item implements, whatever it was stored as. Everything is a lazy projection over the item's own
/// model: reading a property parses only what it needs and copies nothing. Accessors never throw on malformed content;
/// what was repaired is reported by <see cref="Defects"/>. A value an item's format cannot have is null or empty.
/// </summary>
public interface IMailItem
{
    /// <summary>What the item is.</summary>
    MailItemKind Kind { get; }

    /// <summary>The decoded subject, or null when the item has none.</summary>
    string? Subject { get; }

    /// <summary>The actual sender when it differs from <see cref="From"/> (the <c>Sender</c> header), or null.</summary>
    MailAddress? Sender { get; }

    /// <summary>The authors (<c>From</c>), groups flattened to their members. Only the first <c>From</c> field is read.</summary>
    IReadOnlyList<MailAddress> From { get; }

    /// <summary>The primary recipients (the <c>To</c> header).</summary>
    IReadOnlyList<MailAddress> ToRecipients { get; }

    /// <summary>The copy recipients (<c>Cc</c>).</summary>
    IReadOnlyList<MailAddress> Cc { get; }

    /// <summary>The blind-copy recipients (<c>Bcc</c>); present in drafts and sent copies.</summary>
    IReadOnlyList<MailAddress> Bcc { get; }

    /// <summary>Where replies should go (<c>Reply-To</c>).</summary>
    IReadOnlyList<MailAddress> ReplyTo { get; }

    /// <summary>The item's own date, when it was sent: the <c>Date</c> header for MIME (source <see cref="MailDateSource.DateHeader"/>). Null when absent or unreadable.</summary>
    MailDate? Sent { get; }

    /// <summary>Reads one of the item's dates by source.</summary>
    /// <param name="source">Which date.</param>
    /// <param name="value">The date when present.</param>
    /// <returns>True when the item has that date.</returns>
    bool TryGetDate(MailDateSource source, out MailDate value);

    /// <summary>The <c>Message-ID</c> without angle brackets, or null.</summary>
    string? MessageId { get; }

    /// <summary>The message IDs in <c>In-Reply-To</c>, without angle brackets.</summary>
    IReadOnlyList<string> InReplyTo { get; }

    /// <summary>The message IDs in <c>References</c>, without angle brackets.</summary>
    IReadOnlyList<string> References { get; }

    /// <summary>The conversation index bytes (MAPI <c>PidTagConversationIndex</c>; the <c>Thread-Index</c> header of MIME), empty when there is none.</summary>
    ReadOnlyMemory<byte> ConversationIndex { get; }

    /// <summary>The conversation topic (MAPI <c>PidTagConversationTopic</c>; the <c>Thread-Topic</c> header), or null.</summary>
    string? ConversationTopic { get; }

    /// <summary>
    /// The plain-text body candidate, or null. This is a raw candidate, not the best body: choosing between the
    /// representations, and converting one into another, is not done here.
    /// </summary>
    MailBody? TextBody { get; }

    /// <summary>The HTML body candidate, or null. See <see cref="TextBody"/>.</summary>
    MailBody? HtmlBody { get; }

    /// <summary>The RTF body candidate, or null (MIME has none). See <see cref="TextBody"/>.</summary>
    MailBody? RtfBody { get; }

    /// <summary>
    /// The attachments in document order, built on first use. Inline content (images a body refers to by <c>cid:</c>) is
    /// included and has <see cref="IMailAttachment.IsInline"/> set.
    /// </summary>
    IReadOnlyList<IMailAttachment> Attachments { get; }

    /// <summary>Every defect of the item and of what it holds, with the structured headers read first so their defects are included.</summary>
    /// <returns>The defects, in the order the owning reader found them. Evaluated each time it is enumerated.</returns>
    IEnumerable<MailDefect> Defects { get; }

    /// <summary>Where the item came from.</summary>
    MailProvenance Provenance { get; }
}
