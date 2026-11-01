using System.Net;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Sasl;

/// <summary>
/// What the caller can authenticate with. <see cref="SaslMechanisms.Select"/> turns it and the server's offer into mechanisms to try,
/// strongest first. Fill in only what you have: a password, an OAuth access token (obtained by the caller), or a platform credential
/// for Kerberos/SPNEGO.
/// </summary>
public sealed class SaslCredentials
{
    /// <summary>The account name (authentication identity). Needed for everything except platform credentials.</summary>
    public string? UserName { get; init; }

    /// <summary>The password. Enables SCRAM, NTLM, CRAM-MD5, PLAIN and LOGIN.</summary>
    public string? Password { get; init; }

    /// <summary>An OAuth 2.0 access token. Enables OAUTHBEARER and XOAUTH2. The library never fetches or refreshes tokens.</summary>
    public string? AccessToken { get; init; }

    /// <summary>The identity to act as when it differs from <see cref="UserName"/> (PLAIN, SCRAM, GSSAPI), or null.</summary>
    public string? AuthorizationId { get; init; }

    /// <summary>The NTLM domain, or null (a <c>DOMAIN\user</c> user name also works).</summary>
    public string? Domain { get; init; }

    /// <summary>The workstation name NTLM reports, or null.</summary>
    public string? Workstation { get; init; }

    /// <summary>
    /// Enables GSSAPI and GSS-SPNEGO with the current logon or ticket cache (or <see cref="PlatformCredential"/>). The service principal
    /// is <c>service/host</c> with the service from the protocol (<c>smtp</c>, <c>imap</c>, <c>pop</c>).
    /// </summary>
    public bool UsePlatformCredentials { get; init; }

    /// <summary>An explicit Kerberos/SPNEGO credential; null with <see cref="UsePlatformCredentials"/> means the logon identity.</summary>
    public NetworkCredential? PlatformCredential { get; init; }

    /// <summary>
    /// Allows the mechanisms that send the password in the clear (PLAIN, LOGIN) on a connection without TLS. Default false: they are
    /// skipped, so a server that offers nothing better makes authentication fail instead of leaking the password.
    /// </summary>
    public bool AllowInsecure { get; init; }

    /// <summary>Also try the legacy mechanisms (CRAM-MD5, LOGIN, NTLM). Default true; they come after every non-legacy choice.</summary>
    public bool AllowLegacy { get; init; } = true;
}

/// <summary>Picks SASL mechanisms from what a server offers and what the caller holds.</summary>
public static class SaslMechanisms
{
    /// <summary>
    /// Builds the mechanisms to try, strongest first: SCRAM-SHA-256-PLUS, SCRAM-SHA-1-PLUS, OAUTHBEARER, XOAUTH2, SCRAM-SHA-256,
    /// SCRAM-SHA-1, GSSAPI, GSS-SPNEGO, NTLM, CRAM-MD5, PLAIN, LOGIN. A mechanism is included only when the server offered it and the
    /// caller's credentials suffice; PLAIN and LOGIN (and anything else that needs encryption) only on an encrypted connection
    /// unless <see cref="SaslCredentials.AllowInsecure"/> is set.
    /// </summary>
    /// <param name="offered">The mechanism names the server announced.</param>
    /// <param name="credentials">What the caller holds.</param>
    /// <param name="connection">The connection, for TLS state and channel binding.</param>
    /// <param name="service">The service name for Kerberos principals: <c>smtp</c>, <c>imap</c> or <c>pop</c>.</param>
    /// <param name="host">The server's host name.</param>
    /// <param name="port">The server's port (sent by OAUTHBEARER).</param>
    /// <returns>The mechanisms in the order to try; empty if none fits.</returns>
    public static IReadOnlyList<SaslMechanism> Select(IEnumerable<string> offered, SaslCredentials credentials, ProtocolConnection connection, string service, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(connection);
        var names = new HashSet<string>(offered.Select(o => o.ToUpperInvariant()), StringComparer.Ordinal);
        bool secure = connection.IsEncrypted || credentials.AllowInsecure;
        string? user = credentials.UserName;
        string? pass = credentials.Password;
        SaslChannelBinding? binding = connection.IsEncrypted ? SaslChannelBinding.From(connection) : null;
        var result = new List<SaslMechanism>();

        void Add(string name, Func<SaslMechanism> make)
        {
            if (names.Contains(name))
            {
                SaslMechanism m = make();
                if (!m.RequiresEncryption || secure)
                {
                    result.Add(m);
                }
            }
        }

        bool password = user is { Length: > 0 } && pass is not null;
        if (password)
        {
            if (binding is not null)
            {
                Add("SCRAM-SHA-256-PLUS", () => new SaslScram(ScramHash.Sha256, user!, pass!, credentials.AuthorizationId, binding, plus: true));
                Add("SCRAM-SHA-1-PLUS", () => new SaslScram(ScramHash.Sha1, user!, pass!, credentials.AuthorizationId, binding, plus: true));
            }
        }

        if (user is { Length: > 0 } && credentials.AccessToken is { Length: > 0 } token)
        {
            Add("OAUTHBEARER", () => new SaslOAuthBearer(user, token, host, port));
            Add("XOAUTH2", () => new SaslXOAuth2(user, token));
        }

        if (password)
        {
            // With binding available but the server not offering -PLUS, the plain form announces "y" so the server can detect a stripped offer.
            Add("SCRAM-SHA-256", () => new SaslScram(ScramHash.Sha256, user!, pass!, credentials.AuthorizationId, binding));
            Add("SCRAM-SHA-1", () => new SaslScram(ScramHash.Sha1, user!, pass!, credentials.AuthorizationId, binding));
        }

        if (credentials.UsePlatformCredentials)
        {
            string spn = service + "/" + host;
            Add("GSSAPI", () => SaslNegotiate.Gssapi(spn, credentials.PlatformCredential, credentials.AuthorizationId));
            Add("GSS-SPNEGO", () => SaslNegotiate.Spnego(spn, credentials.PlatformCredential));
        }

        if (password && credentials.AllowLegacy)
        {
            Add("NTLM", () => new SaslNtlm(user!, pass!, credentials.Domain, credentials.Workstation));
            Add("CRAM-MD5", () => new SaslCramMd5(user!, pass!));
        }

        if (password)
        {
            Add("PLAIN", () => new SaslPlain(user!, pass!, credentials.AuthorizationId));
            if (credentials.AllowLegacy)
            {
                Add("LOGIN", () => new SaslLogin(user!, pass!));
            }
        }

        return result;
    }
}
