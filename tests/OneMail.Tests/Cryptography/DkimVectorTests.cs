using System.Text;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Cryptography.Dns;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>The example messages and keys of RFC 6376 Appendix A and RFC 8463 Appendix A.</summary>
internal static class DkimVectors
{
    public static byte[] Crlf(string text) => Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

    // RFC 8463 A.2
    public const string Ed25519Key = "v=DKIM1; k=ed25519; p=11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=";

    public const string Rsa8463Key =
        "v=DKIM1; k=rsa; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDkHlOQoBTzWR" +
        "iGs5V6NpP3idY6Wk08a5qhdR6wy5bdOKb2jLQiY/J16JYi0Qvx/byYzCNb3W91y3FutAC" +
        "DfzwQ/BC/e/8uBsCR+yz1Lxj+PL6lHvqMKrM3rG4hstT5QjvHO9PzoxZyVYLzBfO2EeC3" +
        "Ip3G+2kryOTIKT+l/K4w3QIDAQAB";

    public const string Ed25519SeedBase64 = "nWGxne/9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A=";

    public const string Rsa8463Pem = """
        -----BEGIN RSA PRIVATE KEY-----
        MIICXQIBAAKBgQDkHlOQoBTzWRiGs5V6NpP3idY6Wk08a5qhdR6wy5bdOKb2jLQi
        Y/J16JYi0Qvx/byYzCNb3W91y3FutACDfzwQ/BC/e/8uBsCR+yz1Lxj+PL6lHvqM
        KrM3rG4hstT5QjvHO9PzoxZyVYLzBfO2EeC3Ip3G+2kryOTIKT+l/K4w3QIDAQAB
        AoGAH0cxOhFZDgzXWhDhnAJDw5s4roOXN4OhjiXa8W7Y3rhX3FJqmJSPuC8N9vQm
        6SVbaLAE4SG5mLMueHlh4KXffEpuLEiNp9Ss3O4YfLiQpbRqE7Tm5SxKjvvQoZZe
        zHorimOaChRL2it47iuWxzxSiRMv4c+j70GiWdxXnxe4UoECQQDzJB/0U58W7RZy
        6enGVj2kWF732CoWFZWzi1FicudrBFoy63QwcowpoCazKtvZGMNlPWnC7x/6o8Gc
        uSe0ga2xAkEA8C7PipPm1/1fTRQvj1o/dDmZp243044ZNyxjg+/OPN0oWCbXIGxy
        WvmZbXriOWoSALJTjExEgraHEgnXssuk7QJBALl5ICsYMu6hMxO73gnfNayNgPxd
        WFV6Z7ULnKyV7HSVYF0hgYOHjeYe9gaMtiJYoo0zGN+L3AAtNP9huqkWlzECQE1a
        licIeVlo1e+qJ6Mgqr0Q7Aa7falZ448ccbSFYEPD6oFxiOl9Y9se9iYHZKKfIcst
        o7DUw1/hz2Ck4N5JrgUCQQCyKveNvjzkkd8HjYs0SwM0fPjK16//5qDZ2UiDGnOe
        uEzxBDAr518Z8VFbR41in3W4Y3yCDgQlLlcETrS+zYcL
        -----END RSA PRIVATE KEY-----
        """;

    public static byte[] Message8463() => Crlf("""
        DKIM-Signature: v=1; a=ed25519-sha256; c=relaxed/relaxed;
         d=football.example.com; i=@football.example.com;
         q=dns/txt; s=brisbane; t=1528637909; h=from : to :
         subject : date : message-id : from : subject : date;
         bh=2jUSOH9NhtVGCQWNr9BrIAPreKQjO6Sn7XIkfJVOzv8=;
         b=/gCrinpcQOoIfuHNQIbq4pgh9kyIK3AQUdt9OdqQehSwhEIug4D11Bus
         Fa3bT3FY5OsU7ZbnKELq+eXdp1Q1Dw==
        DKIM-Signature: v=1; a=rsa-sha256; c=relaxed/relaxed;
         d=football.example.com; i=@football.example.com;
         q=dns/txt; s=test; t=1528637909; h=from : to : subject :
         date : message-id : from : subject : date;
         bh=2jUSOH9NhtVGCQWNr9BrIAPreKQjO6Sn7XIkfJVOzv8=;
         b=F45dVWDfMbQDGHJFlXUNB2HKfbCeLRyhDXgFpEL8GwpsRe0IeIixNTe3
         DhCVlUrSjV4BwcVcOF6+FF3Zo9Rpo1tFOeS9mPYQTnGdaSGsgeefOsk2Jz
         dA+L10TeYt9BgDfQNZtKdN1WO//KgIqXP7OdEFE4LjFYNcUxZQ4FADY+8=
        From: Joe SixPack <joe@football.example.com>
        To: Suzie Q <suzie@shopping.example.net>
        Subject: Is dinner ready?
        Date: Fri, 11 Jul 2003 21:00:37 -0700 (PDT)
        Message-ID: <20030712040037.46341.5F8J@football.example.com>

        Hi.

        We lost the game.  Are you hungry yet?

        Joe.

        """);

    public static InMemoryTxtResolver Resolver8463() => new InMemoryTxtResolver()
        .Add("brisbane._domainkey.football.example.com", Ed25519Key)
        .Add("test._domainkey.football.example.com", Rsa8463Key);

