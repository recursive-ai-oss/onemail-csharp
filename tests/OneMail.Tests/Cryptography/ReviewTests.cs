using System.Text;
using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Validation;
using RecursiveAI.OneCrypto.Wire.Cms;
using RecursiveAI.OneMail.Cryptography.Arc;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Cryptography.Smime;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>Opens S/MIME with untrusted signers allowed, for the tests that are about signatures and structure rather than trust.</summary>
internal static class SmimeTestOptions
{
    public static SmimeOpenOptions Lax(SmimeOpenOptions? o) => new()
    {
        DecryptionKeys = o?.DecryptionKeys ?? [],
        Certificates = o?.Certificates ?? [],
        Chain = o?.Chain,
        Policy = o?.Policy,
        ReferenceTime = o?.ReferenceTime,
        AllowLegacyCiphers = o?.AllowLegacyCiphers ?? false,
        AllowPkcs1V15KeyTransport = o?.AllowPkcs1V15KeyTransport ?? true,
        MaxDepth = o?.MaxDepth ?? 8,
        Limits = o?.Limits ?? new SmimeOpenOptions().Limits,
        AllowUntrustedSigner = true,
    };

    public static SmimeResult Open(MimeEntity message, SmimeOpenOptions? o = null) => SmimeProcessor.Open(message, Lax(o));

    public static SmimeResult Open(byte[] message, SmimeOpenOptions? o = null) => SmimeProcessor.Open(message, Lax(o));
}

/// <summary>Cases found by reviewing the DKIM, ARC and S/MIME code for results that say more than was checked, and for hostile input.</summary>
public class DkimReviewTests
{
    private static async Task<DkimSignatureResult> VerifyOne(byte[] message, DkimVerifierOptions? options = null)
    {
        var verifier = new DkimVerifier(options ?? DkimFixtures.Options());
        return (await verifier.VerifyAsync(message, TestContext.Current.CancellationToken)).Single();
    }

