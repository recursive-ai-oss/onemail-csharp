using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Smtp.Protocol;

namespace RecursiveAI.OneMail.Client.Smtp;

/// <summary>The base of the failures the SMTP client raises itself. Rejected recipients are not exceptions: see <see cref="SmtpSendResult"/>.</summary>
public class SmtpClientException : MailProtocolException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public SmtpClientException(string message) : base(message) { }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">The cause.</param>
    public SmtpClientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The server did not give a usable greeting or EHLO/HELO answer.</summary>
public sealed class SmtpGreetingException : SmtpClientException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reply">The unexpected reply.</param>
    public SmtpGreetingException(SmtpReply reply) : base("The server did not accept the session: " + reply) => Reply = reply;

    /// <summary>The reply.</summary>
    public SmtpReply Reply { get; }
}

/// <summary>Authentication failed: no mechanism fit, or the server refused.</summary>
public sealed class SmtpAuthenticationException : SmtpClientException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="reply">The server's final reply, if any.</param>
    /// <param name="inner">The cause, if any.</param>
    public SmtpAuthenticationException(string message, SmtpReply? reply = null, Exception? inner = null) : base(message, inner!) => Reply = reply;

    /// <summary>The server's final reply to the last attempt, or null.</summary>
    public SmtpReply? Reply { get; }
}

/// <summary>The message or connection needs an extension the server does not offer, so nothing was sent.</summary>
public sealed class SmtpFeatureRequiredException : SmtpClientException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="feature">The extension keyword (<c>STARTTLS</c>, <c>REQUIRETLS</c>, <c>SMTPUTF8</c>, <c>DSN</c>).</param>
    public SmtpFeatureRequiredException(string feature) : base("The server does not offer " + feature + ", which this operation requires.") => Feature = feature;

    /// <summary>The missing extension.</summary>
    public string Feature { get; }
}

/// <summary>The message is larger than the size the server announced (RFC 1870). Nothing was sent.</summary>
public sealed class SmtpMessageTooLargeException : SmtpClientException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="size">The message size in bytes.</param>
    /// <param name="limit">The server's limit in bytes.</param>
    public SmtpMessageTooLargeException(long size, long limit) : base($"The message is {size} bytes; the server accepts at most {limit}.")
    {
        Size = size;
        Limit = limit;
    }

    /// <summary>The message size.</summary>
    public long Size { get; }

    /// <summary>The server's limit.</summary>
    public long Limit { get; }
}

/// <summary>The message has 8-bit content the server cannot take and it cannot be re-encoded safely (it is signed, or the headers are not ASCII). Nothing was sent.</summary>
public sealed class SmtpDowngradeRefusedException : SmtpClientException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why it was refused.</param>
    public SmtpDowngradeRefusedException(string message) : base(message) { }
}

/// <summary>Raised by <see cref="SmtpSendResult.ThrowIfNotDelivered"/>.</summary>
public sealed class SmtpRejectedException : SmtpClientException
{
    internal SmtpRejectedException(SmtpSendResult result) : base("The server did not take the message: " + Describe(result)) => Result = result;

    /// <summary>The full result.</summary>
    public SmtpSendResult Result { get; }

    private static string Describe(SmtpSendResult r) =>
        r.SenderReply is { IsPositiveCompletion: false } s ? "sender: " + s
        : r.DataReply is { IsPositiveCompletion: false } d ? "data: " + d
        : r.Recipients.Count > 0 && r.Accepted.Any() == false ? "every recipient rejected (" + r.Recipients[0].Reply + ")"
        : "no data was sent";
}
