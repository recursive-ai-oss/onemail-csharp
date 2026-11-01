// Usage: ImapCompare <port> <user> <password> <enron-maildir-root> [count=5000] [reps=3]
// Uploads the corpus sample into a new mailbox with this library, then times the same fetches with this library and with MailKit
// over implicit TLS to 127.0.0.1, reporting best-of-reps wall time and bytes allocated per message. Indicative only.
using System.Diagnostics;
using MK = MailKit;
using MKImap = MailKit.Net.Imap;
using OM = RecursiveAI.OneMail.Client.Imap;
using RecursiveAI.OneMail.Client.Protocol;

int port = int.Parse(args[0]);
string user = args[1], password = args[2], root = args[3];
int count = args.Length > 4 ? int.Parse(args[4]) : 5000;
int reps = args.Length > 5 ? int.Parse(args[5]) : 3;

string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
int step = Math.Max(1, files.Length / count);
List<byte[]> messages = files.Where((_, i) => i % step == 0).Take(count).Select(f => Crlf(File.ReadAllBytes(f))).ToList();
string mailbox = "cmp-" + Guid.NewGuid().ToString("N")[..8];

var ours = await OM.ImapClient.ConnectAsync("127.0.0.1", port, new OM.ImapClientOptions
{
    Connection = new ProtocolConnectionOptions { ImplicitTls = true, ServerCertificateValidationCallback = (_, c, _, _) => c is not null },
});
await ours.LoginAsync(user, password);
await ours.CreateAsync(mailbox);
for (int i = 0; i < messages.Count; i += 100)
{
    await ours.AppendAsync(mailbox, messages.Skip(i).Take(100).Select(m => new OM.ImapAppendItem(m)).ToList());
}

await ours.SelectAsync(mailbox, new OM.ImapSelectOptions { ReadOnly = true });
List<long> uids = (await ours.SearchAsync(OM.ImapSearch.All, uid: true)).Numbers.ToList();
Console.WriteLine($"{uids.Count} messages in {mailbox}, {messages.Sum(m => (long)m.Length) / 1024} KiB");

var mk = new MKImap.ImapClient { ServerCertificateValidationCallback = (_, cert, _, _) => cert is not null };
await mk.ConnectAsync("127.0.0.1", port, MK.Security.SecureSocketOptions.SslOnConnect);
await mk.AuthenticateAsync(user, password);
MK.IMailFolder folder = await mk.GetFolderAsync(mailbox);
await folder.OpenAsync(MK.FolderAccess.ReadOnly);
var mkIds = uids.Select(u => new MailKit.UniqueId((uint)u)).ToList();

void Report(string row, string who, double ms, long allocated, int n) =>
    Console.WriteLine($"{row,-34} {who,-8} {ms,9:F1} ms  {allocated / Math.Max(1, n),9} B/msg");

async Task Row(string row, Func<Task<int>> oursRun, Func<Task<int>> mkRun)
{
    foreach ((string who, Func<Task<int>> run) in new[] { ("OneMail", oursRun), ("MailKit", mkRun) })
    {
        await run(); // warm up
        double best = double.MaxValue;
        long alloc = 0;
        int n = 0;
        for (int r = 0; r < reps; r++)
        {
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            n = await run();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms < best) { best = ms; alloc = GC.GetTotalAllocatedBytes(true) - before; }
        }

        Report(row, who, best, alloc, n);
    }
}

var summary = new OM.ImapFetchQuery { Uid = true, Flags = true, InternalDate = true, Size = true, Envelope = true };
await Row("summary (uid flags date size env)",
    async () => { int n = 0; await foreach (OM.ImapMessage _ in ours.FetchAsync(OM.ImapSet.All, summary, uid: true)) { n++; } return n; },
    async () => (await folder.FetchAsync(mkIds, new MK.FetchRequest(MK.MessageSummaryItems.UniqueId | MK.MessageSummaryItems.Flags | MK.MessageSummaryItems.InternalDate
        | MK.MessageSummaryItems.Size | MK.MessageSummaryItems.Envelope))).Count);

await Row("full message, one FETCH each",
    async () => { int n = 0; foreach (long u in uids) { var ms = new MemoryStream(); await ours.DownloadAsync(u, "", ms); n++; } return n; },
    async () => { int n = 0; foreach (MailKit.UniqueId u in mkIds) { using Stream s = await folder.GetStreamAsync(u, ""); var ms = new MemoryStream(); await s.CopyToAsync(ms); n++; } return n; });

int bulk = 0;
Console.WriteLine("(OneMail only) full messages in one FETCH:");
GC.Collect();
long b0 = GC.GetTotalAllocatedBytes(true);
var sw2 = Stopwatch.StartNew();
await foreach (OM.ImapMessage m in ours.FetchAsync(OM.ImapSet.All, new OM.ImapFetchQuery { Source = true }, uid: true)) { bulk += m.Source.Length > 0 ? 1 : 0; }
Report("full message, bulk FETCH", "OneMail", sw2.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - b0, bulk);

await mk.DisconnectAsync(true);
await ours.CloseMailboxAsync();
await ours.DeleteAsync(mailbox);
await ours.LogoutAsync();

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
