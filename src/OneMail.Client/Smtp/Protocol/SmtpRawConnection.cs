using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;

namespace RecursiveAI.OneMail.Client.Smtp.Protocol;

/// <summary>The BODY parameter of MAIL FROM (RFC 6152, RFC 3030).</summary>
public enum SmtpBodyType
{
    /// <summary>Not stated: the server assumes 7-bit text.</summary>
    Default = 0,

    /// <summary><c>BODY=7BIT</c>.</summary>
    SevenBit,

    /// <summary><c>BODY=8BITMIME</c> (needs the 8BITMIME extension).</summary>
    EightBitMime,

    /// <summary><c>BODY=BINARYMIME</c> (needs CHUNKING and BINARYMIME; the message must go with BDAT).</summary>
    BinaryMime,
}

/// <summary>The parameters of a <c>MAIL FROM</c> command. Each is sent only when set, and only valid when the server announced the extension.</summary>
public sealed class SmtpMailOptions
{
    /// <summary>SIZE=n, the message size in bytes (RFC 1870).</summary>
    public long? Size { get; init; }

    /// <summary>BODY=… (RFC 6152).</summary>
    public SmtpBodyType Body { get; init; }

    /// <summary>SMTPUTF8 (RFC 6531): addresses or headers use UTF-8.</summary>
    public bool SmtpUtf8 { get; init; }

    /// <summary>REQUIRETLS (RFC 8689).</summary>
    public bool RequireTls { get; init; }

    /// <summary>RET=FULL or RET=HDRS (RFC 3461): what a failure report returns. Null: not sent.</summary>
    public string? DsnReturn { get; init; }

    /// <summary>ENVID=… (RFC 3461): an envelope id; xtext-encoded when sent.</summary>
    public string? DsnEnvelopeId { get; init; }

    /// <summary>AUTH=… (RFC 4954 §5): the submitting identity, xtext-encoded when sent; <c>&lt;&gt;</c> for unknown.</summary>
    public string? Auth { get; init; }

    /// <summary>Other parameters, appended verbatim as <c>NAME</c> or <c>NAME=value</c>. The caller makes sure they are valid and safe.</summary>
    public IReadOnlyList<string>? Extra { get; init; }
}

/// <summary>The parameters of a <c>RCPT TO</c> command (RFC 3461).</summary>
public sealed class SmtpRcptOptions
{
    /// <summary>NOTIFY=… keywords (<c>SUCCESS</c>, <c>FAILURE</c>, <c>DELAY</c>, <c>NEVER</c>), sent comma-separated.</summary>
    public IReadOnlyList<string>? Notify { get; init; }

    /// <summary>ORCPT=rfc822;address: the original recipient, xtext-encoded when sent.</summary>
    public string? OriginalRecipient { get; init; }

    /// <summary>Other parameters, appended verbatim.</summary>
    public IReadOnlyList<string>? Extra { get; init; }
}

/// <summary>
/// The raw SMTP session (RFC 5321 and extensions): send commands by hand, read replies, pipeline batches, stream DATA or BDAT.
/// It tracks the EHLO capabilities and nothing else; sequencing a transaction is the caller's (or the typed client's) job.
/// </summary>
/// <remarks>Not thread-safe. Cancellation or a timeout leaves the stream mid-reply; dispose the connection.</remarks>
public sealed class SmtpRawConnection : IAsyncDisposable, IDisposable
{
    private readonly SmtpReplyReader _replies;

    /// <summary>Wraps a connection. The greeting is not read yet.</summary>
    /// <param name="connection">The transport. Disposing this object disposes it.</param>
    public SmtpRawConnection(ProtocolConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
        _replies = new SmtpReplyReader(connection.Reader);
    }

    /// <summary>The transport.</summary>
    public ProtocolConnection Connection { get; }

    /// <summary>The capabilities from the last EHLO, or null before it, after STARTTLS and after authentication (ask again with EHLO).</summary>
    public SmtpCapabilities? Capabilities { get; private set; }

    /// <summary>Connects and wraps the connection.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port: 25, 587, or 465 with <see cref="ProtocolConnectionOptions.ImplicitTls"/>.</param>
    /// <param name="options">Connection options.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The session; read the greeting next.</returns>
    public static async ValueTask<SmtpRawConnection> ConnectAsync(string host, int port, ProtocolConnectionOptions? options = null, CancellationToken cancellationToken = default) =>
        new(await ProtocolConnection.ConnectAsync(host, port, options, cancellationToken).ConfigureAwait(false));

