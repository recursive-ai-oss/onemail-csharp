using System.Net;
using System.Net.Security;
using System.Text;

namespace RecursiveAI.OneMail.Client.Sasl;

/// <summary>
/// Kerberos and SPNEGO through the platform's <see cref="NegotiateAuthentication"/>: <c>GSSAPI</c> (RFC 4752) and
/// <c>GSS-SPNEGO</c>. The operating system's security provider does the cryptography and holds the credentials; with no explicit
/// credential the current logon or ticket cache is used. No security layer is negotiated (the TLS connection protects the session).
/// </summary>
/// <remarks>Needs a platform Kerberos stack (SSPI on Windows, GSS-API libraries elsewhere). On a machine without one, the first challenge raises <see cref="SaslException"/>.</remarks>
public sealed class SaslNegotiate : SaslMechanism
{
    private readonly string _name;
    private readonly bool _gssapi;
    private readonly string _servicePrincipal;
    private readonly NetworkCredential? _credential;
    private readonly string? _authorizationId;
    private NegotiateAuthentication? _auth;
    private bool _finishedGss;

    private SaslNegotiate(string name, bool gssapi, string servicePrincipal, NetworkCredential? credential, string? authorizationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(servicePrincipal);
        _name = name;
        _gssapi = gssapi;
        _servicePrincipal = servicePrincipal;
        _credential = credential;
        _authorizationId = authorizationId;
    }

    /// <summary>Creates the GSSAPI mechanism (RFC 4752, Kerberos v5).</summary>
    /// <param name="servicePrincipal">The server's service principal, for example <c>smtp/mail.example.com</c> (IMAP: <c>imap/…</c>).</param>
    /// <param name="credential">The credential, or null for the current logon / ticket cache.</param>
    /// <param name="authorizationId">The identity to act as, or null.</param>
    /// <returns>The mechanism.</returns>
    public static SaslNegotiate Gssapi(string servicePrincipal, NetworkCredential? credential = null, string? authorizationId = null) =>
        new("GSSAPI", true, servicePrincipal, credential, authorizationId);

    /// <summary>Creates the SPNEGO mechanism (<c>GSS-SPNEGO</c>): Kerberos with an NTLM fallback chosen by the provider.</summary>
    /// <param name="servicePrincipal">The server's service principal.</param>
    /// <param name="credential">The credential, or null for the current logon.</param>
    /// <returns>The mechanism.</returns>
    public static SaslNegotiate Spnego(string servicePrincipal, NetworkCredential? credential = null) =>
        new("GSS-SPNEGO", false, servicePrincipal, credential, null);

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    public override byte[]? GetInitialResponse() => Step([]);

    /// <inheritdoc />
    public override byte[] Respond(ReadOnlySpan<byte> challenge) => Step(challenge);

    /// <inheritdoc />
    public override void Reset()
    {
        _auth?.Dispose();
        _auth = null;
        _finishedGss = false;
    }

    private byte[] Step(ReadOnlySpan<byte> challenge)
    {
        _auth ??= new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = _gssapi ? "Kerberos" : "Negotiate",
            Credential = _credential ?? CredentialCache.DefaultNetworkCredentials,
            TargetName = _servicePrincipal,
            RequiredProtectionLevel = ProtectionLevel.None,
        });

        if (!_auth.IsAuthenticated)
        {
            byte[]? blob = _auth.GetOutgoingBlob(challenge, out NegotiateAuthenticationStatusCode status);
            if (status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
            {
                throw new SaslException(_name + ": the security provider refused the exchange (" + status + ").");
            }

            return blob ?? [];
        }

        if (!_gssapi || _finishedGss)
        {
            return [];
        }

        // RFC 4752 §3.1: the server sends a wrapped octet: the security layers it offers (bitmask, 1 = none) and its maximum buffer.
        // We ask for no layer (0x01), buffer size zero, and append the authorization identity, wrapped without encryption.
        var unwrapped = new System.Buffers.ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode u = _auth.Unwrap(challenge, unwrapped, out _);
        if (u != NegotiateAuthenticationStatusCode.Completed || unwrapped.WrittenCount < 4 || (unwrapped.WrittenSpan[0] & 1) == 0)
        {
            throw new SaslException("GSSAPI: the server does not offer the no-security-layer option, or its message could not be unwrapped.");
        }

        byte[] reply = [1, 0, 0, 0, .. Encoding.UTF8.GetBytes(_authorizationId ?? "")];
        var wrapped = new System.Buffers.ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode w = _auth.Wrap(reply, wrapped, requestEncryption: false, out _);
        if (w != NegotiateAuthenticationStatusCode.Completed)
        {
            throw new SaslException("GSSAPI: wrapping the final message failed (" + w + ").");
        }

        _finishedGss = true;
        return wrapped.WrittenSpan.ToArray();
    }
}
