// ProtocolRows <row> <side>
//
//   rows:  imap-prepare   upload the sample into a fresh mailbox (run once, with the ours side)
//          imap-summary   summary fetch (UID, flags, internal date, size, envelope) of every message in the mailbox
//          imap-full      full BODY[] of the first ROWS_FULL messages, one FETCH each, into memory
//          smtp-small     ROWS_SMALL messages of about 4 KB over one authenticated connection
//          smtp-big       ROWS_BIG messages of about 1 MB over one connection
//   sides: ours | mailkit | mailkit-nodelay (SMTP only: MailKit on a socket we connected with TCP_NODELAY set) | ours-data (SMTP only: DATA, no BDAT)
//
// Environment: ROWS_HOST (127.0.0.1), ROWS_PORT, ROWS_USER, ROWS_PASS, ROWS_SECURITY (implicit | starttls), ROWS_REPS (3),
// ROWS_CORPUS (a maildir for the IMAP sample), ROWS_MAILBOX (rows), ROWS_COUNT (5000), ROWS_FULL (1000), ROWS_SMALL (1000), ROWS_BIG (100).
// Prints one line per repetition and a median line: wall milliseconds for the timed loop only (connection, TLS and login are outside it),
// bytes allocated per message, and the process's peak working set. Each side runs in its own process so the peaks do not mix.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MK = MailKit;
using MKImap = MailKit.Net.Imap;
using MKSmtp = MailKit.Net.Smtp;
using MKSec = MailKit.Security;
using MimeKit;
using OMImap = RecursiveAI.OneMail.Client.Imap;
using OMSmtp = RecursiveAI.OneMail.Client.Smtp;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Composition;

string row = args[0], side = args[1];
string host = Env("ROWS_HOST", "127.0.0.1");
int port = int.Parse(Env("ROWS_PORT", "0"));
string user = Env("ROWS_USER", "test"), pass = Env("ROWS_PASS", "onemail-test");
bool implicitTls = Env("ROWS_SECURITY", "implicit") == "implicit";
int reps = int.Parse(Env("ROWS_REPS", "3"));
string mailbox = Env("ROWS_MAILBOX", "rows");

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
static bool AcceptAll(object? s, System.Security.Cryptography.X509Certificates.X509Certificate? c, System.Security.Cryptography.X509Certificates.X509Chain? ch, System.Net.Security.SslPolicyErrors e) => c is not null;

switch (row)
{
    case "imap-prepare": await ImapPrepare(); break;
    case "imap-summary": await ImapSummary(); break;
    case "imap-full": await ImapFull(); break;
    case "smtp-wire": await SmtpWire(); break;
    case "smtp-trace": await SmtpTrace(); break;
    case "smtp-small": await Smtp(int.Parse(Env("ROWS_SMALL", "1000")), 4 * 1024); break;
    case "smtp-big": await Smtp(int.Parse(Env("ROWS_BIG", "100")), 1024 * 1024); break;
    default: throw new ArgumentException("unknown row " + row);
}

// Runs the timed body `reps` times after one untimed warm-up and prints the rows and their median.
async Task Measure(string name, int messages, Func<Task<int>> body)
{
    await body();
    var times = new List<double>();
    var allocs = new List<long>();
    for (int r = 1; r <= reps; r++)
    {
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        int done = await body();
        double ms = sw.Elapsed.TotalMilliseconds;
        long alloc = (GC.GetTotalAllocatedBytes(true) - before) / Math.Max(1, done);
        times.Add(ms);
        allocs.Add(alloc);
        if (done != messages) { Console.WriteLine($"WARNING: {done} of {messages} messages"); }
        Console.WriteLine($"{name}\t{side}\trep {r}\t{ms:F1} ms\t{alloc} B/msg");
    }

    double median = times.Order().ElementAt(times.Count / 2);
    long allocMedian = allocs.Order().ElementAt(allocs.Count / 2);
    long peak = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
    Console.WriteLine($"{name}\t{side}\tmedian\t{median:F1} ms\t{allocMedian} B/msg\tpeak {peak} MiB");
}