    /// <summary>Synchronous twin of <see cref="ConnectAsync"/>.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port.</param>
    /// <param name="options">Connection options.</param>
    /// <returns>The session.</returns>
    public static SmtpRawConnection Connect(string host, int port, ProtocolConnectionOptions? options = null) =>
        new(ProtocolConnection.Connect(host, port, options));

    // ---- replies ----

    /// <summary>Reads the next reply (the greeting after connecting, or the answer to the oldest unanswered command).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reply.</returns>
    public ValueTask<SmtpReply> ReadReplyAsync(CancellationToken cancellationToken = default) => _replies.ReadReplyAsync(cancellationToken);

    /// <summary>Synchronous twin of <see cref="ReadReplyAsync"/>.</summary>
    /// <returns>The reply.</returns>
    public SmtpReply ReadReply() => _replies.ReadReply();

    // ---- commands ----

    private static string Line(string verb, string? arguments)
    {
        ArgumentNullException.ThrowIfNull(verb);
        if (verb.Length == 0 || verb.Any(c => c <= ' ' || c >= 0x7F) || (arguments?.Any(c => c is '\r' or '\n' or '\0') ?? false))
        {
            throw new ArgumentException("A command verb must be non-empty ASCII without spaces, and arguments must not contain CR, LF or NUL.");
        }

        return arguments is null ? verb : verb + " " + arguments;
    }

    private static string CheckedLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0 || line.Any(c => c is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException("A command line must be non-empty and free of CR, LF and NUL.");
        }

