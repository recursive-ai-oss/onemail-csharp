using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneMail.Client.Sasl;

/// <summary>Channel-binding data for the -PLUS mechanisms (RFC 5056): the binding type and the bytes the TLS session yields for it.</summary>
/// <param name="Type">The binding type name, <c>tls-server-end-point</c> (RFC 5929 §4) or <c>tls-exporter</c> (RFC 9266).</param>
/// <param name="Data">The channel-binding data.</param>
public sealed record SaslChannelBinding(string Type, byte[] Data)
{
    /// <summary>
    /// Derives <c>tls-server-end-point</c> from the server certificate: a hash of the certificate with the hash function of its signature,
    /// where MD5 and SHA-1 are replaced by SHA-256 (RFC 5929 §4.1).
    /// </summary>
    /// <param name="certificate">The certificate the server presented.</param>
    /// <returns>The binding.</returns>
    public static SaslChannelBinding ServerEndPoint(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        string oid = certificate.SignatureAlgorithm.Value ?? string.Empty;
        // Signature algorithm OIDs: sha384/sha512 with RSA, ECDSA (PKCS#1 1.2.840.113549.1.1.12/13, X9.62 1.2.840.10045.4.3.3/4).
        HashAlgorithmName hash = oid switch
        {
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };
        return new SaslChannelBinding("tls-server-end-point", certificate.GetCertHash(hash));
    }

    /// <summary>Derives <c>tls-server-end-point</c> from the certificate of an open connection, or null on a connection without TLS.</summary>
    /// <param name="connection">The connection.</param>
    /// <returns>The binding, or null.</returns>
    public static SaslChannelBinding? From(Protocol.ProtocolConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.RemoteCertificate is { } c
            ? ServerEndPoint(c as System.Security.Cryptography.X509Certificates.X509Certificate2 ?? new System.Security.Cryptography.X509Certificates.X509Certificate2(c))
            : null;
    }
}

/// <summary>Which hash a SCRAM mechanism uses.</summary>
public enum ScramHash
{
    /// <summary>SCRAM-SHA-1 (RFC 5802).</summary>
    Sha1,

    /// <summary>SCRAM-SHA-256 (RFC 7677).</summary>
    Sha256,
}

/// <summary>
/// SCRAM-SHA-1 and SCRAM-SHA-256, with or without the -PLUS channel binding (RFC 5802, RFC 7677). The client checks the server's
/// proof, so a server that does not know the password is detected. The exchange never sends the password or anything that
/// replays; it needs no encrypted connection, but binding protects it against a man in the middle.
/// </summary>
/// <remarks>
/// The password goes through SASLprep (RFC 4013) in the form the mail servers apply: Unicode NFKC, non-ASCII spaces to U+0020,
/// the characters mapped to nothing removed, and passwords with control characters refused. The full stringprep table check (unassigned
/// code points, bidirectional rules) is not done: such a password is sent as typed and the server decides.
/// The server's iteration count is limited to <see cref="MaxIterations"/>.
/// </remarks>
public sealed class SaslScram : SaslMechanism
{
    /// <summary>The largest iteration count the client accepts from a server (a hostile server could otherwise keep it busy). Default 1,048,576.</summary>
    public const int MaxIterations = 1 << 20;

    private readonly ScramHash _hash;
    private readonly string _user;
    private readonly string _password;
    private readonly string? _authzid;
    private readonly SaslChannelBinding? _binding;
    private readonly bool _plus;
    private readonly string _nonce;

