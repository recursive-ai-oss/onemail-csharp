namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>The base of the failures the protocol layer raises itself (as opposed to socket and TLS errors, which pass through).</summary>
public class MailProtocolException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public MailProtocolException(string message) : base(message) { }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">The cause.</param>
    public MailProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A <see cref="ProtocolLimits"/> limit was exceeded.</summary>
public sealed class MailProtocolLimitException : MailProtocolException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which limit and its value.</param>
    public MailProtocolLimitException(string message) : base(message) { }
}

/// <summary>The peer closed the connection (or it ended) in the middle of a reply, line or literal.</summary>
public sealed class MailConnectionClosedException : MailProtocolException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was being read.</param>
    public MailConnectionClosedException(string message) : base(message) { }
}

/// <summary>
/// The peer broke a rule the caller depends on for safety: for example it sent data after the STARTTLS acknowledgement
/// but before the handshake, which would let an attacker inject plaintext into the encrypted session.
/// </summary>
public sealed class MailProtocolViolationException : MailProtocolException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What the peer did.</param>
    public MailProtocolViolationException(string message) : base(message) { }
}