    [Fact]
    public async Task ASecondFromHeaderAboveTheSignedOneIsNotAPass()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key).Sign(DkimFixtures.Message());
        // The signature covers the From nearest the body; a reader that shows the topmost From would show this one.
        byte[] forged = [.. "From: Chief Executive <ceo@example.net>\r\n"u8.ToArray(), .. signed];

        DkimSignatureResult r = await VerifyOne(forged);
        Assert.Equal(DkimStatus.Fail, r.Status);
        Assert.Equal(DkimReason.UnsignedFromHeader, r.Reason);
        Assert.Contains("From", r.UnsignedHeaderInstances);

        // A caller that wants the detail anyway gets a pass that names what is exposed.
        DkimSignatureResult allowed = await VerifyOne(forged, new DkimVerifierOptions { Resolver = DkimFixtures.Dns(), TimeProvider = new DkimVectorTests.FixedTime(1_700_000_100), AllowUnsignedFromInstances = true });
        Assert.True(allowed.IsPass);
        Assert.True(allowed.Warnings.HasFlag(DkimWarnings.UnsignedHeaderInstances));
        Assert.Contains("From", allowed.UnsignedHeaderInstances);
    }

    [Fact]
    public async Task AnAddedUnsignedHeaderOtherThanFromPassesAndIsListed()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key).Sign(DkimFixtures.Message());
        byte[] added = [.. "Subject: Urgent wire transfer\r\n"u8.ToArray(), .. signed];

        DkimSignatureResult r = await VerifyOne(added);
        Assert.True(r.IsPass, r.ToString());
        Assert.Contains("Subject", r.UnsignedHeaderInstances);
        Assert.True(r.Warnings.HasFlag(DkimWarnings.UnsignedHeaderInstances));
    }

    [Fact]
    public async Task OverSignedFromStillCatchesAnAddedFrom()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key, overSign: true).Sign(DkimFixtures.Message());
        byte[] forged = [.. "From: Chief Executive <ceo@example.net>\r\n"u8.ToArray(), .. signed];

        DkimSignatureResult r = await VerifyOne(forged);
        Assert.False(r.IsPass);
        Assert.Equal(DkimStatus.Fail, r.Status);
    }

    [Fact]
    public async Task AnUntouchedMessageHasNoUnsignedInstances()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        DkimSignatureResult r = await VerifyOne(DkimFixtures.Signer(key).Sign(DkimFixtures.Message()));
        Assert.True(r.IsPass);
        Assert.Empty(r.UnsignedHeaderInstances);
        Assert.False(r.Warnings.HasFlag(DkimWarnings.UnsignedHeaderInstances));
    }

    [Fact]
    public async Task AppendedContentAfterABodyLengthTagIsNotAPassByDefault()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key, length: true).Sign(DkimFixtures.Message());
        byte[] appended = [.. signed, .. "Send the money to account 12345.\r\n"u8.ToArray()];

        DkimSignatureResult r = await VerifyOne(appended);
        Assert.Equal(DkimStatus.Fail, r.Status);
        Assert.Equal(DkimReason.BodyLengthLimited, r.Reason);

        // The tag alone, with nothing after the signed part, is fine.
        Assert.True((await VerifyOne(signed)).IsPass);

        DkimSignatureResult allowed = await VerifyOne(appended, new DkimVerifierOptions { Resolver = DkimFixtures.Dns(), TimeProvider = new DkimVectorTests.FixedTime(1_700_000_100), AllowBodyLengthLimit = true });
        Assert.True(allowed.IsPass);
        Assert.True(allowed.BodyHasUnsignedContent);
    }

    [Fact]
    public async Task ABodyLengthTagThatCoversTheWholeBodyPasses()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        DkimSignatureResult r = await VerifyOne(DkimFixtures.Signer(key, length: true).Sign(DkimFixtures.Message()));
        Assert.True(r.IsPass);
        Assert.False(r.BodyHasUnsignedContent);
        Assert.True(r.Warnings.HasFlag(DkimWarnings.BodyLengthLimit));
    }

    [Fact]
    public async Task AMessageWithHugeNumbersOfHeaderFieldsIsNotVerified()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key).Sign(DkimFixtures.Message());
        var sb = new StringBuilder();
        for (int i = 0; i < 50_000; i++)
        {
            sb.Append("X: y\r\n");
        }

        byte[] hostile = [.. Encoding.ASCII.GetBytes(sb.ToString()), .. signed];
        DkimSignatureResult r = await VerifyOne(hostile);
        Assert.Equal(DkimStatus.Neutral, r.Status);
        Assert.Equal(DkimReason.TooManyHeaders, r.Reason);

        ArcVerificationResult arc = await new ArcVerifier(DkimFixtures.Options()).VerifyAsync(hostile, TestContext.Current.CancellationToken);
        Assert.NotEqual(ArcChainStatus.Pass, arc.Status);

        // With the limit lifted the same message verifies: the limit, not the size, is what stopped it.
        DkimSignatureResult lifted = await VerifyOne(hostile, new DkimVerifierOptions { Resolver = DkimFixtures.Dns(), TimeProvider = new DkimVectorTests.FixedTime(1_700_000_100), MaxHeaderFields = 1_000_000 });
        Assert.True(lifted.IsPass, lifted.ToString());
    }

    [Fact]
    public void HeaderNamesThatCouldInjectTagsAreRefusedWhenSigning()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        foreach (string bad in new[] { "X:y", "X;l=0", "X y", "", "X=" })
        {
            Assert.Throws<ArgumentException>(() => new DkimSigner(key, new DkimSignerOptions { Domain = "example.com", Selector = "rsa", HeadersToSign = ["From", bad] }));
        }
    }

    [Fact]
    public async Task RepeatedNamesInHListTakeSuccessiveInstancesFromTheBottom()
    {
        // The header index must keep RFC 6376 §5.4.2's order: the same name twice takes the nearest, then the next one up.
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] message = DkimFixtures.Message(extra: "X-Tag: first\r\nX-Tag: second\r\n");
        var signer = new DkimSigner(key, new DkimSignerOptions
        {
            Domain = "example.com",
            Selector = "rsa",
            HeadersToSign = ["From", "X-Tag", "Subject"],
            TimeProvider = new DkimVectorTests.FixedTime(1_700_000_000),
        });
        byte[] signed = signer.Sign(message);
        Assert.True((await VerifyOne(signed)).IsPass);
        byte[] swapped = DkimFixtures.Replace(signed, "X-Tag: first", "X-Tag: other");
        Assert.Equal(DkimStatus.Fail, (await VerifyOne(swapped)).Status);
    }
}

public sealed class SmimeReviewTests
{
    private static readonly SmimeIdentity Alice = SmimeIdentity.Rsa("alice-review@example.test");
    private static readonly SmimeIdentity Bob = SmimeIdentity.Rsa("bob-review@example.test");

    private static MimeEntity Transport(MimeEntity e, string from)
    {
        e.Headers.Add("MIME-Version", "1.0");
        e.Headers.Add("From", from);
        e.Headers.Add("To", "bob-review@example.test");
        e.Headers.Add("Subject", "Review");
        return MimeEntity.Parse(e.ToArray());
    }

    private static SmimeSignOptions Sign(SmimeIdentity who) => new() { Signers = [who.Signer()], SigningTime = SmimeIdentity.Now, Boundary = "review-boundary" };