// ---- IMAP ----

async Task<OMImap.ImapClient> OursImap()
{
    var c = await OMImap.ImapClient.ConnectAsync(host, port, new OMImap.ImapClientOptions
    {
        Connection = new ProtocolConnectionOptions { ImplicitTls = implicitTls, ServerCertificateValidationCallback = AcceptAll },
    });
    await c.LoginAsync(user, pass);
    return c;
}

async Task<MKImap.ImapClient> MailKitImap()
{
    var c = new MKImap.ImapClient { ServerCertificateValidationCallback = AcceptAll };
    await c.ConnectAsync(host, port, implicitTls ? MKSec.SecureSocketOptions.SslOnConnect : MKSec.SecureSocketOptions.StartTls);
    await c.AuthenticateAsync(user, pass);
    return c;
}

async Task ImapPrepare()
{
    string root = Env("ROWS_CORPUS", "");
    int count = int.Parse(Env("ROWS_COUNT", "5000"));
    string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
    int step = Math.Max(1, files.Length / count);
    List<byte[]> messages = files.Where((_, i) => i % step == 0).Take(count).Select(f => Crlf(File.ReadAllBytes(f))).ToList();
    await using OMImap.ImapClient c = await OursImap();
    try { await c.DeleteAsync(mailbox); } catch (OMImap.ImapCommandException) { }
    await c.CreateAsync(mailbox);
    for (int i = 0; i < messages.Count; i += 100)
    {
        await c.AppendAsync(mailbox, messages.Skip(i).Take(100).Select(m => new OMImap.ImapAppendItem(m)).ToList());
    }

    Console.WriteLine($"{messages.Count} messages, {messages.Sum(m => (long)m.Length) / 1024} KiB in {mailbox}");
    await c.LogoutAsync();
}

async Task ImapSummary()
{
    if (side == "ours")
    {
        await using OMImap.ImapClient c = await OursImap();
        await c.SelectAsync(mailbox, new OMImap.ImapSelectOptions { ReadOnly = true });
        var q = new OMImap.ImapFetchQuery { Uid = true, Flags = true, InternalDate = true, Size = true, Envelope = true };
        int n = (int)c.Mailbox!.Exists;
        await Measure("imap-summary", n, async () =>
        {
            int seen = 0;
            await foreach (OMImap.ImapMessage _ in c.FetchAsync(OMImap.ImapSet.All, q, uid: true)) { seen++; }
            return seen;
        });
    }
    else
    {
        using MKImap.ImapClient c = await MailKitImap();
        MK.IMailFolder f = await c.GetFolderAsync(mailbox);
        await f.OpenAsync(MK.FolderAccess.ReadOnly);
        int n = f.Count;
        var request = new MK.FetchRequest(MK.MessageSummaryItems.UniqueId | MK.MessageSummaryItems.Flags | MK.MessageSummaryItems.InternalDate
            | MK.MessageSummaryItems.Size | MK.MessageSummaryItems.Envelope);
        await Measure("imap-summary", n, async () => (await f.FetchAsync(MK.UniqueIdRange.All, request)).Count);
        await c.DisconnectAsync(true);
    }
}

async Task ImapFull()
{
    int count = int.Parse(Env("ROWS_FULL", "1000"));
    if (side == "ours")
    {
        await using OMImap.ImapClient c = await OursImap();
        await c.SelectAsync(mailbox, new OMImap.ImapSelectOptions { ReadOnly = true });
        List<long> uids = (await c.SearchAsync(OMImap.ImapSearch.All, uid: true)).Numbers.Take(count).ToList();
        await Measure("imap-full", uids.Count, async () =>
        {
            foreach (long u in uids) { var ms = new MemoryStream(); await c.DownloadAsync(u, "", ms); }
            return uids.Count;
        });
    }
    else
    {
        using MKImap.ImapClient c = await MailKitImap();
        MK.IMailFolder f = await c.GetFolderAsync(mailbox);
        await f.OpenAsync(MK.FolderAccess.ReadOnly);
        List<MK.UniqueId> uids = (await f.SearchAsync(MK.Search.SearchQuery.All)).Take(count).ToList();
        await Measure("imap-full", uids.Count, async () =>
        {
            foreach (MK.UniqueId u in uids)
            {
                using Stream s = await f.GetStreamAsync(u, "");
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
            }

            return uids.Count;
        });
        await c.DisconnectAsync(true);
    }
}

