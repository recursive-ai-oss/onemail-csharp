using System.Text;
using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneCrypto.OpenPgp.Packets;
using RecursiveAI.OneMail.Cryptography.OpenPgp;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>Keys for the PGP/MIME tests. Everything is generated here; nothing is a real person's key.</summary>
internal static class PgpFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, PgpCertificate> Cache = [];

    /// <summary>A secret certificate for <paramref name="email"/> under a profile; generated once per test run.</summary>
    public static PgpCertificate Key(string email, PgpKeyProfile profile = PgpKeyProfile.Rfc4880Curve25519)
    {
        string id = profile + ":" + email;
        lock (Cache)
        {
            if (!Cache.TryGetValue(id, out PgpCertificate? cert))
            {
                cert = PgpKeyGenerator.Generate(new PgpKeyGenerationOptions { Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), UserIds = ["Test <" + email + ">"], Profile = profile });
                Cache[id] = cert;
            }

            return cert;
        }
    }

    public static PgpSecretKey Signer(PgpCertificate cert) => PgpSecretKey.Unlock(cert.FindSigningKey(Now)!);

    public static MimeEntity Text(string body, string extraHeaders = "") =>
        MimeEntity.Parse(Encoding.UTF8.GetBytes("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n" + extraHeaders + "\r\n" + body));

    public static MimeEntity Message(MimeEntity wrapped, string from = "alice@example.org", string to = "bob@example.org")
    {
        wrapped.Headers.Insert(0, new MimeHeader("From", "Test <" + from + ">"));
        wrapped.Headers.Insert(1, new MimeHeader("To", to));
        wrapped.Headers.Insert(2, new MimeHeader("Subject", "Greetings"));
        wrapped.Headers.Insert(3, new MimeHeader("MIME-Version", "1.0"));
        return wrapped;
    }

    public static PgpOpenOptions Open(PgpCertificate? verify = null, PgpCertificate? secret = null) => new()
    {
        Now = Now,
        Certificates = verify is null ? [] : [verify.ToPublicCertificate()],
        SecretKeys = secret is null ? [] : [secret],
    };

    public static MimeEntity Reparse(MimeEntity e) => MimeEntity.Parse(e.ToArray());
}

public class PgpMimeTests
{
    private static readonly PgpCertificate Alice = PgpFixtures.Key("alice@example.org");
    private static readonly PgpCertificate Bob = PgpFixtures.Key("bob@example.org");

