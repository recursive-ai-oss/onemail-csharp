using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Pop3.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Client.Smtp.Protocol;

namespace RecursiveAI.OneMail.Fuzz;

// The protocol response readers on hostile server output. Only the layer's own typed outcomes may escape: a parsed response with
// defects, or a MailProtocolException for a closed connection or a limit. Anything else (an index error, an overflow) is a finding.
internal static class ClientTargets
{
    // Small limits keep each input fast and prove the limits are enforced.
    private static readonly ProtocolConnectionOptions Options = new()
    {
        Limits = new ProtocolLimits { MaxLineLength = 4096, MaxResponseSize = 256 * 1024, MaxBufferedLiteral = 32 * 1024, MaxLiteralSize = 1 << 20, MaxNesting = 16, MaxReplyLines = 64, MaxItems = 10_000 },
    };

    /// <summary>A stream that serves the fuzz input and swallows writes.</summary>
    private sealed class InputStream(byte[] input) : Stream
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
            int n = Math.Min(buffer.Length, input.Length - _pos);
            input.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public static void Imap(ReadOnlySpan<byte> data)
    {
        using ProtocolConnection conn = ProtocolConnection.FromStream(new InputStream(data.ToArray()), "localhost", Options);
        var reader = new ImapResponseReader(conn.Reader);
        try
        {
            for (int i = 0; i < 200; i++)
            {
                // Half the inputs stream FETCH literals (reading only part of them), half buffer them.
                ImapResponse r = (data.Length & 1) == 0
                    ? reader.ReadResponse((ctx, s) => { var b = new byte[16]; _ = ctx.Length; _ = s.Read(b, 0, b.Length); return true; })
                    : reader.ReadResponse();
                _ = r.TryGetCapabilities(out _);
                if (r.TryGetFetch(out ImapFetchData f))
                {
                    _ = f.BodyStructure?.MediaType;
                    _ = f.Envelope?.Subject;
                    foreach (ImapFetchSection s in f.Sections) { _ = s.Data.ToString(); }
                }

                _ = r.TryGetList(out _);
                _ = r.TryGetStatus(out _);
                _ = r.TryGetSearch(out _);
                _ = r.TryGetEsearch(out _);
                _ = r.TryGetNamespace(out _);
                if (r.TryGetVanished(out ImapVanishedData v)) { _ = ImapSequenceSet.Expand(v.UidSet, 100, 1000).Count(); }
                _ = r.ToString();
            }
        }
        catch (MailProtocolException)
        {
            // Closed connection or a limit: the expected end of hostile input.
        }
    }

    /// <summary>
    /// The SASL mechanisms on hostile server challenges. The first byte picks the mechanism; the rest is cut into challenges at 0x00 bytes
    /// and answered in turn (a SCRAM or NTLM challenge parser must reject anything bad with a SaslException and nothing else).
    /// </summary>
    public static void Sasl(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        SaslMechanism m = (data[0] % 8) switch
        {
            0 => new SaslScram(ScramHash.Sha1, "user", "pencil", nonce: "fyko+d2lbbFgONRv9qkxdawL"),
            1 => new SaslScram(ScramHash.Sha256, "user", "pencil", nonce: "rOprNGfwEbeRWgbNEkqO"),
            2 => new SaslScram(ScramHash.Sha256, "user", "pencil", binding: new SaslChannelBinding("tls-server-end-point", [1, 2, 3]), plus: true, nonce: "rOprNGfwEbeRWgbNEkqO"),
            3 => new SaslNtlm("User", "Password", "Domain", "WS", clientChallenge: new byte[8], fileTime: 0),
            4 => new SaslCramMd5("tim", "pw"),
            5 => new SaslLogin("u", "p"),
            6 => new SaslOAuthBearer("u", "t"),
            _ => new SaslXOAuth2("u", "t"),
        };
        try
        {
            _ = m.GetInitialResponse();
            ReadOnlySpan<byte> rest = data[1..];
            for (int i = 0; i < 8 && !rest.IsEmpty; i++)
            {
                int cut = rest.IndexOf((byte)0);
                // NTLM messages contain NUL bytes themselves: only the Sasl* text mechanisms are split.
                ReadOnlySpan<byte> challenge = cut < 0 || (data[0] % 8) == 3 ? rest : rest[..cut];
                _ = m.Respond(challenge);
                rest = cut < 0 || (data[0] % 8) == 3 ? default : rest[(cut + 1)..];
            }
        }
        catch (MailProtocolException)
        {
            // A challenge the mechanism refuses: the expected end of hostile input.
        }
    }

    public static void Smtp(ReadOnlySpan<byte> data)
    {
        using ProtocolConnection conn = ProtocolConnection.FromStream(new InputStream(data.ToArray()), "localhost", Options);
        var reader = new SmtpReplyReader(conn.Reader);
        try
        {
            for (int i = 0; i < 200; i++)
            {
                SmtpReply r = reader.ReadReply();
                _ = r.Text;
                _ = r.EnhancedStatus;
                if (r.Code == 250) { var caps = new SmtpCapabilitiesProbe(r); caps.Touch(); }
            }
        }
        catch (MailProtocolException)
        {
        }
    }

    private sealed class SmtpCapabilitiesProbe(SmtpReply reply)
    {
        // The capability table is built by the connection from an EHLO reply; drive it the way the connection does.
        public void Touch()
        {
            using ProtocolConnection c = ProtocolConnection.FromStream(new InputStream(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", reply.Lines.Select((l, i) => "250" + (i == reply.Lines.Count - 1 ? " " : "-") + l)) + "\r\n")), "localhost", Options);
            var smtp = new SmtpRawConnection(c);
            try
            {
                _ = smtp.Ehlo("x");
                SmtpCapabilities? caps = smtp.Capabilities;
                if (caps is not null)
                {
                    _ = caps.AuthMechanisms;
                    _ = caps.MaxMessageSize;
                    foreach (string k in caps.Keywords) { _ = caps.Parameters(k); }
                }
            }
            catch (MailProtocolException)
            {
            }
        }
    }

    public static void Pop3(ReadOnlySpan<byte> data)
    {
        using ProtocolConnection conn = ProtocolConnection.FromStream(new InputStream(data.ToArray()), "localhost", Options);
        var pop = new Pop3RawConnection(conn);
        try
        {
            _ = pop.ReadGreeting();
            for (int i = 0; i < 50; i++)
            {
                (Pop3Response r, Pop3DotStream? body) = pop.Multiline(i % 3 == 0 ? "RETR" : "LIST", i % 3 == 0 ? "1" : null);
                _ = r.Text;
                if (body is not null)
                {
                    var buf = new byte[512];
                    while (body.Read(buf, 0, buf.Length) > 0) { }
                }
            }
        }
        catch (MailProtocolException)
        {
        }
    }

    public static void Pop3Listings(ReadOnlySpan<byte> data)
    {
        using ProtocolConnection conn = ProtocolConnection.FromStream(new InputStream(data.ToArray()), "localhost", Options);
        var pop = new Pop3RawConnection(conn);
        try
        {
            _ = pop.ReadGreeting();
            (_, Pop3Capabilities? caps) = pop.CapaAsync().AsTask().GetAwaiter().GetResult();
            _ = caps?.SaslMechanisms;
            _ = pop.StatAsync().AsTask().GetAwaiter().GetResult();
            _ = pop.ListAsync(true).AsTask().GetAwaiter().GetResult();
        }
        catch (MailProtocolException)
        {
        }
    }
}
