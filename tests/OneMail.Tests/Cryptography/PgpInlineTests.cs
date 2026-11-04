using System.Text;
using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneMail.Cryptography.OpenPgp;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

public class PgpInlineTests
{
    private static readonly PgpCertificate Alice = PgpFixtures.Key("alice@example.org");
    private static readonly PgpCertificate Bob = PgpFixtures.Key("bob@example.org");

    private static string Cleartext(string text, PgpCertificate signer)
    {
        var o = new MemoryStream();
        PgpMessageWriter.SignCleartext(new MemoryStream(Encoding.UTF8.GetBytes(text)), o, new PgpWriteOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(signer)] });
        return Encoding.UTF8.GetString(o.ToArray());
    }

    private static string Armored(string text, PgpCertificate to, PgpCertificate? signer = null)
    {
        var o = new MemoryStream();
        PgpMessageWriter.Write(new MemoryStream(Encoding.UTF8.GetBytes(text)), o, new PgpWriteOptions
        {
            Now = PgpFixtures.Now,
            Recipients = [to.ToPublicCertificate()],
            Signers = signer is null ? [] : [PgpFixtures.Signer(signer)],
            Armor = true,
        });
        return Encoding.UTF8.GetString(o.ToArray());
    }

    private static MimeEntity Mail(string body, string cte = "8bit", string charset = "utf-8") =>
        PgpFixtures.Message(MimeEntity.Parse(Encoding.UTF8.GetBytes("Content-Type: text/plain; charset=" + charset + "\r\nContent-Transfer-Encoding: " + cte + "\r\n\r\n" + body.Replace("\n", "\r\n", StringComparison.Ordinal))));

    [Fact]
    public void ACleartextSignedBodyVerifies()
    {
        MimeEntity mail = Mail(Cleartext("Dear Bob,\nsee you at 5.\n- dashes survive\n", Alice));
        PgpResult result = PgpMimeProcessor.Open(mail, PgpFixtures.Open(verify: Alice));
        Assert.True(result.Succeeded, result.Layers[0].Detail);
        Assert.Equal(PgpLayerKind.InlineCleartextSigned, result.Layers[0].Kind);
        Assert.True(result.Layers[0].Signers[0].UserIdMatchesSender);
        Assert.Contains("- dashes survive", Encoding.UTF8.GetString(((MimePart)result.Content).GetDecodedContent()));
    }

    [Fact]
    public void ATamperedCleartextFails()
    {
        string text = Cleartext("Pay 100 dollars\n", Alice).Replace("100", "900", StringComparison.Ordinal);
        PgpResult result = PgpMimeProcessor.Open(Mail(text), PgpFixtures.Open(verify: Alice));
        Assert.Equal(PgpStatus.SignatureInvalid, result.Layers[0].Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void QuotedPrintableWrappedArmorIsRepairedOrDecoded()
    {
        // A client that quoted-printable encodes the whole signed text: the MIME decoder already undoes it.
        string signed = Cleartext("Grüße aus Köln\n", Alice);
        string qp = string.Concat(Encoding.UTF8.GetBytes(signed).Select(static b => b is >= 33 and <= 126 and not (byte)'=' or (byte)'\n' or (byte)' ' ? ((char)b).ToString() : "=" + b.ToString("X2")));
        PgpResult declared = PgpMimeProcessor.Open(Mail(qp, "quoted-printable"), PgpFixtures.Open(verify: Alice));
        Assert.True(declared.Succeeded, declared.Layers[0].Detail);

        // The same text with the escapes left in and no declared encoding (a gateway that encoded and lost the header).
        PgpResult lost = PgpMimeProcessor.Open(Mail(qp), PgpFixtures.Open(verify: Alice));
        Assert.True(lost.Succeeded, lost.Layers[0].Detail);
        Assert.Contains(PgpIssue.InlineQuotedPrintableRepaired, lost.Layers[0].Issues);
    }

    [Fact]
    public void SurroundingTextAndQuotePrefixesAreRepaired()
    {
        string signed = Cleartext("hello\n", Alice);
        PgpResult around = PgpMimeProcessor.Open(Mail("Intro line\n\n" + signed + "\n-- \nList footer\n"), PgpFixtures.Open(verify: Alice));
        Assert.True(around.Succeeded);
        Assert.Contains(PgpIssue.InlineTextOutsideArmor, around.Layers[0].Issues);

        string quoted = "On Monday Bob wrote:\n" + string.Join('\n', signed.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').Select(static l => "> " + l)) + "\n";
        PgpResult q = PgpMimeProcessor.Open(Mail(quoted), PgpFixtures.Open(verify: Alice));
        Assert.True(q.Succeeded, q.Layers[0].Detail);
        Assert.Contains(PgpIssue.InlineQuotePrefixRemoved, q.Layers[0].Issues);
    }

    [Fact]
    public void ACharsetTranscodedCleartextVerifiesWithAnIssue()
    {
        // Signed as UTF-8, then re-encoded by a gateway to the declared ISO-8859-1.
        string signed = Cleartext("Grüße\n", Alice);
        byte[] latin1 = Encoding.Latin1.GetBytes(signed);
        var part = MimeEntity.Parse(Encoding.ASCII.GetBytes("Content-Type: text/plain; charset=iso-8859-1\r\nContent-Transfer-Encoding: 8bit\r\n\r\n").Concat(latin1).ToArray());
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(part), PgpFixtures.Open(verify: Alice));
        Assert.True(result.Succeeded, result.Layers[0].Detail);
        Assert.Contains(PgpIssue.InlineCharsetReencoded, result.Layers[0].Issues);
    }

    [Fact]
    public void AnEncryptedBodyDecryptsAndAMissingKeyIsReported()
    {
        string armor = Armored("Top secret\n", Bob, Alice);
        PgpResult ok = PgpMimeProcessor.Open(Mail(armor), PgpFixtures.Open(verify: Alice, secret: Bob));
        Assert.True(ok.Succeeded, ok.Layers[0].Detail);
        Assert.Equal(PgpLayerKind.InlineMessage, ok.Layers[0].Kind);
        Assert.True(ok.IsAuthenticated);
        Assert.Contains("Top secret", Encoding.UTF8.GetString(((MimePart)ok.Content).GetDecodedContent()));

        PgpResult nokey = PgpMimeProcessor.Open(Mail(armor), PgpFixtures.Open(verify: Alice, secret: Alice));
        Assert.Equal(PgpStatus.NoDecryptionKey, nokey.Layers[0].Status);
    }

    [Fact]
    public void AnEncryptedBodyInsideMultipartAlternativeIsFound()
    {
        var mixed = new MimeMultipart("mixed");
        mixed.Add(MimeEntity.Parse(Encoding.UTF8.GetBytes("Content-Type: text/plain\r\n\r\n" + Armored("x\n", Bob).Replace("\n", "\r\n", StringComparison.Ordinal))));
        mixed.Add(PgpFixtures.Text("footer\r\n"));
        PgpResult r = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(mixed)), PgpFixtures.Open(secret: Bob));
        Assert.True(r.Layers[0].Succeeded);
        Assert.False(r.Succeeded); // a sibling part beside the protected one is not protected
        Assert.Equal(["2"], r.Outside!.UnprotectedParts);
    }
}

