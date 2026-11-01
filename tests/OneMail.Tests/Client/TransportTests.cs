using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Tests.Client;

public class TransportTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private static async Task ReadLineAsync(Stream s)
    {
        var one = new byte[1];
        while (await s.ReadAsync(one, TestContext.Current.CancellationToken) == 1 && one[0] != (byte)'\n') { }
    }

    [Fact]
    public async Task StartTlsUpgradesAnInProcessConnectionAndValidatesViaTheHook()
    {
        (Stream clientSide, Stream serverSide) = ClientTestSupport.Pair();
        using X509Certificate2 cert = SelfSigned();
        bool hookCalled = false;
        var options = new ProtocolConnectionOptions
        {
            ServerCertificateValidationCallback = (_, c, _, _) => { hookCalled = true; return c is not null; },
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            ReadTimeout = TimeSpan.FromSeconds(10),
        };
        await using ProtocolConnection conn = ProtocolConnection.FromStream(clientSide, "localhost", options);
        var imap = new ImapRawConnection(conn);

        Task server = Task.Run(async () =>
        {
            var line = new byte[64];
            await ReadLineAsync(serverSide);
            await serverSide.WriteAsync("A0001 OK Begin TLS negotiation now\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await serverSide.FlushAsync(TestContext.Current.CancellationToken);
            await using var ssl = new SslStream(serverSide);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
            }, TestContext.Current.CancellationToken);
            int n = await ssl.ReadAsync(line, TestContext.Current.CancellationToken);
            Assert.Equal("A0001 NOOP\r\n", Encoding.ASCII.GetString(line, 0, n));
            await ssl.WriteAsync("A0001 OK done\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await ssl.FlushAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        // The server wrote its OK before the handshake; STARTTLS command text is not needed by this scripted peer.
        conn.Writer.WriteAscii("A0001 STARTTLS\r\n");
        await conn.Writer.FlushAsync(TestContext.Current.CancellationToken);
        ImapResponse ok = await imap.ReadResponseAsync(null, TestContext.Current.CancellationToken);
        Assert.True(ok.IsOk);
        await conn.StartTlsAsync(TestContext.Current.CancellationToken);
        Assert.True(conn.IsEncrypted);
        Assert.True(hookCalled);
        Assert.Equal(SslApplicationProtocol.Http11, conn.NegotiatedApplicationProtocol);
        ImapCommandResult r = await imap.ExecuteAsync(ImapCommand.Create("NOOP"), null, null, TestContext.Current.CancellationToken);
        Assert.True(r.IsOk);
        await server;
    }

    [Fact]
    public async Task DataBufferedAfterTheStartTlsAcknowledgementAbortsTheUpgrade()
    {
        // The attacker's plaintext rides in the same segment as the OK; it must never be read as part of the encrypted session.
        var buffered = new ScriptedStream("A0001 OK go\r\n* INJECTED\r\n"u8.ToArray());
        ProtocolConnection c2 = ProtocolConnection.FromStream(buffered);
        c2.Reader.ReadLine();
        await Assert.ThrowsAsync<MailProtocolViolationException>(async () => await c2.StartTlsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompressionRoundTripsBothDirections()
    {
        (Stream clientSide, Stream serverSide) = ClientTestSupport.Pair();
        await using ProtocolConnection conn = ProtocolConnection.FromStream(clientSide, "localhost", new ProtocolConnectionOptions { ReadTimeout = TimeSpan.FromSeconds(10) });
        var imap = new ImapRawConnection(conn);
        Task server = Task.Run(async () =>
        {
            await ReadLineAsync(serverSide);
            await serverSide.WriteAsync("A0001 OK DEFLATE active\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await serverSide.FlushAsync(TestContext.Current.CancellationToken);
            using var inflate = new System.IO.Compression.DeflateStream(serverSide, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            var buf = new byte[64];
            int n = await inflate.ReadAsync(buf, TestContext.Current.CancellationToken);
            Assert.Equal("A0001 NOOP\r\n", Encoding.ASCII.GetString(buf, 0, n));
            await using var deflate = new System.IO.Compression.DeflateStream(serverSide, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);
            await deflate.WriteAsync("A0001 OK compressed reply\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await deflate.FlushAsync(TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        // The scripted server above answers COMPRESS without reading it, so send it by hand.
        conn.Writer.WriteAscii("A0001 COMPRESS DEFLATE\r\n");
        await conn.Writer.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True((await imap.ReadResponseAsync(null, TestContext.Current.CancellationToken)).IsOk);
        await conn.EnableCompressionAsync(TestContext.Current.CancellationToken);
        Assert.True(conn.IsCompressed);
        ImapCommandResult r = await imap.ExecuteAsync(ImapCommand.Create("NOOP"), null, null, TestContext.Current.CancellationToken);
        Assert.Equal("compressed reply", r.Completion.Text);
        await server;
    }

    [Fact]
    public async Task ReadTimeoutSurfacesAsTimeoutException()
    {
        (Stream clientSide, Stream serverSide) = ClientTestSupport.Pair();
        using (serverSide)
        {
            await using ProtocolConnection conn = ProtocolConnection.FromStream(clientSide, "localhost", new ProtocolConnectionOptions { ReadTimeout = TimeSpan.FromMilliseconds(150) });
            await Assert.ThrowsAsync<TimeoutException>(async () => await conn.Reader.ReadLineAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task CancellationIsHonouredNotMistakenForATimeout()
    {
        (Stream clientSide, Stream serverSide) = ClientTestSupport.Pair();
        using (serverSide)
        {
            await using ProtocolConnection conn = ProtocolConnection.FromStream(clientSide);
            using var cts = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await conn.Reader.ReadLineAsync(cts.Token));
        }
    }

    [Fact]
    public async Task ConnectsOverRealSocketsWithImplicitTlsAndSni()
    {
        using X509Certificate2 cert = SelfSigned();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            string? sni = null;
            Task server = Task.Run(async () =>
            {
                using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { sni = name; return cert; },
                }, TestContext.Current.CancellationToken);
                await ssl.WriteAsync("* OK ready\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
                await ssl.FlushAsync(TestContext.Current.CancellationToken);
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);

            var options = new ProtocolConnectionOptions
            {
                ImplicitTls = true,
                TlsTargetHost = "mail.example.test",
                ServerCertificateValidationCallback = (_, c, _, _) => c is not null,
            };
            await using ImapRawConnection imap = await ImapRawConnection.ConnectAsync("127.0.0.1", port, options, TestContext.Current.CancellationToken);
            ImapResponse greeting = await imap.ReadGreetingAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ImapStatus.Ok, greeting.Status);
            Assert.True(imap.Connection.IsEncrypted);
            await server;
            Assert.Equal("mail.example.test", sni);

            // Default validation refuses a self-signed certificate.
            Task refused = Task.Run(async () =>
            {
                using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                await using var ssl = new SslStream(client.GetStream());
                try { await ssl.AuthenticateAsServerAsync(cert, false, false); } catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { }
            }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<System.Security.Authentication.AuthenticationException>(async () =>
                await ProtocolConnection.ConnectAsync("127.0.0.1", port, new ProtocolConnectionOptions { ImplicitTls = true, TlsTargetHost = "localhost" }, TestContext.Current.CancellationToken));
            await refused;
        }
        finally
        {
            listener.Stop();
        }
    }

    // A hundred megabytes of literal reach the MIME reader through the handler; the working set stays small.
    [Fact]
    public async Task HundredMegabyteLiteralStreamsInBoundedMemory()
    {
        const long Body = 100L * 1024 * 1024;
        string head = "From: a@example.com\r\nContent-Type: text/plain\r\n\r\n";
        long total = head.Length + Body;
        var source = new GeneratedStream("* 1 FETCH (BODY[] {" + total + "}\r\n", head, Body, " UID 1)\r\nA1 OK done\r\n");
        await using ProtocolConnection conn = ProtocolConnection.FromStream(source);
        var reader = new ImapResponseReader(conn.Reader);
        long seen = 0;
        long before = GC.GetTotalAllocatedBytes(true);
        ImapResponse r = await reader.ReadResponseAsync(async (_, s, ct) =>
        {
            using var mime = new MimeReader(s);
            while (await mime.ReadAsync(ct))
            {
                if (mime.Kind == MimeEventKind.Body)
                {
                    seen += mime.Length;
                }
            }

            return true;
        }, TestContext.Current.CancellationToken);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(r.TryGetFetch(out ImapFetchData f));
        Assert.Equal(1, f.Uid);
        Assert.Equal(Body, seen);
        // Allocation, not just retention, stays far below the literal size (buffers are pooled).
        Assert.True(allocated < 8 * 1024 * 1024, "allocated " + allocated);
        Assert.True((await reader.ReadResponseAsync(null, TestContext.Current.CancellationToken)).IsOk);
    }

    private sealed class GeneratedStream(string prefix, string head, long bodyLength, string suffix) : Stream
    {
        private readonly byte[] _prefix = Encoding.ASCII.GetBytes(prefix + head);
        private readonly byte[] _suffix = Encoding.ASCII.GetBytes(suffix);
        private long _pos;
        private static readonly byte[] Line = Encoding.ASCII.GetBytes(new string('x', 78) + "\r\n");

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override int Read(Span<byte> buffer)
        {
            long bodyStart = _prefix.Length;
            long bodyEnd = bodyStart + bodyLength;
            int n = 0;
            while (n < buffer.Length && _pos < bodyEnd + _suffix.Length)
            {
                if (_pos < bodyStart) { buffer[n++] = _prefix[_pos++]; }
                else if (_pos < bodyEnd)
                {
                    long off = (_pos - bodyStart) % Line.Length;
                    int take = (int)Math.Min(Math.Min(Line.Length - off, bodyEnd - _pos), buffer.Length - n);
                    Line.AsSpan((int)off, take).CopyTo(buffer[n..]);
                    n += take;
                    _pos += take;
                }
                else { buffer[n++] = _suffix[_pos++ - bodyEnd]; }
            }

            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
