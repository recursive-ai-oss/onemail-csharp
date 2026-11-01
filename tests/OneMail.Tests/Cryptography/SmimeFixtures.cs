using System.Security.Cryptography;
using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Issuance;
using RecursiveAI.OneCrypto.Keys;
using RecursiveAI.OneCrypto.Primitives;
using RecursiveAI.OneCrypto.X509;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>Generated certificates and keys for the S/MIME tests: one CA and a handful of identities. Nothing here is a real person's key.</summary>
internal sealed class SmimeIdentity
{
    private static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<CertificateAuthority> Authority = new(() =>
    {
        var rsa = RSA.Create(3072);
        return CertificateAuthority.CreateRoot(DistinguishedName.FromString("CN=Test Root,O=Example"), X509Signer.FromRsa(rsa, HashAlgorithmName.SHA256), NotBefore, TimeSpan.FromDays(3650),
            // Permissive so the tests can issue a 1024-bit certificate for the weak-key cases.
            new CertificateAuthorityOptions { Policy = RecursiveAI.OneCrypto.Policy.CryptoPolicy.Permissive });
    });

    public static Certificate Root => Authority.Value.Certificate;

    public required Certificate Certificate { get; init; }

    public required ISigningKey SigningKey { get; init; }

    public required Func<CmsDecryptionKey>? DecryptionKey { get; init; }

    public CmsDecryptionKey Decrypt() => DecryptionKey!();

    private static readonly CertificateProfile EcProfile = new()
    {
        Name = "smime-ec",
        KeyUsages = KeyUsages.DigitalSignature | KeyUsages.NonRepudiation | KeyUsages.KeyAgreement,
        ExtendedKeyUsages = [KnownOids.EmailProtection],
        SubjectAlternativeNameRequired = true,
        AllowedSubjectAlternativeNames = [GeneralNameKind.Rfc822Name],
        MaxValidity = TimeSpan.FromDays(825),
    };

    private static Certificate Issue(PublicKeyInfo key, string email, CertificateProfile profile) =>
        Authority.Value.Issue(key, DistinguishedName.FromString("CN=" + email + ",E=" + email), new CertificateIssueOptions
        {
            Profile = profile,
            NotBefore = NotBefore,
            SubjectAlternativeNames = [GeneralName.Email(email)],
        });

    public static SmimeIdentity Rsa(string email, int bits = 2048)
    {
        var rsa = RSA.Create(bits);
        Certificate cert = Issue(PublicKeyInfo.FromAsymmetricAlgorithm(rsa), email, CertificateProfile.SMime);
        return new SmimeIdentity { Certificate = cert, SigningKey = PrivateKey.FromRsa(rsa), DecryptionKey = () => CmsDecryptionKey.FromRsa(rsa, cert) };
    }

    public static SmimeIdentity Ec(string email)
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Certificate cert = Issue(PublicKeyInfo.FromAsymmetricAlgorithm(ec), email, EcProfile);
        // The same curve key agrees keys as ECDH.
        ECDiffieHellman dh = ECDiffieHellman.Create(ec.ExportParameters(true));
        return new SmimeIdentity { Certificate = cert, SigningKey = PrivateKey.FromECDsa(ec), DecryptionKey = () => CmsDecryptionKey.FromECDiffieHellman(dh, cert) };
    }

    public static SmimeIdentity Ed25519(string email)
    {
        var key = Ed25519PrivateKey.Generate();
        ISigningKey signing = PrivateKey.FromEd25519(key);
        Certificate cert = Issue(signing.PublicKey, email, new CertificateProfile
        {
            Name = "smime-ed",
            KeyUsages = KeyUsages.DigitalSignature,
            ExtendedKeyUsages = [KnownOids.EmailProtection],
            SubjectAlternativeNameRequired = true,
            AllowedSubjectAlternativeNames = [GeneralNameKind.Rfc822Name],
            MaxValidity = TimeSpan.FromDays(825),
        });
        return new SmimeIdentity { Certificate = cert, SigningKey = signing, DecryptionKey = null };
    }

    public CmsSignerSpec Signer(SignatureSpec? spec = null, string? digest = null) =>
        new() { Certificate = Certificate, Key = SigningKey, SignatureSpec = spec, DigestAlgorithm = digest };

    /// <summary>A simple text message with the given sender.</summary>
    public static MimePart TextBody(string text = "Hello,\r\nthis is the signed text.\r\n")
    {
        var p = new MimePart("text/plain; charset=utf-8");
        p.SetDecodedContent(System.Text.Encoding.UTF8.GetBytes(text), RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.SevenBit);
        return p;
    }
}