static byte[] Crlf(byte[] raw)
{
    var ms = new MemoryStream(raw.Length + raw.Length / 20);
    for (int i = 0; i < raw.Length; i++)
    {
        if (raw[i] == (byte)'\n' && (i == 0 || raw[i - 1] != (byte)'\r')) { ms.WriteByte((byte)'\r'); }
        ms.WriteByte(raw[i]);
    }

    return ms.ToArray();
}

// ---- SMTP ----

// The same text on both sides: ASCII lines of 70 characters, so neither library has to pick a transfer encoding for it.
static string BodyOf(int bytes)
{
    const string line = "The quick brown fox jumps over the lazy dog. 0123456789 abcdefghijklmn\n";
    return string.Concat(Enumerable.Repeat(line, Math.Max(1, bytes / line.Length)));
}

async Task Smtp(int count, int size)
{
    string body = BodyOf(size);
    const string recipient = "test@example.test";
    var date = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    string name = size > 64 * 1024 ? "smtp-big" : "smtp-small";
    int serial = 0;
    if (side is "ours" or "ours-data")
    {
        await using OMSmtp.SmtpClient c = await OMSmtp.SmtpClient.ConnectAsync(new OMSmtp.SmtpClientOptions
        {
            UseChunking = side == "ours",
            Host = host, Port = port, Security = implicitTls ? OMSmtp.SmtpSecurity.ImplicitTls : OMSmtp.SmtpSecurity.StartTls,
            Credentials = new RecursiveAI.OneMail.Client.Sasl.SaslCredentials { UserName = user, Password = pass },
            Connection = new ProtocolConnectionOptions { ServerCertificateValidationCallback = AcceptAll },
        });
        // The rows time the send of messages that are already built, on both sides: building is separate work and is not what the
        // protocol rows compare. Each repetition sends the same prepared messages again.
        var prepared = new OMSmtp.SmtpMessage[count];
        for (int i = 0; i < count; i++)
        {
            var m = new OutgoingMessage { From = "sender@example.com", To = [recipient], Subject = "row " + i, Text = body, MessageId = "ours-" + (serial++) + "@rows.test", Date = date };
            prepared[i] = OMSmtp.SmtpMessage.FromEntity(m.ToEntity());
        }

        await Measure(name, count, async () =>
        {
            int ok = 0;
            for (int i = 0; i < count; i++)
            {
                if ((await c.SendAsync(prepared[i])).Delivered) { ok++; }
            }

            return ok;
        });
        await c.QuitAsync();
    }
    else
    {
        using var c = new MKSmtp.SmtpClient { ServerCertificateValidationCallback = AcceptAll };
        var opts = implicitTls ? MKSec.SecureSocketOptions.SslOnConnect : MKSec.SecureSocketOptions.StartTls;
        if (side == "mailkit-nodelay")
        {
            // The one difference: the socket is ours, connected with TCP_NODELAY set as this library's connections are.
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port));
            await c.ConnectAsync(socket, host, port, opts);
        }
        else
        {
            await c.ConnectAsync(host, port, opts);
        }

        await c.AuthenticateAsync(user, pass);
        var prepared = new MimeMessage[count];
        for (int i = 0; i < count; i++)
        {
            var m = new MimeMessage();
            m.From.Add(new MailboxAddress("", "sender@example.com"));
            m.To.Add(new MailboxAddress("", recipient));
            m.Subject = "row " + i;
            m.MessageId = "mailkit-" + (serial++) + "@rows.test";
            m.Date = date;
            m.Body = new TextPart("plain") { Text = body };
            prepared[i] = m;
        }

        await Measure(name, count, async () =>
        {
            int ok = 0;
            for (int i = 0; i < count; i++)
            {
                await c.SendAsync(prepared[i]);
                ok++;
            }

            return ok;
        });
        await c.DisconnectAsync(true);
    }
}

