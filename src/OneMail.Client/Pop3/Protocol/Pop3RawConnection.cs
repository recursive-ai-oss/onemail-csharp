using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;

namespace RecursiveAI.OneMail.Client.Pop3.Protocol;

/// <summary>A scan-listing entry: message number and size (<c>LIST</c>), or message number and unique id (<c>UIDL</c>).</summary>
/// <param name="Number">The message number (1-based).</param>
/// <param name="Value">The size in octets as text for LIST, the unique id for UIDL.</param>
public readonly record struct Pop3ListEntry(int Number, string Value);

/// <summary>
/// The raw POP3 session (RFC 1939, RFC 2449 extensions, RFC 5034 SASL): commands by hand, status lines, and multi-line bodies as streams.
/// </summary>
/// <remarks>
/// Not thread-safe. A multi-line body returned as a stream must be read to its end or drained before another command; the next command
/// throws <see cref="InvalidOperationException"/> otherwise rather than silently discarding what may be 100 MB.
/// </remarks>
public sealed class Pop3RawConnection : IAsyncDisposable, IDisposable
{
    private Pop3DotStream? _open;

    /// <summary>Wraps a connection. The greeting is not read yet.</summary>
    /// <param name="connection">The transport. Disposing this object disposes it.</param>
    public Pop3RawConnection(ProtocolConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
    }

    /// <summary>The transport.</summary>
    public ProtocolConnection Connection { get; }

    /// <summary>The greeting, once read.</summary>
    public Pop3Response? Greeting { get; private set; }

    /// <summary>The APOP timestamp (<c>&lt;1896.697170952@dbc.mtview.ca.us&gt;</c>) from the greeting, or null.</summary>
    public string? ApopTimestamp { get; private set; }

    /// <summary>Connects and wraps the connection.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port: 110, or 995 with <see cref="ProtocolConnectionOptions.ImplicitTls"/>.</param>
    /// <param name="options">Connection options.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The session; read the greeting next.</returns>
    public static async ValueTask<Pop3RawConnection> ConnectAsync(string host, int port, ProtocolConnectionOptions? options = null, CancellationToken cancellationToken = default) =>
        new(await ProtocolConnection.ConnectAsync(host, port, options, cancellationToken).ConfigureAwait(false));

    /// <summary>Synchronous twin of <see cref="ConnectAsync"/>.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port.</param>
    /// <param name="options">Connection options.</param>
    /// <returns>The session.</returns>
    public static Pop3RawConnection Connect(string host, int port, ProtocolConnectionOptions? options = null) =>
        new(ProtocolConnection.Connect(host, port, options));

    private void EnsureIdle()
    {
        if (_open is { IsComplete: false })
        {
            throw new InvalidOperationException("The previous multi-line response has not been read to its end; read or drain the stream first.");
        }

        _open = null;
    }

