using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Composition;

/// <summary>How a forward carries the original message.</summary>
public enum ForwardStyle
{
    /// <summary>The original's text goes into the new body under a header block (From, Date, Subject, To, Cc) and its attachments are carried along.</summary>
    Inline = 0,

    /// <summary>The original travels as a <c>message/rfc822</c> attachment, byte-identical to its source for MIME items and converted by the MS-OXCMAIL mapping for Outlook items.</summary>
    Attachment,
}

/// <summary>Which representations of the quoted body a reply or inline forward carries.</summary>
public enum QuoteBodyFormat
{
    /// <summary>Both text and HTML when the original has both, else the one it has.</summary>
    Auto = 0,

    /// <summary>Plain text only. An original that has only HTML is not quoted (no HTML to text conversion happens here); <see cref="CompositionDefectKind.BodyNotAvailable"/> says so.</summary>
    TextOnly,

    /// <summary>HTML only. An original that has only text is not quoted; <see cref="CompositionDefectKind.BodyNotAvailable"/> says so.</summary>
    HtmlOnly,
}

/// <summary>Whether a new message carries <c>Thread-Topic</c> and <c>Thread-Index</c> (MAPI: <c>PidTagConversationTopic</c> and <c>PidTagConversationIndex</c>).</summary>
public enum ConversationIndexMode
{
    /// <summary>When the original is an Outlook item or already has a conversation index; other MIME messages do not carry one.</summary>
    Auto = 0,

    /// <summary>Always: a fresh header block when the original has none.</summary>
    Always,

    /// <summary>Never.</summary>
    Never,
}

/// <summary>Settings of <see cref="MessageComposer"/>. Every property has a default that gives a usable reply.</summary>
public sealed class CompositionOptions
{
    /// <summary>The user's own addresses. They are left out of reply-all recipients (compared ignoring case). Default: none.</summary>
    public IReadOnlyList<string> OwnAddresses { get; init; } = [];

    /// <summary>The <c>From</c> of the new message. Default: null, no <c>From</c> header is written.</summary>
    public MailAddress? From { get; init; }

    /// <summary>The clock for the <c>Date</c> header and the conversation index. Default: the system clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Makes the <c>Message-ID</c> (without angle brackets). Default: null; then <see cref="MessageIdDomain"/> is used, and without
    /// that no <c>Message-ID</c> is written (a submission server adds one).
    /// </summary>
    public Func<string>? MessageIdFactory { get; init; }

    /// <summary>The domain of generated message IDs (<c>random@domain</c>). Default: null.</summary>
    public string? MessageIdDomain { get; init; }

    /// <summary>Fills a buffer with random bytes: message IDs, conversation GUIDs, the random byte of a conversation level. Default: the system's cryptographic generator. Replace it for reproducible output.</summary>
    public Action<Span<byte>>? RandomBytes { get; init; }

    /// <summary>Whether to quote the original in a reply and an inline forward. Default: true.</summary>
    public bool Quote { get; init; } = true;

    /// <summary>Which body representations are quoted. Default: <see cref="QuoteBodyFormat.Auto"/>.</summary>
    public QuoteBodyFormat BodyFormat { get; init; }

    /// <summary>The author's own new text, placed above the quote (top posting). Default: none.</summary>
    public string? Text { get; init; }

    /// <summary>The author's own new HTML, placed above the quote; used when the message has an HTML body. Default: none (the plain <see cref="Text"/> is escaped and used).</summary>
    public string? Html { get; init; }

    /// <summary>Makes the attribution line (<c>On Mon, 1 Jan 2024 10:00:00 +0000, Ann &lt;a@example.com&gt; wrote:</c>) from the original. Default: null, the built-in English form.</summary>
    public Func<IMailItem, string>? Attribution { get; init; }

    /// <summary>The words of the inline forward's banner line. Default: <c>Forwarded message</c>.</summary>
    public string ForwardBanner { get; init; } = "Forwarded message";

    /// <summary>Inline or as an attachment. Default: <see cref="ForwardStyle.Inline"/>.</summary>
    public ForwardStyle ForwardStyle { get; init; }

    /// <summary>Whether a reply carries the original's attachments (inline images referred to from the quoted HTML are always carried). Default: false. A forward carries them in the inline style regardless.</summary>
    public bool IncludeAttachments { get; init; }

    /// <summary>The prefix put on a reply's subject. Default: <c>Re: </c>.</summary>
    public string ReplyPrefix { get; init; } = "Re: ";

    /// <summary>The prefix put on a forward's subject. Default: <c>Fwd: </c>.</summary>
    public string ForwardPrefix { get; init; } = "Fwd: ";

    /// <summary>When the new message carries a conversation topic and index. Default: <see cref="ConversationIndexMode.Auto"/>.</summary>
    public ConversationIndexMode ConversationIndex { get; init; }

    /// <summary>Also build the message in the MAPI property model. Default: null, which means yes when the original is an Outlook item.</summary>
    public bool? CreateMapi { get; init; }

    /// <summary>How many identifiers <c>References</c> keeps; beyond that the first and the most recent ones stay. Default: 20 (at least 2).</summary>
    public int MaxReferences { get; init; } = 20;