        return line;
    }

    /// <summary>Writes one command line and flushes, without reading the reply (for pipelining, or when the reply is read separately).</summary>
    /// <param name="verb">The command word (<c>MAIL</c>).</param>
    /// <param name="arguments">The rest of the line, or null. UTF-8 is written as is (SMTPUTF8).</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the line is flushed.</returns>
    public async ValueTask SendAsync(string verb, string? arguments = null, CancellationToken cancellationToken = default)
    {
        WriteLine(Line(verb, arguments));
        await Connection.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="SendAsync"/>.</summary>
    /// <param name="verb">The command word.</param>
    /// <param name="arguments">The rest of the line, or null.</param>
    public void Send(string verb, string? arguments = null)
    {
        WriteLine(Line(verb, arguments));
        Connection.Writer.Flush();
    }

    private void WriteLine(string line, bool secret = false)
    {
        if (secret) { Connection.Writer.WriteSecretAscii(line); } else { Connection.Writer.WriteUtf8(line); }
        Connection.Writer.WriteCrLf();
    }

    /// <summary>Sends one command and reads its reply.</summary>
    /// <param name="verb">The command word.</param>
    /// <param name="arguments">The rest of the line, or null.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply.</returns>
    public async ValueTask<SmtpReply> CommandAsync(string verb, string? arguments = null, CancellationToken cancellationToken = default)
    {
        await SendAsync(verb, arguments, cancellationToken).ConfigureAwait(false);
        return await _replies.ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="CommandAsync"/>.</summary>
    /// <param name="verb">The command word.</param>
    /// <param name="arguments">The rest of the line, or null.</param>
    /// <returns>The reply.</returns>
    public SmtpReply Command(string verb, string? arguments = null)
    {
        Send(verb, arguments);
        return _replies.ReadReply();
    }

    /// <summary>
    /// Sends several command lines in one write and reads one reply for each (RFC 2920). The caller guarantees that the server
    /// announced PIPELINING and that the group is one the RFC allows (for example MAIL, RCPT…, RSET, with DATA last).
    /// </summary>
    /// <param name="commands">Complete command lines without CRLF.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The replies in order.</returns>
    public async ValueTask<IReadOnlyList<SmtpReply>> PipelineAsync(IReadOnlyList<string> commands, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (string c in commands)
        {
            WriteLine(CheckedLine(c));
        }

        await Connection.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        var replies = new List<SmtpReply>(commands.Count);
        for (int i = 0; i < commands.Count; i++)
        {
            replies.Add(await _replies.ReadReplyAsync(cancellationToken).ConfigureAwait(false));
        }

        return replies;
    }

    /// <summary>Synchronous twin of <see cref="PipelineAsync"/>.</summary>
    /// <param name="commands">Complete command lines without CRLF.</param>
    /// <returns>The replies in order.</returns>
    public IReadOnlyList<SmtpReply> Pipeline(IReadOnlyList<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (string c in commands)
        {
            WriteLine(CheckedLine(c));
        }

        Connection.Writer.Flush();
        var replies = new List<SmtpReply>(commands.Count);
        for (int i = 0; i < commands.Count; i++)
        {
            replies.Add(_replies.ReadReply());
        }

        return replies;
    }

    // ---- EHLO, STARTTLS, AUTH ----

    /// <summary>Sends <c>EHLO</c> and parses the extensions.</summary>
    /// <param name="domain">The client's domain or an address literal (<c>[192.0.2.1]</c>).</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply; <see cref="Capabilities"/> is set when it is 250.</returns>
    public async ValueTask<SmtpReply> EhloAsync(string domain, CancellationToken cancellationToken = default)
    {
        SmtpReply r = await CommandAsync("EHLO", domain, cancellationToken).ConfigureAwait(false);
        Capabilities = r.Code == 250 ? new SmtpCapabilities(r) : null;
        return r;
    }

    /// <summary>Synchronous twin of <see cref="EhloAsync"/>.</summary>
    /// <param name="domain">The client's domain.</param>
    /// <returns>The reply.</returns>
    public SmtpReply Ehlo(string domain)
    {
        SmtpReply r = Command("EHLO", domain);
        Capabilities = r.Code == 250 ? new SmtpCapabilities(r) : null;
        return r;
    }

    /// <summary>Sends <c>STARTTLS</c> (RFC 3207) and, on 220, runs the TLS handshake. The capabilities are forgotten: send EHLO again.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply to STARTTLS; TLS is active only if it is 220.</returns>
    public async ValueTask<SmtpReply> StartTlsAsync(CancellationToken cancellationToken = default)
    {
        SmtpReply r = await CommandAsync("STARTTLS", null, cancellationToken).ConfigureAwait(false);
        if (r.Code == 220)
        {
            await Connection.StartTlsAsync(cancellationToken).ConfigureAwait(false);
            Capabilities = null;
        }

        return r;
    }

    /// <summary>Synchronous twin of <see cref="StartTlsAsync"/>.</summary>
    /// <returns>The reply to STARTTLS.</returns>
    public SmtpReply StartTls()
    {
        SmtpReply r = Command("STARTTLS");
        if (r.Code == 220)
        {
            Connection.StartTls();
            Capabilities = null;
        }

        return r;
    }

    /// <summary>Runs <c>AUTH</c> (RFC 4954) with a SASL mechanism. The initial response rides on the command when the mechanism speaks first.</summary>
    /// <param name="mechanism">The mechanism; its secrets are redacted in the trace.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The final reply: 235 on success. <see cref="Capabilities"/> is forgotten after success.</returns>
    /// <exception cref="InvalidOperationException">The mechanism needs an encrypted connection and this one is not.</exception>
    public ValueTask<SmtpReply> AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken = default) =>
        AuthenticateCore(false, mechanism, cancellationToken);

    /// <summary>Synchronous twin of <see cref="AuthenticateAsync"/>.</summary>
    /// <param name="mechanism">The mechanism.</param>
    /// <returns>The final reply.</returns>
    public SmtpReply Authenticate(SaslMechanism mechanism) => SyncBridge.Run(AuthenticateCore(true, mechanism, default));

    private async ValueTask<SmtpReply> AuthenticateCore(bool sync, SaslMechanism mechanism, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        if (mechanism.RequiresEncryption && !Connection.IsEncrypted && !Connection.Options.AllowInsecureAuthentication)
        {
            throw new InvalidOperationException(mechanism.Name + " sends the password in the clear; use STARTTLS or implicit TLS, or set AllowInsecureAuthentication.");
        }

        ProtocolWriter w = Connection.Writer;
        w.WriteAscii("AUTH " + mechanism.Name);
        bool initialSent = false;
        if (mechanism.ClientFirst)
        {
            byte[] init = mechanism.GetInitialResponse() ?? [];
            w.WriteAscii(" ");
            if (init.Length == 0) { w.WriteAscii("="); } else { w.WriteSecretAscii(Convert.ToBase64String(init)); }
            initialSent = true;
        }

        w.WriteCrLf();
        if (sync) { w.Flush(); } else { await w.FlushAsync(ct).ConfigureAwait(false); }
        while (true)
        {
            SmtpReply r = sync ? _replies.ReadReply() : await _replies.ReadReplyAsync(ct).ConfigureAwait(false);
            if (r.Code != 334)
            {
                if (r.Code == 235)
                {
                    Capabilities = null;
                }

                return r;
            }

            byte[] response;
            try
            {
                byte[] challenge = r.Text.Length == 0 ? [] : Convert.FromBase64String(r.Text.Trim());
                response = !initialSent && mechanism.ClientFirst && challenge.Length == 0 ? mechanism.GetInitialResponse() ?? [] : mechanism.Respond(challenge);
                initialSent = true;
            }
            catch (FormatException)
            {
                // RFC 4954 §4: "*" cancels the exchange; the server answers 501.
                w.WriteAscii("*");
                w.WriteCrLf();
                if (sync) { w.Flush(); } else { await w.FlushAsync(ct).ConfigureAwait(false); }
                continue;
            }

            // RFC 4954 §4: an empty response to a challenge is an empty line; "=" means an empty response only in place of an initial response.
            if (response.Length > 0) { w.WriteSecretAscii(Convert.ToBase64String(response)); }
            w.WriteCrLf();
            if (sync) { w.Flush(); } else { await w.FlushAsync(ct).ConfigureAwait(false); }
        }
    }

    // ---- envelope ----

    /// <summary>Builds the argument of <c>MAIL</c>: <c>FROM:&lt;address&gt; params</c>.</summary>
    /// <param name="sender">The reverse-path mailbox, or empty for the null sender <c>&lt;&gt;</c>.</param>
    /// <param name="options">Parameters, or null.</param>
    /// <returns>The argument text (not sent).</returns>
    /// <exception cref="ArgumentException">The address contains a character that would break the command.</exception>
    public static string MailFromArguments(string sender, SmtpMailOptions? options = null)
    {
        var sb = new StringBuilder("FROM:").Append(Path(sender));
        if (options is not null)
        {
            if (options.Size is { } size) { sb.Append(" SIZE=").Append(size.ToString(CultureInfo.InvariantCulture)); }
            sb.Append(options.Body switch
            {
                SmtpBodyType.SevenBit => " BODY=7BIT",
                SmtpBodyType.EightBitMime => " BODY=8BITMIME",
                SmtpBodyType.BinaryMime => " BODY=BINARYMIME",
                _ => "",
            });
            if (options.SmtpUtf8) { sb.Append(" SMTPUTF8"); }
            if (options.RequireTls) { sb.Append(" REQUIRETLS"); }
            if (options.DsnReturn is { } ret) { sb.Append(" RET=").Append(Keyword(ret)); }
            if (options.DsnEnvelopeId is { } envid) { sb.Append(" ENVID=").Append(XText(envid)); }
            if (options.Auth is { } auth) { sb.Append(" AUTH=").Append(auth == "<>" ? auth : XText(auth)); }
            AppendExtra(sb, options.Extra);
        }

        return sb.ToString();
    }

    /// <summary>Builds the argument of <c>RCPT</c>: <c>TO:&lt;address&gt; params</c>.</summary>
    /// <param name="recipient">The forward-path mailbox.</param>
    /// <param name="options">Parameters, or null.</param>
    /// <returns>The argument text (not sent).</returns>
    public static string RcptToArguments(string recipient, SmtpRcptOptions? options = null)
    {
        var sb = new StringBuilder("TO:").Append(Path(recipient));
        if (options is not null)
        {
            if (options.Notify is { Count: > 0 } notify) { sb.Append(" NOTIFY=").Append(string.Join(',', notify.Select(Keyword))); }
            if (options.OriginalRecipient is { } orcpt) { sb.Append(" ORCPT=").Append(XText(orcpt)); }
            AppendExtra(sb, options.Extra);
        }

        return sb.ToString();
    }

    private static void AppendExtra(StringBuilder sb, IReadOnlyList<string>? extra)
    {
        if (extra is null)
        {
            return;
        }

        foreach (string e in extra)
        {
            if (e.Length == 0 || e.Any(c => c <= ' ' || c == 0x7F))
            {
                throw new ArgumentException("A parameter must be a non-empty word without spaces or controls: " + e);
            }

            sb.Append(' ').Append(e);
        }
    }

    private static string Keyword(string s)
    {
        if (s.Length == 0 || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Not a keyword: " + s);
        }

        return s.ToUpperInvariant();
    }

    // A path is "<" mailbox ">"; spaces, angle brackets and controls in the mailbox would end the path early or add a command.
    private static string Path(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Any(c => c <= ' ' || c == 0x7F || c is '<' or '>'))
        {
            throw new ArgumentException("An address must not contain spaces, angle brackets or control characters: " + address);
        }

        return "<" + address + ">";
    }

    // RFC 3461 §4: xtext encodes "+", "=" and anything outside 0x21-0x7E as "+HH" (UTF-8 bytes for non-ASCII).
    internal static string XText(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            if (b is >= 0x21 and <= 0x7E and not (byte)'+' and not (byte)'=')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('+').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    // ---- DATA and BDAT ----

    /// <summary>Sends <c>DATA</c> and, if the server answers 354, opens the body stream.</summary>
    /// <param name="normalizeLineEndings">Turn bare LF and bare CR into CRLF. Default false; see <see cref="SmtpDataStream"/>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The server's reply to DATA, and the stream (null unless the reply is 354). Finish with <see cref="SmtpDataStream.CompleteAsync"/>.</returns>
    public async ValueTask<(SmtpReply Reply, SmtpDataStream? Stream)> BeginDataAsync(bool normalizeLineEndings = false, CancellationToken cancellationToken = default)
    {
        SmtpReply r = await CommandAsync("DATA", null, cancellationToken).ConfigureAwait(false);
        return (r, r.Code == 354 ? new SmtpDataStream(Connection, _replies, normalizeLineEndings) : null);
    }

    // For callers that sent DATA inside a pipelined group and have read its 354 themselves.
    internal SmtpDataStream OpenDataStream(bool normalizeLineEndings) => new(Connection, _replies, normalizeLineEndings);

    /// <summary>Synchronous twin of <see cref="BeginDataAsync"/>.</summary>
    /// <param name="normalizeLineEndings">Turn bare LF and bare CR into CRLF.</param>
    /// <returns>The reply to DATA and the stream.</returns>
    public (SmtpReply Reply, SmtpDataStream? Stream) BeginData(bool normalizeLineEndings = false)
    {
        SmtpReply r = Command("DATA");
        return (r, r.Code == 354 ? new SmtpDataStream(Connection, _replies, normalizeLineEndings) : null);
    }

    /// <summary>
    /// Writes one <c>BDAT</c> chunk (RFC 3030) and flushes, without reading the reply. Chunks may be pipelined: write several, then read one reply per chunk.
    /// Needs the CHUNKING extension. The bytes are sent as they are: no dot-stuffing, any line ends.
    /// </summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="last">Whether this is the last chunk of the message (<c>BDAT n LAST</c>).</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the chunk is flushed.</returns>
    public async ValueTask SendBdatAsync(ReadOnlyMemory<byte> chunk, bool last, CancellationToken cancellationToken = default)
    {
        WriteBdatHeader(chunk.Length, last);
        Connection.Writer.Write(chunk.Span);
        await Connection.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="SendBdatAsync(ReadOnlyMemory{byte}, bool, CancellationToken)"/>.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="last">Whether this is the last chunk.</param>
    public void SendBdat(ReadOnlySpan<byte> chunk, bool last)
    {
        WriteBdatHeader(chunk.Length, last);
        Connection.Writer.Write(chunk);
        Connection.Writer.Flush();
    }

    /// <summary>Writes one BDAT chunk read from a stream, without buffering it all, and flushes. Reply not read.</summary>
    /// <param name="source">The stream.</param>
    /// <param name="length">The exact chunk size.</param>
    /// <param name="last">Whether this is the last chunk.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the chunk is flushed.</returns>
    public async ValueTask SendBdatAsync(Stream source, long length, bool last, CancellationToken cancellationToken = default)
    {
        WriteBdatHeader(length, last);
        await Connection.Writer.WriteFromAsync(source, length, cancellationToken).ConfigureAwait(false);
        await Connection.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void WriteBdatHeader(long length, bool last)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Connection.Writer.WriteAscii("BDAT " + length.ToString(CultureInfo.InvariantCulture) + (last ? " LAST" : ""));
        Connection.Writer.WriteCrLf();
    }

    /// <summary>Closes the connection (no QUIT is sent; send it first with <see cref="CommandAsync"/>).</summary>
    public void Dispose() => Connection.Dispose();

    /// <summary>Closes the connection.</summary>
    /// <returns>A task that completes when closed.</returns>
    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
