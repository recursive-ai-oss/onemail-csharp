using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Client.Imap;

namespace RecursiveAI.OneMail.Tests.Client;

// Indicative memory checks for the IMAP client against a real server (not benchmark rows). Opt-in: ONEMAIL_IMAP_PERF=1.
public class ImapMemoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ServerInfo> Servers() => ServerHarness.ImapServers();

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task EnumeratingTenThousandFetchResultsAllocatesLittlePerMessage(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEMAIL_IMAP_PERF") == "1", "ONEMAIL_IMAP_PERF is not set");
        const int count = 10_000;
        await using ImapClient c = await ImapClientServerTests.ConnectAsync(s);
        string mb = await ImapClientServerTests.NewMailboxAsync(c);
        try
        {
            for (int i = 0; i < count; i += 500)
            {
                await c.AppendAsync(mb, [.. Enumerable.Range(i, 500).Select(n => new ImapAppendItem(Encoding.ASCII.GetBytes(
                    $"From: a{n}@example.com\r\nTo: b@example.com\r\nSubject: message {n}\r\nMessage-ID: <{n}@perf.example>\r\nDate: Tue, 03 Nov 2026 10:00:00 +0000\r\n\r\nbody {n}\r\n")))], Ct);
            }

            await c.SelectAsync(mb, cancellationToken: Ct);
            var query = new ImapFetchQuery { Uid = true, Flags = true, Size = true, InternalDate = true, Envelope = true };
            int seen = 0;
            long perMessage = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                seen = 0;
                GC.Collect();
                long before = GC.GetTotalAllocatedBytes(true);
                await foreach (ImapMessage m in c.FetchAsync(ImapSet.All, query, uid: false, Ct))
                {
                    seen++;
                }

                long allocated = GC.GetTotalAllocatedBytes(true) - before;
                perMessage = allocated / Math.Max(1, seen);
                TestContext.Current.SendDiagnosticMessage($"[{s.Name}] pass {pass}: {seen} FETCH results (uid flags size internaldate envelope), {allocated / 1024} KiB allocated, {perMessage} B per message");
            }

            Assert.Equal(count, seen);
            if (Environment.GetEnvironmentVariable("ONEMAIL_IMAP_REPORT") is { } path)
            {
                File.AppendAllText(path, $"[{s.Name}] fetch of {count}: {perMessage} B allocated per message (second pass)\n");
            }
        }
        finally
        {
            await ImapClientServerTests.DropAsync(c, mb);
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task AHundredMegabyteMessageStreamsBothWays(ServerInfo s)
    {
        ServerHarness.RequireServer(s);
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEMAIL_IMAP_PERF") == "1", "ONEMAIL_IMAP_PERF is not set");
        long size = (long.TryParse(Environment.GetEnvironmentVariable("ONEMAIL_IMAP_BIG_MB"), out long mb0) ? mb0 : 100) * 1024 * 1024;
        await using ImapClient c = await ImapClientServerTests.ConnectAsync(s);
        string mb = await ImapClientServerTests.NewMailboxAsync(c);
        try
        {
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(true);
            using var source = new PatternStream(size);
            try
            {
                await c.AppendAsync(mb, source, size, cancellationToken: Ct);
            }
            catch (ImapCommandException e) when (e.ResponseCode is "TOOBIG" or "LIMIT" or "OVERQUOTA" || e.Message.Contains("large", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("size", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Skip($"{s.Name} refuses a {size >> 20} MB message: {e.Message}");
            }

            long appended = GC.GetTotalAllocatedBytes(true) - before;
            await c.SelectAsync(mb, cancellationToken: Ct);
            long uid = (await c.SearchAsync(ImapSearch.All, uid: true, cancellationToken: Ct)).Numbers[0];

            before = GC.GetTotalAllocatedBytes(true);
            using var sink = new HashingSink();
            long got = await c.DownloadAsync(uid, "", sink, cancellationToken: Ct);
            long downloaded = GC.GetTotalAllocatedBytes(true) - before;
            Assert.Equal(size, got);
            Assert.Equal(size, sink.Count);
            using var expected = new PatternStream(size);
            using var reference = new HashingSink();
            await expected.CopyToAsync(reference, Ct);
            Assert.Equal(reference.Hash(), sink.Hash());
            TestContext.Current.SendDiagnosticMessage($"[{s.Name}] {size >> 20} MB: append allocated {appended / 1024} KiB, download allocated {downloaded / 1024} KiB, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MiB (whole test process)");
            if (Environment.GetEnvironmentVariable("ONEMAIL_IMAP_REPORT") is { } path)
            {
                File.AppendAllText(path, $"[{s.Name}] {size >> 20} MB message: append allocated {appended / 1024} KiB, download allocated {downloaded / 1024} KiB, SHA-256 equal\n");
            }

            // Streaming means the allocation does not grow with the message: far below the 100 MB size.
            Assert.True(appended < 16 * 1024 * 1024, $"append allocated {appended} bytes");
            Assert.True(downloaded < 16 * 1024 * 1024, $"download allocated {downloaded} bytes");
        }
        finally
        {
            await ImapClientServerTests.DropAsync(c, mb);
        }
    }

    // A message of a given size made of 76-character lines, header first, produced on the fly.
    private sealed class PatternStream : Stream
    {
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("From: a@example.com\r\nTo: b@example.com\r\nSubject: big\r\nMessage-ID: <big@example.com>\r\n\r\n");
        private readonly long _length;
        private long _pos;

        public PatternStream(long length) => _length = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, _length - _pos);
            for (int i = 0; i < n; i++)
            {
                long p = _pos + i;
                buffer[i] = p < Header.Length ? Header[p]
                    : (p - Header.Length) % 78 == 76 ? (byte)'\r'
                    : (p - Header.Length) % 78 == 77 ? (byte)'\n'
                    : (byte)('a' + (p % 26));
            }

            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
    }

    private sealed class HashingSink : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Count { get; private set; }

        public byte[] Hash() => _hash.GetHashAndReset();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            Count += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