    /// <summary>Reads the server greeting.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The greeting.</returns>
    public async ValueTask<Pop3Response> ReadGreetingAsync(CancellationToken cancellationToken = default) =>
        Greeted(Pop3Response.Parse((await Connection.Reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)).Span));

    /// <summary>Synchronous twin of <see cref="ReadGreetingAsync"/>.</summary>
    /// <returns>The greeting.</returns>
    public Pop3Response ReadGreeting() => Greeted(Pop3Response.Parse(Connection.Reader.ReadLine().Span));

    private Pop3Response Greeted(Pop3Response g)
    {
        Greeting = g;
        int lt = g.Text.IndexOf('<', StringComparison.Ordinal);
        int gt = lt < 0 ? -1 : g.Text.IndexOf('>', lt);
        ApopTimestamp = gt > lt && lt >= 0 ? g.Text[lt..(gt + 1)] : null;
        return g;
    }

    /// <summary>Reads one status line.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The response.</returns>
    public async ValueTask<Pop3Response> ReadResponseAsync(CancellationToken cancellationToken = default) =>
        Pop3Response.Parse((await Connection.Reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)).Span);

    /// <summary>Synchronous twin of <see cref="ReadResponseAsync"/>.</summary>
    /// <returns>The response.</returns>
    public Pop3Response ReadResponse() => Pop3Response.Parse(Connection.Reader.ReadLine().Span);

    private static string Line(string command, string? arguments)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0 || command.Any(c => c <= ' ' || c >= 0x7F) || (arguments?.Any(c => c is '\r' or '\n' or '\0') ?? false))
        {
            throw new ArgumentException("A command must be non-empty ASCII without spaces, and arguments must not contain CR, LF or NUL.");
        }

        return arguments is null ? command : command + " " + arguments;
    }

    /// <summary>Writes a command without reading its response, so several can be in flight (PIPELINING, RFC 2449). Pair every call with one <see cref="ReadResponseAsync"/> or <see cref="ReadMultilineAsync"/>, in order.</summary>
    /// <param name="command">The command word.</param>
    /// <param name="arguments">Arguments, or null.</param>
    /// <param name="flush">Whether to flush now. Pass false to batch several commands into one write, then call <see cref="FlushAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the command is written.</returns>
    public async ValueTask SendAsync(string command, string? arguments = null, bool flush = true, CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        Connection.Writer.WriteAscii(Line(command, arguments));
        Connection.Writer.WriteCrLf();
        if (flush)
        {
            await Connection.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes commands written with <see cref="SendAsync"/> <c>flush: false</c>.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when flushed.</returns>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => Connection.Writer.FlushAsync(cancellationToken);

    /// <summary>Reads the response to an earlier <see cref="SendAsync"/> of a command with a multi-line reply (RETR, TOP, LIST, UIDL).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The status line, and the body stream when it was +OK (read it to its end before the next read).</returns>
    public async ValueTask<(Pop3Response Response, Pop3DotStream? Body)> ReadMultilineAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        Pop3Response r = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        return (r, Begin(r));
    }

    /// <summary>Sends a command and reads its single-line status.</summary>
    /// <param name="command">The command word (<c>STAT</c>).</param>
    /// <param name="arguments">The rest of the line, or null.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The status line.</returns>
    public async ValueTask<Pop3Response> CommandAsync(string command, string? arguments = null, CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        Connection.Writer.WriteAscii(Line(command, arguments));
        Connection.Writer.WriteCrLf();
        await Connection.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="CommandAsync"/>.</summary>
    /// <param name="command">The command word.</param>
    /// <param name="arguments">The rest of the line, or null.</param>
    /// <returns>The status line.</returns>
    public Pop3Response Command(string command, string? arguments = null)
    {
        EnsureIdle();
        Connection.Writer.WriteAscii(Line(command, arguments));
        Connection.Writer.WriteCrLf();
        Connection.Writer.Flush();
        return ReadResponse();
    }

    /// <summary>Sends a command whose success response is followed by a dot-terminated body (<c>RETR</c>, <c>TOP</c>, <c>LIST</c>, <c>UIDL</c>, <c>CAPA</c>).</summary>
    /// <param name="command">The command word.</param>
    /// <param name="arguments">The rest of the line, or null.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The status line, and the body stream when the status is +OK (otherwise null). Read the stream to its end before the next command.</returns>
    public async ValueTask<(Pop3Response Response, Pop3DotStream? Body)> MultilineAsync(string command, string? arguments = null, CancellationToken cancellationToken = default)
    {
        Pop3Response r = await CommandAsync(command, arguments, cancellationToken).ConfigureAwait(false);
        return (r, Begin(r));
    }

    /// <summary>Synchronous twin of <see cref="MultilineAsync"/>.</summary>
    /// <param name="command">The command word.</param>
    /// <param name="arguments">The rest of the line, or null.</param>
    /// <returns>The status line and body.</returns>
    public (Pop3Response Response, Pop3DotStream? Body) Multiline(string command, string? arguments = null)
    {
        Pop3Response r = Command(command, arguments);
        return (r, Begin(r));
    }

    private Pop3DotStream? Begin(Pop3Response r) => r.IsOk ? _open = new Pop3DotStream(Connection.Reader) : null;

    private static async ValueTask<List<string>> ReadTextLinesAsync(Pop3DotStream body, CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, false, 1024, leaveOpen: true);
        var lines = new List<string>();
        string? l;
        while ((l = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            lines.Add(l);
            if (lines.Count > 10_000_000)
            {
                throw new MailProtocolLimitException("A listing has more than 10,000,000 lines.");
            }
        }

        return lines;
    }

    /// <summary>Sends <c>CAPA</c> (RFC 2449) and parses the list.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The response and, when +OK, the capabilities.</returns>
    public async ValueTask<(Pop3Response Response, Pop3Capabilities? Capabilities)> CapaAsync(CancellationToken cancellationToken = default)
    {
        (Pop3Response r, Pop3DotStream? body) = await MultilineAsync("CAPA", null, cancellationToken).ConfigureAwait(false);
        return (r, body is null ? null : new Pop3Capabilities(await ReadTextLinesAsync(body, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Sends <c>STAT</c>.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The response and, when +OK and well formed, the message count and total size in octets.</returns>
    public async ValueTask<(Pop3Response Response, int Count, long Size)> StatAsync(CancellationToken cancellationToken = default)
    {
        Pop3Response r = await CommandAsync("STAT", null, cancellationToken).ConfigureAwait(false);
        string[] w = r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return r.IsOk && w.Length >= 2 && int.TryParse(w[0], NumberStyles.None, CultureInfo.InvariantCulture, out int c) && long.TryParse(w[1], NumberStyles.None, CultureInfo.InvariantCulture, out long s)
            ? (r, c, s)
            : (r, -1, -1);
    }

    /// <summary>Sends <c>LIST</c> (all messages) or <c>UIDL</c> and parses the listing.</summary>
    /// <param name="uidl">True for UIDL (RFC 1939 §7), false for LIST.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The response and, when +OK, the entries (lines that do not parse are skipped).</returns>
    public async ValueTask<(Pop3Response Response, IReadOnlyList<Pop3ListEntry> Entries)> ListAsync(bool uidl = false, CancellationToken cancellationToken = default)
    {
        (Pop3Response r, Pop3DotStream? body) = await MultilineAsync(uidl ? "UIDL" : "LIST", null, cancellationToken).ConfigureAwait(false);
        var entries = new List<Pop3ListEntry>();
        if (body is not null)
        {
            foreach (string line in await ReadTextLinesAsync(body, cancellationToken).ConfigureAwait(false))
            {
                string[] w = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length == 2 && int.TryParse(w[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                {
                    entries.Add(new Pop3ListEntry(n, w[1].Trim()));
                }
            }
        }

        return (r, entries);
    }

    /// <summary>Sends <c>STLS</c> (RFC 2595) and, on +OK, runs the TLS handshake.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The status; TLS is active only if it is +OK.</returns>
    public async ValueTask<Pop3Response> StartTlsAsync(CancellationToken cancellationToken = default)
    {
        Pop3Response r = await CommandAsync("STLS", null, cancellationToken).ConfigureAwait(false);
        if (r.IsOk)
        {
            await Connection.StartTlsAsync(cancellationToken).ConfigureAwait(false);
        }

        return r;
    }

    /// <summary>Synchronous twin of <see cref="StartTlsAsync"/>.</summary>
    /// <returns>The status.</returns>
    public Pop3Response StartTls()
    {
        Pop3Response r = Command("STLS");
        if (r.IsOk)
        {
            Connection.StartTls();
        }

        return r;
    }

    /// <summary>Logs in with <c>USER</c> and <c>PASS</c>. The password is redacted in the trace. Refuses to send it unencrypted unless <see cref="ProtocolConnectionOptions.AllowInsecureAuthentication"/>.</summary>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The status of PASS, or of USER if it failed.</returns>
    public async ValueTask<Pop3Response> LoginAsync(string user, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!Connection.IsEncrypted && !Connection.Options.AllowInsecureAuthentication)
        {
            throw new InvalidOperationException("Refusing to send credentials over an unencrypted connection; use STLS or implicit TLS, or set AllowInsecureAuthentication.");
        }

        Pop3Response u = await CommandAsync("USER", user, cancellationToken).ConfigureAwait(false);
        if (!u.IsOk)
        {
            return u;
        }

        EnsureIdle();
        if (password.Any(c => c is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException("A password must not contain CR, LF or NUL.", nameof(password));
        }

        Connection.Writer.WriteAscii("PASS ");
        Connection.Writer.WriteSecret(Encoding.UTF8.GetBytes(password));
        Connection.Writer.WriteCrLf();
        await Connection.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <c>AUTH</c> (RFC 5034) with a SASL mechanism.</summary>
    /// <param name="mechanism">The mechanism; secrets are redacted in the trace.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The final status: +OK on success.</returns>
    /// <exception cref="InvalidOperationException">The mechanism needs an encrypted connection and this one is not.</exception>
    public ValueTask<Pop3Response> AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken = default) =>
        AuthenticateCore(false, mechanism, cancellationToken);

    /// <summary>Synchronous twin of <see cref="AuthenticateAsync"/>.</summary>
    /// <param name="mechanism">The mechanism.</param>
    /// <returns>The final status.</returns>
    public Pop3Response Authenticate(SaslMechanism mechanism) => SyncBridge.Run(AuthenticateCore(true, mechanism, default));

    private async ValueTask<Pop3Response> AuthenticateCore(bool sync, SaslMechanism mechanism, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        EnsureIdle();
        if (mechanism.RequiresEncryption && !Connection.IsEncrypted && !Connection.Options.AllowInsecureAuthentication)
        {
            throw new InvalidOperationException(mechanism.Name + " sends the password in the clear; use STLS or implicit TLS, or set AllowInsecureAuthentication.");
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
            Pop3Response r = sync ? ReadResponse() : await ReadResponseAsync(ct).ConfigureAwait(false);
            if (r.Kind != Pop3StatusKind.Continuation)
            {
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
                // RFC 5034 §4: "*" cancels the exchange.
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

    /// <summary>Closes the connection (no QUIT is sent).</summary>
    public void Dispose() => Connection.Dispose();

    /// <summary>Closes the connection.</summary>
    /// <returns>A task that completes when closed.</returns>
    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
