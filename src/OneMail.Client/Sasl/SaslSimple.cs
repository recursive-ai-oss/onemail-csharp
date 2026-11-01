using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneMail.Client.Sasl;

/// <summary>
/// LOGIN, the de-facto mechanism Outlook-era servers still offer: the server asks for the user name and then the password, each in
/// its own round trip. It is not an RFC, sends the password in the clear and needs an encrypted connection.
/// </summary>
public sealed class SaslLogin : SaslMechanism
{
    private readonly byte[] _user;
    private readonly byte[] _password;
    private int _step;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="userName">The user name.</param>
    /// <param name="password">The password.</param>
    /// <exception cref="ArgumentException">The user name is empty.</exception>
    public SaslLogin(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        _user = Encoding.UTF8.GetBytes(userName);
        _password = Encoding.UTF8.GetBytes(password);
    }

    /// <inheritdoc />
    public override string Name => "LOGIN";

    /// <inheritdoc />
    public override bool RequiresEncryption => true;

    /// <inheritdoc />
    public override bool IsLegacy => true;

    // The server speaks first ("Username:"); the prompts are not standardised, so the answers follow the order, not the text.
    /// <inheritdoc />
    public override bool ClientFirst => false;

    /// <inheritdoc />
    public override byte[]? GetInitialResponse() => null;

    /// <inheritdoc />
    public override byte[] Respond(ReadOnlySpan<byte> challenge) => _step++ == 0 ? _user : _password;

    /// <inheritdoc />
    public override void Reset() => _step = 0;
}

/// <summary>
/// CRAM-MD5 (RFC 2195): the user name and an HMAC-MD5 of the server's challenge keyed with the password. Kept for old servers; it
/// lets anyone who records the exchange guess the password offline, so <see cref="SaslMechanism.IsLegacy"/> is true. It does not
/// need an encrypted connection.
/// </summary>
public sealed class SaslCramMd5 : SaslMechanism
{
    private readonly string _user;
    private readonly byte[] _password;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="userName">The user name.</param>
    /// <param name="password">The password.</param>
    /// <exception cref="ArgumentException">The user name is empty.</exception>
    public SaslCramMd5(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        _user = userName;
        _password = Encoding.UTF8.GetBytes(password);
    }

    /// <inheritdoc />
    public override string Name => "CRAM-MD5";

    /// <inheritdoc />
    public override bool IsLegacy => true;

    /// <inheritdoc />
    public override bool ClientFirst => false;

    /// <inheritdoc />
    public override byte[]? GetInitialResponse() => null;

    /// <inheritdoc />
    public override byte[] Respond(ReadOnlySpan<byte> challenge)
    {
        // RFC 2195 §2: the digest is lower-case hex of HMAC-MD5(password, challenge), after "user SPACE".
        byte[] mac = ProtocolHashes.HmacMd5(_password, challenge);
        var sb = new StringBuilder(_user).Append(' ');
        foreach (byte b in mac)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>
/// XOAUTH2, the bearer-token mechanism Google and Microsoft accept. The caller obtains the access token; this only formats it.
/// </summary>
public sealed class SaslXOAuth2 : SaslMechanism
{
    private readonly byte[] _message;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="userName">The account (usually the e-mail address).</param>
    /// <param name="accessToken">The OAuth 2.0 access token.</param>
    /// <exception cref="ArgumentException">A value is empty or contains a control character.</exception>
    public SaslXOAuth2(string userName, string accessToken)
    {
        SaslOAuthBearer.CheckToken(userName, accessToken);
        _message = Encoding.UTF8.GetBytes("user=" + userName + "\u0001auth=Bearer " + accessToken + "\u0001\u0001");
    }

    /// <inheritdoc />
    public override string Name => "XOAUTH2";

    /// <inheritdoc />
    public override bool RequiresEncryption => true;

    /// <inheritdoc />
    public override byte[]? GetInitialResponse() => _message;

    // On failure the server sends a JSON error as a challenge; an empty answer lets it finish with the final error reply.
    /// <inheritdoc />
    public override byte[] Respond(ReadOnlySpan<byte> challenge) => [];
}

/// <summary>OAUTHBEARER (RFC 7628): a bearer token in a GS2-style message. The caller obtains the access token.</summary>
public sealed class SaslOAuthBearer : SaslMechanism
{
    private readonly byte[] _message;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="userName">The authorization identity (the account).</param>
    /// <param name="accessToken">The OAuth 2.0 bearer token.</param>
    /// <param name="host">The server's host name, sent as <c>host=</c> (optional).</param>
    /// <param name="port">The server's port, sent as <c>port=</c> (optional).</param>
    /// <exception cref="ArgumentException">A value is empty or contains a control character.</exception>
    public SaslOAuthBearer(string userName, string accessToken, string? host = null, int? port = null)
    {
        CheckToken(userName, accessToken);
        // RFC 7628 §3.1: gs2-header = "n,a=" authzid ",", where "=" and "," in the authzid become "=3D" and "=2C".
        var sb = new StringBuilder("n,a=").Append(userName.Replace("=", "=3D", StringComparison.Ordinal).Replace(",", "=2C", StringComparison.Ordinal)).Append(',').Append('\u0001');
        if (host is not null) { sb.Append("host=").Append(host).Append('\u0001'); }
        if (port is { } p) { sb.Append("port=").Append(p.ToString(CultureInfo.InvariantCulture)).Append('\u0001'); }
        sb.Append("auth=Bearer ").Append(accessToken).Append('\u0001').Append('\u0001');
        _message = Encoding.UTF8.GetBytes(sb.ToString());
    }

    internal static void CheckToken(string userName, string accessToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        if (userName.Any(char.IsControl) || accessToken.Any(char.IsControl))
        {
            throw new ArgumentException("The user name and the token must not contain control characters.");
        }
    }

    /// <inheritdoc />
    public override string Name => "OAUTHBEARER";

    /// <inheritdoc />
    public override bool RequiresEncryption => true;

    /// <inheritdoc />
    public override byte[]? GetInitialResponse() => _message;

    // RFC 7628 §3.2.3: after a JSON error challenge the client sends a single 0x01 and the server then fails the exchange.
    /// <inheritdoc />
    public override byte[] Respond(ReadOnlySpan<byte> challenge) => [0x01];
}
