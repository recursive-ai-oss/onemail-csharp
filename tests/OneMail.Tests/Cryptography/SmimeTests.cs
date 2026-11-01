using System.Text;
using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Keys;
using RecursiveAI.OneCrypto.Policy;
using RecursiveAI.OneCrypto.Validation;
using RecursiveAI.OneCrypto.Wire.Cms;
using RecursiveAI.OneMail.Cryptography.Smime;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

public sealed class SmimeTests : IDisposable
{
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (IDisposable d in _open)
        {
            d.Dispose();
        }
    }

    private static readonly SmimeIdentity Alice = SmimeIdentity.Rsa("alice@example.test");
    private static readonly SmimeIdentity Bob = SmimeIdentity.Rsa("bob@example.test");
    private static readonly SmimeIdentity Carol = SmimeIdentity.Ec("carol@example.test");
    private static readonly SmimeIdentity Eve = SmimeIdentity.Ed25519("eve@example.test");

    private static MimeEntity Transport(MimeEntity e, string from = "alice@example.test")
    {
        e.Headers.Add("MIME-Version", "1.0");
        e.Headers.Add("From", from);
        e.Headers.Add("To", "bob@example.test");
        e.Headers.Add("Subject", "Test");
        return MimeEntity.Parse(e.ToArray());
    }

    private static string Text(byte[] b) => Encoding.Latin1.GetString(b);

    private static SmimeSignOptions Sign(SmimeIdentity who, SignatureSpec? spec = null, string? digest = null) =>
        new() { Signers = [who.Signer(spec, digest)], SigningTime = SmimeIdentity.Now, Boundary = "test-boundary-1" };

    [Fact]
    public void DetachedRsaSignatureVerifiesAndReportsEverything()
    {
        MimeMultipart signed = SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice));
        Assert.Equal("multipart/signed", signed.MediaType);
        Assert.Equal("sha-256", signed.GetContentTypeParameter("micalg"));
        Assert.Equal("application/pkcs7-signature", signed.GetContentTypeParameter("protocol"));

        MimeEntity wire = Transport(signed);
        SmimeResult r = SmimeProcessor.Open(wire);
        SmimeSignedLayer layer = Assert.IsType<SmimeSignedLayer>(Assert.Single(r.Layers));
        Assert.Equal(SmimeLayerKind.SignedDetached, layer.Kind);
        Assert.Equal(SmimeStatus.Valid, layer.Status);
        Assert.True(r.Succeeded);
        SmimeSignerResult s = Assert.Single(layer.Signers);
        Assert.True(s.IsValid);
        Assert.Equal(SmimeSenderMatch.Match, s.SenderMatch);
        Assert.Equal(SmimeChainStatus.NotChecked, s.Chain);
        Assert.Contains("alice@example.test", s.CertificateAddresses);
        Assert.True(s.HasEncryptionKeyPreference);
        Assert.Equal(CmsOids.Aes256Gcm, s.Capabilities[0]);
        Assert.Equal(4, s.Capabilities.Count);
        Assert.Empty(s.WeakAlgorithms);
        Assert.Empty(layer.Issues);

        // The covered bytes are exactly the first part as stored, and the report says where they sit in the source.
        var multipart = (MimeMultipart)wire;
        Assert.Equal(multipart.Children[0].ToArray(), layer.CoveredBytes.ToArray());
        Assert.True(layer.CoveredSourceOffset > 0);
        Assert.Equal(multipart.Children[0].SourceLength, layer.CoveredSourceLength);

        // Unsigned: the outer headers; the signed entity is a plain text part, so nothing is protected.
        Assert.NotNull(r.Outside);
        Assert.Contains("From", r.Outside!.UnsignedHeaders);
        Assert.Contains("Subject", r.Outside.UnsignedHeaders);
        Assert.Empty(r.Outside.ProtectedHeaders);
        Assert.Contains("signed text", r.InnerItem!.TextBody!.ReadText());
    }

    [Fact]
    public void OpaqueSignatureVerifiesAndYieldsTheContent()
    {
        MimePart opaque = SmimeSigner.SignOpaque(SmimeIdentity.TextBody("opaque body\r\n"), Sign(Alice));
        Assert.Equal("application/pkcs7-mime", opaque.MediaType);
        Assert.Equal("signed-data", opaque.GetContentTypeParameter("smime-type"));
        SmimeResult r = SmimeProcessor.Open(Transport(opaque));
        SmimeSignedLayer layer = Assert.IsType<SmimeSignedLayer>(Assert.Single(r.Layers));
        Assert.Equal(SmimeLayerKind.SignedOpaque, layer.Kind);
        Assert.True(layer.Succeeded);
        Assert.Contains("opaque body", r.InnerItem!.TextBody!.ReadText());
        Assert.Equal(SmimeSenderMatch.Match, layer.Signers[0].SenderMatch);
    }

    public static TheoryData<string> SignatureKinds() => ["rsa", "pss", "ecdsa", "ed25519"];

    [Theory]
    [MemberData(nameof(SignatureKinds))]
    public void SignatureAlgorithmsRoundTripDetachedAndOpaque(string kind)
    {
        (SmimeIdentity who, SignatureSpec? spec, string? digest, string micalg) = kind switch
        {
            "rsa" => (Alice, null, null, "sha-256"),
            "pss" => (Alice, SignatureSpec.RsaPss(System.Security.Cryptography.HashAlgorithmName.SHA256), null, "sha-256"),
            "ecdsa" => (Carol, null, null, "sha-256"),
            _ => (Eve, SignatureSpec.Ed25519(), "2.16.840.1.101.3.4.2.3", "sha-512"),
        };
        string from = who.Certificate.SubjectAlternativeName!.Names[0].Value!;
        MimeMultipart d = SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(who, spec, digest));
        Assert.Equal(micalg, d.GetContentTypeParameter("micalg"));
        SmimeResult dr = SmimeProcessor.Open(Transport(d, from));
        Assert.True(dr.Succeeded, dr.Layers[0].Detail);
        Assert.Equal(SmimeSenderMatch.Match, dr.Signers.Single().SenderMatch);
        SmimeResult or = SmimeProcessor.Open(Transport(SmimeSigner.SignOpaque(SmimeIdentity.TextBody(), Sign(who, spec, digest)), from));
        Assert.True(or.Succeeded, or.Layers[0].Detail);
    }

    [Fact]
    public void WeakSignaturesAreRefusedForNewMessages()
    {
        // SHA-1 is refused by the default policy (new data).
        Assert.Throws<PolicyViolationException>(() => SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice, SignatureSpec.RsaPkcs1(System.Security.Cryptography.HashAlgorithmName.SHA1))));
        // A 1024-bit RSA key is refused too.
        SmimeIdentity small = SmimeIdentity.Rsa("small@example.test", 1024);
        Assert.Throws<PolicyViolationException>(() => SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(small)));
    }

    [Fact]
    public void WeakSignaturesAreAcceptedOnReadingAndFlagged()
    {
        // A permissive policy can still write one (for tests of old data); reading flags it.
        SmimeSignOptions o = new()
        {
            Signers = [Alice.Signer(SignatureSpec.RsaPkcs1(System.Security.Cryptography.HashAlgorithmName.SHA1))],
            Policy = CryptoPolicy.Permissive,
            Boundary = "b1",
        };
        MimeMultipart d = SmimeSigner.SignDetached(SmimeIdentity.TextBody(), o);
        Assert.Equal("sha-1", d.GetContentTypeParameter("micalg"));
        SmimeResult r = SmimeProcessor.Open(Transport(d));
        SmimeSignerResult s = r.Signers.Single();
        Assert.True(s.IsValid);
        Assert.Contains("digest:sha-1", s.WeakAlgorithms);

        // With a standard policy and a reference time the same signature is rejected, not just flagged.
        SmimeResult strict = SmimeProcessor.Open(Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), o)),
            new SmimeOpenOptions { Policy = CryptoPolicy.Strict, ReferenceTime = SmimeIdentity.Now });
        Assert.Equal(SmimeStatus.PolicyRejected, strict.Signers.Single().Status);
    }

    [Fact]
    public void ChainIsCheckedOnlyWhenAsked()
    {
        MimeEntity wire = Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), new SmimeSignOptions { Signers = [Alice.Signer()], Certificates = [SmimeIdentity.Root] }));
        var anchored = new SmimeOpenOptions
        {
            Chain = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(SmimeIdentity.Root)], ReferenceTime = SmimeIdentity.Now },
        };
        SmimeSignerResult ok = SmimeProcessor.Open(wire, anchored).Signers.Single();
        Assert.Equal(SmimeChainStatus.Passed, ok.Chain);
        Assert.True(ok.IsValid);

        SmimeIdentity stranger = SmimeIdentity.Rsa("x@example.test");
        var wrongAnchor = new SmimeOpenOptions
        {
            Chain = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(stranger.Certificate)], ReferenceTime = SmimeIdentity.Now },
        };
        SmimeSignerResult bad = SmimeProcessor.Open(wire, wrongAnchor).Signers.Single();
        Assert.Equal(SmimeChainStatus.Failed, bad.Chain);
        Assert.Equal(SmimeStatus.UntrustedChain, bad.Status);
        Assert.False(SmimeProcessor.Open(wire, wrongAnchor).Succeeded);
    }

    [Fact]
    public void SenderMismatchIsReported()
    {
        MimeEntity wire = Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)), "mallory@example.test");
        SmimeSignerResult s = SmimeProcessor.Open(wire).Signers.Single();
        Assert.True(s.IsValid);
        Assert.Equal(SmimeSenderMatch.Mismatch, s.SenderMatch);
    }

    // ---- tampering ----

    private static byte[] Wire(MimeEntity e, string from = "alice@example.test") => Transport(e, from).ToArray();

    private static SmimeResult OpenBytes(byte[] b, SmimeOpenOptions? o = null) => SmimeProcessor.Open(b, o);

    [Fact]
    public void ChangedBodyByteIsContentModified()
    {
        byte[] b = Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)));
        string t = Text(b).Replace("signed text", "signed t3xt", StringComparison.Ordinal);
        SmimeResult r = OpenBytes(Encoding.Latin1.GetBytes(t));
        Assert.Equal(SmimeStatus.ContentModified, r.Signers.Single().Status);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void ChangedHeaderOfTheSignedPartIsContentModified()
    {
        byte[] b = Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)));
        string t = Text(b).Replace("charset=utf-8", "charset=utf-7", StringComparison.Ordinal);
        Assert.Equal(SmimeStatus.ContentModified, OpenBytes(Encoding.Latin1.GetBytes(t)).Signers.Single().Status);
    }

    [Fact]
    public void ChangedOuterHeadersDoNotBreakTheSignatureButAreListedAsUnsigned()
    {
        byte[] b = Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)));
        string t = Text(b).Replace("Subject: Test", "Subject: Other", StringComparison.Ordinal);
        SmimeResult r = OpenBytes(Encoding.Latin1.GetBytes(t));
        Assert.True(r.Succeeded);
        Assert.Contains("Subject", r.Outside!.UnsignedHeaders);
    }

    [Fact]
    public void ExtraPartsInsideTheSignedMultipartAreRefused()
    {
        byte[] b = Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)));
        // A third part after the signature, "boundary games": a display could show it, the signature does not cover it.
        string t = Text(b).Replace("\r\n--test-boundary-1--", "\r\n--test-boundary-1\r\nContent-Type: text/plain\r\n\r\nEVIL\r\n--test-boundary-1--", StringComparison.Ordinal);
        SmimeResult r = OpenBytes(Encoding.Latin1.GetBytes(t));
        Assert.Equal(SmimeStatus.StructureInvalid, r.Layers[0].Status);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void SignatureFromAnotherMessageIsContentModified()
    {
        var a = (MimeMultipart)Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody("one\r\n"), Sign(Alice)));
        var b = (MimeMultipart)Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody("two\r\n"), Sign(Alice)));
        string sigB = Text(((MimePart)b.Children[1]).GetDecodedContent());
        var swapped = (MimePart)a.Children[1];
        swapped.SetDecodedContent(Encoding.Latin1.GetBytes(sigB));
        SmimeResult r = SmimeProcessor.Open(MimeEntity.Parse(a.ToArray()));
        Assert.Equal(SmimeStatus.ContentModified, r.Signers.Single().Status);
    }

    [Fact]
    public void DamagedSignatureValueIsSignatureInvalid()
    {
        var m = (MimeMultipart)Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)));
        var sig = (MimePart)m.Children[1];
        byte[] cms = sig.GetDecodedContent();
        cms[^3] ^= 0x55; // inside the signature value, the last element of the SignerInfo
        sig.SetDecodedContent(cms);
        SmimeResult r = SmimeProcessor.Open(MimeEntity.Parse(m.ToArray()));
        Assert.Equal(SmimeStatus.SignatureInvalid, r.Signers.Single().Status);
    }

    [Fact]
    public void SignerCertificateReplacedByAnotherIsNotAccepted()
    {
        // The SignerInfo names Alice's certificate (issuer and serial); a message carrying Bob's certificate instead has no matching certificate.
        MimeMultipart d = SmimeSigner.SignDetached(SmimeIdentity.TextBody(), new SmimeSignOptions
        {
            Signers = [Alice.Signer()],
            Boundary = "b2",
        });
        var wire = (MimeMultipart)Transport(d);
        var sig = (MimePart)wire.Children[1];
        SignedData sd = SignedData.Parse(sig.GetDecodedContent());
        byte[] cms = sd.Encoded.ToArray();
        byte[] aliceCert = Alice.Certificate.Encoded.ToArray();
        int at = cms.AsSpan().IndexOf(aliceCert);
        Assert.True(at > 0);
        // Flip the last byte of the serial number in the SignerInfo, so no certificate matches the identifier.
        byte[] serial = Alice.Certificate.SerialNumber.ToArray();
        int sAt = cms.AsSpan(at + aliceCert.Length).IndexOf(serial);
        Assert.True(sAt > 0);
        cms[at + aliceCert.Length + sAt + serial.Length - 1] ^= 0x01;
        sig.SetDecodedContent(cms);
        SmimeSignerResult s = SmimeProcessor.Open(MimeEntity.Parse(wire.ToArray())).Signers.Single();
        Assert.Equal(SmimeStatus.CertificateNotFound, s.Status);
        Assert.Equal(SmimeSenderMatch.NoCertificate, s.SenderMatch);
    }

    [Fact]
    public void TruncatedAndGarbageCmsAreMalformedNeverExceptions()
    {
        var m = (MimeMultipart)Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)));
        var sig = (MimePart)m.Children[1];
        byte[] cms = sig.GetDecodedContent();
        for (int cut = 0; cut < cms.Length; cut += Math.Max(1, cms.Length / 40))
        {
            sig.SetDecodedContent(cms.AsSpan(0, cut));
            SmimeResult r = SmimeProcessor.Open(MimeEntity.Parse(m.ToArray()));
            Assert.False(r.Succeeded);
            Assert.Equal(SmimeStatus.Malformed, r.Layers[0].Status);
        }

        sig.SetDecodedContent("not cms at all"u8);
        Assert.Equal(SmimeStatus.Malformed, SmimeProcessor.Open(MimeEntity.Parse(m.ToArray())).Layers[0].Status);

        // The same for an opaque part and an encrypted one.
        MimePart opaque = SmimeSigner.SignOpaque(SmimeIdentity.TextBody(), Sign(Alice));
        byte[] ocms = opaque.GetDecodedContent();
        opaque.SetDecodedContent(ocms.AsSpan(0, ocms.Length / 2));
        Assert.Equal(SmimeStatus.Malformed, SmimeProcessor.Open(MimeEntity.Parse(opaque.ToArray())).Layers[0].Status);
    }

    [Fact]
    public void LfOnlyStorageIsVerifiedAfterCanonicalisationAndSaysSo()
    {
        byte[] b = Wire(SmimeSigner.SignDetached(SmimeIdentity.TextBody("a\r\nb\r\n"), Sign(Alice)));
        string lf = Text(b).Replace("\r\n", "\n", StringComparison.Ordinal);
        SmimeResult r = OpenBytes(Encoding.Latin1.GetBytes(lf));
        SmimeSignedLayer layer = Assert.IsType<SmimeSignedLayer>(r.Layers[0]);
        Assert.Equal(SmimeStatus.Valid, layer.Status);
        Assert.Contains(SmimeIssue.SignedPartLineEndingsNormalised, layer.Issues);
    }

    [Fact]
    public void EightBitContentIsReencodedBeforeSigning()
    {
        var p = new MimePart("text/plain; charset=utf-8");
        p.SetDecodedContent(Encoding.UTF8.GetBytes("caf\u00e9 \u2603\r\n"), RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.EightBit);
        MimeMultipart d = SmimeSigner.SignDetached(p, Sign(Alice));
        byte[] signedPart = d.Children[0].ToArray();
        Assert.All(signedPart, b => Assert.True(b < 0x80));
        Assert.Contains("quoted-printable", Text(signedPart), StringComparison.OrdinalIgnoreCase);
        SmimeResult r = SmimeProcessor.Open(Transport(d));
        Assert.True(r.Succeeded);
        Assert.Contains("caf\u00e9", r.InnerItem!.TextBody!.ReadText());
    }

    // ---- encryption ----

    [Fact]
    public void EnvelopedRsaOaepRoundTrip()
    {
        MimePart enc = SmimeEncryptor.Encrypt(SmimeIdentity.TextBody("secret\r\n"), new SmimeEncryptOptions { Recipients = [Bob.Certificate, Alice.Certificate] });
        Assert.Equal("enveloped-data", enc.GetContentTypeParameter("smime-type"));
        MimeEntity wire = Transport(enc);
        SmimeResult closed = SmimeProcessor.Open(wire);
        Assert.Equal(SmimeStatus.NoDecryptionKey, closed.Layers[0].Status);
        Assert.Null(closed.InnerItem);

        SmimeResult r = SmimeProcessor.Open(wire, new SmimeOpenOptions { DecryptionKeys = [Bob.Decrypt()] });
        var layer = Assert.IsType<SmimeContentLayer>(Assert.Single(r.Layers));
        Assert.Equal(SmimeStatus.Decrypted, layer.Status);
        Assert.Equal(2, layer.RecipientCount);
        Assert.False(layer.IsAuthenticated);
        Assert.Contains(SmimeIssue.UnauthenticatedEncryption, layer.Issues);
        Assert.Equal(Bob.Certificate, layer.RecipientCertificate);
        Assert.Contains("secret", r.InnerItem!.TextBody!.ReadText());
    }

    [Fact]
    public void AuthEnvelopedGcmRoundTripAndTamper()
    {
        MimePart enc = SmimeEncryptor.Encrypt(SmimeIdentity.TextBody("gcm secret\r\n"), new SmimeEncryptOptions { Recipients = [Bob.Certificate, Carol.Certificate], Kind = SmimeEncryptionKind.AuthEnveloped });
        Assert.Equal("authEnveloped-data", enc.GetContentTypeParameter("smime-type"));
        MimeEntity wire = Transport(enc);
        foreach (SmimeIdentity who in new[] { Bob, Carol })
        {
            SmimeResult r = SmimeProcessor.Open(wire, new SmimeOpenOptions { DecryptionKeys = [who.Decrypt()] });
            var layer = Assert.IsType<SmimeContentLayer>(r.Layers[0]);
            Assert.Equal(SmimeStatus.Decrypted, layer.Status);
            Assert.True(layer.IsAuthenticated);
            Assert.DoesNotContain(SmimeIssue.UnauthenticatedEncryption, layer.Issues);
            Assert.Contains("gcm secret", r.InnerItem!.TextBody!.ReadText());
        }

        // A flipped ciphertext byte fails the tag: nothing is released.
        var part = (MimePart)MimeEntity.Parse(wire.ToArray());
        byte[] cms = part.GetDecodedContent();
        AuthEnvelopedData ad = AuthEnvelopedData.Parse(cms);
        int at = cms.AsSpan().IndexOf(ad.EncryptedContent.Span);
        cms[at + 1] ^= 0x01;
        part.SetDecodedContent(cms);
        SmimeResult bad = SmimeProcessor.Open(MimeEntity.Parse(part.ToArray()), new SmimeOpenOptions { DecryptionKeys = [Bob.Decrypt()] });
        Assert.Equal(SmimeStatus.DecryptionFailed, bad.Layers[0].Status);
        Assert.Null(bad.InnerItem);
    }

    [Fact]
    public void EcdhEnvelopedRoundTrip()
    {
        MimePart enc = SmimeEncryptor.Encrypt(SmimeIdentity.TextBody("ecdh secret\r\n"), new SmimeEncryptOptions { Recipients = [Carol.Certificate] });
        SmimeResult r = SmimeProcessor.Open(Transport(enc), new SmimeOpenOptions { DecryptionKeys = [Carol.Decrypt()] });
        Assert.Equal(SmimeStatus.Decrypted, r.Layers[0].Status);
        Assert.Contains("ecdh secret", r.InnerItem!.TextBody!.ReadText());
    }

    [Fact]
    public void WrongRecipientAndWrongKeyAreToldApart()
    {
        MimeEntity wire = Transport(SmimeEncryptor.Encrypt(SmimeIdentity.TextBody(), new SmimeEncryptOptions { Recipients = [Bob.Certificate] }));
        SmimeResult notForAlice = SmimeProcessor.Open(wire, new SmimeOpenOptions { DecryptionKeys = [Alice.Decrypt()] });
        Assert.Equal(SmimeStatus.NoMatchingRecipient, notForAlice.Layers[0].Status);
        Assert.Same(wire, notForAlice.Content);
    }

    [Fact]
    public void EncryptionRefusesWeakRecipientKeys()
    {
        SmimeIdentity small = SmimeIdentity.Rsa("small2@example.test", 1024);
        Assert.Throws<PolicyViolationException>(() => SmimeEncryptor.Encrypt(SmimeIdentity.TextBody(), new SmimeEncryptOptions { Recipients = [small.Certificate] }));
        Assert.Throws<ArgumentException>(() => SmimeEncryptor.Encrypt(SmimeIdentity.TextBody(), new SmimeEncryptOptions { Recipients = [Eve.Certificate] }));
    }

    [Fact]
    public void SignedThenEncryptedThenSignedUnwrapsAllThree()
    {
        // Triple wrap (RFC 8551 §3.6): sign, encrypt, sign again.
        MimeMultipart inner = SmimeSigner.SignDetached(SmimeIdentity.TextBody("triple\r\n"), Sign(Alice));
        MimePart enc = SmimeEncryptor.Encrypt(inner, new SmimeEncryptOptions { Recipients = [Bob.Certificate] });
        MimeMultipart outer = SmimeSigner.SignDetached(enc, new SmimeSignOptions { Signers = [Carol.Signer()], Boundary = "outer-b" });
        MimeEntity wire = Transport(outer, "carol@example.test");

        SmimeResult r = SmimeProcessor.Open(wire, new SmimeOpenOptions { DecryptionKeys = [Bob.Decrypt()] });
        Assert.Equal(3, r.Layers.Count);
        Assert.Equal([SmimeLayerKind.SignedDetached, SmimeLayerKind.Enveloped, SmimeLayerKind.SignedDetached], r.Layers.Select(static l => l.Kind));
        Assert.True(r.Succeeded);
        Assert.Equal(2, r.Signers.Count());
        Assert.Equal(["carol@example.test", "alice@example.test"], r.Signers.Select(static s => s.CertificateAddresses[0]));
        Assert.Contains("triple", r.InnerItem!.TextBody!.ReadText());

        // Without Bob's key it stops at the encrypted layer, with the outer signature still reported.
        SmimeResult closed = SmimeProcessor.Open(wire);
        Assert.Equal(2, closed.Layers.Count);
        Assert.True(closed.Layers[0].Succeeded);
        Assert.Equal(SmimeStatus.NoDecryptionKey, closed.Layers[1].Status);
        Assert.False(closed.Succeeded);
    }

    [Fact]
    public void DepthLimitStopsRunawayNesting()
    {
        MimeEntity e = SmimeIdentity.TextBody();
        for (int i = 0; i < 4; i++)
        {
            e = SmimeSigner.SignOpaque(e, Sign(Alice));
        }

        SmimeResult r = SmimeProcessor.Open(MimeEntity.Parse(e.ToArray()), new SmimeOpenOptions { MaxDepth = 2 });
        Assert.Equal(2, r.Layers.Count);
        Assert.Contains(SmimeIssue.DepthLimitReached, r.Issues);
    }

    // ---- structure ----

    [Fact]
    public void SignedPartInsideAnUnsignedWrapperListsTheUnsignedSiblings()
    {
        var wrapper = new MimeMultipart("mixed", "wrap-b");
        wrapper.Add(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)));
        var extra = new MimePart("text/plain");
        extra.SetDecodedContent("added later\r\n"u8, RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.SevenBit);
        wrapper.Add(extra);
        SmimeResult r = SmimeProcessor.Open(Transport(wrapper));
        Assert.True(r.Succeeded);
        Assert.Contains(SmimeIssue.SignedPartInUnsignedWrapper, r.Issues);
        Assert.Equal(["2"], r.Outside!.UnsignedParts);
    }

    [Fact]
    public void SignedMessageRfc822ProtectsItsHeaders()
    {
        var inner = MimeEntity.Parse("From: alice@example.test\r\nSubject: Protected subject\r\nContent-Type: text/plain\r\n\r\nbody\r\n"u8.ToArray());
        var msg = new MimeMessagePart { Message = inner };
        SmimeResult r = SmimeProcessor.Open(Transport(SmimeSigner.SignDetached(msg, Sign(Alice))));
        Assert.True(r.Succeeded);
        Assert.Contains("Subject", r.Outside!.ProtectedHeaders);
    }

    [Fact]
    public void LenientTypeSpellingsAreRecognised()
    {
        MimePart p = SmimeEncryptor.Encrypt(SmimeIdentity.TextBody(), new SmimeEncryptOptions { Recipients = [Bob.Certificate] });
        var keys = new SmimeOpenOptions { DecryptionKeys = [Bob.Decrypt()] };

        p.Headers.Set("Content-Type", "application/x-pkcs7-mime; name=smime.p7m");
        SmimeResult noType = SmimeProcessor.Open(MimeEntity.Parse(p.ToArray()), keys);
        Assert.Equal(SmimeStatus.Decrypted, noType.Layers[0].Status);
        Assert.Contains(SmimeIssue.SmimeTypeMissing, noType.Layers[0].Issues);

        p.Headers.Set("Content-Type", "application/octet-stream; name=\"smime.p7m\"");
        SmimeResult byName = SmimeProcessor.Open(MimeEntity.Parse(p.ToArray()), keys);
        Assert.Equal(SmimeStatus.Decrypted, byName.Layers[0].Status);
        Assert.Contains(SmimeIssue.RecognisedByFileName, byName.Layers[0].Issues);

        p.Headers.Set("Content-Type", "application/pkcs7-mime; smime-type=signed-data");
        SmimeResult mismatch = SmimeProcessor.Open(MimeEntity.Parse(p.ToArray()), keys);
        Assert.Equal(SmimeStatus.Decrypted, mismatch.Layers[0].Status);
        Assert.Contains(SmimeIssue.SmimeTypeMismatch, mismatch.Layers[0].Issues);
    }

    // ---- Outlook items (MS-OXOSMIME) ----

    private static MapiProperty Str(ushort id, string v) => MapiProperty.Create(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    private static MapiMessage OutlookItem(string messageClass, byte[] attachment, string mimeTag, string? contentTypeProperty)
    {
        var m = new MapiMessage();
        m.Properties.Add(Str(MapiPid.MessageClass, messageClass));
        m.Properties.Add(Str(MapiPid.Subject, "Outlook S/MIME item"));
        if (contentTypeProperty is not null)
        {
            m.Properties.Add(MapiProperty.Create(new MapiTag(0x8000, MapiType.UnicodeString), Encoding.Unicode.GetBytes(contentTypeProperty),
                new MapiNamedProperty(MapiPropertySets.InternetHeaders, "Content-Type")));
        }

        var a = new MapiAttachment(m);
        a.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachMethod, MapiType.Integer32), BitConverter.GetBytes(1)));
        a.Properties.Add(Str(MapiPid.AttachMimeTag, mimeTag));
        a.Properties.Add(Str(MapiPid.AttachLongFilename, "smime.p7m"));
        a.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachDataBinary, MapiType.Binary), attachment));
        m.AddAttachment(a);
        return m;
    }

    private MapiMessage ThroughMsg(MapiMessage m)
    {
        byte[] bytes = MsgWriter.ToArray(m);
        MailContainer c = Mail.Open(new MemoryStream(bytes));
        _open.Add(c); // the message reads its values lazily from the open file
        return ((MapiMailItem)c.Enumerate().Single()).Message;
    }

    [Fact]
    public void OutlookClearSignedItemVerifiesThroughMsgAndKeepsTheSignedBytes()
    {
        MimeMultipart signed = SmimeSigner.SignDetached(SmimeIdentity.TextBody("outlook clear\r\n"), Sign(Alice));
        MapiMessage item = OutlookItem("IPM.Note.SMIME.MultipartSigned", signed.ToArray(), "multipart/signed", null);
        foreach (MapiMessage m in new[] { item, ThroughMsg(item) })
        {
            SmimeResult r = SmimeOutlook.Open(m);
            Assert.True(r.Succeeded, r.Layers.Count > 0 ? r.Layers[0].Detail : null);
            Assert.Equal(SmimeLayerKind.SignedDetached, r.Layers[0].Kind);
            Assert.Contains("outlook clear", r.InnerItem!.TextBody!.ReadText());
        }
    }

    [Fact]
    public void OutlookOpaqueAndEncryptedItemsOpenThroughTheSamePath()
    {
        MimePart opaque = SmimeSigner.SignOpaque(SmimeIdentity.TextBody("outlook opaque\r\n"), Sign(Alice));
        MapiMessage signedItem = OutlookItem("IPM.Note.SMIME", opaque.GetDecodedContent(), "application/x-pkcs7-mime", "application/x-pkcs7-mime; smime-type=signed-data; name=smime.p7m");
        SmimeResult sr = SmimeOutlook.Open(ThroughMsg(signedItem));
        Assert.True(sr.Succeeded);
        Assert.Contains("outlook opaque", sr.InnerItem!.TextBody!.ReadText());

        MimePart enc = SmimeEncryptor.Encrypt(SmimeIdentity.TextBody("outlook secret\r\n"), new SmimeEncryptOptions { Recipients = [Bob.Certificate] });
        // Without the named Content-Type property the media type comes from the attachment's MIME tag and name (MS-OXOSMIME 2.1.3.2.2.3).
        MapiMessage encItem = OutlookItem("IPM.Note.SMIME", enc.GetDecodedContent(), "application/pkcs7-mime", null);
        SmimeResult er = SmimeOutlook.Open(ThroughMsg(encItem), new SmimeOpenOptions { DecryptionKeys = [Bob.Decrypt()] });
        Assert.True(er.Succeeded, er.Layers.Count > 0 ? er.Layers[0].Detail : null);
        Assert.Contains("outlook secret", er.InnerItem!.TextBody!.ReadText());
        Assert.Contains(SmimeIssue.SmimeTypeMissing, er.Layers[0].Issues);
    }

    [Fact]
    public void OutlookItemOfAnotherClassIsNotSmime()
    {
        var m = new MapiMessage();
        m.Properties.Add(Str(MapiPid.MessageClass, "IPM.Note"));
        Assert.False(SmimeOutlook.Open(m).IsSmime);
        Assert.True(SmimeOutlook.IsSmimeClass("IPM.InfoPathForm.Foo.SMIME.MultipartSigned"));
        Assert.False(SmimeOutlook.IsSmimeClass("IPM.Note.SMIMEish"));
    }

    [Fact]
    public void CompressedDataLayerIsInflatedAndBombsAreStopped()
    {
        byte[] inner = SmimeIdentity.TextBody("compressed text\r\n").ToArray();
        MimePart comp = SmimeContent(CompressedData.Create(inner).Encoded.ToArray(), "compressed-data");
        SmimeResult r = SmimeProcessor.Open(MimeEntity.Parse(comp.ToArray()));
        Assert.Equal(SmimeStatus.Decompressed, r.Layers[0].Status);
        Assert.Contains("compressed text", r.InnerItem!.TextBody!.ReadText());

        byte[] bomb = CompressedData.Create(new byte[20_000_000]).Encoded.ToArray();
        SmimeResult b = SmimeProcessor.Open(MimeEntity.Parse(SmimeContent(bomb, "compressed-data").ToArray()));
        Assert.Equal(SmimeStatus.LimitExceeded, b.Layers[0].Status);
        Assert.Null(b.InnerItem);
    }

    private static MimePart SmimeContent(byte[] cms, string smimeType)
    {
        var p = new MimePart("application/pkcs7-mime; smime-type=" + smimeType);
        p.SetDecodedContent(cms);
        return p;
    }

    [Fact]
    public void TwoSignersAreReportedIndividually()
    {
        var both = new SmimeSignOptions { Signers = [Alice.Signer(), Carol.Signer()], Boundary = "two-b" };
        SmimeResult r = SmimeProcessor.Open(Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), both)));
        SmimeSignedLayer layer = (SmimeSignedLayer)r.Layers[0];
        Assert.Equal(2, layer.Signers.Count);
        Assert.All(layer.Signers, s => Assert.True(s.IsValid));
        Assert.Equal([SmimeSenderMatch.Match, SmimeSenderMatch.Mismatch], layer.Signers.Select(static s => s.SenderMatch));
    }

    [Fact]
    public void CertificatesOnlyMessageIsRecognised()
    {
        SignedData sd = SignedData.Create(ReadOnlySpan<byte>.Empty, [], new SignedDataOptions { Certificates = [Alice.Certificate], Detached = true });
        SmimeResult r = SmimeProcessor.Open(MimeEntity.Parse(SmimeContent(sd.Encoded.ToArray(), "certs-only").ToArray()));
        Assert.Equal(SmimeLayerKind.CertsOnly, r.Layers[0].Kind);
        Assert.Equal(SmimeStatus.CertificatesOnly, r.Layers[0].Status);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void NonSmimeMessageGivesEmptyResult()
    {
        SmimeResult r = SmimeProcessor.Open(MimeEntity.Parse("Subject: x\r\n\r\nplain\r\n"u8.ToArray()));
        Assert.False(r.IsSmime);
        Assert.False(r.Succeeded);
        Assert.Null(r.Outside);
    }

    [Fact]
    public void PolicyWithoutReferenceTimeIsRefused() =>
        Assert.Throws<ArgumentException>(() => SmimeProcessor.Open(MimeEntity.Parse("Subject: x\r\n\r\n"u8.ToArray()), new SmimeOpenOptions { Policy = CryptoPolicy.Standard }));
}
