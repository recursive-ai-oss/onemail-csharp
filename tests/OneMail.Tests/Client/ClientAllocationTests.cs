using System.Text;
using RecursiveAI.OneMail.Client.Imap;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Tests.Client;

// Allocation checks for the IMAP client hot paths over scripted server output: no sockets, deterministic.
public class ClientAllocationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Allocation counts are only meaningful when nothing else allocates beside them, so these two run on request, filtered to this class.
    private static bool Measure => Environment.GetEnvironmentVariable("ONEMAIL_ALLOC_TESTS") == "1";

    private const string MeasureHint = "ONEMAIL_ALLOC_TESTS is not set";

    private const string Greeting = "* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN UIDPLUS] ready\r\n";
    private const string Login = "A0001 OK [CAPABILITY IMAP4rev1 UIDPLUS] in\r\n";

    /// <summary>Serves the script in chunks of a fixed size, the way a socket hands over what has arrived.</summary>
    private sealed class ChunkedStream(byte[] script, int chunk) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, chunk), script.Length - _pos);
            script.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Write(ReadOnlySpan<byte> buffer) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class CountingSink : Stream
    {
        public long Total;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Total += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Total += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Total += buffer.Length; return ValueTask.CompletedTask; }
    }

    private static async Task<ImapClient> OpenAsync(byte[] script, int chunk)
    {
        var conn = ProtocolConnection.FromStream(new ChunkedStream(script, chunk), "localhost", new ProtocolConnectionOptions { AllowInsecureAuthentication = true });
        ImapClient c = await ImapClient.FromConnectionAsync(conn, cancellationToken: Ct);
        await c.LoginAsync("u", "p", Ct);
        return c;
    }

    private static string FetchLine(int n) =>
        $"* {n} FETCH (UID {n} FLAGS (\\Seen $Label1) INTERNALDATE \"03-Nov-2026 10:00:00 +0000\" RFC822.SIZE {200 + n} " +
        $"ENVELOPE (\"Tue, 03 Nov 2026 10:00:00 +0000\" \"message {n}\" ((\"Alice Example\" NIL \"a{n}\" \"example.com\")) ((\"Alice Example\" NIL \"a{n}\" \"example.com\")) " +
        $"((\"Alice Example\" NIL \"a{n}\" \"example.com\")) ((NIL NIL \"b\" \"example.com\") (\"Carol\" NIL \"c\" \"example.org\")) NIL NIL NIL \"<{n}@perf.example>\"))\r\n";

    [Fact]
    public async Task FetchEnumerationAllocatesLittlePerMessage()
    {
        Assert.SkipUnless(Measure, MeasureHint);
        const int count = 10_000;
        var sb = new StringBuilder(Greeting).Append(Login).Append("* 10000 EXISTS\r\nA0002 OK [READ-ONLY] done\r\n");
        for (int pass = 3; pass <= 4; pass++)
        {
            for (int i = 1; i <= count; i++)
            {
                sb.Append(FetchLine(i));
            }

            sb.Append('A').Append(pass.ToString("D4")).Append(" OK done\r\n");
        }

        await using ImapClient c = await OpenAsync(Encoding.UTF8.GetBytes(sb.ToString()), 16 * 1024);
        await c.SelectAsync("INBOX", cancellationToken: Ct);
        var query = new ImapFetchQuery { Uid = true, Flags = true, Size = true, InternalDate = true, Envelope = true };
        long perMessage = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int seen = 0;
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(true);
            await foreach (ImapMessage m in c.FetchAsync(ImapSet.All, query, uid: false, Ct))
            {
                seen++;
                if (m.Uid != seen) { throw new InvalidOperationException("uid"); }
            }

            perMessage = (GC.GetTotalAllocatedBytes(true) - before) / count;
            Assert.Equal(count, seen);
            TestContext.Current.SendDiagnosticMessage($"fetch pass {pass}: {perMessage} B per message");
        }

        // What is left is the item itself: ImapMessage, ImapFetchData, the flags, the envelope with its strings and five addresses (about 1 KB).
        Assert.True(perMessage < 1500, $"{perMessage} B per message");
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(16 * 1024)]
    public async Task DownloadAllocationDoesNotGrowWithTheNumberOfReads(int chunk)
    {
        Assert.SkipUnless(Measure, MeasureHint);
        const int size = 8 * 1024 * 1024;
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(Greeting + Login + "* 1 EXISTS\r\nA0002 OK [READ-ONLY] done\r\n"));
        ms.Write(Encoding.ASCII.GetBytes($"* 1 FETCH (UID 1 BODY[] {{{size}}}\r\n"));
        ms.Write(new byte[size]);
        ms.Write(Encoding.ASCII.GetBytes(")\r\nA0003 OK done\r\n"));
        await using ImapClient c = await OpenAsync(ms.ToArray(), chunk);
        await c.SelectAsync("INBOX", cancellationToken: Ct);
        var sink = new CountingSink();
        using (var warm = new CancellationTokenSource()) { warm.CancelAfter(TimeSpan.FromMinutes(1)); } // the first timer in a process allocates its queues
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        long written = await c.DownloadAsync(1, "", sink, cancellationToken: Ct);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.Equal(size, written);
        Assert.Equal(size, sink.Total);
        TestContext.Current.SendDiagnosticMessage($"download of {size} bytes in {chunk}-byte reads: {allocated} B allocated");
        Assert.True(allocated < 16 * 1024, $"{allocated} B allocated");
    }

    // Lines the fast path takes and lines it hands to the general parser must decode to the same values.
    public static TheoryData<string> FetchLines() =>
    [
        "* 1 FETCH (UID 7 FLAGS (\\Seen \\Answered $Forwarded custom-Flag) INTERNALDATE \"03-Nov-2026 10:00:00 +0530\" RFC822.SIZE 1234 MODSEQ (99) EMAILID (M1abc) THREADID (T22))",
        "* 2 FETCH (FLAGS ())",
        "* 3 FETCH (INTERNALDATE \" 3-Nov-2026 10:00:00 -0800\" UID 5)",
        "* 4 FETCH (INTERNALDATE \"31-Feb-2026 10:00:00 +0000\")",
        "* 5 FETCH (UID 9 ENVELOPE (NIL NIL NIL NIL NIL NIL NIL NIL NIL NIL))",
        "* 6 FETCH (ENVELOPE (\"Tue, 03 Nov 2026 10:00:00 +0000\" \"a \\\"quoted\\\" \\\\ subject\" ((\"Zo\u00eb\" NIL \"zoe\" \"example.com\")) NIL NIL ((NIL NIL \"undisclosed\" NIL)(NIL NIL NIL NIL)) NIL NIL \"<r@x>\" \"<m@x>\"))",
        "* 7 FETCH (UID 1 ENVELOPE (\"d\" \"s\" ((\"n\" NIL \"m\" \"h\")) NIL NIL NIL))",
        "* 8 FETCH (UID 2 X-GM-MSGID 1278455344230334865 FLAGS (\\Seen))",
        "* 9 FETCH (UID 3 BODYSTRUCTURE (\"text\" \"plain\" (\"charset\" \"utf-8\") NIL NIL \"7bit\" 12 1))",
        "* 10 FETCH (UID abc)",
        "* 11 FETCH (uid 4 flags (\\SEEN) rfc822.size 5)",
        "* 12 FETCH (UID 6  FLAGS (\\Seen))",
    ];

    [Theory]
    [MemberData(nameof(FetchLines))]
    public async Task TheFastFetchPathDecodesLikeTheGeneralParser(string line)
    {
        byte[] script = Encoding.UTF8.GetBytes(line + "\r\n");
        var general = ProtocolConnection.FromStream(new ChunkedStream(script, 16 * 1024));
        RecursiveAI.OneMail.Client.Imap.Protocol.ImapResponse tree = await new RecursiveAI.OneMail.Client.Imap.Protocol.ImapResponseReader(general.Reader).ReadResponseAsync(null, Ct);
        Assert.True(tree.TryGetFetch(out RecursiveAI.OneMail.Client.Imap.Protocol.ImapFetchData expected));

        string full = Greeting + Login + "* 12 EXISTS\r\nA0002 OK [READ-ONLY] done\r\n" + line + "\r\nA0003 OK done\r\n";
        await using ImapClient c = await OpenAsync(Encoding.UTF8.GetBytes(full), 16 * 1024);
        await c.SelectAsync("INBOX", cancellationToken: Ct);
        ImapMessage? got = null;
        await foreach (ImapMessage m in c.FetchAsync(ImapSet.All, new ImapFetchQuery { Uid = true }, cancellationToken: Ct))
        {
            got = m;
        }

        Assert.NotNull(got);
        RecursiveAI.OneMail.Client.Imap.Protocol.ImapFetchData actual = got.Data;
        Assert.Equal(expected.SequenceNumber, actual.SequenceNumber);
        Assert.Equal(expected.Uid, actual.Uid);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.InternalDateText, actual.InternalDateText);
        Assert.Equal(expected.InternalDate, actual.InternalDate);
        Assert.Equal(expected.Rfc822Size, actual.Rfc822Size);
        Assert.Equal(expected.ModSeq, actual.ModSeq);
        Assert.Equal(expected.EmailId, actual.EmailId);
        Assert.Equal(expected.ThreadId, actual.ThreadId);
        Assert.Equal(expected.Defects.Count, actual.Defects.Count);
        Assert.Equal(expected.Other.Select(o => o.Key), actual.Other.Select(o => o.Key));
        Assert.Equal(expected.BodyStructure is null, actual.BodyStructure is null);
        Assert.Equal(expected.Envelope is null, actual.Envelope is null);
        if (expected.Envelope is { } e && actual.Envelope is { } a)
        {
            Assert.Equal((e.Date, e.Subject, e.InReplyTo, e.MessageId), (a.Date, a.Subject, a.InReplyTo, a.MessageId));
            Assert.Equal(e.From, a.From);
            Assert.Equal(e.Sender, a.Sender);
            Assert.Equal(e.ReplyTo, a.ReplyTo);
            Assert.Equal(e.To, a.To);
            Assert.Equal(e.Cc, a.Cc);
            Assert.Equal(e.Bcc, a.Bcc);
        }
    }
}