    private static SmimeOpenOptions Anchored => new()
    {
        Chain = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(SmimeIdentity.Root)], ReferenceTime = SmimeIdentity.Now },
    };

    [Fact]
    public void ASignatureNobodyVouchedForIsValidButNotTrusted()
    {
        // Anyone can make a certificate that names alice@..., so a pass without a trust anchor must not read as "from Alice".
        MimeEntity wire = Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), new SmimeSignOptions { Signers = [Alice.Signer()], Certificates = [SmimeIdentity.Root], Boundary = "b" }), "alice-review@example.test");

        // By default an untrusted signer is not a success, and the status says why.
        SmimeResult none = SmimeProcessor.Open(wire);
        Assert.False(none.Succeeded);
        Assert.Equal(SmimeStatus.UntrustedChain, none.Layers.Single().Status);
        Assert.Contains("untrusted", none.Layers.Single().Detail, StringComparison.Ordinal);
        Assert.True(none.Signers.Single().IsValid);
        Assert.Equal(SmimeChainStatus.NotChecked, none.Signers.Single().Chain);
        Assert.False(none.Signers.Single().IsTrusted);
        Assert.False(none.IsTrusted);

        // With the opt-out it succeeds, and is still not trusted.
        SmimeResult lax = SmimeProcessor.Open(wire, new SmimeOpenOptions { AllowUntrustedSigner = true });
        Assert.True(lax.Succeeded);
        Assert.False(lax.IsTrusted);

        SmimeResult anchored = SmimeProcessor.Open(wire, Anchored);
        Assert.True(anchored.Signers.Single().IsTrusted);
        Assert.True(anchored.IsTrusted);
    }

    [Fact]
    public void ASignerIsMatchedAgainstTheSignedFromNotTheOuterOne()
    {
        // The signed message says it is from the chief executive; the unsigned envelope says Alice, whose certificate signed it.
        var inner = MimeEntity.Parse("From: ceo@example.test\r\nContent-Type: text/plain\r\n\r\nPlease pay.\r\n"u8.ToArray());
        MimePart opaque = SmimeSigner.SignOpaque(inner, Sign(Alice));
        SmimeSignerResult s = SmimeProcessor.Open(Transport(opaque, "alice-review@example.test")).Signers.Single();
        Assert.True(s.IsValid);
        Assert.Equal(SmimeSenderMatch.Mismatch, s.SenderMatch);

        var matching = MimeEntity.Parse("From: alice-review@example.test\r\nContent-Type: text/plain\r\n\r\nHello.\r\n"u8.ToArray());
        SmimeSignerResult ok = SmimeProcessor.Open(Transport(SmimeSigner.SignOpaque(matching, Sign(Alice)), "somebody@example.test")).Signers.Single();
        Assert.Equal(SmimeSenderMatch.Match, ok.SenderMatch);
    }

    [Fact]
    public void AFromThatNamesSomeoneElseAsWellIsAMismatch()
    {
        MimeEntity wire = Transport(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)), "alice-review@example.test, ceo@example.test");
        Assert.Equal(SmimeSenderMatch.Mismatch, SmimeProcessor.Open(wire).Signers.Single().SenderMatch);
    }

    [Fact]
    public void ASignedPartInAnUnsignedWrapperIsNotASuccessfulMessage()
    {
        var wrapper = new MimeMultipart("mixed", "wrap-review");
        wrapper.Add(SmimeSigner.SignDetached(SmimeIdentity.TextBody(), Sign(Alice)));
        var extra = new MimePart("text/plain");
        extra.SetDecodedContent("added later\r\n"u8, RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.SevenBit);
        wrapper.Add(extra);
        SmimeResult r = SmimeProcessor.Open(Transport(wrapper, "alice-review@example.test"), Anchored);
        Assert.False(r.Succeeded);
        Assert.False(r.IsTrusted);
        Assert.True(r.Layers.Single().Succeeded);
    }

    [Fact]
    public void EveryFailureAfterARecipientMatchedLooksTheSame()
    {
        // CBC enveloped data: damage the encrypted key or any of the last cipher blocks, and the outcome is a decryption failure with one message,
        // or (for a change that keeps the padding valid) a decryption of different text. Never a different status or text for a padding error.
        MimePart enc = SmimeEncryptor.Encrypt(SmimeIdentity.TextBody("secret text for the padding check\r\n"), new SmimeEncryptOptions { Recipients = [Bob.Certificate] });
        MimeEntity wire = Transport(enc, "alice-review@example.test");
        var part = (MimePart)MimeEntity.Parse(wire.ToArray());
        byte[] cms = part.GetDecodedContent();
        EnvelopedData ed = EnvelopedData.Parse(cms);
        int content = cms.AsSpan().IndexOf(ed.EncryptedContent.Span);
        Assert.True(content > 0);

        var statuses = new HashSet<SmimeStatus>();
        var details = new HashSet<string?>();
        var keys = new SmimeOpenOptions { DecryptionKeys = [Bob.Decrypt()] };
        for (int i = 0; i < ed.EncryptedContent.Length; i++)
        {
            byte[] damaged = (byte[])cms.Clone();
            damaged[content + i] ^= 0x5a;
            var p = (MimePart)MimeEntity.Parse(wire.ToArray());
            p.SetDecodedContent(damaged);
            SmimeLayer layer = SmimeProcessor.Open(MimeEntity.Parse(p.ToArray()), keys).Layers[0];
            statuses.Add(layer.Status);
            if (layer.Status != SmimeStatus.Decrypted)
            {
                details.Add(layer.Detail);
            }
        }

        Assert.True(statuses.IsSubsetOf([SmimeStatus.Decrypted, SmimeStatus.DecryptionFailed]), string.Join(",", statuses));
        Assert.True(details.Count <= 1, string.Join(" | ", details));

        // The wrapped content key: every flipped byte is also one failure.
        // (the encrypted key is the tail of the recipient info)
        ReadOnlyMemory<byte> ri = ed.RecipientInfos[0].Encoded;
        int keyAt = cms.AsSpan().IndexOf(ri.Span) + ri.Length - 200;
        Assert.True(keyAt > 0);
        var keyStatuses = new HashSet<(SmimeStatus, string?)>();
        for (int i = 0; i < 190; i += 7)
        {
            byte[] damaged = (byte[])cms.Clone();
            damaged[keyAt + i] ^= 0x01;
            var p = (MimePart)MimeEntity.Parse(wire.ToArray());
            p.SetDecodedContent(damaged);
            SmimeLayer layer = SmimeProcessor.Open(MimeEntity.Parse(p.ToArray()), keys).Layers[0];
            keyStatuses.Add((layer.Status, layer.Detail));
        }

        Assert.Single(keyStatuses);
        Assert.Equal(SmimeStatus.DecryptionFailed, keyStatuses.Single().Item1);
    }

    [Fact]
    public void LegacyContentCiphersAreRefusedForNewMessages()
    {
        var options = new SmimeEncryptOptions { Recipients = [Bob.Certificate], ContentEncryption = CmsOids.TripleDesCbc };
        Assert.ThrowsAny<Exception>(() => SmimeEncryptor.Encrypt(SmimeIdentity.TextBody(), options));
    }

    [Fact]
    public void Pkcs1V15KeyTransportNeedsTheExplicitFlagToWrite()
    {
        var options = new SmimeEncryptOptions { Recipients = [Bob.Certificate], KeyTransport = KeyTransportScheme.Pkcs1V15 };
        Assert.ThrowsAny<Exception>(() => SmimeEncryptor.Encrypt(SmimeIdentity.TextBody(), options));
    }

    [Fact]
    public void DeeplyNestedWrappersStopAtTheDepthLimit()
    {
        MimeEntity e = SmimeIdentity.TextBody("deep\r\n");
        for (int i = 0; i < 20; i++)
        {
            e = SmimeSigner.SignOpaque(e, Sign(Alice));
        }

        SmimeResult r = SmimeProcessor.Open(Transport(e, "alice-review@example.test"));
        Assert.Equal(8, r.Layers.Count);
        Assert.Contains(SmimeIssue.DepthLimitReached, r.Issues);
    }

    [Fact]
    public void HostileNestingAndStructureEndAsResultsNotExceptions()
    {
        // 3000 nested multiparts, then a signed part buried at the bottom: the reader's depth limit keeps the walk short.
        var sb = new StringBuilder("MIME-Version: 1.0\r\nFrom: a@example.test\r\n");
        for (int i = 0; i < 3000; i++)
        {
            sb.Append("Content-Type: multipart/mixed; boundary=b").Append(i).Append("\r\n\r\n--b").Append(i).Append("\r\n");
        }

        sb.Append("Content-Type: application/pkcs7-mime; smime-type=enveloped-data\r\n\r\nMAA=\r\n");
        SmimeResult r = SmimeProcessor.Open(Encoding.Latin1.GetBytes(sb.ToString()), new SmimeOpenOptions { DecryptionKeys = [Bob.Decrypt()] });
        Assert.False(r.Succeeded);

        // A multipart/signed with a thousand parts is refused for its structure, whatever the parts hold.
        var many = new StringBuilder("Content-Type: multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=m\r\n\r\n");
        for (int i = 0; i < 1000; i++)
        {
            many.Append("--m\r\n\r\npart\r\n");
        }

        many.Append("--m--\r\n");
        SmimeResult s = SmimeProcessor.Open(Encoding.Latin1.GetBytes(many.ToString()));
        Assert.Equal(SmimeStatus.StructureInvalid, s.Layers.Single().Status);
    }
}
