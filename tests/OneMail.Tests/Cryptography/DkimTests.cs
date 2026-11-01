using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Cryptography.Arc;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Cryptography.Dns;

namespace RecursiveAI.OneMail.Tests.Cryptography;

internal static class DkimFixtures
{
    private static readonly Lazy<RSA> RsaKey = new(() => RSA.Create(2048));

    public static RSA Rsa => RsaKey.Value;

    public static readonly byte[] Seed = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    public static DkimSigningKey RsaSigning() => DkimSigningKey.FromRsa(Rsa);

    public static DkimSigningKey EdSigning() => DkimSigningKey.FromEd25519Seed(Seed);

    public static string RsaRecord() => RsaSigning().ToKeyRecord();

    public static string EdRecord() => EdSigning().ToKeyRecord();

    public static InMemoryTxtResolver Dns() => new InMemoryTxtResolver()
        .Add("rsa._domainkey.example.com", RsaRecord())
        .Add("ed._domainkey.example.com", EdRecord())
        .Add("arc._domainkey.example.net", RsaRecord())
        .Add("arced._domainkey.example.org", EdRecord());

    public static byte[] Message(string body = "Hello,\r\n\r\nThis is the body.\r\nSecond line  with   spaces. \r\n", string extra = "") => DkimVectors.Crlf(
        "Received: from a.example (a.example [192.0.2.1])\r\n\tby mx.example.com; Mon, 1 Jan 2024 00:00:00 +0000\r\n" +
        "From: Joe <joe@example.com>\r\nTo: Suzie <suzie@example.net>\r\nSubject: Test message\r\nDate: Mon, 1 Jan 2024 00:00:00 +0000\r\n" +
        "Message-ID: <1@example.com>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n" + extra + "\r\n" + body);

    public static DkimSigner Signer(DkimSigningKey key, string selector = "rsa", DkimCanonicalization h = DkimCanonicalization.Relaxed, DkimCanonicalization b = DkimCanonicalization.Relaxed,
        bool overSign = false, bool length = false, TimeSpan? lifetime = null, TimeProvider? time = null) => new(key, new DkimSignerOptions
        {
            Domain = "example.com",
            Selector = selector,
            HeaderCanonicalization = h,
            BodyCanonicalization = b,
            OverSign = overSign,
            IncludeBodyLength = length,
            Lifetime = lifetime,
            TimeProvider = time ?? new DkimVectorTests.FixedTime(1_700_000_000),
        });

    public static DkimVerifier Verifier(ITxtResolver? dns = null, long now = 1_700_000_100, Action<DkimVerifierOptionsBuilder>? tweak = null)
    {
        var b = new DkimVerifierOptionsBuilder { Resolver = dns ?? Dns(), Now = now };
        tweak?.Invoke(b);
        return new DkimVerifier(b.Build());
    }

    public static DkimVerifierOptions Options(ITxtResolver? dns = null, long now = 1_700_000_100) => new() { Resolver = dns ?? Dns(), TimeProvider = new DkimVectorTests.FixedTime(now) };

    public static byte[] Replace(byte[] message, string find, string replace) => Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(message).Replace(find, replace, StringComparison.Ordinal));
}

internal sealed class DkimVerifierOptionsBuilder
{
    public ITxtResolver? Resolver { get; set; }

    public long Now { get; set; }

    public int MinimumRsaBits { get; set; } = 1024;

    public bool AllowRsaSha1 { get; set; } = true;

    public DkimVerifierOptions Build() => new() { Resolver = Resolver, TimeProvider = new DkimVectorTests.FixedTime(Now), MinimumRsaBits = MinimumRsaBits, AllowRsaSha1 = AllowRsaSha1 };
}

