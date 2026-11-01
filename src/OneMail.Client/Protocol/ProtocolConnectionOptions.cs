using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>How a <see cref="ProtocolConnection"/> connects, secures and bounds a session.</summary>
public sealed class ProtocolConnectionOptions
{
    /// <summary>Start TLS immediately after the TCP connect (implicit TLS: IMAPS 993, POP3S 995, SMTPS 465). Default false.</summary>
    public bool ImplicitTls { get; init; }

    /// <summary>The name sent as SNI and checked against the certificate. Default: the host passed to connect.</summary>
    public string? TlsTargetHost { get; init; }

    /// <summary>
    /// Replaces certificate validation. Default null: the platform's chain validation, with no exception for self-signed
    /// certificates. The callback gets the usual arguments of <see cref="RemoteCertificateValidationCallback"/>.
    /// </summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; init; }

    /// <summary>Client certificates offered to the server. Default none.</summary>
    public X509CertificateCollection? ClientCertificates { get; init; }

    /// <summary>ALPN protocols offered during the handshake. Default none (IMAP, SMTP and POP3 do not use ALPN, but proxies may).</summary>
    public IList<SslApplicationProtocol>? ApplicationProtocols { get; init; }

    /// <summary>TLS versions allowed. Default <see cref="SslProtocols.None"/>: the operating system's defaults.</summary>
    public SslProtocols EnabledSslProtocols { get; init; } = SslProtocols.None;

    /// <summary>Runs last on the options handed to <see cref="SslStream"/>, for anything not exposed above.</summary>
    public Action<SslClientAuthenticationOptions>? ConfigureTls { get; init; }

    /// <summary>Time allowed for the TCP connect. Default 30 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Time allowed for one read to make progress. Default 2 minutes; <see cref="Timeout.InfiniteTimeSpan"/> disables it.
    /// Can be changed on the open connection (IDLE waits much longer). The limit is checked four times per period, so a stalled read
    /// fails after between one and 1.25 times this time (no timer is armed per read).
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Time allowed for one write to make progress. Default 2 minutes; checked like <see cref="ReadTimeout"/>.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The limits the readers enforce. Default <see cref="ProtocolLimits.Default"/>.</summary>
    public ProtocolLimits Limits { get; init; } = ProtocolLimits.Default;

    /// <summary>Receives a copy of the plaintext protocol, credentials redacted. Default none.</summary>
    public IProtocolTrace? Trace { get; init; }

    /// <summary>
    /// Allows SASL mechanisms that send the secret in the clear (PLAIN, LOGIN) over a connection that is not encrypted. Default
    /// false: such an exchange throws <see cref="InvalidOperationException"/> before anything is sent.
    /// </summary>
    public bool AllowInsecureAuthentication { get; init; }
}
