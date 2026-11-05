using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Issuance;
using RecursiveAI.OneCrypto.Keys;
using RecursiveAI.OneCrypto.X509;
using RecursiveAI.OneMail.Cryptography.Smime;
using MimeEntity = RecursiveAI.OneMail.Mime.MimeEntity;
using MimePart = RecursiveAI.OneMail.Mime.MimePart;
using OneMime = RecursiveAI.OneMail.Mime;

namespace OneMail.Tools.SmimeInterop;

// Writes the S/MIME vectors that tools/thunderbird opens: for each key type one directory with the CA certificate, the user's
// certificate and PKCS #12 file (password "pw"), the expected text, and the messages this library signs and encrypts to that
// user's own address (the way the OpenPGP vectors work, so Thunderbird's identity matches). Usage: SmimeInterop thunderbird <out dir>
internal static partial class Program
{
    /// <summary>The text every vector carries; Thunderbird's own messages carry the same.</summary>
    internal const string TbBody = "S/MIME interop vector.\r\ncaf\u00e9 line two, \u00fcber line three.\r\n";

    private static int ThunderbirdVectors(string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-7);
        using var caKey = RSA.Create(3072);
        CertificateAuthority ca = CertificateAuthority.CreateRoot(DistinguishedName.FromString("CN=Vector Test Root"), X509Signer.FromRsa(caKey, HashAlgorithmName.SHA256), notBefore, TimeSpan.FromDays(3650));
        File.WriteAllText(Path.Combine(outDir, "root.pem"), ca.Certificate.ToPem());
        var keys = new (string Name, Func<(Certificate Cert, ISigningKey Key, CmsDecryptionKey Dec, X509Certificate2 Pfx)> Make)[]
        {
            ("rsa2048", () => RsaIdentity(ca, "rsa2048", 2048, notBefore)),
            ("rsa3072", () => RsaIdentity(ca, "rsa3072", 3072, notBefore)),
            ("ecdsa-p256", () => EcIdentity(ca, "ecdsa-p256", notBefore)),
        };
        foreach ((string name, var make) in keys)
        {
            string dir = Path.Combine(outDir, "ours", name);
            Directory.CreateDirectory(dir);
            string email = "vector-" + name + "@example.org";
            (Certificate cert, ISigningKey key, CmsDecryptionKey dec, X509Certificate2 pfx) = make();
            File.WriteAllText(Path.Combine(dir, "cert.pem"), cert.ToPem());
            File.WriteAllText(Path.Combine(dir, "root.pem"), ca.Certificate.ToPem());
            File.WriteAllBytes(Path.Combine(dir, "key.p12"), pfx.Export(X509ContentType.Pkcs12, "pw"));
            File.WriteAllText(Path.Combine(dir, "expected.txt"), TbBody, new UTF8Encoding(false));
            var so = new SmimeSignOptions { Signers = [new CmsSignerSpec { Certificate = cert, Key = key }], Certificates = [ca.Certificate], Boundary = "vector-boundary" };
            var eo = new SmimeEncryptOptions { Recipients = [cert] };
            TbWrite(dir, "signed", SmimeSigner.SignDetached(TbContent(), so), email);
            TbWrite(dir, "signed-opaque", SmimeSigner.SignOpaque(TbContent(), so), email);
            TbWrite(dir, "encrypted", SmimeEncryptor.Encrypt(TbContent(), eo), email);
            // Variants for clients that take only some key transport schemes (Outlook's reading of them is checked in tools/outlook).
            TbWrite(dir, "encrypted-oaep-sha1", SmimeEncryptor.Encrypt(TbContent(), new SmimeEncryptOptions { Recipients = [cert], KeyTransport = KeyTransportScheme.OaepSha1, AllowLegacy = true }), email);
            TbWrite(dir, "encrypted-pkcs1", SmimeEncryptor.Encrypt(TbContent(), new SmimeEncryptOptions { Recipients = [cert], KeyTransport = KeyTransportScheme.Pkcs1V15, AllowLegacy = true }), email);
            TbWrite(dir, "signed-encrypted", SmimeEncryptor.Encrypt(SmimeSigner.SignDetached(TbContent(), so), eo), email);
            // A signed part beside an unsigned one inside a wrapper: only the signed part is signed.
            var wrapper = new OneMime.MimeMultipart("mixed");
            wrapper.Add(SmimeSigner.SignDetached(TbContent(), so));
            var extra = new MimePart("text/plain; charset=utf-8");
            extra.SetDecodedContent(Encoding.UTF8.GetBytes("unsigned addition\r\n"), OneMime.Transfer.ContentTransferEncoding.SevenBit);
            wrapper.Add(extra);
            TbWrite(dir, "signed-in-wrapper", wrapper, email);
            Console.WriteLine("wrote " + name);
        }

        return 0;
    }

    private static MimePart TbContent()
    {
        var p = new MimePart("text/plain; charset=utf-8");
        p.SetDecodedContent(Encoding.UTF8.GetBytes(TbBody), OneMime.Transfer.ContentTransferEncoding.QuotedPrintable);
        return p;
    }

    private static void TbWrite(string dir, string kind, MimeEntity e, string email)
    {
        e.Headers.Add("MIME-Version", "1.0");
        e.Headers.Add("From", "Vector <" + email + ">");
        e.Headers.Add("To", "Vector <" + email + ">");
        e.Headers.Add("Subject", "Interop vector");
        e.Headers.Add("Date", "Mon, 02 Nov 2026 10:00:00 +0000");
        e.Headers.Add("Message-ID", "<" + kind + "@vector.example.org>");
        File.WriteAllBytes(Path.Combine(dir, kind + ".eml"), e.ToArray());
    }

    private static (Certificate, ISigningKey, CmsDecryptionKey, X509Certificate2) RsaIdentity(CertificateAuthority ca, string n, int bits, DateTimeOffset nb)
    {
        string email = "vector-" + n + "@example.org";
        var rsa = RSA.Create(bits);
        Certificate c = ca.Issue(PublicKeyInfo.FromAsymmetricAlgorithm(rsa), DistinguishedName.FromString("CN=Vector " + n + ",E=" + email), Opts(email, nb, CertificateProfile.SMime));
        return (c, PrivateKey.FromRsa(rsa), CmsDecryptionKey.FromRsa(rsa, c), c.ToX509Certificate2().CopyWithPrivateKey(rsa));
    }

    private static (Certificate, ISigningKey, CmsDecryptionKey, X509Certificate2) EcIdentity(CertificateAuthority ca, string n, DateTimeOffset nb)
    {
        string email = "vector-" + n + "@example.org";
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Certificate c = ca.Issue(PublicKeyInfo.FromAsymmetricAlgorithm(ec), DistinguishedName.FromString("CN=Vector " + n + ",E=" + email), Opts(email, nb, EcProfile));
        ECDiffieHellman dh = ECDiffieHellman.Create(ec.ExportParameters(true));
        return (c, PrivateKey.FromECDsa(ec), CmsDecryptionKey.FromECDiffieHellman(dh, c), c.ToX509Certificate2().CopyWithPrivateKey(ec));
    }
}
