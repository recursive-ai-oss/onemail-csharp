using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Asn1;
using RecursiveAI.OneCrypto.Policy;
using RecursiveAI.OneCrypto.Wire.Cms;
using RecursiveAI.OneCrypto.X509;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.Smime;

/// <summary>Choices for signing a message with <see cref="SmimeSigner"/>.</summary>
public sealed class SmimeSignOptions
{
    /// <summary>
    /// The signers, each a certificate with a key (see <see cref="CmsSignerSpec"/>). At least one is required. The signature algorithm follows
    /// the key (RSA PKCS #1 v1.5 by default; pass a <see cref="CmsSignerSpec.SignatureSpec"/> for RSA-PSS, ECDSA or Ed25519) and the digest follows
    /// the signature algorithm unless the spec names one.
    /// </summary>
    public required IReadOnlyList<CmsSignerSpec> Signers { get; init; }

    /// <summary>Further certificates to carry (the issuing chain). The signers' own certificates are always carried. Default none.</summary>
    public IReadOnlyList<Certificate> Certificates { get; init; } = [];

    /// <summary>Add the SMIMECapabilities attribute (RFC 8551 §2.5.2: AES-256-GCM, AES-128-GCM, AES-256-CBC, AES-128-CBC) to every signer. Default true.</summary>
    public bool IncludeCapabilities { get; init; } = true;

    /// <summary>
    /// Add the SMIMEEncryptionKeyPreference attribute naming the signer's certificate (RFC 8551 §2.5.2) when that certificate can receive encrypted
    /// mail (an RSA or EC key whose key usage, when present, allows key encipherment or key agreement). Default true.
    /// </summary>
    public bool IncludeEncryptionKeyPreference { get; init; } = true;

    /// <summary>
    /// The crypto policy for new signatures. Null means <see cref="CryptoPolicy.Standard"/>: SHA-1, MD5 and RSA keys under 2048 bits are refused
    /// with <see cref="PolicyViolationException"/>.
    /// </summary>
    public CryptoPolicy? Policy { get; init; }

    /// <summary>The boundary of a <c>multipart/signed</c>; null generates a random one. Pass one for reproducible output. Ignored by opaque signing.</summary>
    public string? Boundary { get; init; }

    /// <summary>The signing time written to every signer that sets none itself. Null writes no signing time. The library never reads a clock.</summary>
    public DateTimeOffset? SigningTime { get; init; }
}

/// <summary>
/// Creates S/MIME signed messages (RFC 8551 §3.4 and §3.5): <c>multipart/signed</c> with a detached CMS signature, and opaque
/// <c>application/pkcs7-mime; smime-type=signed-data</c>. All CMS work is done by OneCrypto; this type does the MIME side: the canonical
/// form of the signed entity, the <c>micalg</c> token and the framing.
/// </summary>
public static class SmimeSigner
{
    private static readonly string[] CapabilityOids =
    [
        CmsOids.Aes256Gcm,
        CmsOids.Aes128Gcm,
        CmsOids.Aes256Cbc,
        CmsOids.Aes128Cbc,
    ];

    private const string SmimeCapabilitiesOid = "1.2.840.113549.1.9.15";
    private const string SmimeEncryptionKeyPreferenceOid = "1.2.840.113549.1.9.16.2.11";

