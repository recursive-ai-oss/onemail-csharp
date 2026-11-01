using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;

namespace RecursiveAI.OneMail.Client.Smtp;

/// <summary>How the connection to the submission server is protected.</summary>
public enum SmtpSecurity
{
    /// <summary>STARTTLS (RFC 3207) is required: if the server does not offer it, connecting fails with <see cref="SmtpFeatureRequiredException"/>. Port 587 by default. This is the default.</summary>
    StartTls = 0,

    /// <summary>TLS from the first byte (RFC 8314), usually port 465.</summary>
    ImplicitTls,

    /// <summary>STARTTLS when the server offers it, plain text otherwise. An attacker who strips the offer wins: use only on trusted networks.</summary>
    StartTlsIfOffered,

    /// <summary>No encryption. Port 25 by default. Authentication that sends the password in the clear is refused unless <see cref="SaslCredentials.AllowInsecure"/> is set.</summary>
    None,
}

/// <summary>Settings for <see cref="SmtpClient"/>.</summary>
public sealed class SmtpClientOptions
{
    /// <summary>The server's host name. Also the TLS certificate name unless <see cref="Connection"/> sets one.</summary>
    public required string Host { get; init; }

    /// <summary>The port; null picks 465 for <see cref="SmtpSecurity.ImplicitTls"/>, 25 for <see cref="SmtpSecurity.None"/> and 587 otherwise.</summary>
    public int? Port { get; init; }

    /// <summary>How the connection is protected. Default <see cref="SmtpSecurity.StartTls"/>.</summary>
    public SmtpSecurity Security { get; init; }

    /// <summary>What to authenticate with, or null to send without authenticating.</summary>
    public SaslCredentials? Credentials { get; init; }

    /// <summary>
    /// Replaces the mechanism choice: the client tries these instead of <see cref="SaslMechanisms.Select"/>, given the mechanism names the server
    /// offered. Return an empty list to fail. Null: the default selection.
    /// </summary>
    public Func<IReadOnlyList<string>, ProtocolConnection, IReadOnlyList<SaslMechanism>>? MechanismSelector { get; init; }

    /// <summary>The name announced in EHLO: a domain or an address literal. Default <c>localhost</c>; set your public host name for servers that check it.</summary>
    public string ClientDomain { get; init; } = "localhost";

    /// <summary>Transport settings: certificate validation, timeouts, limits, trace. Its <see cref="ProtocolConnectionOptions.ImplicitTls"/> is ignored; <see cref="Security"/> decides.</summary>
    public ProtocolConnectionOptions? Connection { get; init; }

    /// <summary>Use pipelining (RFC 2920) when the server offers it. Default true.</summary>
    public bool UsePipelining { get; init; } = true;

    /// <summary>Use BDAT (RFC 3030) instead of DATA when the server offers CHUNKING. Default true.</summary>
    public bool UseChunking { get; init; } = true;

    /// <summary>The BDAT chunk size in bytes. Default 262,144; 1,024 to 16 MiB.</summary>
    public int ChunkSize { get; init; } = 256 * 1024;

    /// <summary>
    /// Turn bare LF and bare CR in the message into CRLF while sending (SMTP forbids both). Default true. A message that already uses CRLF
    /// is sent byte for byte; set false to send the bytes exactly as they are.
    /// </summary>
    public bool NormalizeLineEndings { get; init; } = true;
}
