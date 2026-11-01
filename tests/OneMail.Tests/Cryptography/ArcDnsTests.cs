using System.Net;
using System.Net.Sockets;
using System.Text;
using RecursiveAI.OneMail.Cryptography.Arc;
using RecursiveAI.OneMail.Cryptography.Dkim;
using RecursiveAI.OneMail.Cryptography.Dns;

namespace RecursiveAI.OneMail.Tests.Cryptography;

public class ArcTests
{
    private static ArcSealer Sealer(DkimSigningKey key, string domain, string selector, long now) =>
        new(key, new ArcSealerOptions { Domain = domain, Selector = selector, TimeProvider = new DkimVectorTests.FixedTime(now), Verification = DkimFixtures.Options(now: now + 5) });

    private static ArcVerifier Verifier(long now = 1_700_000_100) => new(DkimFixtures.Options(now: now));

    private static async Task<byte[]> Hop1()
    {
        using DkimSigningKey k = DkimFixtures.RsaSigning();
        ArcSealResult r = await Sealer(k, "example.net", "arc", 1_700_000_001).SealAsync(DkimFixtures.Message(), "mx.example.net; spf=pass smtp.mailfrom=joe@example.com", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(r.Sealed);
        return r.Message;
    }

    private static async Task<ArcSealResult> Hop2(byte[] message, long now = 1_700_000_002, ArcChainStatus? status = null)
    {
        using DkimSigningKey k = DkimFixtures.EdSigning();
        return await Sealer(k, "example.org", "arced", now).SealAsync(message, "mx.example.org; arc=pass; dkim=none", chainStatus: status, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OneSetPasses()
    {
        byte[] m = await Hop1();
        ArcVerificationResult v = await Verifier().VerifyAsync(m, TestContext.Current.CancellationToken);
        Assert.Equal(ArcChainStatus.Pass, v.Status);
        Assert.Equal(1, v.InstanceCount);
        Assert.Null(v.OldestPass);
        ArcSetResult s = Assert.Single(v.Sets);
        Assert.Equal("none", s.ChainValidation);
        Assert.Equal("example.net", s.SealDomain);
        Assert.StartsWith("mx.example.net; spf=pass", s.AuthenticationResults, StringComparison.Ordinal);
        Assert.True(s.MessageSignature!.IsPass);
        Assert.True(s.Seal!.IsPass);
        Assert.StartsWith("arc=pass header.s=arc header.d=example.net", v.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoSetsPassWithMixedAlgorithms()
    {
        ArcSealResult r2 = await Hop2(await Hop1());
        Assert.Equal(2, r2.Instance);
        Assert.Equal(ArcChainStatus.Pass, r2.ChainStatus);
        ArcVerificationResult v = await Verifier().VerifyAsync(r2.Message, TestContext.Current.CancellationToken);
        Assert.Equal(ArcChainStatus.Pass, v.Status);
        Assert.Equal(0, v.OldestPass);
        Assert.Equal(DkimAlgorithm.Ed25519Sha256, v.Sets[1].Seal!.Algorithm);
        Assert.Equal("pass", v.Sets[1].ChainValidation);
    }

    [Fact]
    public async Task ModifyingTheMessageBetweenHopsMovesOldestPass()
    {
        byte[] m1 = await Hop1();
        byte[] modified = DkimFixtures.Replace(m1, "This is the body.", "This is the body, edited by a list.");
        // The receiving hop validates the chain as it arrives, then modifies, then seals with the status it found (RFC 8617 section 5.1).
        ArcChainStatus arrived = (await Verifier().VerifyAsync(m1, TestContext.Current.CancellationToken)).Status;
        ArcSealResult r2 = await Hop2(modified, status: arrived);
        Assert.Equal(ArcChainStatus.Pass, r2.ChainStatus);
        ArcVerificationResult v = await Verifier().VerifyAsync(r2.Message, TestContext.Current.CancellationToken);
        Assert.Equal(ArcChainStatus.Pass, v.Status);
        Assert.Equal(2, v.OldestPass);
        Assert.Equal(DkimStatus.Fail, v.Sets[0].MessageSignature!.Status);
    }

    [Fact]
    public async Task BreakingTheNewestMessageSignatureFailsTheChain()
    {
        ArcSealResult r2 = await Hop2(await Hop1());
        byte[] tampered = DkimFixtures.Replace(r2.Message, "This is the body.", "This is the bodY.");
        ArcVerificationResult v = await Verifier().VerifyAsync(tampered, TestContext.Current.CancellationToken);
        Assert.Equal(ArcChainStatus.Fail, v.Status);
        Assert.Equal(ArcFailure.MessageSignatureFailed, v.Failure);
        Assert.Equal(2, v.FailedInstance);
    }

    [Fact]
    public async Task AlteredEarlierResultsBreakItsSeal()
    {
        ArcSealResult r2 = await Hop2(await Hop1());
        byte[] tampered = DkimFixtures.Replace(r2.Message, "spf=pass", "spf=fail");
        ArcVerificationResult v = await Verifier().VerifyAsync(tampered, TestContext.Current.CancellationToken);
        Assert.Equal(ArcChainStatus.Fail, v.Status);
        Assert.Equal(ArcFailure.SealFailed, v.Failure);
    }

    [Fact]
    public async Task StructureErrors()
    {
        ArcSealResult r2 = await Hop2(await Hop1());
        string text = Encoding.Latin1.GetString(r2.Message);
        // A gap: renumber set 2 as 3.
        byte[] gap = Encoding.Latin1.GetBytes(text.Replace("i=2;", "i=3;", StringComparison.Ordinal));
        Assert.Equal(ArcFailure.InstanceSequence, (await Verifier().VerifyAsync(gap, TestContext.Current.CancellationToken)).Failure);
        // A missing seal.
        int at = text.IndexOf("ARC-Seal: i=2", StringComparison.Ordinal);
        int end = text.IndexOf("ARC-Message-Signature", at, StringComparison.Ordinal);
        byte[] noSeal = Encoding.Latin1.GetBytes(text.Remove(at, end - at));
        Assert.Equal(ArcFailure.IncompleteSet, (await Verifier().VerifyAsync(noSeal, TestContext.Current.CancellationToken)).Failure);
        // A duplicate of the first set's results.
        int aar = text.IndexOf("ARC-Authentication-Results: i=1", StringComparison.Ordinal);
        byte[] dup = Encoding.Latin1.GetBytes(text.Insert(aar, "ARC-Authentication-Results: i=1; x\r\n"));
        Assert.Equal(ArcFailure.IncompleteSet, (await Verifier().VerifyAsync(dup, TestContext.Current.CancellationToken)).Failure);
        // No ARC headers at all.
        Assert.Equal(ArcChainStatus.None, (await Verifier().VerifyAsync(DkimFixtures.Message(), TestContext.Current.CancellationToken)).Status);
        // More than 50 sets.
        var sb = new StringBuilder();
        for (int i = 1; i <= 51; i++)
        {
            sb.Append("ARC-Seal: i=").Append(i).Append("; a=rsa-sha256; cv=pass; d=a.test; s=s; b=AAAA\r\n");
        }

        ArcVerificationResult many = await Verifier().VerifyAsync(Encoding.Latin1.GetBytes(sb + "From: a@b.test\r\n\r\n"), TestContext.Current.CancellationToken);
        Assert.Equal(ArcFailure.TooManySets, many.Failure);
        // An h= on a seal.
        byte[] withH = DkimFixtures.Replace(r2.Message, "ARC-Seal: i=2;", "ARC-Seal: i=2; h=from;");
        Assert.Equal(ArcChainStatus.Fail, (await Verifier().VerifyAsync(withH, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task FailedChainsAreSealedAsFailAndNotSealedAgain()
    {
        ArcSealResult r2 = await Hop2(await Hop1());
        byte[] broken = DkimFixtures.Replace(r2.Message, "spf=pass", "spf=fail");
        using DkimSigningKey k = DkimFixtures.RsaSigning();
        ArcSealResult r3 = await Sealer(k, "example.net", "arc", 1_700_000_003).SealAsync(broken, "mx.example.net; arc=fail", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(r3.Sealed);
        Assert.Equal(ArcChainStatus.Fail, r3.ChainStatus);
        Assert.Equal(3, r3.Instance);
        Assert.Contains("cv=fail", r3.Seal, StringComparison.Ordinal);
        ArcVerificationResult v = await Verifier().VerifyAsync(r3.Message, TestContext.Current.CancellationToken);
        Assert.Equal(ArcFailure.LatestSealSaysFail, v.Failure);
        ArcSealResult again = await Sealer(k, "example.net", "arc", 1_700_000_004).SealAsync(r3.Message, "x; arc=fail", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(again.Sealed);
        Assert.Equal(r3.Message, again.Message);
    }

    [Fact]
    public async Task SealingNeedsAStatusWhenTheMessageHasAChain()
    {
        byte[] m = await Hop1();
        using DkimSigningKey k = DkimFixtures.EdSigning();
        var noVerification = new ArcSealer(k, new ArcSealerOptions { Domain = "example.org", Selector = "arced" });
        await Assert.ThrowsAsync<ArgumentException>(() => noVerification.SealAsync(m, "x", cancellationToken: TestContext.Current.CancellationToken));
        ArcSealResult ok = await noVerification.SealAsync(m, "x; arc=pass", ArcChainStatus.Pass, TestContext.Current.CancellationToken);
        Assert.Equal(2, ok.Instance);
    }

    [Fact]
    public async Task ArcMessageSignatureCoversTheDkimSignatureOnTheMessage()
    {
        using DkimSigningKey rsa = DkimFixtures.RsaSigning();
        byte[] dk = DkimFixtures.Signer(rsa).Sign(DkimFixtures.Message());
        using DkimSigningKey k = DkimFixtures.EdSigning();
        ArcSealResult r = await Sealer(k, "example.org", "arced", 1_700_000_002).SealAsync(dk, "mx.example.org; dkim=pass", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("dkim-signature", r.MessageSignature, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ArcChainStatus.Pass, (await Verifier().VerifyAsync(r.Message, TestContext.Current.CancellationToken)).Status);
        // The DKIM signature itself is untouched by the seal.
        Assert.True((await DkimFixtures.Verifier().VerifyAsync(r.Message, TestContext.Current.CancellationToken)).Single().IsPass);
    }

    [Fact]
    public async Task AuthenticationResultsAreRendered()
    {
        using DkimSigningKey rsa = DkimFixtures.RsaSigning();
        byte[] dk = DkimFixtures.Signer(rsa).Sign(DkimFixtures.Message());
        IReadOnlyList<DkimSignatureResult> d = await DkimFixtures.Verifier().VerifyAsync(dk, TestContext.Current.CancellationToken);
        string line = AuthenticationResultsFormatter.Format("mx.example.com", d);
        Assert.StartsWith("mx.example.com; dkim=pass header.d=example.com header.s=rsa header.b=", line, StringComparison.Ordinal);
        Assert.Equal("mx.example.com; dkim=none", AuthenticationResultsFormatter.Format("mx.example.com", []));
        // The core library's parser reads what we write.
        var parsed = RecursiveAI.OneMail.Mime.Authentication.AuthenticationResults.Parse(line);
        Assert.Equal("pass", parsed.Results[0].Result);
    }
}

public class DnsTests
{
    private static byte[] Reply(byte[] query, Action<List<byte>>? answers = null, int an = 0, int rcode = 0, bool tc = false, ushort? id = null)
    {
        // Echo the question, then the answers.
        int qend = 12;
        while (query[qend] != 0)
        {
            qend += query[qend] + 1;
        }

        qend += 5;
        var r = new List<byte> { (byte)((id ?? (ushort)((query[0] << 8) | query[1])) >> 8), (byte)(id ?? (ushort)((query[0] << 8) | query[1])), (byte)(0x81 | (tc ? 0x02 : 0)), (byte)(0x80 | rcode), 0, 1, (byte)(an >> 8), (byte)an, 0, 0, 0, 0 };
        r.AddRange(query[12..qend]);
        answers?.Invoke(r);
        return [.. r];
    }

    private static void Txt(List<byte> r, params string[] strings)
    {
        int len = strings.Sum(s => s.Length + 1);
        r.AddRange([0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 60, (byte)(len >> 8), (byte)len]);
        foreach (string s in strings)
        {
            r.Add((byte)s.Length);
            r.AddRange(Encoding.Latin1.GetBytes(s));
        }
    }

    private sealed class Stub : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly TcpListener _tcp;
        private readonly CancellationTokenSource _cts = new();
        public Func<byte[], bool, byte[]?> Respond = (_, _) => null;

        public Stub()
        {
            int port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _tcp = new TcpListener(IPAddress.Loopback, port);
            _tcp.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        UdpReceiveResult q = await _udp.ReceiveAsync(_cts.Token);
                        byte[]? a = Respond(q.Buffer, false);
                        if (a is not null)
                        {
                            await _udp.SendAsync(a, q.RemoteEndPoint, _cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        using TcpClient c = await _tcp.AcceptTcpClientAsync(_cts.Token);
                        NetworkStream s = c.GetStream();
                        byte[] len = new byte[2];
                        await s.ReadExactlyAsync(len, _cts.Token);
                        byte[] q = new byte[(len[0] << 8) | len[1]];
                        await s.ReadExactlyAsync(q, _cts.Token);
                        byte[]? a = Respond(q, true);
                        if (a is not null)
                        {
                            await s.WriteAsync(new byte[] { (byte)(a.Length >> 8), (byte)a.Length }, _cts.Token);
                            await s.WriteAsync(a, _cts.Token);
                        }
                    }
                    catch (Exception e) when (e is OperationCanceledException or IOException or SocketException)
                    {
                        if (_cts.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                }
            });
        }

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public DnsTxtClient Client(int attempts = 1) => new(new DnsTxtClientOptions { Servers = [new IPEndPoint(IPAddress.Loopback, Port)], Timeout = TimeSpan.FromMilliseconds(400), Attempts = attempts });

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
            _tcp.Stop();
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task TxtStringsAreConcatenatedAndMultipleRecordsKept()
    {
        using var stub = new Stub { Respond = (q, _) => Reply(q, r => { Txt(r, "v=DKIM1; k=rsa; p=AB", "CD"); Txt(r, "second"); }, 2) };
        TxtLookupResult r = await stub.Client().LookupTxtAsync("sel._domainkey.example.com", TestContext.Current.CancellationToken);
        Assert.Equal(TxtLookupStatus.Success, r.Status);
        Assert.Equal(["v=DKIM1; k=rsa; p=ABCD", "second"], r.Records);
    }

    [Fact]
    public async Task TruncatedUdpFallsBackToTcp()
    {
        bool tcpSeen = false;
        using var stub = new Stub();
        stub.Respond = (q, tcp) =>
        {
            if (!tcp)
            {
                return Reply(q, null, 0, 0, tc: true);
            }

            tcpSeen = true;
            return Reply(q, r => Txt(r, new string('x', 200), new string('y', 200)), 1);
        };
        TxtLookupResult r = await stub.Client().LookupTxtAsync("big.example.com", TestContext.Current.CancellationToken);
        Assert.True(tcpSeen);
        Assert.Equal(400, r.Records.Single().Length);
    }

    [Fact]
    public async Task NameErrorsAndNoData()
    {
        using var stub = new Stub();
        stub.Respond = (q, _) => Reply(q, null, 0, 3);
        Assert.Equal(TxtLookupStatus.NameNotFound, (await stub.Client().LookupTxtAsync("nx.example.com", TestContext.Current.CancellationToken)).Status);
        stub.Respond = (q, _) => Reply(q, null, 0, 0);
        Assert.Equal(TxtLookupStatus.NoRecords, (await stub.Client().LookupTxtAsync("empty.example.com", TestContext.Current.CancellationToken)).Status);
        stub.Respond = (q, _) => Reply(q, null, 0, 2);
        Assert.Equal(TxtLookupStatus.TemporaryFailure, (await stub.Client().LookupTxtAsync("fail.example.com", TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CnameChainsAreFollowed()
    {
        using var stub = new Stub();
        stub.Respond = (q, _) => Reply(q, r =>
        {
            // CNAME to target.example.net (uncompressed), then the TXT at the target.
            byte[] target = [6, .. "target"u8.ToArray(), 7, .. "example"u8.ToArray(), 3, .. "net"u8.ToArray(), 0];
            r.AddRange([0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, (byte)target.Length]);
            r.AddRange(target);
            r.AddRange(target);
            r.AddRange([0, 16, 0, 1, 0, 0, 0, 60, 0, 6, 5, .. "hello"u8.ToArray()]);
        }, 2);
        TxtLookupResult r = await stub.Client().LookupTxtAsync("alias.example.com", TestContext.Current.CancellationToken);
        Assert.Equal("hello", r.Records.Single());
    }

    [Fact]
    public async Task HostileRepliesNeverThrow()
    {
        using var stub = new Stub();
        var bad = new List<Func<byte[], byte[]>>
        {
            q => [],
            q => q[..5],
            q => Reply(q, null, 0, 0, id: 1234),
            q => [.. Reply(q, r => r.AddRange([0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 1, 0xFF, 0xFF]), 1)],
            q => Reply(q, r => r.AddRange([0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 1, 0, 3, 200, 1, 2]), 1),
            q => Reply(q, r => r.AddRange([0xC0, (byte)(Reply(q).Length), 0, 16, 0, 1, 0, 0, 0, 1, 0, 2, 1, 65]), 1),
            q => Reply(q, r => r.AddRange([0x0C | 0xC0, 0x0C - 0x0C + 0x0C, 0, 16]), 60000),
            q => Reply(q, r => { r.AddRange([0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 1, 0, 0]); }, 1),
        };
        foreach (Func<byte[], byte[]> b in bad)
        {
            stub.Respond = (q, _) => b(q);
            TxtLookupResult r = await stub.Client().LookupTxtAsync("x.example.com", TestContext.Current.CancellationToken);
            Assert.NotEqual(TxtLookupStatus.Success, r.Status);
        }

        // Garbage straight into the parser, including compression pointer loops.
        byte[] query = [0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, (byte)'x', 0, 0, 16, 0, 1];
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            byte[] junk = Reply(query, r => r.AddRange(Enumerable.Range(0, rnd.Next(0, 60)).Select(_ => (byte)rnd.Next(256))), rnd.Next(0, 4));
            DnsReply p = DnsWire.ParseReply(junk, 1, "x");
            Assert.NotNull(p);
        }

        // A pointer loop.
        byte[] loop = Reply(query, r => r.AddRange([0xC0, (byte)r.Count, 0, 16, 0, 1, 0, 0, 0, 1, 0, 1, 0]), 1);
        Assert.Equal(DnsReplyKind.Malformed, DnsWire.ParseReply(loop, 1, "x").Kind);
    }

    [Fact]
    public async Task SilentServerIsATemporaryFailureAndCancellationThrows()
    {
        using var stub = new Stub();
        TxtLookupResult r = await stub.Client(attempts: 2).LookupTxtAsync("x.example.com", TestContext.Current.CancellationToken);
        Assert.Equal(TxtLookupStatus.TemporaryFailure, r.Status);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stub.Client().LookupTxtAsync("x.example.com", cts.Token));
    }

    [Fact]
    public async Task InvalidNamesAreNotQueried()
    {
        using var stub = new Stub();
        TxtLookupResult r = await stub.Client().LookupTxtAsync(new string('a', 64) + ".example.com", TestContext.Current.CancellationToken);
        Assert.Equal(TxtLookupStatus.NameNotFound, r.Status);
    }

    // Talks to the system's resolver; runs only when ONEMAIL_DNS_LIVE=1 (set on the machine that has DNS).
    [Fact]
    public async Task SystemResolverFindsARealKeyAndAMissingName()
    {
        if (Environment.GetEnvironmentVariable("ONEMAIL_DNS_LIVE") != "1")
        {
            return;
        }

        var client = new DnsTxtClient();
        Assert.NotEmpty(client.Servers);
        // A retired selector is published with an empty p=: a real revoked key.
        TxtLookupResult retired = await client.LookupTxtAsync("20230601._domainkey.gmail.com", TestContext.Current.CancellationToken);
        Assert.Equal(TxtLookupStatus.Success, retired.Status);
        Assert.True(DkimKeyRecord.TryParse(retired.Records[0], out DkimKeyRecord? revoked, out _, out _));
        Assert.True(revoked!.Revoked);
        // A selector that is a CNAME to another provider's TXT record: the chain is followed.
        TxtLookupResult ok = await client.LookupTxtAsync("s1._domainkey.github.com", TestContext.Current.CancellationToken);
        Assert.Equal(TxtLookupStatus.Success, ok.Status);
        Assert.True(DkimKeyRecord.TryParse(ok.Records[0], out DkimKeyRecord? key, out _, out _));
        Assert.True(key!.KeyBits >= 2048, key.KeyBits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        TxtLookupResult none = await client.LookupTxtAsync("no-such-selector-onemail._domainkey.gmail.com", TestContext.Current.CancellationToken);
        Assert.Equal(TxtLookupStatus.NameNotFound, none.Status);
        // A TXT answer that does not fit a UDP datagram comes back over TCP.
        TxtLookupResult big = await client.LookupTxtAsync("google.com", TestContext.Current.CancellationToken);
        Assert.Equal(TxtLookupStatus.Success, big.Status);
        Assert.True(big.Records.Count > 3);
    }
}