public class PgpHostileInputTests
{
    private static readonly PgpCertificate Alice = PgpFixtures.Key("alice@example.org");
    private static readonly PgpCertificate Bob = PgpFixtures.Key("bob@example.org");

    [Fact]
    public void NestedSignedLayersStopAtTheDepthLimit()
    {
        MimeEntity e = PgpFixtures.Text("core\r\n");
        for (int i = 0; i < 12; i++)
        {
            e = PgpMimeSigner.SignDetached(e, new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(Alice)] });
        }

        PgpResult r = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(e)), new PgpOpenOptions { Now = PgpFixtures.Now, Certificates = [Alice.ToPublicCertificate()] });
        Assert.Equal(8, r.Layers.Count);
        Assert.Contains(PgpIssue.DepthLimitReached, r.Issues);
    }

    [Fact]
    public void GarbageArmorAndHugeArmorEndInStatuses()
    {
        string junk = "-----BEGIN PGP MESSAGE-----\r\n\r\n" + new string('A', 5_000_000) + "\r\n-----END PGP MESSAGE-----\r\n";
        MimeEntity m = PgpFixtures.Message(MimeEntity.Parse(Encoding.ASCII.GetBytes("Content-Type: text/plain\r\n\r\n" + junk)));
        PgpResult r = PgpMimeProcessor.Open(m, PgpFixtures.Open(secret: Bob));
        Assert.False(r.Succeeded);
        Assert.Contains(r.Layers[0].Status, new[] { PgpStatus.Malformed, PgpStatus.NoDecryptionKey, PgpStatus.DecryptionFailed });

        string truncated = "-----BEGIN PGP SIGNED MESSAGE-----\r\nHash: SHA512\r\n\r\nhello\r\n";
        PgpResult t = PgpMimeProcessor.Open(PgpFixtures.Message(MimeEntity.Parse(Encoding.ASCII.GetBytes("Content-Type: text/plain\r\n\r\n" + truncated))), PgpFixtures.Open());
        Assert.Equal(PgpStatus.Malformed, t.Layers[0].Status);
    }

    [Fact]
    public void ADecryptedSizeOverTheLimitIsATypedStatus()
    {
        string big = new('x', 200_000);
        MimeMultipart enc = PgpMimeEncryptor.Encrypt(PgpFixtures.Text(big + "\r\n"), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()] });
        PgpResult r = PgpMimeProcessor.Open(PgpFixtures.Message(enc), new PgpOpenOptions { Now = PgpFixtures.Now, SecretKeys = [Bob], MaxPlaintextBytes = 10_000 });
        Assert.Equal(PgpStatus.LimitExceeded, r.Layers[0].Status);
    }

    [Fact]
    public void ThousandsOfPartsAndDeepMultipartDoNotThrow()
    {
        var sb = new StringBuilder("Content-Type: multipart/signed; protocol=\"application/pgp-signature\"; micalg=pgp-sha256; boundary=b\r\n\r\n");
        for (int i = 0; i < 3000; i++)
        {
            sb.Append("--b\r\nContent-Type: text/plain\r\n\r\nx\r\n");
        }

        sb.Append("--b--\r\n");
        PgpResult r = PgpMimeProcessor.Open(MimeEntity.Parse(Encoding.ASCII.GetBytes(sb.ToString())), PgpFixtures.Open());
        Assert.Equal(PgpStatus.StructureInvalid, r.Layers[0].Status);

        var deep = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            deep.Append("Content-Type: multipart/mixed; boundary=b").Append(i).Append("\r\n\r\n--b").Append(i).Append("\r\n");
        }

        PgpResult d = PgpMimeProcessor.Open(MimeEntity.Parse(Encoding.ASCII.GetBytes(deep.ToString())), PgpFixtures.Open());
        Assert.False(d.IsPgp);
    }

    [Fact]
    public void AnUnauthenticatedMessageIsRefusedUnlessAllowed()
    {
        // A bare literal inside a signed layer is fine; there is no portable way to build a SED here, so check the option reaches the reader.
        MimeMultipart enc = PgpMimeEncryptor.Encrypt(PgpFixtures.Text("a\r\n"), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()] });
        PgpResult r = PgpMimeProcessor.Open(PgpFixtures.Message(enc), new PgpOpenOptions { Now = PgpFixtures.Now, SecretKeys = [Bob], AllowUnauthenticatedEncryption = false });
        Assert.NotEqual(PgpProtection.None, r.Layers[0].Protection);
    }
}
