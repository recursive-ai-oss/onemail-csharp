using System.Text;
using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneMail.Cryptography.OpenPgp;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Transfer;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>Attacks on the OpenPGP mail path: what lets a reader believe a message is signed by, or private to, someone it is not.</summary>
public class PgpReviewTests
{
    private static readonly PgpCertificate Alice = PgpFixtures.Key("alice@example.org");
    private static readonly PgpCertificate Bob = PgpFixtures.Key("bob@example.org");
    private static readonly PgpCertificate Mallory = PgpFixtures.Key("mallory@example.org");

    private static MimeMultipart Sign(MimeEntity content, PgpCertificate signer, DateTimeOffset? at = null) =>
        PgpMimeSigner.SignDetached(content, new PgpMimeSignOptions { Now = at ?? PgpFixtures.Now, Signers = [PgpSecretKey.Unlock(signer.FindSigningKey(at ?? PgpFixtures.Now)!)] });

    /// <summary>A copy of <paramref name="cert"/> with a User ID packet nobody certified, as anyone can add to a public certificate in transit or on a key server.</summary>
    private static PgpCertificate WithUncertifiedUserId(PgpCertificate cert, string userId)
    {
        byte[] bytes = cert.ExportPublic();
        int first = bytes[0];
        int headerLength;
        int bodyLength;
        if ((first & 0x40) != 0)
        {
            int l = bytes[1];
            (headerLength, bodyLength) = l < 192 ? (2, l) : l < 224 ? (3, ((l - 192) << 8) + bytes[2] + 192) : throw new NotSupportedException();
        }
        else
        {
            (headerLength, bodyLength) = (first & 3) switch
            {
                0 => (2, (int)bytes[1]),
                1 => (3, (bytes[1] << 8) | bytes[2]),
                _ => throw new NotSupportedException(),
            };
        }

        int end = headerLength + bodyLength;
        byte[] uid = Encoding.UTF8.GetBytes(userId);
        var o = new MemoryStream();
        o.Write(bytes, 0, end);
        o.WriteByte(0xB4);
        o.WriteByte((byte)uid.Length);
        o.Write(uid);
        o.Write(bytes, end, bytes.Length - end);
        return PgpCertificate.Parse(o.ToArray());
    }

