using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RecursiveAI.OneMail.Tests.Client;

/// <summary>A tiny loopback SMTP server for client logic tests: records the commands and the delivered messages, rejects chosen recipients.</summary>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();

    public FakeSmtpServer(params string[] extensions)
    {
        Extensions = extensions;
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    public string[] Extensions { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Recipients whose address contains this text get 550.</summary>
    public string? RejectRecipientContaining { get; set; }

    /// <summary>The queue acknowledgement text.</summary>
    public List<string> Commands { get; } = [];

    public List<byte[]> Messages { get; } = [];

    public List<string> MailFromLines { get; } = [];

    private async Task Serve()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using TcpClient c = await _listener.AcceptTcpClientAsync(_cts.Token);
                await Session(c);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException) { }
    }

    private static async Task<string?> Line(Stream s)
    {
        var sb = new List<byte>();
        int b;
        byte[] one = new byte[1];
        while ((b = await s.ReadAsync(one)) > 0)
        {
            if (one[0] == (byte)'\n')
            {
                break;
            }

            sb.Add(one[0]);
        }

        return b == 0 && sb.Count == 0 ? null : Encoding.UTF8.GetString([.. sb]).TrimEnd('\r');
    }

    private static Task Say(Stream s, string text) => s.WriteAsync(Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n")).AsTask();

    private async Task Session(TcpClient c)
    {
        Stream s = c.GetStream();
        await Say(s, "220 fake.test ESMTP");
        var bdat = new MemoryStream();
        while (await Line(s) is { } line)
        {
            lock (Commands) { Commands.Add(line); }
            string verb = line.Split(' ')[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    await Say(s, "250-fake.test\n" + string.Join("\n", Extensions.Select(e => "250-" + e)) + "\n250 OK");
                    break;
                case "MAIL":
                    lock (MailFromLines) { MailFromLines.Add(line); }
                    await Say(s, "250 2.1.0 Sender ok");
                    break;
                case "RCPT":
                    await Say(s, RejectRecipientContaining is { } r && line.Contains(r, StringComparison.OrdinalIgnoreCase) ? "550 5.1.1 No such user" : "250 2.1.5 Recipient ok");
                    break;
                case "DATA":
                    await Say(s, "354 go ahead");
                    var ms = new MemoryStream();
                    var tail = new List<byte>();
                    byte[] one = new byte[1];
                    while (await s.ReadAsync(one) > 0)
                    {
                        ms.WriteByte(one[0]);
                        if (ms.Length >= 5 && ms.GetBuffer().AsSpan((int)ms.Length - 5, 5).SequenceEqual("\r\n.\r\n"u8))
                        {
                            break;
                        }
                    }

                    byte[] raw = ms.ToArray();
                    byte[] body = raw[..^3]; // keep the CRLF that ends the last line, drop ".\r\n"
                    lock (Messages) { Messages.Add(Unstuff(body)); }
                    await Say(s, "250 2.0.0 Ok: queued as FAKE1");
                    break;
                case "BDAT":
                    string[] parts = line.Split(' ');
                    int n = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    byte[] chunk = new byte[n];
                    int got = 0;
                    while (got < n)
                    {
                        int k = await s.ReadAsync(chunk.AsMemory(got));
                        if (k == 0) { return; }
                        got += k;
                    }

                    bdat.Write(chunk);
                    if (parts.Length > 2)
                    {
                        lock (Messages) { Messages.Add(bdat.ToArray()); }
                        bdat.SetLength(0);
                        await Say(s, "250 2.0.0 Ok: queued as FAKE2");
                    }
                    else
                    {
                        await Say(s, "250 2.0.0 chunk ok");
                    }

                    break;
                case "RSET":
                case "NOOP":
                    await Say(s, "250 2.0.0 Ok");
                    break;
                case "QUIT":
                    await Say(s, "221 2.0.0 Bye");
                    return;
                default:
                    await Say(s, "502 5.5.2 Command not recognized");
                    break;
            }
        }
    }

    // RFC 5321 §4.5.2: remove the dot a client adds to lines that start with one.
    private static byte[] Unstuff(byte[] data)
    {
        var o = new MemoryStream();
        bool start = true;
        for (int i = 0; i < data.Length; i++)
        {
            if (start && data[i] == (byte)'.' && i + 1 < data.Length && data[i + 1] == (byte)'.')
            {
                start = false;
                continue;
            }

            o.WriteByte(data[i]);
            start = data[i] == (byte)'\n';
        }

        return o.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
