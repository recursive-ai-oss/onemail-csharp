using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Policy;
using RecursiveAI.OneCrypto.Wire.Cms;
using RecursiveAI.OneCrypto.X509;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.Smime;

/// <summary>Which CMS content type carries encrypted mail.</summary>
public enum SmimeEncryptionKind
{
    /// <summary>EnvelopedData (RFC 5652 §6), <c>smime-type=enveloped-data</c>, AES-256-CBC by default. Every S/MIME client reads it; the cipher has no integrity protection of its own.</summary>
    Enveloped,

    /// <summary>AuthEnvelopedData (RFC 5083), <c>smime-type=authEnveloped-data</c>, AES-256-GCM by default (RFC 8551 §2.7). Authenticated; not every client reads it yet.</summary>
    AuthEnveloped,
}

/// <summary>Choices for encrypting a message with <see cref="SmimeEncryptor"/>.</summary>
public sealed class SmimeEncryptOptions
{
    /// <summary>
    /// The recipients' certificates. At least one is required; add the sender's own certificate to keep a readable copy. An RSA certificate gets
    /// key transport (<see cref="KeyTransport"/>), an EC, X25519 or X448 certificate gets key agreement (ECDH / RFC 8418).
    /// </summary>
    public required IReadOnlyList<Certificate> Recipients { get; init; }

    /// <summary>EnvelopedData (default) or AuthEnvelopedData.</summary>
    public SmimeEncryptionKind Kind { get; init; } = SmimeEncryptionKind.Enveloped;

    /// <summary>The content-encryption algorithm OID (for example <see cref="CmsOids.Aes128Gcm"/>); null takes AES-256-CBC or AES-256-GCM by <see cref="Kind"/>.</summary>
    public string? ContentEncryption { get; init; }

    /// <summary>The key-transport scheme for RSA recipients. Default RSA-OAEP with SHA-256 (RFC 8551 §2.5.1.1 requires OAEP support; RFC 3560).</summary>
    public KeyTransportScheme KeyTransport { get; init; } = KeyTransportScheme.OaepSha256;

    /// <summary>Allow writing RSA PKCS #1 v1.5 key transport, which older clients need and which new data should avoid. Default false: choosing <see cref="KeyTransportScheme.Pkcs1V15"/> without this throws.</summary>
    public bool AllowLegacy { get; init; }

    /// <summary>The crypto policy for new data. Null means <see cref="CryptoPolicy.Standard"/>: RSA recipient keys under 2048 bits are refused; CBC content encryption is flagged but written.</summary>
    public CryptoPolicy? Policy { get; init; }
}

/// <summary>
/// Creates S/MIME encrypted messages (RFC 8551 §3.3): one <c>application/pkcs7-mime</c> part holding the CMS. All CMS work is OneCrypto's;
/// this type prepares the canonical form of the content and frames the result.
/// </summary>
public static class SmimeEncryptor
{
    /// <summary>
    /// Encrypts <paramref name="content"/> (a whole MIME entity: its headers and body) for the recipients.
    /// To sign and encrypt, pass the result of <see cref="SmimeSigner.SignDetached"/> or <see cref="SmimeSigner.SignOpaque"/> as the content.
    /// </summary>
    /// <param name="content">The entity to hide. It is not modified; see <see cref="SmimeSigner.SignDetached"/> for the canonical form.</param>
    /// <param name="options">The recipients and choices.</param>
    /// <returns>A part <c>application/pkcs7-mime; smime-type=enveloped-data</c> (or <c>authEnveloped-data</c>), named <c>smime.p7m</c>. The caller adds the outer message headers.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">There is no recipient, or a recipient certificate has a key that cannot receive encrypted mail.</exception>
    /// <exception cref="PolicyViolationException">The cipher or a recipient key is refused by the policy.</exception>
    public static MimePart Encrypt(MimeEntity content, SmimeEncryptOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Recipients.Count == 0)
        {
            throw new ArgumentException("At least one recipient is required.", nameof(options));
        }

        var recipients = new List<CmsRecipient>(options.Recipients.Count);
        foreach (Certificate c in options.Recipients)
        {
            recipients.Add(c.PublicKey.KeyAlgorithm switch
            {
                KeyAlgorithm.Rsa or KeyAlgorithm.RsaOaep => CmsRecipient.KeyTransport(c, options.KeyTransport),
                KeyAlgorithm.EllipticCurve or KeyAlgorithm.X25519 or KeyAlgorithm.X448 => CmsRecipient.KeyAgreement(c),
                _ => throw new ArgumentException("A recipient certificate has a " + c.PublicKey.Algorithm.Name + " key, which cannot receive S/MIME mail.", nameof(options)),
            });
        }

        byte[] plaintext = SmimeContent.Canonicalise(content);
        var cms = new CmsEncryptOptions
        {
            ContentType = CmsOids.Data,
            ContentEncryption = options.ContentEncryption,
            AllowLegacy = options.AllowLegacy,
            Policy = options.Policy,
        };
        if (options.Kind == SmimeEncryptionKind.AuthEnveloped)
        {
            AuthEnvelopedData a = AuthEnvelopedData.Create(plaintext, recipients, cms);
            return SmimeContent.CmsPart(a.Encoded.Span, "application/pkcs7-mime; smime-type=authEnveloped-data", "smime.p7m", null);
        }

        EnvelopedData e = EnvelopedData.Create(plaintext, recipients, cms);
        return SmimeContent.CmsPart(e.Encoded.Span, "application/pkcs7-mime; smime-type=enveloped-data", "smime.p7m", null);
    }
}
