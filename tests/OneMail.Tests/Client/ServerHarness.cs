using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Tests.Client;

/// <summary>
/// The published server images the container tests talk to. They are started by <c>tools/servers/servers.sh</c>, which tells the test
/// process which ones are up (ONEMAIL_SERVERS) and where (ONEMAIL_&lt;SERVER&gt;_&lt;PROTOCOL&gt;_PORT). Without it the tests skip.
/// </summary>
public sealed record ServerInfo(string Name, string User, string Password, string Recipient)
{
    public int Port(string protocol) =>
        int.TryParse(Environment.GetEnvironmentVariable($"ONEMAIL_{Name.ToUpperInvariant()}_{protocol.ToUpperInvariant()}_PORT"), out int p) ? p : 0;

    public bool Has(string protocol) => Port(protocol) != 0;

    public override string ToString() => Name;
}

public static class ServerHarness
{
    private static readonly Dictionary<string, ServerInfo> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dovecot"] = new("dovecot", "test", "onemail-test", "someone@example.com"),
        ["stalwart"] = new("stalwart", "test@example.test", "onemail-test", "test@example.test"),
        ["cyrus"] = new("cyrus", "user1", "x", "user1@example.com"),
        ["postfix"] = new("postfix", "test", "onemail-test", "test@example.test"),
    };

    public static IEnumerable<ServerInfo> Up =>
        (Environment.GetEnvironmentVariable("ONEMAIL_SERVERS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(Known.ContainsKey).Select(n => Known[n]);

    public static TheoryData<ServerInfo> ImapServers() => Data("imaps");

    public static TheoryData<ServerInfo> Pop3Servers() => Data("pop3s");

    public static TheoryData<ServerInfo> SmtpServers() => Data("submissions");

    private static TheoryData<ServerInfo> Data(string protocol)
    {
        var d = new TheoryData<ServerInfo>();
        bool any = false;
        foreach (ServerInfo s in Up.Where(s => s.Has(protocol)))
        {
            d.Add(s);
            any = true;
        }

        if (!any)
        {
            // xUnit needs at least one row; the test skips itself on this one.
            d.Add(new ServerInfo("none", "", "", ""));
        }

        return d;
    }

    public static void RequireServer(ServerInfo s) =>
        Assert.SkipWhen(s.Name == "none", "No container servers are running (start them with tools/servers/servers.sh).");

    /// <summary>Test servers use self-signed certificates; a certificate must be presented, nothing more is checked.</summary>
    public static ProtocolConnectionOptions Options(bool implicitTls, string name, string proto) => new()
    {
        ImplicitTls = implicitTls,
        ServerCertificateValidationCallback = (_, cert, _, _) => cert is not null,
        ReadTimeout = TimeSpan.FromSeconds(30),
        Trace = Recorder(name, proto),
    };

    private static RecordingTrace? Recorder(string name, string proto)
    {
        string? dir = Environment.GetEnvironmentVariable("ONEMAIL_CAPTURE_DIR");
        return dir is null ? null : new RecordingTrace(Path.Combine(dir, $"{name}-{proto}-{Environment.TickCount64}.s2c"));
    }

    /// <summary>The message the tests upload: a multipart with an attachment and an encoded subject.</summary>
    public static byte[] SampleMessage(string marker)
    {
        string text =
            "From: Ann <ann@example.com>\r\n" +
            "To: Bob <bob@example.com>\r\n" +
            "Subject: =?UTF-8?B?w4RwZmVsIHVuZCBCaXJuZW4=?= " + marker + "\r\n" +
            "Date: Tue, 03 Nov 2026 10:00:00 +0000\r\n" +
            "Message-ID: <" + marker + "@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"bnd\"\r\n" +
            "\r\n" +
            "--bnd\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "Hello from the container tests.\r\n" +
            ".A line that starts with a period.\r\n" +
            "\r\n" +
            "--bnd\r\n" +
            "Content-Type: application/octet-stream; name=\"data.bin\"\r\n" +
            "Content-Disposition: attachment; filename=\"data.bin\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(marker)), Base64FormattingOptions.InsertLineBreaks).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n" +
            "--bnd--\r\n";
        return Encoding.UTF8.GetBytes(text);
    }
}

/// <summary>Writes the bytes the server sent to a file, so a session can be kept as a parser test input.</summary>
internal sealed class RecordingTrace : IProtocolTrace
{
    private readonly string _path;

    public RecordingTrace(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    public void Sent(ReadOnlySpan<byte> data) { }

    public void Received(ReadOnlySpan<byte> data)
    {
        using var f = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        f.Write(data);
    }
}