    private static MimeMultipart Sign(MimeEntity content, PgpCertificate signer, PgpHashAlgorithm? hash = null) =>
        PgpMimeSigner.SignDetached(content, new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(signer)], Hash = hash });

    [Theory]
    [InlineData(PgpKeyProfile.Rfc4880Curve25519)]
    [InlineData(PgpKeyProfile.Rfc9580V4)]
    [InlineData(PgpKeyProfile.Rfc9580)]
    [InlineData(PgpKeyProfile.Rfc4880Rsa)]
    public void SignedMessagesVerifyAfterAReparse(PgpKeyProfile profile)
    {
        PgpCertificate key = PgpFixtures.Key("carol@example.org", profile);
        MimeMultipart signed = Sign(PgpFixtures.Text("Hello, Bob.\r\nSecond line  \r\nFrom me to you.\r\n"), key);
        Assert.StartsWith("pgp-", signed.GetContentTypeParameter("micalg"));
        MimeEntity message = PgpFixtures.Message(signed, "carol@example.org");

        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), PgpFixtures.Open(verify: key));

        Assert.True(result.Succeeded, result.Layers[0].Detail);
        Assert.True(result.IsAuthenticated);
        PgpLayer layer = Assert.Single(result.Layers);
        Assert.Equal(PgpLayerKind.SignedDetached, layer.Kind);
        PgpSignerResult signer = Assert.Single(layer.Signers);
        Assert.True(signer.IsValid);
        Assert.True(signer.KeyValidAtSigning);
        Assert.True(signer.UserIdMatchesSender);
        Assert.Equal("Test <carol@example.org>", signer.UserId);
        Assert.Equal(key.Fingerprint, signer.CertificateFingerprint);
        Assert.Equal(PgpFixtures.Now, signer.CreatedAt);
        Assert.Equal(((MimeMultipart)result.Layers[0].Entity).Children[0].ToArray(), layer.CoveredBytes.ToArray());
        // No trailing white space and no bare "From " line survives in what was signed (RFC 3156 §3).
        Assert.DoesNotContain(" \r\n", Encoding.UTF8.GetString(layer.CoveredBytes.Span));
        Assert.Contains("Subject", result.Outside!.UnprotectedHeaders);
        Assert.Contains(PgpIssue.HeadersNotProtected, layer.Issues);
    }

    [Fact]
    public void TheSignedTextComesBackThroughTheSharedView()
    {
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("Plain words."), Alice));
        PgpResult result = PgpMimeProcessor.Open(message, PgpFixtures.Open(verify: Alice));
        Assert.NotNull(result.InnerItem);
        Assert.Contains("Plain words.", result.InnerItem!.TextBody!.ReadText());
    }

    [Fact]
    public void AChangedByteInTheSignedPartFails()
    {
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("Pay 100 dollars.\r\n"), Alice));
        byte[] bytes = message.ToArray();
        int at = Encoding.UTF8.GetString(bytes).IndexOf("100", StringComparison.Ordinal);
        bytes[at] = (byte)'9';

        PgpResult result = PgpMimeProcessor.Open(MimeEntity.Parse(bytes), PgpFixtures.Open(verify: Alice));

        Assert.False(result.Succeeded);
        Assert.Equal(PgpStatus.SignatureInvalid, result.Layers[0].Status);
        Assert.False(result.IsAuthenticated);
        Assert.NotNull(result.Layers[0].Detail);
    }

    [Fact]
    public void ASignatureFromAnotherMessageFails()
    {
        var a = (MimeMultipart)Sign(PgpFixtures.Text("first\r\n"), Alice);
        var b = (MimeMultipart)Sign(PgpFixtures.Text("second\r\n"), Alice);
        // Swap the signature parts.
        MimeMultipart swapped = new("signed", "x1");
        swapped.Headers.Set("Content-Type", "multipart/signed; protocol=\"application/pgp-signature\"; micalg=pgp-sha512; boundary=\"x1\"");
        swapped.Add(MimeEntity.Parse(a.Children[0].ToArray()));
        swapped.Add(MimeEntity.Parse(b.Children[1].ToArray()));

        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(swapped), PgpFixtures.Open(verify: Alice));

        Assert.Equal(PgpStatus.SignatureInvalid, result.Layers[0].Status);
    }

    [Fact]
    public void AStrippedSignatureIsAStructureFailure()
    {
        var signed = Sign(PgpFixtures.Text("body\r\n"), Alice);
        signed.RemoveAt(1);
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(signed), PgpFixtures.Open(verify: Alice));
        Assert.Equal(PgpStatus.StructureInvalid, result.Layers[0].Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void AnEmptySignaturePartIsMalformed()
    {
        var signed = Sign(PgpFixtures.Text("body\r\n"), Alice);
        var empty = new MimePart();
        empty.Headers.Add("Content-Type", "application/pgp-signature");
        empty.SetRawContent("no signature here\r\n"u8.ToArray());
        signed.RemoveAt(1);
        signed.Add(empty);
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(signed), PgpFixtures.Open(verify: Alice));
        Assert.Equal(PgpStatus.Malformed, result.Layers[0].Status);
    }

    [Fact]
    public void AWrongMicalgFailsWithTheReason()
    {
        MimeMultipart signed = Sign(PgpFixtures.Text("body\r\n"), Alice, PgpHashAlgorithm.Sha512);
        string ct = signed.Headers.GetFirst("Content-Type")!.Value.Replace("pgp-sha512", "pgp-sha256", StringComparison.Ordinal);
        signed.Headers.Set("Content-Type", ct);
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(signed)), PgpFixtures.Open(verify: Alice));
        Assert.Equal(PgpStatus.MicalgMismatch, result.Layers[0].Status);
        Assert.Contains("micalg", result.Layers[0].Detail);
        Assert.True(result.Layers[0].Signers[0].IsValid); // the signature itself is sound; the framing lies
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void AMissingMicalgIsTolerated()
    {
        MimeMultipart signed = Sign(PgpFixtures.Text("body\r\n"), Alice);
        signed.Headers.Set("Content-Type", "multipart/signed; protocol=\"application/pgp-signature\"; boundary=\"" + signed.Boundary + "\"");
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(signed)), PgpFixtures.Open(verify: Alice));
        Assert.True(result.Succeeded);
        Assert.Contains(PgpIssue.MicalgMissing, result.Layers[0].Issues);
    }

    [Fact]
    public void AnUnknownSignerIsReportedNotTrusted()
    {
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("x\r\n"), Alice));
        PgpResult result = PgpMimeProcessor.Open(message, PgpFixtures.Open(verify: Bob));
        Assert.Equal(PgpStatus.SignerUnknown, result.Layers[0].Status);
        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public void AnLfOnlyCopyVerifiesWithAnIssue()
    {
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("one\r\ntwo\r\n"), Alice));
        byte[] lf = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(message.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal));
        PgpResult result = PgpMimeProcessor.Open(MimeEntity.Parse(lf), PgpFixtures.Open(verify: Alice));
        Assert.True(result.Succeeded, result.Layers[0].Detail);
        Assert.Contains(PgpIssue.SignedPartLineEndingsNormalised, result.Layers[0].Issues);
    }

    [Fact]
    public void NewSignaturesRefuseWeakHashes()
    {
        PgpException e = Assert.Throws<PgpException>(() => Sign(PgpFixtures.Text("x\r\n"), Alice, PgpHashAlgorithm.Sha1));
        Assert.Equal(PgpFailure.PolicyViolation, e.Failure);
    }

    [Theory]
    [InlineData(PgpKeyProfile.Rfc4880Curve25519)]
    [InlineData(PgpKeyProfile.Rfc9580)]
    [InlineData(PgpKeyProfile.Rfc4880Rsa)]
    public void EncryptedMessagesDecryptAndKeepTheirBytes(PgpKeyProfile profile)
    {
        PgpCertificate bob = PgpFixtures.Key("bob2@example.org", profile);
        MimeEntity content = PgpFixtures.Text("Secret ünïcode body\r\n");
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(content, new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [bob.ToPublicCertificate()] });
        Assert.Equal("multipart/encrypted", encrypted.MediaType);
        Assert.Equal("application/pgp-encrypted", encrypted.GetContentTypeParameter("protocol"));

        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(encrypted)), PgpFixtures.Open(secret: bob));

        Assert.True(result.Succeeded, result.Layers[0].Detail);
        Assert.Equal(PgpStatus.Decrypted, result.Layers[0].Status);
        Assert.Equal(content.ToArray(), result.Content.ToArray());
        Assert.NotEqual(PgpProtection.None, result.Layers[0].Protection);
        Assert.NotEqual(PgpProtection.NotEncrypted, result.Layers[0].Protection);
        Assert.Contains(PgpIssue.EncryptedWithoutSignature, result.Layers[0].Issues);
        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public void AnotherKeyCannotDecrypt()
    {
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(PgpFixtures.Text("s\r\n"), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()] });
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(encrypted), PgpFixtures.Open(secret: Alice));
        Assert.Equal(PgpStatus.NoDecryptionKey, result.Layers[0].Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ACorruptedCiphertextHasOneFailureStatus()
    {
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(PgpFixtures.Text("s\r\n"), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()] });
        var data = (MimePart)encrypted.Children[1];
        byte[] armor = data.GetDecodedContent();
        string text = Encoding.ASCII.GetString(armor);
        int mid = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4 + 40;
        char[] chars = text.ToCharArray();
        chars[mid] = chars[mid] == 'A' ? 'B' : 'A';
        data.SetRawContent(Encoding.ASCII.GetBytes(new string(chars)));
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(encrypted), PgpFixtures.Open(secret: Bob));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Layers[0].Status, new[] { PgpStatus.DecryptionFailed, PgpStatus.Malformed });
    }

    [Theory]
    [InlineData(PgpKeyProfile.Rfc4880Curve25519)]
    [InlineData(PgpKeyProfile.Rfc9580)]
    public void SignedAndEncryptedInOnePacketSequence(PgpKeyProfile profile)
    {
        PgpCertificate from = PgpFixtures.Key("dan@example.org", profile);
        PgpCertificate to = PgpFixtures.Key("erin@example.org", profile);
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(PgpFixtures.Text("combined \r\nbody\r\n"), new PgpMimeEncryptOptions
        {
            Now = PgpFixtures.Now,
            Recipients = [to.ToPublicCertificate()],
            Signers = [PgpFixtures.Signer(from)],
        });

        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(encrypted, "dan@example.org", "erin@example.org")),
            new PgpOpenOptions { Now = PgpFixtures.Now, Certificates = [from.ToPublicCertificate()], SecretKeys = [to] });

        Assert.True(result.Succeeded, result.Layers[0].Detail);
        Assert.True(result.IsAuthenticated);
        PgpSignerResult s = Assert.Single(result.Signers);
        Assert.True(s.UserIdMatchesSender);
        Assert.DoesNotContain(PgpIssue.EncryptedWithoutSignature, result.Layers[0].Issues);
    }

    [Fact]
    public void SignThenEncryptNestsTwoLayers()
    {
        MimeMultipart signed = Sign(PgpFixtures.Text("nested\r\n"), Alice);
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(signed, new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()] });
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(encrypted)), PgpFixtures.Open(verify: Alice, secret: Bob));
        Assert.True(result.Succeeded);
        Assert.Equal([PgpLayerKind.Encrypted, PgpLayerKind.SignedDetached], result.Layers.Select(static l => l.Kind));
        Assert.True(result.IsAuthenticated);
    }

    [Fact]
    public void ProtectedHeadersInsideTheEncryptionAreReported()
    {
        MimeEntity inner = PgpFixtures.Text("Body\r\n", "Subject: The real subject\r\n");
        inner.Headers.Set("Content-Type", "text/plain; charset=utf-8; protected-headers=\"v1\"");
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(inner, new PgpMimeEncryptOptions
        {
            Now = PgpFixtures.Now,
            Recipients = [Bob.ToPublicCertificate()],
            Signers = [PgpFixtures.Signer(Alice)],
        });
        MimeEntity message = PgpFixtures.Message(encrypted);
        message.Headers.Set("Subject", "...");

        PgpResult result = PgpMimeProcessor.Open(message, PgpFixtures.Open(verify: Alice, secret: Bob));

        Assert.True(result.HeadersProtected);
        Assert.Contains("Subject", result.Outside!.ProtectedHeaders);
        Assert.Equal("The real subject", result.InnerItem!.Subject);
        Assert.Contains(PgpIssue.ProtectedHeadersPresent, result.Layers[^1].Issues);
    }

    [Fact]
    public void AnOpenPgpWrapperInsideMultipartMixedIsFlagged()
    {
        MimeMultipart mixed = new("mixed");
        mixed.Add(Sign(PgpFixtures.Text("inside\r\n"), Alice));
        mixed.Add(PgpFixtures.Text("added by a list\r\n"));
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(mixed)), PgpFixtures.Open(verify: Alice));
        Assert.True(result.Layers[0].Succeeded);
        Assert.False(result.Succeeded);
        Assert.Equal(["2"], result.Outside!.UnprotectedParts);
    }

    [Fact]
    public void NothingFoundInPlainMail()
    {
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(PgpFixtures.Text("hello\r\n")), PgpFixtures.Open());
        Assert.False(result.IsPgp);
        Assert.Empty(result.Layers);
    }

    [Fact]
    public async Task KeyLookupByAddressFindsUsableKeysOnly()
    {
        IPgpKeySource source = PgpKeySource.FromCertificates([Alice.ToPublicCertificate(), Bob.ToPublicCertificate()]);
        var (found, missing) = await PgpKeySource.ResolveRecipientsAsync(source, ["BOB@example.org", "nobody@example.org"], PgpFixtures.Now, TestContext.Current.CancellationToken);
        Assert.Single(found);
        Assert.Equal(["nobody@example.org"], missing);
        // Before the key existed it is not usable.
        var (early, missingEarly) = await PgpKeySource.ResolveRecipientsAsync(source, ["bob@example.org"], PgpFixtures.Now.AddYears(-5), TestContext.Current.CancellationToken);
        Assert.Empty(early);
        Assert.Single(missingEarly);
    }

    [Fact]
    public void AutocryptHeadersAreReadIntoTypedValues()
    {
        string header = AutocryptHeader.Create("alice@example.org", Alice, AutocryptPreference.Mutual).ToHeaderField();
        MimeEntity message = MimeEntity.Parse(Encoding.ASCII.GetBytes("From: Alice <alice@example.org>\r\nDate: Mon, 01 Jun 2026 00:00:00 +0000\r\n" + header + "\r\nAutocrypt: addr=x@y; keydata=!!\r\n\r\nhi\r\n"));
        PgpAutocryptInfo info = PgpAutocrypt.Read(message);
        Assert.Equal(2, info.Headers.Count);
        Assert.NotNull(info.Selected);
        Assert.Equal(AutocryptPreference.Mutual, info.Selected!.Header!.PreferEncrypt);
        Assert.NotEqual(AutocryptHeaderError.None, info.Headers[1].Error);
        AutocryptMessage? described = PgpAutocrypt.Describe(message, PgpFixtures.Now);
        Assert.NotNull(described);
        AutocryptUpdate update = AutocryptPeer.Apply(null, described!);
        Assert.True(update.Changed);
        Assert.NotNull(update.Peer.PublicKey);
    }
}