// ---- the wire pattern of one message ----

// Sends one 4 KB message with each side to a loopback server that logs every read as one flight, so the number of round trips and
// writes per message is visible. The server speaks PIPELINING, CHUNKING, SIZE, 8BITMIME, SMTPUTF8, as the Postfix guest does.
async Task SmtpWire()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int p = ((IPEndPoint)listener.LocalEndpoint).Port;
    foreach (string who in new[] { "ours", "mailkit" })
    {
        Task server = Task.Run(() =>
        {
            using Socket s = listener.AcceptSocket();
            s.NoDelay = true;
            void Say(string t) { s.Send(System.Text.Encoding.ASCII.GetBytes(t)); Console.WriteLine("    server -> " + t.Replace("\r\n", " | ")); }
            Say("220 wire ESMTP\r\n");
            byte[] buf = new byte[65536];
            long skip = 0;
            bool inData = false;
            while (true)
            {
                int n = s.Receive(buf);
                if (n == 0) { return; }
                string text = System.Text.Encoding.Latin1.GetString(buf, 0, n);
                string head = string.Join(" | ", text.Split("\r\n").Take(6).Select(l => l.Length > 40 ? l[..40] + "..." : l));
                Console.WriteLine($"  client flight, {n} bytes: {head}");
                foreach (string line in text.Split("\r\n"))
                {
                    if (inData) { if (line == ".") { inData = false; Say("250 queued\r\n"); } continue; }
                    if (skip > 0) { continue; }
                    if (line.StartsWith("EHLO", StringComparison.Ordinal)) { Say("250-wire\r\n250-PIPELINING\r\n250-SIZE 52428800\r\n250-8BITMIME\r\n250-CHUNKING\r\n250 SMTPUTF8\r\n"); }
                    else if (line.StartsWith("MAIL", StringComparison.Ordinal) || line.StartsWith("RCPT", StringComparison.Ordinal)) { Say("250 ok\r\n"); }
                    else if (line.StartsWith("DATA", StringComparison.Ordinal)) { Say("354 go\r\n"); inData = true; }
                    else if (line.StartsWith("BDAT", StringComparison.Ordinal)) { Say("250 chunk\r\n"); }
                    else if (line.StartsWith("QUIT", StringComparison.Ordinal)) { Say("221 bye\r\n"); return; }
                    else if (line.StartsWith("RSET", StringComparison.Ordinal) || line.StartsWith("NOOP", StringComparison.Ordinal)) { Say("250 ok\r\n"); }
                }
            }
        });
        Console.WriteLine("== " + who);
        string body = BodyOf(4096);
        if (who == "ours")
        {
            await using OMSmtp.SmtpClient c = await OMSmtp.SmtpClient.ConnectAsync(new OMSmtp.SmtpClientOptions { Host = "127.0.0.1", Port = p, Security = OMSmtp.SmtpSecurity.None });
            Console.WriteLine("  -- message");
            await c.SendAsync(OMSmtp.SmtpMessage.FromEntity(new OutgoingMessage { From = "a@example.com", To = ["b@example.com"], Subject = "x", Text = body }.ToEntity()));
            await c.QuitAsync();
        }
        else
        {
            using var c = new MKSmtp.SmtpClient();
            await c.ConnectAsync("127.0.0.1", p, MKSec.SecureSocketOptions.None);
            Console.WriteLine("  -- message");
            var m = new MimeMessage();
            m.From.Add(new MailboxAddress("", "a@example.com"));
            m.To.Add(new MailboxAddress("", "b@example.com"));
            m.Subject = "x";
            m.Body = new TextPart("plain") { Text = body };
            await c.SendAsync(m);
            await c.DisconnectAsync(true);
        }

        await server;
    }
}