    private string _gs2Header = "";
    private string _clientFirstBare = "";
    private string _serverFirst = "";
    private byte[]? _expectedServerSignature;
    private int _step;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="hash">SHA-1 or SHA-256.</param>
    /// <param name="userName">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="authorizationId">The identity to act as, or null.</param>
    /// <param name="binding">The channel-binding data of the connection, when it has TLS. With <paramref name="plus"/> false it is only used to tell the server we support binding (<c>y</c> flag), which makes a downgrade by an attacker fail.</param>
    /// <param name="plus">Run the -PLUS form. Needs <paramref name="binding"/>.</param>
    /// <param name="nonce">A fixed client nonce, for test vectors only. Null: 24 random bytes, base64.</param>
    /// <exception cref="ArgumentException">The user name is empty, or <paramref name="plus"/> without a binding.</exception>
    public SaslScram(ScramHash hash, string userName, string password, string? authorizationId = null, SaslChannelBinding? binding = null, bool plus = false, string? nonce = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        if (plus && binding is null)
        {
            throw new ArgumentException("A -PLUS mechanism needs channel-binding data.", nameof(binding));
        }

        _hash = hash;
        _user = userName;
        _password = password;
        _authzid = authorizationId;
        _binding = binding;
        _plus = plus;
        _nonce = nonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    }

    /// <inheritdoc />
    public override string Name => (_hash == ScramHash.Sha1 ? "SCRAM-SHA-1" : "SCRAM-SHA-256") + (_plus ? "-PLUS" : "");

    // RFC 5802 §5.1: the first message is client-first. Some servers want it empty-challenged; the initial response form is valid for all.
    /// <inheritdoc />
    public override byte[]? GetInitialResponse()
    {
        // RFC 5802 §7: gs2-cbind-flag is "p=type" when binding is required, "y" when we support it but the server did not offer it, else "n".
        string flag = _plus ? "p=" + _binding!.Type : _binding is not null ? "y" : "n";
        _gs2Header = flag + "," + (_authzid is null ? "" : "a=" + Escape(_authzid)) + ",";
        _clientFirstBare = "n=" + Escape(Prep(_user)) + ",r=" + _nonce;
        _step = 1;
        return Encoding.UTF8.GetBytes(_gs2Header + _clientFirstBare);
    }

    /// <inheritdoc />
    /// <exception cref="SaslException">The server's message is malformed, uses a mandatory extension, repeats a nonce wrongly, asks for too many iterations or proves it does not know the password.</exception>
    public override byte[] Respond(ReadOnlySpan<byte> challenge)
    {
        string message = Encoding.UTF8.GetString(challenge);
        switch (_step)
        {
            case 1:
                _step = 2;
                return ClientFinal(message);
            case 2:
                _step = 3;
                ServerFinal(message);
                return [];
            default:
                throw new SaslException(Name + ": unexpected challenge after the server's final message.");
        }
    }

    /// <inheritdoc />
    public override void Reset() { _step = 0; _expectedServerSignature = null; }