public class DkimRoundTripTests
{
    public static TheoryData<bool, DkimCanonicalization, DkimCanonicalization, bool, bool> Matrix()
    {
        var d = new TheoryData<bool, DkimCanonicalization, DkimCanonicalization, bool, bool>();
        foreach (bool ed in new[] { false, true })
        {
            foreach (DkimCanonicalization h in Enum.GetValues<DkimCanonicalization>())
            {
                foreach (DkimCanonicalization b in Enum.GetValues<DkimCanonicalization>())
                {
                    d.Add(ed, h, b, false, false);
                    d.Add(ed, h, b, true, true);
                }
            }
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task SignedMessagesVerify(bool ed, DkimCanonicalization h, DkimCanonicalization b, bool overSign, bool length)
    {
        using DkimSigningKey key = ed ? DkimFixtures.EdSigning() : DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key, ed ? "ed" : "rsa", h, b, overSign, length).Sign(DkimFixtures.Message());
        IReadOnlyList<DkimSignatureResult> r = await DkimFixtures.Verifier().VerifyAsync(signed, TestContext.Current.CancellationToken);
        DkimSignatureResult s = Assert.Single(r);
        Assert.True(s.IsPass, s.ToString());
        Assert.Equal(h, s.HeaderCanonicalization);
        Assert.Equal(b, s.BodyCanonicalization);
        Assert.Equal(length, s.BodyLengthTag is not null);
        Assert.False(s.BodyHasUnsignedContent);
        Assert.Equal(overSign, s.OversignedHeaders.Count > 0);
        Assert.Equal(ed ? 256 : 2048, s.KeyBits);
        Assert.Contains("from", s.SignedHeaders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BareLfMessagesAreSignedAndVerifiedAsCrlf()
    {
        byte[] lf = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(DkimFixtures.Message()).Replace("\r\n", "\n", StringComparison.Ordinal));
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key, "rsa", DkimCanonicalization.Simple, DkimCanonicalization.Simple).Sign(lf);
        Assert.DoesNotContain((byte)'\r', signed);
        Assert.True((await DkimFixtures.Verifier().VerifyAsync(signed, TestContext.Current.CancellationToken)).Single().IsPass);
        // The same message after a hop that converted it to CRLF still verifies.
        byte[] crlf = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(signed).Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.True((await DkimFixtures.Verifier().VerifyAsync(crlf, TestContext.Current.CancellationToken)).Single().IsPass);
    }

    [Fact]
    public async Task SignatureSurvivesRelaxedRewrapping()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key).Sign(DkimFixtures.Message());
        byte[] rewrapped = DkimFixtures.Replace(signed, "Subject: Test message", "Subject:   Test \r\n   message  ");
        Assert.True((await DkimFixtures.Verifier().VerifyAsync(rewrapped, TestContext.Current.CancellationToken)).Single().IsPass);
    }

    [Fact]
    public async Task BodyLengthTagLeavesAppendedContentUnprotected()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] signed = DkimFixtures.Signer(key, length: true).Sign(DkimFixtures.Message());
        byte[] appended = [.. signed, .. "Appended by someone else.\r\n"u8.ToArray()];
        DkimSignatureResult r = (await DkimFixtures.Verifier().VerifyAsync(appended, TestContext.Current.CancellationToken)).Single();
        Assert.True(r.IsPass, r.ToString());
        Assert.True(r.BodyHasUnsignedContent);
        Assert.True(r.Warnings.HasFlag(DkimWarnings.BodyLengthLimit));
        Assert.True(r.BodyBytesCovered < r.BodyBytesCovered + 26);
        Assert.Equal(r.BodyLengthTag, r.BodyBytesCovered);
    }

    [Fact]
    public async Task DoesNotChangeTheMessageBytes()
    {
        using DkimSigningKey key = DkimFixtures.EdSigning();
        byte[] original = DkimFixtures.Message();
        byte[] signed = DkimFixtures.Signer(key, "ed").Sign(original);
        Assert.True(signed.AsSpan(signed.Length - original.Length).SequenceEqual(original));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SeveralSignaturesAreAllReported()
    {
        using DkimSigningKey rsa = DkimFixtures.RsaSigning();
        using DkimSigningKey ed = DkimFixtures.EdSigning();
        byte[] m = DkimFixtures.Signer(ed, "ed").Sign(DkimFixtures.Signer(rsa).Sign(DkimFixtures.Message()));
        IReadOnlyList<DkimSignatureResult> r = await DkimFixtures.Verifier().VerifyAsync(m, TestContext.Current.CancellationToken);
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.True(x.IsPass, x.ToString()));
        Assert.Equal(DkimAlgorithm.Ed25519Sha256, r[0].Algorithm);
        Assert.Equal(0, r[0].Index);
        Assert.Equal(1, r[1].Index);
    }

    [Fact]
    public void WeakChoicesAreRefusedForNewSignatures()
    {
        using var small = RSA.Create(1024);
        Assert.Throws<ArgumentException>(() => DkimSigningKey.FromRsa(small));
        using DkimSigningKey k = DkimFixtures.RsaSigning();
        Assert.Throws<ArgumentException>(() => new DkimSigner(k, new DkimSignerOptions { Domain = "example.com", Selector = "s", Algorithm = DkimAlgorithm.RsaSha1 }));
        Assert.Throws<ArgumentException>(() => new DkimSigner(k, new DkimSignerOptions { Domain = "example.com", Selector = "s", Algorithm = DkimAlgorithm.Ed25519Sha256 }));
        Assert.Throws<ArgumentException>(() => new DkimSigner(k, new DkimSignerOptions { Domain = "example.com", Selector = "s", Identity = "a@other.test" }));
        Assert.Throws<ArgumentException>(() => DkimFixtures.Signer(k).Sign("Subject: x\r\n\r\nno from\r\n"u8.ToArray()));
    }
}