    /// <summary>The most recipients taken over from the original; further ones are dropped with a defect. Default: 500.</summary>
    public int MaxRecipients { get; init; } = 500;

    /// <summary>The most characters of the original's body that are quoted; the rest is cut at a line end with a defect. Default: 1,048,576.</summary>
    public int MaxQuoteCharacters { get; init; } = 1 << 20;

    /// <summary>The most bytes of attachments and embedded originals carried in total; what does not fit is skipped with a defect. Default: 64 MiB.</summary>
    public long MaxCarriedBytes { get; init; } = 64L << 20;

    /// <summary>
    /// False (the default) writes display names, the subject, the conversation topic and file names as ASCII: RFC 2047 encoded-words and RFC 2231 extended
    /// parameters. True writes them as raw UTF-8 (RFC 6532), which only an SMTPUTF8 transport (RFC 6531) or a store that keeps bytes can carry.
    /// Addresses are never converted either way: a non-ASCII local part or domain is written as given.
    /// </summary>
    public bool Utf8Headers { get; init; }
}

/// <summary>What the composer repaired, skipped or could not do.</summary>
public enum CompositionDefectKind
{
    /// <summary>The original has no <c>Message-ID</c>: no <c>In-Reply-To</c> is written and <c>References</c> holds only what the original had.</summary>
    ParentHasNoMessageId = 0,

    /// <summary>An identifier in the original's <c>References</c> or <c>In-Reply-To</c> cannot be written (white space, angle brackets, control characters, too long) and was left out.</summary>
    InvalidMessageIdDropped,

    /// <summary><c>References</c> was cut to <see cref="CompositionOptions.MaxReferences"/> identifiers.</summary>
    ReferencesTruncated,

    /// <summary>A recipient has no SMTP address (an Exchange distinguished name only) and cannot be written in a header; it is listed in <see cref="ComposedMessage.Unaddressable"/>.</summary>
    UnaddressableRecipient,

    /// <summary>A recipient's address is not usable (no single <c>@</c>, white space or specials) and was left out.</summary>
    InvalidAddressDropped,

    /// <summary>More recipients than <see cref="CompositionOptions.MaxRecipients"/>; the rest were left out.</summary>
    TooManyRecipients,

    /// <summary>No recipient is left for the reply (the original has no usable sender).</summary>
    NoReplyTarget,

    /// <summary>The original's conversation index is not in the MS-OXOMSG form; a fresh header block was made.</summary>
    ConversationIndexInvalid,

    /// <summary>The conversation index has no room for another level; the original's index is kept.</summary>
    ConversationIndexFull,

    /// <summary>The original's time is after the clock's; the new level's time delta is zero.</summary>
    ConversationIndexTimeClamped,

    /// <summary>The quoted body was cut at <see cref="CompositionOptions.MaxQuoteCharacters"/>.</summary>
    QuoteTruncated,

    /// <summary>The requested body representation does not exist in the original and is not converted here.</summary>
    BodyNotAvailable,

    /// <summary>An attachment or the embedded original was not carried: over <see cref="CompositionOptions.MaxCarriedBytes"/>, or unreadable.</summary>
    AttachmentSkipped,

    /// <summary>The original is neither a MIME nor an Outlook item; the embedded copy was rebuilt from the shared view and is not the item's bytes.</summary>
    ParentSynthesized,
}

/// <summary>One thing the composer repaired or skipped.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">The identifier, address or attachment concerned; empty when none.</param>
public readonly record struct CompositionDefect(CompositionDefectKind Kind, string Detail);

/// <summary>A reply or forward that was made.</summary>
public sealed class ComposedMessage
{
    internal ComposedMessage(MimeEntity message, MapiMessage? mapi, LossReport? report, string subject, IReadOnlyList<MailAddress> to, IReadOnlyList<MailAddress> cc, IReadOnlyList<MailAddress> unaddressable, IReadOnlyList<CompositionDefect> defects)
    {
        Message = message;
        Mapi = mapi;
        Report = report;
        Subject = subject;
        To = to;
        Cc = cc;
        Unaddressable = unaddressable;
        Defects = defects;
    }

    /// <summary>The new message as a MIME entity tree; write it with <see cref="MimeEntity.WriteTo(Stream)"/> or <see cref="MimeEntity.ToArray"/>.</summary>
    public MimeEntity Message { get; }

    /// <summary>The same message in the MAPI property model (made by the MS-OXCMAIL import), or null when not asked for or not an Outlook original.</summary>
    public MapiMessage? Mapi { get; }

    /// <summary>What the MIME to MAPI import did not carry over, or null when there is no MAPI message.</summary>
    public LossReport? Report { get; }

    /// <summary>The new subject, decoded.</summary>
    public string Subject { get; }

    /// <summary>The <c>To</c> recipients written.</summary>
    public IReadOnlyList<MailAddress> To { get; }

    /// <summary>The <c>Cc</c> recipients written.</summary>
    public IReadOnlyList<MailAddress> Cc { get; }

    /// <summary>Recipients of the original that could not be written because they have no SMTP form; never dropped without being listed here.</summary>
    public IReadOnlyList<MailAddress> Unaddressable { get; }

    /// <summary>What was repaired or skipped, in the order it happened.</summary>
    public IReadOnlyList<CompositionDefect> Defects { get; }
}
