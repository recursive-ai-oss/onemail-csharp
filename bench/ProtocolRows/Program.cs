// ProtocolRows <row> <side>
//
//   rows:  imap-prepare   upload the sample into a fresh mailbox (run once, with the ours side)
//          imap-summary   summary fetch (UID, flags, internal date, size, envelope) of every message in the mailbox
//          imap-full      full BODY[] of the first ROWS_FULL messages, one FETCH each, into memory
//          smtp-small     ROWS_SMALL messages of about 4 KB over one authenticated connection
//          smtp-big       ROWS_BIG messages of about 1 MB over one connection
//   sides: ours | mailkit | mailkit-nodelay (SMTP only: MailKit on a socket we connected with TCP_NODELAY set)
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
    if (side == "ours")
    {
        await using OMSmtp.SmtpClient c = await OMSmtp.SmtpClient.ConnectAsync(new OMSmtp.SmtpClientOptions
        {
            Host = host, Port = port, Security = implicitTls ? OMSmtp.SmtpSecurity.ImplicitTls : OMSmtp.SmtpSecurity.StartTls,
            Credentials = new RecursiveAI.OneMail.Client.Sasl.SaslCredentials { UserName = user, Password = pass },
            Connection = new ProtocolConnectionOptions { ServerCertificateValidationCallback = AcceptAll },
        });
        await Measure(name, count, async () =>
        {
            int ok = 0;
            for (int i = 0; i < count; i++)
            {
                var m = new OutgoingMessage { From = "sender@example.com", To = [recipient], Subject = "row " + i, Text = body, MessageId = "ours-" + (serial++) + "@rows.test", Date = date };
                if ((await c.SendAsync(OMSmtp.SmtpMessage.FromEntity(m.ToEntity()))).Delivered) { ok++; }
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
        await Measure(name, count, async () =>
        {
            int ok = 0;
            for (int i = 0; i < count; i++)
            {
                var m = new MimeMessage();
                m.From.Add(new MailboxAddress("", "sender@example.com"));
                m.To.Add(new MailboxAddress("", recipient));
                m.Subject = "row " + i;
                m.MessageId = "mailkit-" + (serial++) + "@rows.test";
                m.Date = date;
                m.Body = new TextPart("plain") { Text = body };
                await c.SendAsync(m);
                ok++;
            }

            return ok;
        });
        await c.DisconnectAsync(true);
    }
}