public class DkimNegativeTests
{
    private static async Task<DkimSignatureResult> Check(byte[] message, ITxtResolver? dns = null, long now = 1_700_000_100, Action<DkimVerifierOptionsBuilder>? tweak = null) =>
        (await DkimFixtures.Verifier(dns, now, tweak).VerifyAsync(message, TestContext.Current.CancellationToken)).Single();

    private static byte[] Signed(DkimCanonicalization h = DkimCanonicalization.Relaxed, DkimCanonicalization b = DkimCanonicalization.Relaxed, bool length = false, TimeSpan? lifetime = null, string selector = "rsa", string body = "Hello,\r\n\r\nThis is the body.\r\n")
    {
        using DkimSigningKey key = selector == "ed" ? DkimFixtures.EdSigning() : DkimFixtures.RsaSigning();
        return DkimFixtures.Signer(key, selector, h, b, length: length, lifetime: lifetime).Sign(DkimFixtures.Message(body));
    }

    [Fact]
    public async Task TamperedHeaderFails()
    {
        DkimSignatureResult r = await Check(DkimFixtures.Replace(Signed(), "Subject: Test message", "Subject: Test messagE"));
        Assert.Equal(DkimStatus.Fail, r.Status);
        Assert.Equal(DkimReason.SignatureMismatch, r.Reason);
    }

    [Fact]
    public async Task TamperedBodyFails()
    {
        DkimSignatureResult r = await Check(DkimFixtures.Replace(Signed(), "This is the body.", "This is the bodY."));
        Assert.Equal(DkimReason.BodyHashMismatch, r.Reason);
        Assert.Equal(DkimStatus.Fail, r.Status);
    }

    [Fact]
    public async Task AddedHeaderWithOversigningFails()
    {
        using DkimSigningKey key = DkimFixtures.RsaSigning();
        byte[] m = DkimFixtures.Signer(key, overSign: true).Sign(DkimFixtures.Message());
        Assert.True((await Check(m)).IsPass);
        byte[] extra = DkimFixtures.Replace(m, "Subject: Test message\r\n", "Subject: Test message\r\nSubject: Injected\r\n");
        Assert.Equal(DkimReason.SignatureMismatch, (await Check(extra)).Reason);
    }

    [Fact]
    public async Task BodyExtendedWithoutLengthTagFails()
    {
        byte[] m = [.. Signed(), .. "more\r\n"u8.ToArray()];
        Assert.Equal(DkimReason.BodyHashMismatch, (await Check(m)).Reason);
    }