    /// <summary>
    /// Signs <paramref name="content"/> and returns a <c>multipart/signed</c> entity: the signed entity as the first part and the detached
    /// signature (<c>application/pkcs7-signature</c>, <c>smime.p7s</c>) as the second. The caller adds the message headers (<c>From</c>, <c>To</c> …)
    /// to the returned entity; they are not covered by the signature.
    /// </summary>
    /// <param name="content">The entity to sign. It is not modified; 8-bit and binary leaves are re-encoded in a copy (RFC 8551 §3.1.1) and the line ends are made CRLF.</param>
    /// <param name="options">The signers and choices.</param>
    /// <returns>The new <c>multipart/signed</c> entity, whose first child holds exactly the bytes that were signed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><see cref="SmimeSignOptions.Signers"/> is empty.</exception>
    /// <exception cref="PolicyViolationException">A signer's algorithm or key is refused by the policy (weak digest, RSA under 2048 bits).</exception>
    public static MimeMultipart SignDetached(MimeEntity content, SmimeSignOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        byte[] signedBytes = SmimeContent.Canonicalise(content);
        SignedData sd = Create(signedBytes, options, detached: true);

        string micalg = string.Join(',', sd.Signers.Select(static s => SmimeContent.MicalgFor(s.DigestAlgorithm.Oid)).Distinct(StringComparer.Ordinal));
        string? boundary = options.Boundary;
        if (boundary is null)
        {
            // A random boundary must not occur in the signed bytes, or the part would end early.
            do
            {
                boundary = "=_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            }
            while (SmimeContent.ContainsBoundary(signedBytes, boundary));
        }
        else if (SmimeContent.ContainsBoundary(signedBytes, boundary))
        {
            throw new ArgumentException("The boundary occurs in the content to be signed.", nameof(options));
        }

        var multipart = new MimeMultipart("signed", boundary);
        multipart.Headers.Set("Content-Type",
            "multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=" + micalg + "; boundary=\"" + boundary + "\"");
        // The signed part goes in as parsed bytes, so it is written back unchanged (RFC 1847 §2.1: the signature covers the part as transmitted).
        multipart.Add(MimeEntity.Parse(signedBytes));
        multipart.Add(SmimeContent.CmsPart(sd.Encoded.Span, "application/pkcs7-signature", "smime.p7s", "S/MIME Cryptographic Signature"));
        return multipart;
    }

    /// <summary>
    /// Signs <paramref name="content"/> into one opaque <c>application/pkcs7-mime; smime-type=signed-data</c> part (<c>smime.p7m</c>): the content
    /// travels inside the CMS, so readers without S/MIME cannot see it.
    /// </summary>
    /// <param name="content">The entity to sign; see <see cref="SignDetached"/>.</param>
    /// <param name="options">The signers and choices; <see cref="SmimeSignOptions.Boundary"/> is ignored.</param>
    /// <returns>The new part.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><see cref="SmimeSignOptions.Signers"/> is empty.</exception>
    /// <exception cref="PolicyViolationException">A signer's algorithm or key is refused by the policy.</exception>
    public static MimePart SignOpaque(MimeEntity content, SmimeSignOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        byte[] signedBytes = SmimeContent.Canonicalise(content);
        SignedData sd = Create(signedBytes, options, detached: false);
        return SmimeContent.CmsPart(sd.Encoded.Span, "application/pkcs7-mime; smime-type=signed-data", "smime.p7m", null);
    }

    private static SignedData Create(byte[] content, SmimeSignOptions options, bool detached)
    {
        if (options.Signers.Count == 0)
        {
            throw new ArgumentException("At least one signer is required.", nameof(options));
        }

        var specs = new List<CmsSignerSpec>(options.Signers.Count);
        foreach (CmsSignerSpec s in options.Signers)
        {
            specs.Add(WithAttributes(s, options));
        }

        return SignedData.Create(content, specs, new SignedDataOptions
        {
            Detached = detached,
            Certificates = options.Certificates,
            Policy = options.Policy,
        });
    }

    private static CmsSignerSpec WithAttributes(CmsSignerSpec s, SmimeSignOptions options)
    {
        var extra = new List<CmsAttributeEntry>(s.AdditionalSignedAttributes);
        if (options.IncludeCapabilities && extra.All(static a => a.Oid != SmimeCapabilitiesOid))
        {
            extra.Add(CmsAttributeEntry.Create(SmimeCapabilitiesOid, Capabilities()));
        }

        if (options.IncludeEncryptionKeyPreference && extra.All(static a => a.Oid != SmimeEncryptionKeyPreferenceOid) && CanReceive(s.Certificate))
        {
            extra.Add(CmsAttributeEntry.Create(SmimeEncryptionKeyPreferenceOid, KeyPreference(s.Certificate)));
        }

        return new CmsSignerSpec
        {
            Certificate = s.Certificate,
            Signer = s.Signer,
            Key = s.Key,
            SignatureSpec = s.SignatureSpec,
            Identifier = s.Identifier,
            DigestAlgorithm = s.DigestAlgorithm,
            SignedAttributes = s.SignedAttributes,
            SigningTime = s.SigningTime ?? options.SigningTime,
            SigningCertificate = s.SigningCertificate,
            AdditionalSignedAttributes = extra,
            UnsignedAttributes = s.UnsignedAttributes,
        };
    }

    // RFC 8551 §2.5.2: SMIMECapabilities ::= SEQUENCE OF SMIMECapability, each SEQUENCE { capabilityID OID, parameters ANY OPTIONAL }.
    private static byte[] Capabilities()
    {
        var w = new Asn1Writer();
        w.PushSequence();
        foreach (string oid in CapabilityOids)
        {
            w.PushSequence();
            w.WriteObjectIdentifier(oid);
            w.Pop();
        }

        w.Pop();
        return w.Encode();
    }

    private static bool CanReceive(Certificate certificate)
    {
        if (certificate.PublicKey.KeyAlgorithm is not (KeyAlgorithm.Rsa or KeyAlgorithm.EllipticCurve or KeyAlgorithm.X25519 or KeyAlgorithm.X448))
        {
            return false;
        }

        return certificate.KeyUsage is not { } ku || (ku.Usages & (KeyUsages.KeyEncipherment | KeyUsages.KeyAgreement)) != 0;
    }

    // RFC 8551 §2.5.2: SMIMEEncryptionKeyPreference ::= CHOICE { issuerAndSerialNumber [0] IssuerAndSerialNumber, ... }.
    // The [0] is implicit, so the SEQUENCE tag of IssuerAndSerialNumber (0x30) is replaced by the constructed context tag 0xA0.
    private static byte[] KeyPreference(Certificate certificate)
    {
        byte[] ias = CmsIdentifier.ForCertificate(certificate).Encoded.ToArray();
        ias[0] = 0xA0;
        return ias;
    }
}
