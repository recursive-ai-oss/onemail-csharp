using System.Diagnostics;

using MailKit.Security;
using MimeKit;
using RecursiveAI.OneMail.Client.Smtp;
using RecursiveAI.OneMail.Composition;

// SmtpCompare <host> <port> <user> <password> [count=1000] [security=starttls|implicit]
// Sends `count` messages of about 4 KB to <recipient> with each library over one authenticated connection, three rounds each.
string host = args[0];
int port = int.Parse(args[1]);
string user = args[2], pass = args[3];
int count = args.Length > 4 ? int.Parse(args[4]) : 1000;
bool implicitTls = args.Length > 5 && args[5] == "implicit";
const string recipient = "test@example.test";
string body = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. 0123456789\n", 70));

static bool AcceptAll(object? s, System.Security.Cryptography.X509Certificates.X509Certificate? c, System.Security.Cryptography.X509Certificates.X509Chain? ch, System.Net.Security.SslPolicyErrors e) => c is not null;

async Task<(double Seconds, long Bytes, int Ok)> Ours()
{
    long before = GC.GetTotalAllocatedBytes(true);
    var sw = Stopwatch.StartNew();
    int ok = 0;
    await using RecursiveAI.OneMail.Client.Smtp.SmtpClient c = await RecursiveAI.OneMail.Client.Smtp.SmtpClient.ConnectAsync(new SmtpClientOptions
    {
        Host = host, Port = port, Security = implicitTls ? SmtpSecurity.ImplicitTls : SmtpSecurity.StartTls,
        Credentials = new RecursiveAI.OneMail.Client.Sasl.SaslCredentials { UserName = user, Password = pass },
        Connection = new RecursiveAI.OneMail.Client.Protocol.ProtocolConnectionOptions { ServerCertificateValidationCallback = AcceptAll },
    });
    for (int i = 0; i < count; i++)
    {
        var m = new OutgoingMessage { From = "sender@example.com", To = [recipient], Subject = "compare " + i, Text = body, MessageId = "ours-" + i + "@compare.test" };
        if ((await c.SendAsync(SmtpMessage.FromEntity(m.ToEntity()))).Delivered) { ok++; }
    }

    await c.QuitAsync();
    sw.Stop();
    return (sw.Elapsed.TotalSeconds, GC.GetTotalAllocatedBytes(true) - before, ok);
}

async Task<(double Seconds, long Bytes, int Ok)> MailKitRun()
{
    long before = GC.GetTotalAllocatedBytes(true);
    var sw = Stopwatch.StartNew();
    int ok = 0;
    using var c = new MailKit.Net.Smtp.SmtpClient { ServerCertificateValidationCallback = AcceptAll };
    await c.ConnectAsync(host, port, implicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
    await c.AuthenticateAsync(user, pass);
    for (int i = 0; i < count; i++)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "sender@example.com"));
        m.To.Add(new MailboxAddress("", recipient));
        m.Subject = "compare " + i;
        m.MessageId = "mailkit-" + i + "@compare.test";
        m.Body = new TextPart("plain") { Text = body };
        await c.SendAsync(m);
        ok++;
    }

    await c.DisconnectAsync(true);
    sw.Stop();
    return (sw.Elapsed.TotalSeconds, GC.GetTotalAllocatedBytes(true) - before, ok);
}

Console.WriteLine($"{count} messages per run, host {host}:{port}, {Environment.ProcessorCount} cores");
foreach (string which in new[] { "warmup-ours", "warmup-mailkit" })
{
    _ = which == "warmup-ours" ? await Ours() : await MailKitRun();
}

for (int round = 1; round <= 3; round++)
{
    var o = await Ours();
    var k = await MailKitRun();
    Console.WriteLine($"round {round}: ours {o.Seconds:F2}s {o.Bytes / count} B/msg ok={o.Ok} | MailKit {k.Seconds:F2}s {k.Bytes / count} B/msg ok={k.Ok} | ratio {o.Seconds / k.Seconds:F2}");
}