// ---- timed trace of a few messages against the real server ----

// Prints each command and reply with the time since the previous line, for ROWS_TRACE (5) messages after the first, so a gap such as
// a delayed acknowledgement shows up as a round trip that takes tens of milliseconds.
async Task SmtpTrace()
{
    int n = int.Parse(Env("ROWS_TRACE", "5"));
    var clock = Stopwatch.StartNew();
    long last = 0;
    void Line(string who, string text)
    {
        long now = clock.ElapsedTicks * 1000000 / Stopwatch.Frequency;
        Console.WriteLine($"{side,-16} +{(now - last) / 1000.0,7:F2} ms  {who} {(text.Length > 70 ? text[..70] : text).Replace("\r", "").Replace("\n", " | ")}");
        last = now;
    }

    var body = BodyOf(4096);
    if (side == "ours")
    {
        var trace = new ClockTrace(Line);
        await using OMSmtp.SmtpClient c = await OMSmtp.SmtpClient.ConnectAsync(new OMSmtp.SmtpClientOptions
        {
            Host = host, Port = port, Security = implicitTls ? OMSmtp.SmtpSecurity.ImplicitTls : OMSmtp.SmtpSecurity.StartTls,
            Credentials = new RecursiveAI.OneMail.Client.Sasl.SaslCredentials { UserName = user, Password = pass },
            Connection = new ProtocolConnectionOptions { ServerCertificateValidationCallback = AcceptAll, Trace = trace },
        });
        for (int i = 0; i < n + 1; i++)
        {
            if (i == 1) { Console.WriteLine("-- traced messages"); }
            var m = new OutgoingMessage { From = "sender@example.com", To = ["test@example.test"], Subject = "t" + i, Text = body, MessageId = "t" + i + "@rows.test" };
            trace.On = i > 0;
            await c.SendAsync(OMSmtp.SmtpMessage.FromEntity(m.ToEntity()));
            if (i > 0) { Line("--", "message done"); }
        }

        await c.QuitAsync();
    }
    else
    {
        var sink = new ClockStream(Line);
        using var c = new MKSmtp.SmtpClient(new MK.ProtocolLogger(sink)) { ServerCertificateValidationCallback = AcceptAll };
        var opts = implicitTls ? MKSec.SecureSocketOptions.SslOnConnect : MKSec.SecureSocketOptions.StartTls;
        if (side == "mailkit-nodelay")
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port));
            await c.ConnectAsync(socket, host, port, opts);
        }
        else
        {
            await c.ConnectAsync(host, port, opts);
        }

        await c.AuthenticateAsync(user, pass);
        for (int i = 0; i < n + 1; i++)
        {
            if (i == 1) { Console.WriteLine("-- traced messages"); }
            var m = new MimeMessage();
            m.From.Add(new MailboxAddress("", "sender@example.com"));
            m.To.Add(new MailboxAddress("", "test@example.test"));
            m.Subject = "t" + i;
            m.MessageId = "t" + i + "@rows.test";
            m.Body = new TextPart("plain") { Text = body };
            sink.On = i > 0;
            await c.SendAsync(m);
            if (i > 0) { Line("--", "message done"); }
        }

        await c.DisconnectAsync(true);
    }
}

sealed class ClockTrace(Action<string, string> line) : IProtocolTrace
{
    public bool On;
    public void Sent(ReadOnlySpan<byte> data) { if (On) { foreach (string l in System.Text.Encoding.Latin1.GetString(data).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Take(4)) { line("C:", l); } } }
    public void Received(ReadOnlySpan<byte> data) { if (On) { foreach (string l in System.Text.Encoding.Latin1.GetString(data).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Take(4)) { line("S:", l); } } }
}

sealed class ClockStream(Action<string, string> line) : Stream
{
    public bool On;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { if (On) { foreach (string l in System.Text.Encoding.Latin1.GetString(buffer, offset, count).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Take(3)) { line("", l); } } }
}
