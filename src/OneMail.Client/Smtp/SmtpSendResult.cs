using RecursiveAI.OneMail.Client.Smtp.Protocol;

namespace RecursiveAI.OneMail.Client.Smtp;

/// <summary>The server's answer to one <c>RCPT TO</c>.</summary>
/// <param name="Address">The recipient address as sent.</param>
/// <param name="Reply">The server's reply.</param>
public sealed record SmtpRecipientResult(string Address, SmtpReply Reply)
{
    /// <summary>Whether the server accepted the recipient (a 2xx reply).</summary>
    public bool Accepted => Reply.IsPositiveCompletion;

    /// <summary>Whether a rejection is temporary (4xx): the recipient may be tried again later.</summary>
    public bool IsTemporaryFailure => Reply.IsTransientFailure;

    /// <summary>The enhanced status code (RFC 3463) of the reply, when it carries one.</summary>
    public SmtpEnhancedStatus? EnhancedStatus => Reply.EnhancedStatus;
}

/// <summary>What happened to one message. Rejections are results, not exceptions.</summary>
public sealed class SmtpSendResult
{
    internal SmtpSendResult(IReadOnlyList<SmtpRecipientResult> recipients) => Recipients = recipients;

    /// <summary>The answer to every <c>RCPT TO</c>, in order. Empty when the sender was rejected or the message was refused before the envelope.</summary>
    public IReadOnlyList<SmtpRecipientResult> Recipients { get; }

    /// <summary>The answer to <c>MAIL FROM</c>; a failure here means nothing was sent.</summary>
    public SmtpReply? SenderReply { get; internal set; }

    /// <summary>The server's final reply to the data (the queue acknowledgement, for example <c>250 2.0.0 Ok: queued as 4F3A1</c>), or null when no data was sent.</summary>
    public SmtpReply? DataReply { get; internal set; }

    /// <summary>Whether the server took the message for at least one recipient (a 2xx final reply).</summary>
    public bool Delivered => DataReply is { IsPositiveCompletion: true };

    /// <summary>The recipients the server accepted.</summary>
    public IEnumerable<SmtpRecipientResult> Accepted => Recipients.Where(r => r.Accepted);

    /// <summary>The recipients the server rejected.</summary>
    public IEnumerable<SmtpRecipientResult> Rejected => Recipients.Where(r => !r.Accepted);

    /// <summary>Whether the message was taken and every recipient was accepted.</summary>
    public bool Complete => Delivered && Recipients.All(r => r.Accepted);

    /// <summary>The size in bytes of the message as sent, when it was measured (it is when the server announced SIZE).</summary>
    public long? Size { get; internal set; }

    /// <summary>Whether the data went by BDAT (RFC 3030) rather than DATA.</summary>
    public bool UsedChunking { get; internal set; }

    /// <summary>Whether 8-bit parts were re-encoded because the server lacks 8BITMIME.</summary>
    public bool Downgraded { get; internal set; }

    /// <summary>Whether the message was sent with the SMTPUTF8 parameter.</summary>
    public bool UsedSmtpUtf8 { get; internal set; }

    /// <summary>Throws if the message was not delivered to anyone.</summary>
    /// <exception cref="SmtpRejectedException">The sender, all recipients or the data were rejected.</exception>
    public void ThrowIfNotDelivered()
    {
        if (!Delivered)
        {
            throw new SmtpRejectedException(this);
        }
    }
}
