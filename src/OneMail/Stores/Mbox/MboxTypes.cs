namespace RecursiveAI.OneMail.Stores.Mbox;

/// <summary>
/// The mbox dialects. They differ in how a message body line that would read as an envelope line is quoted and in
/// whether a <c>Content-Length</c> header says where the message ends.
/// </summary>
public enum MboxVariant
{
    /// <summary>
    /// Not known. A reader treats the store as unquoted-agnostic: it uses a <c>Content-Length</c> only when that length
    /// checks out, and a writer uses <see cref="Mboxrd"/>.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The original form: a body line starting with <c>From </c> is written as <c>&gt;From </c>. Lines already starting
    /// with <c>&gt;From </c> are written unchanged, so the quoting cannot be undone exactly. No <c>Content-Length</c>.
    /// </summary>
    Mboxo,

    /// <summary>
    /// The reversible form: a body line that is zero or more <c>&gt;</c> followed by <c>From </c> gets one more
    /// <c>&gt;</c>, and reading removes one. No <c>Content-Length</c>.
    /// </summary>
    Mboxrd,

    /// <summary>
    /// Like <see cref="Mboxo"/> quoting, plus a <c>Content-Length</c> header counting the stored body bytes after the
    /// blank line that ends the headers. Readers use it to find the message end.
    /// </summary>
    Mboxcl,

    /// <summary>Like <see cref="Mboxcl"/> but body lines are not quoted at all: the <c>Content-Length</c> alone protects them.</summary>
    Mboxcl2,
}

/// <summary>How a reader decides that a line starting with <c>From </c> begins a message.</summary>
public enum MboxFromLineMode
{
    /// <summary>
    /// The line must read as an envelope line: <c>From </c>, a sender, and a date such as <c>Mon Jan  1 00:00:00 2024</c>
    /// (RFC 4155). Other lines starting with <c>From </c> are message text and raise
    /// <see cref="StoreDefects.AmbiguousFromLine"/>.
    /// </summary>
    Strict = 0,

    /// <summary>Every line starting with <c>From </c> begins a message, whatever follows. This is what Python's <c>mailbox</c> and MimeKit do.</summary>
    Any,
}

/// <summary>Options for <see cref="MboxReader"/>.</summary>
public sealed class MboxReaderOptions
{
    /// <summary>
    /// The variant to read. <see cref="MboxVariant.Unknown"/> (the default) splits on envelope lines and trusts a
    /// <c>Content-Length</c> only when it lands on an envelope line or the end of the file. <see cref="MboxVariant.Mboxo"/>
    /// and <see cref="MboxVariant.Mboxrd"/> ignore <c>Content-Length</c>; <see cref="MboxVariant.Mboxcl"/> and
    /// <see cref="MboxVariant.Mboxcl2"/> use it when it checks out.
    /// </summary>
    public MboxVariant Variant { get; init; } = MboxVariant.Unknown;

    /// <summary>Which <c>From </c> lines delimit messages. Default <see cref="MboxFromLineMode.Strict"/>.</summary>
    public MboxFromLineMode FromLineMode { get; init; } = MboxFromLineMode.Strict;

    /// <summary>
    /// The longest line that can be an envelope line, in bytes. A longer <c>From </c> line is message text in strict mode;
    /// in <see cref="MboxFromLineMode.Any"/> mode it is an envelope that raises <see cref="StoreDefects.OverlongEnvelopeLine"/>.
    /// Default 4096.
    /// </summary>
    public int MaxEnvelopeLineBytes { get; init; } = 4096;

    /// <summary>
    /// The most header bytes the reader looks through for a <c>Content-Length</c> header. Default 65536. A longer header
    /// block means the length is not used.
    /// </summary>
    public int MaxHeaderBytes { get; init; } = 65536;

    /// <summary>The size of the read window in bytes, at least 16384. Default 65536. Working memory never exceeds this plus the pooled copy buffers.</summary>
    public int BufferSize { get; init; } = 65536;

    /// <summary>Called for every defect with its byte offset. It runs on the enumerating thread and must not block. Default none.</summary>
    public Action<StoreDefect>? DefectHandler { get; init; }
}

/// <summary>
/// One message of an mbox file: where its envelope line and bytes are, and what the reader noted. The entry is plain data
/// and keeps no reference to the file.
/// </summary>
/// <remarks>
/// The record is laid out as <c>[envelope line][message][separator]</c> and records tile the file after the preamble:
/// <c>Offset + RecordLength</c> is the next record's <c>Offset</c>. The message excludes the single empty line that mbox
/// writers add after it, so the bytes of <c>[MessageOffset, MessageOffset + MessageLength)</c> are the message as the
/// writer was given it (still quoted, see <see cref="MboxReader.OpenMessage"/>).
/// </remarks>
public readonly struct MboxEntry
{
    /// <summary>Creates an entry. Readers create them; callers rarely need to.</summary>
    /// <param name="index">Zero-based position among the file's messages.</param>
    /// <param name="offset">Offset of the envelope line.</param>
    /// <param name="envelopeLength">Length of the envelope line including its line ending.</param>
    /// <param name="messageLength">Length of the message bytes.</param>
    /// <param name="recordLength">Length of envelope, message and separator together.</param>
    /// <param name="contentLength">The <c>Content-Length</c> used to find the end, or -1.</param>
    /// <param name="defects">The defects noted for this entry.</param>
    public MboxEntry(int index, long offset, long envelopeLength, long messageLength, long recordLength, long contentLength, StoreDefects defects)
    {
        Index = index;
        Offset = offset;
        EnvelopeLength = envelopeLength;
        MessageLength = messageLength;
        RecordLength = recordLength;
        ContentLength = contentLength;
        Defects = defects;
    }

    /// <summary>Zero-based position among the file's messages.</summary>
    public int Index { get; }

    /// <summary>Offset of the envelope (<c>From </c>) line.</summary>
    public long Offset { get; }

    /// <summary>Length of the envelope line including its line ending (LF or CRLF).</summary>
    public long EnvelopeLength { get; }

    /// <summary>Offset of the first message byte, right after the envelope line.</summary>
    public long MessageOffset => Offset + EnvelopeLength;

    /// <summary>Length of the message in bytes, without the separating empty line. May be 0.</summary>
    public long MessageLength { get; }

    /// <summary>Length of the envelope line, message and separator together; the next record starts this far on.</summary>
    public long RecordLength { get; }

    /// <summary>The <c>Content-Length</c> that located the end of this message, or -1 when the end was found by scanning.</summary>
    public long ContentLength { get; }

    /// <summary>Whether a valid <c>Content-Length</c> decided where this message ends.</summary>
    public bool EndedByContentLength => ContentLength >= 0;

    /// <summary>What the reader repaired or guessed for this message.</summary>
    public StoreDefects Defects { get; }
}