    [Fact]
    public void AUserIdNobodyCertifiedDoesNotNameTheSender()
    {
        PgpCertificate forged = WithUncertifiedUserId(Mallory, "Boss <boss@example.org>");
        Assert.Contains("Boss <boss@example.org>", forged.UserIds);
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("wire the money\r\n"), Mallory), from: "boss@example.org");

        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), new PgpOpenOptions { Now = PgpFixtures.Now, Certificates = [forged.ToPublicCertificate()] });

        Assert.True(result.IsAuthenticated);
        PgpSignerResult s = Assert.Single(result.Signers);
        Assert.False(s.UserIdMatchesSender);
        Assert.False(result.IsSenderAuthenticated);
    }

    [Fact]
    public void ACertifiedUserIdNamingTheSenderAuthenticatesTheSender()
    {
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("hello\r\n"), Alice));
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), PgpFixtures.Open(verify: Alice));
        Assert.True(result.IsSenderAuthenticated);
    }

    [Fact]
    public void ASignatureByAnotherKeyIsAuthenticatedButNotTheSender()
    {
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("hello\r\n"), Mallory), from: "alice@example.org");
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), PgpFixtures.Open(verify: Mallory));
        Assert.True(result.IsAuthenticated);
        Assert.False(result.IsSenderAuthenticated);
    }

    [Fact]
    public void ARevokedUserIdDoesNotNameTheSender()
    {
        DateTimeOffset revokedAt = PgpFixtures.Now.AddDays(-1);
        PgpCertificate revoked = PgpKeyEditor.RevokeUserId(Mallory, PgpSecretKey.Unlock(Mallory.Primary), "Test <mallory@example.org>", PgpRevocationReason.UserIdNoLongerValid, "left", revokedAt);
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("hello\r\n"), Mallory), from: "mallory@example.org");
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), new PgpOpenOptions { Now = PgpFixtures.Now, Certificates = [revoked.ToPublicCertificate()] });
        Assert.False(Assert.Single(result.Signers).UserIdMatchesSender);
    }

    [Fact]
    public void EveryFromAddressMustBeNamedBySomeUserId()
    {
        // A message showing "boss" as one of its senders must not count as authenticated for the signer who only names alice.
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("hello\r\n"), Alice));
        message.Headers.Set("From", "Alice <alice@example.org>, Boss <boss@example.org>");
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), PgpFixtures.Open(verify: Alice));
        Assert.False(Assert.Single(result.Signers).UserIdMatchesSender);

        // A second From header is another way to show a different sender.
        MimeEntity twice = PgpFixtures.Message(Sign(PgpFixtures.Text("hello\r\n"), Alice));
        twice.Headers.Add("From", "Boss <boss@example.org>");
        Assert.False(Assert.Single(PgpMimeProcessor.Open(PgpFixtures.Reparse(twice), PgpFixtures.Open(verify: Alice)).Signers).UserIdMatchesSender);
    }

    [Fact]
    public async Task AUserIdNobodyCertifiedIsNotARecipientAddress()
    {
        PgpCertificate forged = WithUncertifiedUserId(Mallory, "Boss <boss@example.org>");
        IPgpKeySource source = PgpKeySource.FromCertificates([forged.ToPublicCertificate(), Bob.ToPublicCertificate()]);
        var (found, missing) = await PgpKeySource.ResolveRecipientsAsync(source, ["boss@example.org", "mallory@example.org", "bob@example.org"], PgpFixtures.Now, TestContext.Current.CancellationToken);
        Assert.Equal(["boss@example.org"], missing);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void HeadersRepeatedInsideAnUnsignedEncryptedMessageAreNotTrusted()
    {
        // Anyone holding Bob's public key can encrypt a message to him. What is inside says "From: boss", but nobody vouched for it.
        MimeEntity inner = PgpFixtures.Text("Please pay.\r\n", "From: Boss <boss@example.org>\r\nSubject: Urgent\r\n");
        inner.Headers.Set("Content-Type", "text/plain; charset=utf-8; protected-headers=\"v1\"");
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(inner, new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()] });
        MimeEntity message = PgpFixtures.Message(encrypted, from: "mallory@example.org");

        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), PgpFixtures.Open(secret: Bob));

        Assert.True(result.Succeeded);
        Assert.False(result.IsAuthenticated);
        Assert.False(result.HeadersProtected);
        Assert.Empty(result.Outside!.ProtectedHeaders);
        Assert.Contains("mallory@example.org", result.InnerItem!.From.Select(static a => a.Address));
        Assert.DoesNotContain("boss@example.org", result.InnerItem.From.Select(static a => a.Address));
        Assert.NotEqual("Urgent", result.InnerItem.Subject);
    }

    [Fact]
    public void HeadersInsideAForgedSignatureAreNotTrusted()
    {
        MimeEntity inner = PgpFixtures.Text("Please pay.\r\n", "From: Boss <boss@example.org>\r\n");
        inner.Headers.Set("Content-Type", "text/plain; charset=utf-8; protected-headers=\"v1\"");
        MimeMultipart signed = Sign(inner, Alice);
        byte[] bytes = PgpFixtures.Message(signed).ToArray();
        int at = Encoding.UTF8.GetString(bytes).IndexOf("Please", StringComparison.Ordinal);
        bytes[at] = (byte)'p';
        PgpResult result = PgpMimeProcessor.Open(MimeEntity.Parse(bytes), PgpFixtures.Open(verify: Alice));
        Assert.False(result.IsAuthenticated);
        Assert.Empty(result.Outside!.ProtectedHeaders);
        Assert.DoesNotContain("boss@example.org", result.InnerItem!.From.Select(static a => a.Address));
    }

    [Fact]
    public void SignersWithDifferentHashesGiveAValidMicalgParameter()
    {
        PgpCertificate rsa = PgpFixtures.Key("p256@example.org", PgpKeyProfile.NistP256);
        PgpCertificate ed = PgpFixtures.Key("ed@example.org", PgpKeyProfile.NistP521);
        MimeMultipart signed = PgpMimeSigner.SignDetached(PgpFixtures.Text("two signers\r\n"), new PgpMimeSignOptions
        {
            Now = PgpFixtures.Now,
            Signers = [PgpFixtures.Signer(rsa), PgpFixtures.Signer(ed)],
        });
        MimeEntity reparsed = PgpFixtures.Reparse(PgpFixtures.Message(signed, "p256@example.org"));
        string micalg = reparsed.GetContentTypeParameter("micalg")!;
        Assert.Contains(",", micalg);
        // The parameter survives a reparse whole: a comma outside quotes would end the value (RFC 2045 §5.1, tspecials).
        PgpResult result = PgpMimeProcessor.Open(reparsed, new PgpOpenOptions { Now = PgpFixtures.Now, Certificates = [rsa.ToPublicCertificate(), ed.ToPublicCertificate()] });
        Assert.True(result.Succeeded, result.Layers[0].Detail);
        Assert.Equal(2, result.Signers.Count());
        Assert.Contains("micalg=\"" + micalg + "\"", Encoding.ASCII.GetString(reparsed.ToArray()));
    }

    [Fact]
    public void WhatIsSignedIsWhatIsSentForAwkwardContent()
    {
        string raw = "Content-Type: multipart/mixed; boundary=\"in\"  \r\nX-Note: trailing blank   \r\n\r\npreamble  \r\n--in  \r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\ncaf\u00e9 \nFrom here\n\r\n--in\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: binary\r\n\r\n\u0001\u0002\r\n--in--\r\nepilogue \r\n";
        MimeEntity content = MimeEntity.Parse(Encoding.Latin1.GetBytes(raw.Replace("\u00e9", "\u00c3\u00a9", StringComparison.Ordinal)));
        MimeMultipart signed = Sign(content, Alice);
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(signed)), PgpFixtures.Open(verify: Alice));
        Assert.True(result.IsAuthenticated, result.Layers[0].Detail);
        // No repair was needed: the form that was signed is the form that was sent.
        Assert.DoesNotContain(PgpIssue.SignedPartLineEndingsNormalised, result.Layers[0].Issues);
        Assert.DoesNotContain(PgpIssue.SignedPartTrailingWhitespaceStripped, result.Layers[0].Issues);
    }

    [Fact]
    public void HiddenRecipientsWriteTheWildcardKeyIdAndStillDecrypt()
    {
        MimeMultipart enc = PgpMimeEncryptor.Encrypt(PgpFixtures.Text("blind copy\r\n"), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()], HiddenRecipients = true });
        byte[] binary = Dearmor(((MimePart)enc.Children[1]).GetDecodedContent());

        // RFC 9580 5.1.1: packet 1 is a version 3 public-key encrypted session key; its key ID is eight zero bytes for a hidden recipient.
        Assert.Equal(0xC1, binary[0]);
        Assert.Equal(3, binary[2]);
        Assert.All(binary[3..11], static b => Assert.Equal(0, b));

        PgpResult r = PgpMimeProcessor.Open(enc, new PgpOpenOptions { Now = PgpFixtures.Now, SecretKeys = [Bob] });
        Assert.True(r.Layers[0].Content is MimePart);
        Assert.Contains("blind copy", Encoding.UTF8.GetString(((MimePart)r.Layers[0].Content!).GetDecodedContent()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CuttingOrExtendingTheEncryptedPacketNeverGivesPlaintext(bool seipd1)
    {
        PgpCertificate key = seipd1 ? Bob : PgpFixtures.Key("bob6@example.org", PgpKeyProfile.Rfc9580);
        MimeMultipart enc = PgpMimeEncryptor.Encrypt(PgpFixtures.Text("the plain secret\r\n"), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [key.ToPublicCertificate()], ForceSeipd1 = seipd1 });
        byte[] armor = ((MimePart)enc.Children[1]).GetDecodedContent();
        byte[] binary = Dearmor(armor);
        var variants = new List<byte[]>();
        for (int cut = 1; cut <= 40; cut++)
        {
            variants.Add(binary[..^cut]);
        }

        // Another packet after the encrypted one: the tail of a plaintext literal rides along.
        byte[] literal = [0xCB, 11, (byte)'b', 0, 0, 0, 0, 0, (byte)'e', (byte)'v', (byte)'i', (byte)'l', (byte)'!'];
        variants.Add([.. binary, .. literal]);
        variants.Add([.. literal, .. binary]);
        int leaks = 0;
        foreach (byte[] v in variants)
        {
            MimeMultipart m = (MimeMultipart)MimeEntity.Parse(enc.ToArray());
            var data = (MimePart)m.Children[1];
            data.SetDecodedContent(v, ContentTransferEncoding.Base64);
            PgpResult r = PgpMimeProcessor.Open(m, new PgpOpenOptions { Now = PgpFixtures.Now, SecretKeys = [key] });
            string shown = r.Layers.Count > 0 && r.Layers[0].Content is MimePart p ? Encoding.UTF8.GetString(p.GetDecodedContent()) : string.Empty;
            if (r.Succeeded && v.Length != binary.Length || shown.Contains("evil", StringComparison.Ordinal))
            {
                leaks++;
            }
        }

        Assert.Equal(0, leaks);
    }

    [Fact]
    public void InlineCleartextWithTextAroundItOnlyHandsBackTheSignedText()
    {
        var o = new MemoryStream();
        PgpMessageWriter.SignCleartext(new MemoryStream("the real words\r\n"u8.ToArray()), o, new PgpWriteOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(Alice)] });
        string body = "Alice says all is fine, wire the money.\r\n" + Encoding.UTF8.GetString(o.ToArray()) + "\r\nP.S. Alice also approves the second payment.\r\n";
        MimeEntity message = PgpFixtures.Message(MimeEntity.Parse(Encoding.UTF8.GetBytes("Content-Type: text/plain; charset=utf-8\r\n\r\n" + body)));
        PgpResult result = PgpMimeProcessor.Open(message, PgpFixtures.Open(verify: Alice));
        Assert.Contains(PgpIssue.InlineTextOutsideArmor, result.Layers[0].Issues);
        string content = Encoding.UTF8.GetString(((MimePart)result.Content).GetDecodedContent());
        Assert.Contains("the real words", content);
        Assert.DoesNotContain("wire the money", content);
        Assert.DoesNotContain("second payment", content);
    }

    [Fact]
    public void AKeyRevokedForCompromiseInvalidatesEarlierSignatures()
    {
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("old mail\r\n"), Mallory), from: "mallory@example.org");
        PgpCertificate hard = PgpKeyEditor.RevokeKey(Mallory, PgpSecretKey.Unlock(Mallory.Primary), PgpRevocationReason.Compromised, "stolen", PgpFixtures.Now.AddDays(30));
        PgpCertificate soft = PgpKeyEditor.RevokeKey(Mallory, PgpSecretKey.Unlock(Mallory.Primary), PgpRevocationReason.Retired, "retired", PgpFixtures.Now.AddDays(30));
        DateTimeOffset later = PgpFixtures.Now.AddDays(60);
        PgpResult hardResult = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), new PgpOpenOptions { Now = later, Certificates = [hard.ToPublicCertificate()] });
        PgpResult softResult = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), new PgpOpenOptions { Now = later, Certificates = [soft.ToPublicCertificate()] });
        Assert.False(hardResult.IsAuthenticated);
        Assert.True(softResult.IsAuthenticated);
    }

    [Fact]
    public void ASignatureFromTheFutureIsNotValid()
    {
        MimeEntity message = PgpFixtures.Message(Sign(PgpFixtures.Text("from tomorrow\r\n"), Alice, PgpFixtures.Now.AddDays(10)), from: "alice@example.org");
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(message), PgpFixtures.Open(verify: Alice));
        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public void AutocryptIgnoresAHeaderNamingAnotherAddressAndDuplicatedFrom()
    {
        string header = AutocryptHeader.Create("alice@example.org", Alice, AutocryptPreference.Mutual).ToHeaderField();
        MimeEntity other = MimeEntity.Parse(Encoding.ASCII.GetBytes("From: Mallory <mallory@example.org>\r\n" + header + "\r\n\r\nhi\r\n"));
        Assert.Null(PgpAutocrypt.Read(other).Selected);
        MimeEntity two = MimeEntity.Parse(Encoding.ASCII.GetBytes("From: Alice <alice@example.org>\r\nFrom: Mallory <mallory@example.org>\r\n" + header + "\r\n\r\nhi\r\n"));
        Assert.Null(PgpAutocrypt.Read(two).Selected);
        // Gossip in the unprotected headers is not "from the encrypted payload": the caller passes the decrypted entity, and this only reads what it is given.
        MimeEntity twoHeaders = MimeEntity.Parse(Encoding.ASCII.GetBytes("From: Alice <alice@example.org>\r\n" + header + "\r\n" + header + "\r\n\r\nhi\r\n"));
        Assert.Null(PgpAutocrypt.Read(twoHeaders).Selected);
    }

    [Fact]
    public void EncryptionRefusesRevokedRecipientsAndABoundaryInsideTheCiphertext()
    {
        PgpCertificate revoked = PgpKeyEditor.RevokeKey(Mallory, PgpSecretKey.Unlock(Mallory.Primary), PgpRevocationReason.Compromised, "stolen", PgpFixtures.Now.AddDays(-1));
        Assert.ThrowsAny<Exception>(() => PgpMimeEncryptor.Encrypt(PgpFixtures.Text("x\r\n"), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [revoked.ToPublicCertificate()] }));
        Assert.Throws<ArgumentException>(() => PgpMimeEncryptor.Encrypt(PgpFixtures.Text("x\r\n"), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()], Boundary = "BEGIN" }));
    }

    private static byte[] Dearmor(byte[] armor)
    {
        string s = Encoding.ASCII.GetString(armor);
        int start = s.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        int end = s.IndexOf("\r\n=", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = s.IndexOf("\r\n-----END", start, StringComparison.Ordinal);
        }

        return Convert.FromBase64String(s[start..end].Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }
}
