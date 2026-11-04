using RecursiveAI.OneMail.Cryptography.Arc;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Cryptography.Dns;

namespace RecursiveAI.OneMail.Tests.Cryptography;

public class DkimAlignmentTests
{
    private static DkimSignatureResult Result(string? domain)
    {
        var r = new DkimSignatureResult();
        r.GetType().GetProperty(nameof(DkimSignatureResult.Domain))!.SetValue(r, domain);
        return r;
    }

    [Theory]
    [InlineData("example.com", "joe@example.com", DkimAlignment.Exact)]
    [InlineData("Example.COM", "Joe <joe@example.com>", DkimAlignment.Exact)]
    [InlineData("example.com", "example.com.", DkimAlignment.Exact)]
    [InlineData("example.com", "joe@mail.example.com", DkimAlignment.ParentOrChild)]
    [InlineData("mail.example.com", "joe@example.com", DkimAlignment.ParentOrChild)]
    [InlineData("a.example.com", "joe@b.example.com", DkimAlignment.None)]
    [InlineData("notexample.com", "joe@example.com", DkimAlignment.None)]
    [InlineData("example.org", "joe@example.com", DkimAlignment.None)]
    [InlineData("bücher.example", "joe@xn--bcher-kva.example", DkimAlignment.Exact)]
    [InlineData(null, "joe@example.com", DkimAlignment.None)]
    [InlineData("example.com", "", DkimAlignment.None)]
    public void SignerAndFromDomainsAreRelated(string? signer, string from, DkimAlignment want) =>
        Assert.Equal(want, Result(signer).CheckAlignment(from));
}

public class CachingTxtResolverTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => 1000;
        public void Advance(TimeSpan t) => Ticks += (long)(t.TotalSeconds * 1000);
    }

    private sealed class Counting(Func<int, TxtLookupResult> next) : ITxtResolver
    {
        public int Calls;
        public ValueTask<TxtLookupResult> LookupTxtAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult(next(++Calls));
    }

    private static TxtLookupResult Ok(int seconds) => new(TxtLookupStatus.Success, ["v=DKIM1; p="], null, TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task AnAnswerIsKeptForItsTimeToLiveAndThenAskedAgain()
    {
        var clock = new Clock();
        var inner = new Counting(_ => Ok(60));
        var c = new CachingTxtResolver(inner, new CachingTxtResolverOptions { TimeProvider = clock });
        CancellationToken ct = TestContext.Current.CancellationToken;
        await c.LookupTxtAsync("s._domainkey.example.com", ct);
        await c.LookupTxtAsync("S._domainkey.Example.com.", ct);
        Assert.Equal(1, inner.Calls);
        clock.Advance(TimeSpan.FromSeconds(59));
        await c.LookupTxtAsync("s._domainkey.example.com", ct);
        Assert.Equal(1, inner.Calls);
        clock.Advance(TimeSpan.FromSeconds(2));
        await c.LookupTxtAsync("s._domainkey.example.com", ct);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task LifetimesAreCappedAndZeroAndTemporaryFailuresAreNotKept()
    {
        var clock = new Clock();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var longLived = new Counting(_ => Ok(86_400));
        var a = new CachingTxtResolver(longLived, new CachingTxtResolverOptions { TimeProvider = clock, MaxTtl = TimeSpan.FromMinutes(10) });
        await a.LookupTxtAsync("x.example.com", ct);
        clock.Advance(TimeSpan.FromMinutes(11));
        await a.LookupTxtAsync("x.example.com", ct);
        Assert.Equal(2, longLived.Calls);

        var zero = new Counting(_ => Ok(0));
        var b = new CachingTxtResolver(zero, new CachingTxtResolverOptions { TimeProvider = clock });
        await b.LookupTxtAsync("x.example.com", ct);
        await b.LookupTxtAsync("x.example.com", ct);
        Assert.Equal(2, zero.Calls);

        var flaky = new Counting(n => n == 1 ? new TxtLookupResult(TxtLookupStatus.TemporaryFailure) : Ok(60));
        var d = new CachingTxtResolver(flaky, new CachingTxtResolverOptions { TimeProvider = clock });
        Assert.Equal(TxtLookupStatus.TemporaryFailure, (await d.LookupTxtAsync("x.example.com", ct)).Status);
        Assert.Equal(TxtLookupStatus.Success, (await d.LookupTxtAsync("x.example.com", ct)).Status);
        Assert.Equal(2, flaky.Calls);
    }

    [Fact]
    public async Task MissingKeysAreKeptBrieflyAndTheTableIsBounded()
    {
        var clock = new Clock();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var missing = new Counting(_ => new TxtLookupResult(TxtLookupStatus.NameNotFound, null, null, TimeSpan.FromHours(1)));
        var c = new CachingTxtResolver(missing, new CachingTxtResolverOptions { TimeProvider = clock });
        await c.LookupTxtAsync("gone.example.com", ct);
        await c.LookupTxtAsync("gone.example.com", ct);
        Assert.Equal(1, missing.Calls);
        clock.Advance(TimeSpan.FromSeconds(61));
        await c.LookupTxtAsync("gone.example.com", ct);
        Assert.Equal(2, missing.Calls);

        var many = new CachingTxtResolver(new Counting(_ => Ok(600)), new CachingTxtResolverOptions { TimeProvider = clock, MaxEntries = 4 });
        for (int i = 0; i < 20; i++)
        {
            await many.LookupTxtAsync("n" + i + ".example.com", ct);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(many.Count <= 4);
    }
}

public class ArcSealerClaimTests
{
    [Fact]
    public async Task APassedStatusThatContradictsTheMessageIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DkimSigningKey k = DkimFixtures.EdSigning();
        using DkimSigningKey rsa = DkimFixtures.RsaSigning();
        var noCheck = new ArcSealer(k, new ArcSealerOptions { Domain = "example.org", Selector = "arced" });
        // No chain on the message: only none can be true.
        await Assert.ThrowsAsync<ArgumentException>(() => noCheck.SealAsync(DkimFixtures.Message(), "x", ArcChainStatus.Pass, ct));
        ArcSealResult first = await new ArcSealer(rsa, new ArcSealerOptions { Domain = "example.net", Selector = "arc" }).SealAsync(DkimFixtures.Message(), "x", ArcChainStatus.None, ct);
        // A chain exists: none cannot be true.
        await Assert.ThrowsAsync<ArgumentException>(() => noCheck.SealAsync(first.Message, "x", ArcChainStatus.None, ct));
    }
}