    // RFC 6376 Appendix A.2 / Appendix C
    public const string Rsa6376Key =
        "v=DKIM1; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQ" +
        "KBgQDwIRP/UC3SBsEmGqZ9ZJW3/DkMoGeLnQg1fWn7/zYt" +
        "IxN2SnFCjxOCKG9v3b4jYfcTNh5ijSsq631uBItLa7od+v" +
        "/RtdC2UzJ1lWT947qR+Rcac2gbto/NMqJ0fzfVjH4OuKhi" +
        "tdY9tf6mcwGjaNBcWToIMmPSPDdQPNUYckcQ2QIDAQAB";

    public static byte[] Message6376() => Crlf("""
        DKIM-Signature: v=1; a=rsa-sha256; s=brisbane; d=example.com;
              c=simple/simple; q=dns/txt; i=joe@football.example.com;
              h=Received : From : To : Subject : Date : Message-ID;
              bh=2jUSOH9NhtVGCQWNr9BrIAPreKQjO6Sn7XIkfJVOzv8=;
              b=AuUoFEfDxTDkHlLXSZEpZj79LICEps6eda7W3deTVFOk4yAUoqOB
              4nujc7YopdG5dWLSdNg6xNAZpOPr+kHxt1IrE+NahM6L/LbvaHut
              KVdkLLkpVaVVQPzeRDI009SO2Il5Lu7rDNH6mZckBdrIx0orEtZV
              4bmp/YzhwvcubU4=;
        Received: from client1.football.example.com  [192.0.2.1]
              by submitserver.example.com with SUBMISSION;
              Fri, 11 Jul 2003 21:01:54 -0700 (PDT)
        From: Joe SixPack <joe@football.example.com>
        To: Suzie Q <suzie@shopping.example.net>
        Subject: Is dinner ready?
        Date: Fri, 11 Jul 2003 21:00:37 -0700 (PDT)
        Message-ID: <20030712040037.46341.5F8J@football.example.com>

        Hi.

        We lost the game.  Are you hungry yet?

        Joe.

        """);

    public static InMemoryTxtResolver Resolver6376() => new InMemoryTxtResolver().Add("brisbane._domainkey.example.com", Rsa6376Key);
}

public class DkimVectorTests
{
    [Fact]
    public async Task Rfc8463BothSignaturesVerify()
    {
        var v = new DkimVerifier(new DkimVerifierOptions { Resolver = DkimVectors.Resolver8463() });
        IReadOnlyList<DkimSignatureResult> r = await v.VerifyAsync(DkimVectors.Message8463(), TestContext.Current.CancellationToken);
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.True(x.IsPass, x.ToString()));
        Assert.Equal(DkimAlgorithm.Ed25519Sha256, r[0].Algorithm);
        Assert.Equal(DkimAlgorithm.RsaSha256, r[1].Algorithm);
        Assert.Equal(1024, r[1].KeyBits);
        Assert.True(r[1].Warnings.HasFlag(DkimWarnings.WeakKey));
        Assert.Equal(256, r[0].KeyBits);
        // The second "from", "subject" and "date" in h= name headers the message does not have: over-signing.
        Assert.Contains("from", r[0].OversignedHeaders);
    }

    [Fact]
    public async Task Rfc6376AppendixAExampleIsInconsistentAndReportsABodyHashMismatch()
    {
        // The example in RFC 6376 Appendix A.2 says c=simple/simple, but its bh= is the hash of the relaxed body (the double space in
        // "game.  Are" is collapsed). The example cannot verify as written; we report the body hash, not a signature, as the failure.
        var v = new DkimVerifier(new DkimVerifierOptions { Resolver = DkimVectors.Resolver6376() });
        IReadOnlyList<DkimSignatureResult> r = await v.VerifyAsync(DkimVectors.Message6376(), TestContext.Current.CancellationToken);
        Assert.Single(r);
        Assert.Equal(DkimStatus.Fail, r[0].Status);
        Assert.Equal(DkimReason.BodyHashMismatch, r[0].Reason);
        Assert.Equal(DkimAlgorithm.RsaSha256, r[0].Algorithm);
        Assert.Equal(1024, r[0].KeyBits);
    }

    [Fact]
    public async Task OurSignatureOverTheRfc8463MessageVerifiesWithTheRfcKeys()
    {
        byte[] original = DkimVectors.Message8463();
        int at = Encoding.Latin1.GetString(original).IndexOf("From: Joe", StringComparison.Ordinal);
        byte[] unsigned = original[at..];
        using var ed = DkimSigningKey.FromEd25519Seed(Convert.FromBase64String(DkimVectors.Ed25519SeedBase64));
        // The RFC's RSA key is 1024 bits: fine to verify with (flagged weak), refused for new signatures.
        Assert.Throws<ArgumentException>(() => DkimSigningKey.FromPem(DkimVectors.Rsa8463Pem));
        Assert.Equal("v=DKIM1; k=ed25519; p=11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=", ed.ToKeyRecord());
        var v = new DkimVerifier(new DkimVerifierOptions { Resolver = DkimVectors.Resolver8463() });
        var signed = new DkimSigner(ed, new DkimSignerOptions { Domain = "football.example.com", Selector = "brisbane", OverSign = true }).Sign(unsigned);
        IReadOnlyList<DkimSignatureResult> r = await v.VerifyAsync(signed, TestContext.Current.CancellationToken);
        Assert.True(r.Single().IsPass, r.Single().ToString());
    }

    internal sealed class FixedTime(long seconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