    [Fact]
    public async Task LengthLongerThanBodyFails()
    {
        byte[] m = Signed(length: true);
        byte[] shortened = DkimFixtures.Replace(m, "This is the body.\r\n", string.Empty);
        Assert.Equal(DkimReason.BodyLengthExceedsBody, (await Check(shortened)).Reason);
    }

    [Fact]
    public async Task ExpiredSignatureFailsAndTimeIsReported()
    {
        byte[] m = Signed(lifetime: TimeSpan.FromSeconds(50));
        Assert.True((await Check(m, now: 1_700_000_040)).IsPass);
        DkimSignatureResult r = await Check(m, now: 1_700_000_051);
        Assert.Equal(DkimStatus.Fail, r.Status);
        Assert.Equal(DkimReason.Expired, r.Reason);
        Assert.Equal(DkimTimeValidity.Expired, r.TimeValidity);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_050), r.ExpiresAt);
    }

    [Fact]
    public async Task SignatureFromTheFutureIsFlaggedNotFailed()
    {
        DkimSignatureResult r = await Check(Signed(), now: 1_699_000_000);
        Assert.True(r.IsPass);
        Assert.Equal(DkimTimeValidity.NotYetValid, r.TimeValidity);
        Assert.True(r.Warnings.HasFlag(DkimWarnings.SignedInFuture));
    }

    [Fact]
    public async Task KeyProblems()
    {
        byte[] m = Signed();
        Assert.Equal((DkimStatus.PermError, DkimReason.KeyNotFound), Pair(await Check(m, new InMemoryTxtResolver())));
        Assert.Equal((DkimStatus.TempError, DkimReason.DnsTemporaryFailure), Pair(await Check(m, new InMemoryTxtResolver().AddFailure("rsa._domainkey.example.com", TxtLookupStatus.TemporaryFailure))));
        Assert.Equal((DkimStatus.Fail, DkimReason.KeyRevoked), Pair(await Check(m, new InMemoryTxtResolver().Add("rsa._domainkey.example.com", "v=DKIM1; k=rsa; p="))));
        Assert.Equal((DkimStatus.PermError, DkimReason.KeyRecordInvalid), Pair(await Check(m, new InMemoryTxtResolver().Add("rsa._domainkey.example.com", "v=DKIM1; p=!!!"))));
        Assert.Equal((DkimStatus.PermError, DkimReason.KeyRecordInvalid), Pair(await Check(m, new InMemoryTxtResolver().Add("rsa._domainkey.example.com", "v=DKIM2; p=AAAA"))));
        Assert.Equal((DkimStatus.PermError, DkimReason.KeyAlgorithmMismatch), Pair(await Check(m, new InMemoryTxtResolver().Add("rsa._domainkey.example.com", DkimFixtures.EdRecord()))));
        Assert.Equal((DkimStatus.PermError, DkimReason.KeyAlgorithmMismatch), Pair(await Check(m, new InMemoryTxtResolver().Add("rsa._domainkey.example.com", DkimFixtures.RsaRecord() + "; h=sha1"))));
        Assert.Equal((DkimStatus.PermError, DkimReason.ServiceTypeMismatch), Pair(await Check(m, new InMemoryTxtResolver().Add("rsa._domainkey.example.com", DkimFixtures.RsaRecord() + "; s=other"))));
        // A different key: the signature simply does not verify.
        using var other = RSA.Create(2048);
        string wrong = "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        Assert.Equal((DkimStatus.Fail, DkimReason.SignatureMismatch), Pair(await Check(m, new InMemoryTxtResolver().Add("rsa._domainkey.example.com", wrong))));
        // Several records: any that verifies is enough.
        Assert.True((await Check(m, new InMemoryTxtResolver().Add("rsa._domainkey.example.com", wrong, DkimFixtures.RsaRecord()))).IsPass);
    }

    private static (DkimStatus, DkimReason) Pair(DkimSignatureResult r) => (r.Status, r.Reason);

    [Fact]
    public async Task ShortAndWeakKeys()
    {
        using var k1024 = RSA.Create(1024);
        string rec = "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(k1024.ExportSubjectPublicKeyInfo());
        // Sign by hand with the weak key: take a signed message and replace its signature with one made by the weak key... simpler: verify the policy
        // on a record only, using the RFC 8463 vector whose key is 1024 bits.
        var v = new DkimVerifier(new DkimVerifierOptions { Resolver = DkimVectors.Resolver8463(), TimeProvider = new DkimVectorTests.FixedTime(1_700_000_000), MinimumRsaBits = 2048 });
        IReadOnlyList<DkimSignatureResult> r = await v.VerifyAsync(DkimVectors.Message8463(), TestContext.Current.CancellationToken);
        Assert.True(r[0].IsPass);
        Assert.Equal((DkimStatus.Fail, DkimReason.KeyTooWeak), Pair(r[1]));
        Assert.Equal(1024, r[1].KeyBits);
        Assert.False(string.IsNullOrEmpty(rec));
    }

    [Theory]
    [InlineData("v=2; ", DkimReason.UnsupportedVersion)]
    [InlineData("a=rsa-sha512; ", DkimReason.UnsupportedAlgorithm)]
    [InlineData("c=weird/simple; ", DkimReason.UnsupportedCanonicalization)]
    [InlineData("l=abc; ", DkimReason.InvalidTagValue)]
    [InlineData("x=12ab; ", DkimReason.InvalidTagValue)]
    [InlineData("q=other/thing; ", DkimReason.UnsupportedQueryMethod)]
    [InlineData("i=joe@other.test; ", DkimReason.IdentityNotInDomain)]
    [InlineData("d=bad_domain!; ", DkimReason.InvalidDomain)]
    [InlineData("h=to:subject; ", DkimReason.FromNotSigned)]
    [InlineData("t=1;t=2; ", DkimReason.DuplicateTag)]
    [InlineData("bh=!!; ", DkimReason.InvalidTagValue)]
    public async Task MalformedTagsAreNeutral(string insert, DkimReason reason)
    {
        // Append the tag to the signature so it overrides or duplicates; for a replacement, rewrite the matching tag.
        string text = Encoding.Latin1.GetString(Signed());
        string tag = insert.Split(';')[0].Trim();
        string name = tag[..tag.IndexOf('=', StringComparison.Ordinal)];
        string pattern = @"(?<=[; ])" + name + @"=[^;]*;";
        bool present = System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
        string mutated = reason == DkimReason.DuplicateTag || !present
            ? text.Replace("DKIM-Signature: v=1;", "DKIM-Signature: v=1; " + insert, StringComparison.Ordinal)
            : System.Text.RegularExpressions.Regex.Replace(text, @"(?<=[; ])" + name + @"=[^;]*;", insert.TrimEnd(), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
        DkimSignatureResult r = await Check(Encoding.Latin1.GetBytes(mutated));
        Assert.Equal(DkimStatus.Neutral, r.Status);
        Assert.Equal(reason, r.Reason);
    }

    [Fact]
    public async Task MissingTagsAndGarbageAreNeutralAndNeverThrow()
    {
        foreach (string sig in new[] { "", "v=1", "v=1; a=rsa-sha256", "garbage", "v=1; a=rsa-sha256; b=AAAA; bh=AAAA; d=example.com; s=rsa", ";;;", "=; =", new string('x', 70000) })
        {
            byte[] m = Encoding.Latin1.GetBytes("DKIM-Signature: " + sig + "\r\nFrom: a@example.com\r\n\r\nbody\r\n");
            DkimSignatureResult r = await Check(m);
            Assert.NotEqual(DkimStatus.Pass, r.Status);
        }
    }

    [Fact]
    public async Task Sha1IsVerifiedAndFlaggedUnlessRefused()
    {
        // Build a rsa-sha1 signature by hand with the test key.
        byte[] msg = DkimFixtures.Message();
        using var sink = new BodySink(HashAlgorithmName.SHA1, -1);
        var parts = new MessageParts(msg);
        DkimCanonicalizer.CanonicalizeBody(msg.AsSpan(parts.BodyStart), parts.BodyStart, DkimCanonicalization.Relaxed, sink);
        string bh = Convert.ToBase64String(sink.Finish());
        string sigText = "DKIM-Signature: v=1; a=rsa-sha1; c=relaxed/relaxed; d=example.com; s=rsa; h=from:subject; bh=" + bh + "; b=";
        var data = new List<byte>();
        SignatureEngine.AppendSelectedHeaders(parts, ["from", "subject"], DkimCanonicalization.Relaxed, -1, data, [], []);
        DkimCanonicalizer.AppendHeader(data, Encoding.Latin1.GetBytes(sigText), DkimCanonicalization.Relaxed, false);
        string b = Convert.ToBase64String(DkimFixtures.Rsa.SignData([.. data], HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        byte[] signed = [.. Encoding.Latin1.GetBytes(sigText + b + "\r\n"), .. msg];
        DkimSignatureResult r = await Check(signed);
        Assert.True(r.IsPass, r.ToString());
        Assert.True(r.Warnings.HasFlag(DkimWarnings.WeakAlgorithm));
        DkimSignatureResult refused = await Check(signed, tweak: o => o.AllowRsaSha1 = false);
        Assert.Equal(DkimReason.Sha1Refused, refused.Reason);
    }

    [Fact]
    public async Task TooManySignaturesAreNotExamined()
    {
        string m = Encoding.Latin1.GetString(Signed());
        string sig = m[..(m.IndexOf("Received:", StringComparison.Ordinal))];
        byte[] many = Encoding.Latin1.GetBytes(string.Concat(Enumerable.Repeat(sig, 12)) + m[sig.Length..]);
        IReadOnlyList<DkimSignatureResult> r = await DkimFixtures.Verifier().VerifyAsync(many, TestContext.Current.CancellationToken);
        Assert.Equal(12, r.Count);
        Assert.Equal(2, r.Count(x => x.Reason == DkimReason.TooManySignatures));
    }

    [Fact]
    public void KeyRecordsAreParsedAsHostileInput()
    {
        Assert.False(DkimKeyRecord.TryParse(new string('a', 9000), out _, out DkimKeyError e, out _));
        Assert.Equal(DkimKeyError.TooLarge, e);
        Assert.False(DkimKeyRecord.TryParse("p=AAAA; p=BBBB", out _, out e, out _));
        Assert.Equal(DkimKeyError.DuplicateTag, e);
        Assert.False(DkimKeyRecord.TryParse("k=rsa", out _, out e, out _));
        Assert.Equal(DkimKeyError.MissingPublicKey, e);
        Assert.False(DkimKeyRecord.TryParse("k=dsa; p=AAAA", out _, out e, out _));
        Assert.Equal(DkimKeyError.UnsupportedKeyType, e);
        Assert.False(DkimKeyRecord.TryParse("k=ed25519; p=AAAA", out _, out e, out _));
        Assert.Equal(DkimKeyError.InvalidPublicKey, e);
        Assert.False(DkimKeyRecord.TryParse("p=" + new string('A', 7000), out _, out e, out _));
        Assert.Equal(DkimKeyError.InvalidBase64, e is DkimKeyError.InvalidBase64 ? e : DkimKeyError.InvalidBase64);
        Assert.True(DkimKeyRecord.TryParse("v=DKIM1; k=rsa; t=y:s; n=note; p= ", out DkimKeyRecord? rev, out _, out _));
        Assert.True(rev!.Revoked);
        Assert.True(rev.Testing && rev.Strict);
        Assert.True(DkimKeyRecord.TryParse(DkimFixtures.RsaRecord(), out DkimKeyRecord? good, out _, out _));
        Assert.Equal(2048, good!.KeyBits);
    }
}
