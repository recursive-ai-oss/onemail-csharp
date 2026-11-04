using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Validation;
using RecursiveAI.OneMail.Cryptography.Smime;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>The S/MIME status block on a display: signer, chain, encryption, what lies outside the signature.</summary>
public class SmimeDisplayTests
{
    private static readonly SmimeIdentity Alice = SmimeIdentity.Rsa("alice@example.test");
    private static readonly SmimeIdentity Bob = SmimeIdentity.Rsa("bob@example.test");

    private static MimeEntity Wire(MimeEntity e, string from = "alice@example.test")
    {
        e.Headers.Add("MIME-Version", "1.0");
        e.Headers.Add("From", from);
        e.Headers.Add("To", "bob@example.test");
        e.Headers.Add("Subject", "Quarterly <b>figures</b>");
        return MimeEntity.Parse(e.ToArray());
    }

    private static SmimeSignOptions Sign() => new() { Signers = [Alice.Signer()], Certificates = [SmimeIdentity.Root] };

    private static SmimeOpenOptions Anchored(params CmsDecryptionKey[] keys) => new()
    {
        DecryptionKeys = keys,
        Chain = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(SmimeIdentity.Root)], ReferenceTime = SmimeIdentity.Now },
    };

    [Fact]
    public void ATrustedSignerForTheSenderIsShownAsVerified()
    {
        SmimeResult r = SmimeProcessor.Open(Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody("signed words\r\n"), Sign())), Anchored());
        string html = r.ToDisplayHtml().Html;
        Assert.Contains("omx-status", html, StringComparison.Ordinal);
        Assert.Contains("Signed by alice@example.test. The signature is valid and the certificate chains to a trusted root.", html, StringComparison.Ordinal);
        Assert.Contains("not covered by the signature", html, StringComparison.Ordinal);
        Assert.Contains("signed words", html, StringComparison.Ordinal);
        // The header block is the message's own, escaped.
        Assert.Contains("Quarterly &lt;b&gt;figures", html, StringComparison.Ordinal);
        Assert.DoesNotContain("color:#a30", SmimeDisplay.StatusHtml(r).Split("not covered")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsignedPartBesideTheSignedOneIsNeitherShownNorCalledSigned()
    {
        MimeMultipart mixed = new("mixed");
        mixed.Add(SmimeSigner.SignDetached(SmimeIdentity.TextBody("the signed text\r\n"), Sign()));
        mixed.Add(SmimeIdentity.TextBody("INJECTED by a list\r\n"));
        SmimeResult r = SmimeProcessor.Open(Wire(mixed), Anchored());
        Assert.False(r.Succeeded);
        string html = r.ToDisplayHtml().Html;
        Assert.Contains("the signed text", html, StringComparison.Ordinal);
        Assert.DoesNotContain("INJECTED", html, StringComparison.Ordinal);
        Assert.Contains("Other parts of this message are not signed and are not shown here (parts", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignerNobodyVouchesForIsNotShownAsVerified()
    {
        SmimeResult r = SmimeTestOptions.Open(Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign())));
        string status = SmimeDisplay.StatusHtml(r);
        Assert.Contains("not checked against a trusted root", status, StringComparison.Ordinal);
        Assert.Contains("color:#a30", status, StringComparison.Ordinal);
        Assert.DoesNotContain("chains to a trusted root", status, StringComparison.Ordinal);

        SmimeResult strict = SmimeProcessor.Open(Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign())));
        Assert.StartsWith("<div", SmimeDisplay.StatusHtml(strict), StringComparison.Ordinal);
        Assert.Contains("Not verified", SmimeDisplay.StatusHtml(strict), StringComparison.Ordinal);
    }

    [Fact]
    public void ACertificateForAnotherAddressThanTheSenderIsFlagged()
    {
        SmimeResult r = SmimeProcessor.Open(Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign()), from: "ceo@example.test"), Anchored());
        string status = SmimeDisplay.StatusHtml(r);
        Assert.Contains("does not match the sender shown in From", status, StringComparison.Ordinal);
        Assert.Contains("The sender is not verified", status, StringComparison.Ordinal);
    }

    [Fact]
    public void AModifiedMessageIsShownAsNotVerified()
    {
        byte[] bytes = Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody("pay 100 now\r\n"), Sign())).ToArray();
        int at = Encoding.Latin1.GetString(bytes).IndexOf("pay 100", StringComparison.Ordinal);
        bytes[at + 4] = (byte)'9';
        SmimeResult r = SmimeProcessor.Open(bytes, Anchored());
        string html = r.ToDisplayHtml().Html;
        Assert.Contains("Not verified", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The signature is valid", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EncryptionIsReportedWithAndWithoutASignature()
    {
        var enc = new SmimeEncryptOptions { Recipients = [Bob.Certificate], Kind = SmimeEncryptionKind.AuthEnveloped };
        SmimeResult only = SmimeProcessor.Open(Wire(SmimeEncryptor.Encrypt(SmimeIdentity.TextBody("secret\r\n"), enc)), Anchored(Bob.Decrypt()));
        string status = SmimeDisplay.StatusHtml(only);
        Assert.Contains("Encrypted but not signed", status, StringComparison.Ordinal);
        Assert.Contains("secret", only.ToDisplayHtml().Html, StringComparison.Ordinal);

        SmimeResult both = SmimeProcessor.Open(Wire(SmimeEncryptor.Encrypt(SmimeSigner.SignDetached(SmimeIdentity.TextBody("secret\r\n"), Sign()), enc)), Anchored(Bob.Decrypt()));
        Assert.Contains("Encrypted and signed by alice@example.test", SmimeDisplay.StatusHtml(both), StringComparison.Ordinal);

        // CBC does not authenticate the content: the status says so.
        var cbc = new SmimeEncryptOptions { Recipients = [Bob.Certificate], Kind = SmimeEncryptionKind.Enveloped };
        SmimeResult unauth = SmimeProcessor.Open(Wire(SmimeEncryptor.Encrypt(SmimeIdentity.TextBody("secret\r\n"), cbc)), Anchored(Bob.Decrypt()));
        Assert.Contains("does not authenticate the content", SmimeDisplay.StatusHtml(unauth), StringComparison.Ordinal);

        // Without the key the status is a failure and no content is made up.
        SmimeResult locked = SmimeProcessor.Open(Wire(SmimeEncryptor.Encrypt(SmimeIdentity.TextBody("secret\r\n"), enc)), Anchored());
        string lockedHtml = locked.ToDisplayHtml().Html;
        Assert.Contains("Not verified", lockedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", lockedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageWithoutSmimeHasNoStatusBlock()
    {
        SmimeResult r = SmimeProcessor.Open(Wire(SmimeIdentity.TextBody("plain\r\n")), Anchored());
        Assert.Equal(string.Empty, SmimeDisplay.StatusHtml(r));
        string html = r.ToDisplayHtml().Html;
        Assert.DoesNotContain("omx-status", html, StringComparison.Ordinal);
        Assert.Contains("plain", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressInTheCertificateIsEscaped()
    {
        // The address comes from the certificate, so it is text an attacker chose.
        SmimeIdentity evil = SmimeIdentity.Rsa("a&b@example.test");
        SmimeResult r = SmimeTestOptions.Open(Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), new SmimeSignOptions { Signers = [evil.Signer()] }), from: "a&b@example.test"));
        string status = SmimeDisplay.StatusHtml(r);
        Assert.Contains("a&amp;b@example.test", status, StringComparison.Ordinal);
    }
}

/// <summary>
/// The Thunderbird vector set (testdata/smime/vectors): messages this library signed and encrypted (<c>ours</c>) and messages Thunderbird made
/// with the same certificates (<c>thunderbird</c>), one directory per key type with the CA certificate, the user's certificate and PKCS #12 file
/// (password "pw"), the expected text and the messages. tools/thunderbird/smime.py runs Thunderbird over them; this reads them back with no outside tool.
/// </summary>
public class SmimeInteropVectors
{
    private static string VectorRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "smime", "vectors"));

    [Fact]
    public void EveryCommittedVectorOpens()
    {
        string root = VectorRoot();
        Assert.True(Directory.Exists(root), "the vector set is missing: " + root);
        int opened = 0;
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Where(static d => File.Exists(Path.Combine(d, "key.p12"))))
        {
            Certificate ca = Certificate.FromPem(File.ReadAllText(Path.Combine(dir, "root.pem")));
            Certificate cert = Certificate.FromPem(File.ReadAllText(Path.Combine(dir, "cert.pem")));
            using X509Certificate2 pfx = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(Path.Combine(dir, "key.p12")), "pw", X509KeyStorageFlags.Exportable);
            CmsDecryptionKey key;
            if (pfx.GetRSAPrivateKey() is { } rsa)
            {
                key = CmsDecryptionKey.FromRsa(rsa, cert);
            }
            else
            {
                using ECDsa ec = pfx.GetECDsaPrivateKey()!;
                key = CmsDecryptionKey.FromECDiffieHellman(ECDiffieHellman.Create(ec.ExportParameters(true)), cert);
            }

            string expected = File.ReadAllText(Path.Combine(dir, "expected.txt"), Encoding.UTF8).Replace("\r\n", "\n").TrimEnd();
            string address = cert.SubjectAlternativeName!.Names.First(static n => n.Kind == GeneralNameKind.Rfc822Name).Value!;
            foreach (string eml in Directory.GetFiles(dir, "*.eml"))
            {
                string kind = Path.GetFileNameWithoutExtension(eml);
                // The certificates were issued the week before the vectors were made, so read at a fixed later time.
                var options = new SmimeOpenOptions
                {
                    DecryptionKeys = [key],
                    Chain = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(ca)], ReferenceTime = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) },
                };
                SmimeResult r = SmimeProcessor.Open(File.ReadAllBytes(eml), options);
                string where = dir + "/" + kind;
                string html = r.ToDisplayHtml().Html;
                if (kind == "signed-in-wrapper")
                {
                    Assert.False(r.Succeeded, where);
                    Assert.DoesNotContain("unsigned addition", html, StringComparison.Ordinal);
                    Assert.Contains("are not signed and are not shown here", html, StringComparison.Ordinal);
                    continue;
                }

                Assert.True(r.Succeeded, where + ": " + string.Join("; ", r.Layers.Select(static l => l.Kind + " " + l.Status + " " + l.Detail)));
                bool wantsSignature = kind != "encrypted";
                Assert.Equal(wantsSignature, r.IsTrusted);
                if (wantsSignature)
                {
                    Assert.Equal(address, r.Signers.Single().CertificateAddresses.Single());
                    Assert.Equal(SmimeSenderMatch.Match, r.Signers.Single().SenderMatch);
                    Assert.Contains("chains to a trusted root", html, StringComparison.Ordinal);
                }

                MimePart leaf = Assert.IsType<MimePart>(FirstTextLeaf(r.Content));
                Assert.Equal(expected, Encoding.UTF8.GetString(leaf.GetDecodedContent()).Replace("\r\n", "\n").TrimEnd());
                seen.Add(Path.GetFileName(Path.GetDirectoryName(dir)!) + "/" + Path.GetFileName(dir) + "/" + kind);
                opened++;
            }
        }

        // Three key types, ours: signed, signed-opaque, encrypted, signed-encrypted; Thunderbird's: signed, encrypted, signed-encrypted.
        Assert.Equal(21, opened);
        Assert.Contains("thunderbird/ecdsa-p256/signed-encrypted", seen);
    }

    private static MimeEntity FirstTextLeaf(MimeEntity e) =>
        e is MimeMultipart m ? FirstTextLeaf(m.Children[0]) : e;
}