    private byte[] ClientFinal(string serverFirst)
    {
        _serverFirst = serverFirst;
        string? r = null, s = null, i = null;
        foreach (string attr in serverFirst.Split(','))
        {
            if (attr.Length < 2 || attr[1] != '=')
            {
                throw new SaslException(Name + ": malformed server-first-message.");
            }

            switch (attr[0])
            {
                case 'r': r = attr[2..]; break;
                case 's': s = attr[2..]; break;
                case 'i': i = attr[2..]; break;
                case 'm': throw new SaslException(Name + ": the server requires an extension this client does not support.");
                case 'e': throw new SaslException(Name + ": server error: " + attr[2..]);
                default: break; // RFC 5802 §7: unknown optional attributes are ignored
            }
        }

        if (r is null || s is null || i is null)
        {
            throw new SaslException(Name + ": server-first-message lacks r, s or i.");
        }

        // RFC 5802 §5.1: the server nonce must extend ours.
        if (!r.StartsWith(_nonce, StringComparison.Ordinal) || r.Length == _nonce.Length)
        {
            throw new SaslException(Name + ": the server nonce does not extend the client nonce.");
        }

        if (!int.TryParse(i, NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) || iterations < 1 || iterations > MaxIterations)
        {
            throw new SaslException(Name + ": the iteration count is missing or outside 1.." + MaxIterations.ToString(CultureInfo.InvariantCulture) + ".");
        }

        byte[] salt;
        try { salt = Convert.FromBase64String(s); }
        catch (FormatException) { throw new SaslException(Name + ": the salt is not base64."); }

        HashAlgorithmName alg = _hash == ScramHash.Sha1 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
        int size = _hash == ScramHash.Sha1 ? 20 : 32;
        byte[] salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Prep(_password)), salt, iterations, alg, size);
        byte[] clientKey = Hmac(alg, salted, "Client Key"u8);
        byte[] storedKey = _hash == ScramHash.Sha1 ? ProtocolHashes.Sha1(clientKey) : SHA256.HashData(clientKey);

        // RFC 5802 §7: c= is the base64 of the GS2 header followed, when binding, by the channel-binding data.
        byte[] cbind = Encoding.UTF8.GetBytes(_gs2Header);
        if (_plus)
        {
            cbind = [.. cbind, .. _binding!.Data];
        }

        string withoutProof = "c=" + Convert.ToBase64String(cbind) + ",r=" + r;
        byte[] authMessage = Encoding.UTF8.GetBytes(_clientFirstBare + "," + _serverFirst + "," + withoutProof);
        byte[] clientSignature = Hmac(alg, storedKey, authMessage);
        byte[] proof = new byte[size];
        for (int k = 0; k < size; k++)
        {
            proof[k] = (byte)(clientKey[k] ^ clientSignature[k]);
        }

        byte[] serverKey = Hmac(alg, salted, "Server Key"u8);
        _expectedServerSignature = Hmac(alg, serverKey, authMessage);
        return Encoding.UTF8.GetBytes(withoutProof + ",p=" + Convert.ToBase64String(proof));
    }

    private void ServerFinal(string message)
    {
        if (message.StartsWith("e=", StringComparison.Ordinal))
        {
            throw new SaslException(Name + ": server error: " + message[2..]);
        }

        if (!message.StartsWith("v=", StringComparison.Ordinal))
        {
            throw new SaslException(Name + ": malformed server-final-message.");
        }

        byte[] given;
        try { given = Convert.FromBase64String(message[2..].Split(',')[0]); }
        catch (FormatException) { throw new SaslException(Name + ": the server signature is not base64."); }

        // The server proves it knows the password; a failed check means we may be talking to an impostor.
        if (_expectedServerSignature is null || !CryptographicOperations.FixedTimeEquals(given, _expectedServerSignature))
        {
            throw new SaslException(Name + ": the server's signature is wrong; it does not know the password.");
        }
    }

    private static byte[] Hmac(HashAlgorithmName alg, byte[] key, ReadOnlySpan<byte> data) =>
        alg == HashAlgorithmName.SHA1 ? ProtocolHashes.HmacSha1(key, data) : HMACSHA256.HashData(key, data);

    // RFC 5802 §5.1: "=" and "," in names are sent as "=3D" and "=2C".
    private static string Escape(string s) => s.Replace("=", "=3D", StringComparison.Ordinal).Replace(",", "=2C", StringComparison.Ordinal);

    // RFC 4013 profile of stringprep: map (non-ASCII space -> SPACE, soft hyphen and other "commonly mapped to nothing" -> removed), NFKC.
    private static string Prep(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '\u00AD' or '\u034F' or '\u1806' or '\u180B' or '\u180C' or '\u180D' or '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFE00' or '\uFE01' or '\uFE02' or '\uFE03' or '\uFE04' or '\uFE05' or '\uFE06' or '\uFE07' or '\uFE08' or '\uFE09' or '\uFE0A' or '\uFE0B' or '\uFE0C' or '\uFE0D' or '\uFE0E' or '\uFE0F' or '\uFEFF')
            {
                continue;
            }

            sb.Append(c is '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200B') or '\u202F' or '\u205F' or '\u3000' ? ' ' : c);
        }

        string result = sb.ToString().Normalize(NormalizationForm.FormKC);
        if (result.Any(char.IsControl))
        {
            throw new SaslException("SCRAM: the user name or password contains a control character.");
        }

        return result;
    }
}

/// <summary>A SASL exchange failed on the client side: the server's challenge was malformed or failed a check the mechanism makes.</summary>
public sealed class SaslException : Protocol.MailProtocolException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    public SaslException(string message) : base(message) { }
}
