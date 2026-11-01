namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>
/// Resource limits for the protocol readers. They are on by default and protect against a server (or a man in the middle)
/// that sends endless lines, nested lists or literals. A limit that is hit throws <see cref="MailProtocolLimitException"/>,
/// because the stream cannot be resynchronised safely afterwards.
/// </summary>
public sealed class ProtocolLimits
{
    /// <summary>The defaults.</summary>
    public static ProtocolLimits Default { get; } = new();

    /// <summary>
    /// The longest line, in bytes without the line end. Default 1,048,576. RFC 5321 §4.5.3.1.5 allows 512 for SMTP replies and
    /// RFC 2449 §4 allows 512 for POP3 lines, but IMAP lines carry whole envelopes and folded lists, so one generous limit is used.
    /// </summary>
    public int MaxLineLength { get; init; } = 1024 * 1024;

    /// <summary>
    /// The most bytes one response may buffer, lines and buffered literals together. Default 67,108,864 (64 MiB). Streamed
    /// literals do not count.
    /// </summary>
    public long MaxResponseSize { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// The largest literal that is read into memory. Default 16,777,216 (16 MiB). A larger literal that nobody streams is
    /// skipped (not stored) and reported with <see cref="ProtocolDefectKind.LiteralSkipped"/>.
    /// </summary>
    public long MaxBufferedLiteral { get; init; } = 16L * 1024 * 1024;

    /// <summary>The largest literal that is accepted at all, streamed or not. Default 4 GiB. A larger announcement throws.</summary>
    public long MaxLiteralSize { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>The deepest nesting of parenthesised lists. Default 64.</summary>
    public int MaxNesting { get; init; } = 64;

    /// <summary>The most lines in one SMTP reply or POP3 status block. Default 1,000.</summary>
    public int MaxReplyLines { get; init; } = 1000;

    /// <summary>The most items in one parenthesised list or response. Default 1,000,000.</summary>
    public int MaxItems { get; init; } = 1_000_000;
}
