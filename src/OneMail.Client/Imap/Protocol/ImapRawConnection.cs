using System.Text;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;

namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>What a command produced: the untagged responses that arrived while it ran, and the tagged completion.</summary>
public sealed class ImapCommandResult
{
    internal ImapCommandResult(ImapResponse completion, List<ImapResponse> untagged)
    {
        Completion = completion;
        Untagged = untagged;
    }

    /// <summary>The tagged response that completed the command.</summary>
    public ImapResponse Completion { get; }

    /// <summary>The untagged responses received before the completion, in order. Empty when an untagged callback took them.</summary>
    public IReadOnlyList<ImapResponse> Untagged { get; }

    /// <summary>Whether the command completed with OK.</summary>
    public bool IsOk => Completion.Status == ImapStatus.Ok;
}

/// <summary>
/// The raw IMAP session: send commands built by hand, read responses as trees, with the connection state the protocol needs
/// (cached capabilities, UTF8=ACCEPT, literal announcement mode, tags). Nothing here knows what SELECT or FETCH mean; the typed
/// clients are built on it and anything an extension adds can be driven through it.
/// </summary>
/// <remarks>
/// Not thread-safe: one command at a time unless the caller pipelines by sending several and reading all the completions. Cancellation or a
/// timeout leaves the stream mid-response; dispose the connection.
/// </remarks>
public sealed class ImapRawConnection : IAsyncDisposable, IDisposable
{
    private readonly ImapResponseReader _responses;
    private readonly Queue<ImapResponse> _pending = new();
    private readonly HashSet<string> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private int _tag;

    /// <summary>Wraps a connection. The greeting is not read yet; call <see cref="ReadGreetingAsync"/>.</summary>
    /// <param name="connection">The transport. Disposing this object disposes it.</param>
    public ImapRawConnection(ProtocolConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
        _responses = new ImapResponseReader(connection.Reader);
    }

    /// <summary>The transport.</summary>
    public ProtocolConnection Connection { get; }

    /// <summary>The server greeting, once read.</summary>
    public ImapResponse? Greeting { get; private set; }

    /// <summary>The capabilities last announced, as written. Cleared after STARTTLS and authentication, as RFC 9051 §6.2.1 requires; ask again with <c>CAPABILITY</c>.</summary>
    public IReadOnlyCollection<string> Capabilities => _capabilities;

    /// <summary>Whether <paramref name="capability"/> is in <see cref="Capabilities"/> (ASCII case-insensitive).</summary>
    /// <param name="capability">The capability atom, e.g. <c>IDLE</c> or <c>AUTH=PLAIN</c>.</param>
    /// <returns>True if announced.</returns>
    public bool HasCapability(string capability) => _capabilities.Contains(capability);

    /// <summary>Whether the server confirmed <c>ENABLE UTF8=ACCEPT</c> (RFC 6855): then mailbox names and strings are sent as UTF-8.</summary>
    public bool Utf8Enabled { get; private set; }

    /// <summary>The default for literals whose own mode is <see cref="ImapLiteralMode.Auto"/> is decided from capabilities; this overrides it for all. Default Auto.</summary>
    public ImapLiteralMode LiteralMode { get; set; }

    /// <summary>Connects and wraps the connection.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port: 143, or 993 with <see cref="ProtocolConnectionOptions.ImplicitTls"/>.</param>
    /// <param name="options">Connection options.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The session; call <see cref="ReadGreetingAsync"/> next.</returns>
    public static async ValueTask<ImapRawConnection> ConnectAsync(string host, int port, ProtocolConnectionOptions? options = null, CancellationToken cancellationToken = default) =>
        new(await ProtocolConnection.ConnectAsync(host, port, options, cancellationToken).ConfigureAwait(false));

    /// <summary>Synchronous twin of <see cref="ConnectAsync"/>.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port.</param>
    /// <param name="options">Connection options.</param>
    /// <returns>The session.</returns>
    public static ImapRawConnection Connect(string host, int port, ProtocolConnectionOptions? options = null) =>
        new(ProtocolConnection.Connect(host, port, options));

    /// <summary>Allocates the next tag (<c>A0001</c>, <c>A0002</c>, ...).</summary>
    /// <returns>The tag.</returns>
    public string NextTag() => "A" + (++_tag).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    // ---- greeting ----

    /// <summary>Reads the server greeting (<c>* OK</c>, <c>* PREAUTH</c> or <c>* BYE</c>) and takes any capability list from it.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The greeting.</returns>
    public async ValueTask<ImapResponse> ReadGreetingAsync(CancellationToken cancellationToken = default)
    {
        ImapResponse g = await _responses.ReadResponseAsync(null, cancellationToken).ConfigureAwait(false);
        return Greeted(g);
    }

    /// <summary>Synchronous twin of <see cref="ReadGreetingAsync"/>.</summary>
    /// <returns>The greeting.</returns>
    public ImapResponse ReadGreeting() => Greeted(_responses.ReadResponse());

    private ImapResponse Greeted(ImapResponse g)
    {
        Greeting = g;
        Observe(g);
        return g;
    }

    // ---- reading ----

    /// <summary>Reads the next response (queued ones first). Capability lists and ENABLED are noted on the way.</summary>
    /// <param name="literalHandler">Receives FETCH literals as streams; null buffers them.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The response.</returns>
    public ValueTask<ImapResponse> ReadResponseAsync(ImapLiteralHandler? literalHandler = null, CancellationToken cancellationToken = default) =>
        ReadCore(false, literalHandler, null, cancellationToken);

    /// <summary>Synchronous twin of <see cref="ReadResponseAsync"/>.</summary>
    /// <param name="literalHandler">Receives FETCH literals as streams; null buffers them.</param>
    /// <returns>The response.</returns>
    public ImapResponse ReadResponse(ImapLiteralHandlerSync? literalHandler = null) =>
        SyncBridge.Run(ReadCore(true, null, literalHandler, default));

    private async ValueTask<ImapResponse> ReadCore(bool sync, ImapLiteralHandler? ah, ImapLiteralHandlerSync? sh, CancellationToken ct)
    {
        if (_pending.TryDequeue(out ImapResponse? queued))
        {
            return queued;
        }

        ImapResponse r = sync ? _responses.ReadResponse(sh) : await _responses.ReadResponseAsync(ah, ct).ConfigureAwait(false);
        Observe(r);
        return r;
    }

    private void Observe(ImapResponse r)
    {
        if (r.TryGetCapabilities(out IReadOnlyList<string> caps))
        {
            _capabilities.Clear();
            foreach (string c in caps)
            {
                _capabilities.Add(c);
            }
        }
        else if (r.Kind == ImapResponseKind.Untagged && r.Is("ENABLED") && r.Data.Any(v => v.IsAtom("UTF8=ACCEPT")))
        {
            Utf8Enabled = true;
        }
    }

    // ---- sending ----

    /// <summary>Writes a command and flushes it. Synchronising literals wait for the server's continuation first.</summary>
    /// <param name="command">The command. A tag is assigned if it has none.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>
    /// True when the whole command went out. False when the server refused a literal (it answered the tag with NO or BAD instead of
    /// a continuation): the rest was not sent, and that completion is the next response <see cref="ReadResponseAsync"/> returns.
    /// </returns>
    public ValueTask<bool> SendAsync(ImapCommand command, CancellationToken cancellationToken = default) => SendCore(false, command, cancellationToken);

    /// <summary>Synchronous twin of <see cref="SendAsync"/>.</summary>
    /// <param name="command">The command.</param>
    /// <returns>See <see cref="SendAsync"/>.</returns>
    public bool Send(ImapCommand command) => SyncBridge.Run(SendCore(true, command, default));

    private async ValueTask<bool> SendCore(bool sync, ImapCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ProtocolWriter w = Connection.Writer;
        string tag = command.Tag ??= NextTag();
        if (tag.Any(c => c <= ' ' || c is '(' or ')' or '{' or '%' or '*' or '"' or '\\' or '+'))
        {
            throw new ArgumentException("Not a valid tag: " + tag, nameof(command));
        }

        w.WriteAscii(tag);
        bool space = true;
        foreach (ImapCommand.Part p in command.Parts)
        {
            if (p.Kind == ImapCommand.PartKind.Close)
            {
                w.WriteAscii(")");
                space = true;
                continue;
            }

            if (space)
            {
                w.WriteAscii(" ");
            }

            space = true;
            switch (p.Kind)
            {
                case ImapCommand.PartKind.Open:
                    w.WriteAscii("(");
                    space = false;
                    break;
                case ImapCommand.PartKind.Atom:
                case ImapCommand.PartKind.Raw:
                    w.WriteAscii(p.Text!);
                    break;
                case ImapCommand.PartKind.Astring:
                case ImapCommand.PartKind.Quoted:
                case ImapCommand.PartKind.Mailbox:
                {
                    string text = p.Kind == ImapCommand.PartKind.Mailbox ? ImapCommand.EncodeMailbox(p.Text!, Utf8Enabled) : p.Text!;
                    bool ascii = text.All(c => c is >= ' ' and < (char)0x7F);
                    bool quotable = text.All(c => c is >= ' ' and not (char)0x7F && (c < 0x80 || Utf8Enabled));
                    if (p.Kind != ImapCommand.PartKind.Quoted && ImapCommand.IsAtomSafe(text))
                    {
                        WriteText(w, text, p.Secret);
                    }
                    else if (quotable && text.All(c => c != '\0'))
                    {
                        var q = new StringBuilder(text.Length + 2).Append('"');
                        foreach (char c in text)
                        {
                            if (c is '"' or '\\')
                            {
                                q.Append('\\');
                            }

                            q.Append(c);
                        }

                        q.Append('"');
                        if (Utf8Enabled && !ascii)
                        {
                            WriteUtf8Text(w, q.ToString(), p.Secret);
                        }
                        else
                        {
                            WriteText(w, q.ToString(), p.Secret);
                        }
                    }
                    else
                    {
                        byte[] bytes = ImapCommand.ToUtf8(text);
                        if (!await SendLiteralHeader(sync, tag, bytes.Length, ImapLiteralMode.Auto, ct).ConfigureAwait(false))
                        {
                            return false;
                        }

                        if (p.Secret) { w.WriteSecret(bytes); } else { w.Write(bytes); }
                    }

                    break;
                }

                case ImapCommand.PartKind.LiteralBytes:
                    if (!await SendLiteralHeader(sync, tag, p.Length, p.Mode, ct).ConfigureAwait(false))
                    {
                        return false;
                    }

                    w.Write(p.Bytes.Span);
                    break;
                case ImapCommand.PartKind.LiteralStream:
                    if (!await SendLiteralHeader(sync, tag, p.Length, p.Mode, ct).ConfigureAwait(false))
                    {
                        return false;
                    }

                    if (sync) { w.WriteFrom(p.Stream!, p.Length); } else { await w.WriteFromAsync(p.Stream!, p.Length, ct).ConfigureAwait(false); }
                    break;
                default:
                    break;
            }
        }

        w.WriteCrLf();
        if (sync) { w.Flush(); } else { await w.FlushAsync(ct).ConfigureAwait(false); }
        return true;
    }

    private static void WriteText(ProtocolWriter w, string text, bool secret)
    {
        if (secret) { w.WriteSecretAscii(text); } else { w.WriteAscii(text); }
    }

    private static void WriteUtf8Text(ProtocolWriter w, string text, bool secret)
    {
        byte[] b = ImapCommand.ToUtf8(text);
        if (secret) { w.WriteSecret(b); } else { w.Write(b); }
    }

    // Writes "{n}" or "{n+}" and the line end; for the synchronising form flushes and waits for the continuation request (RFC 9051 §4.3).
    private async ValueTask<bool> SendLiteralHeader(bool sync, string tag, long length, ImapLiteralMode mode, CancellationToken ct)
    {
        ProtocolWriter w = Connection.Writer;
        if (mode == ImapLiteralMode.Auto)
        {
            mode = LiteralMode;
        }

        bool nonSync = mode switch
        {
            ImapLiteralMode.NonSynchronizing => true,
            ImapLiteralMode.Synchronizing => false,
            _ => HasCapability("LITERAL+") || (HasCapability("LITERAL-") && length <= 4096),
        };
        w.WriteAscii("{" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + (nonSync ? "+}" : "}"));
        w.WriteCrLf();
        if (nonSync)
        {
            return true;
        }

        if (sync) { w.Flush(); } else { await w.FlushAsync(ct).ConfigureAwait(false); }
        while (true)
        {
            ImapResponse r = sync ? _responses.ReadResponse() : await _responses.ReadResponseAsync(null, ct).ConfigureAwait(false);
            Observe(r);
            if (r.Kind == ImapResponseKind.Continuation)
            {
                return true;
            }

            _pending.Enqueue(r);
            if (r.Kind == ImapResponseKind.Tagged && r.Tag == tag)
            {
                return false;
            }
        }
    }

    // ---- executing ----

    /// <summary>Sends a command and reads until its tagged completion.</summary>
    /// <param name="command">The command.</param>
    /// <param name="literalHandler">Receives FETCH literals as streams; null buffers them.</param>
    /// <param name="onUntagged">Called for each untagged response instead of collecting it (constant memory over a long FETCH). Null collects.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The completion and the collected untagged responses.</returns>
    public ValueTask<ImapCommandResult> ExecuteAsync(ImapCommand command, ImapLiteralHandler? literalHandler = null, Action<ImapResponse>? onUntagged = null, CancellationToken cancellationToken = default) =>
        ExecuteCore(false, command, literalHandler, null, onUntagged, cancellationToken);

    /// <summary>Synchronous twin of <see cref="ExecuteAsync"/>.</summary>
    /// <param name="command">The command.</param>
    /// <param name="literalHandler">Receives FETCH literals as streams; null buffers them.</param>
    /// <param name="onUntagged">Called for each untagged response instead of collecting it.</param>
    /// <returns>The completion and the collected untagged responses.</returns>
    public ImapCommandResult Execute(ImapCommand command, ImapLiteralHandlerSync? literalHandler = null, Action<ImapResponse>? onUntagged = null) =>
        SyncBridge.Run(ExecuteCore(true, command, null, literalHandler, onUntagged, default));

    private async ValueTask<ImapCommandResult> ExecuteCore(bool sync, ImapCommand command, ImapLiteralHandler? ah, ImapLiteralHandlerSync? sh, Action<ImapResponse>? onUntagged, CancellationToken ct)
    {
        await SendCore(sync, command, ct).ConfigureAwait(false);
        return await CompleteCore(sync, command.Tag!, ah, sh, onUntagged, null, ct).ConfigureAwait(false);
    }

    private async ValueTask<ImapCommandResult> CompleteCore(bool sync, string tag, ImapLiteralHandler? ah, ImapLiteralHandlerSync? sh, Action<ImapResponse>? onUntagged,
        Func<ImapResponse, CancellationToken, ValueTask>? onContinuation, CancellationToken ct)
    {
        var untagged = new List<ImapResponse>();
        while (true)
        {
            ImapResponse r = await ReadCore(sync, ah, sh, ct).ConfigureAwait(false);
            switch (r.Kind)
            {
                case ImapResponseKind.Tagged when r.Tag == tag:
                    if (r.Code is { } code && code.Is("CAPABILITY"))
                    {
                        Observe(r);
                    }

                    return new ImapCommandResult(r, untagged);
                case ImapResponseKind.Continuation:
                    if (onContinuation is null)
                    {
                        throw new MailProtocolViolationException("The server sent a continuation request the command did not expect.");
                    }

                    await onContinuation(r, ct).ConfigureAwait(false);
                    break;
                case ImapResponseKind.Untagged:
                    if (onUntagged is not null) { onUntagged(r); } else { untagged.Add(r); }
                    break;
                default:
                    // A tagged response for another command (pipelining): keep it for whoever reads for that tag.
                    untagged.Add(r);
                    break;
            }
        }
    }

    // ---- common commands that change connection state ----

    /// <summary>Runs STARTTLS (RFC 9051 §6.2.1) and, on OK, the TLS handshake. Capabilities are forgotten afterwards.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The completion of the STARTTLS command (check <see cref="ImapCommandResult.IsOk"/>; TLS is active only if OK).</returns>
    public async ValueTask<ImapCommandResult> StartTlsAsync(CancellationToken cancellationToken = default)
    {
        ImapCommandResult r = await ExecuteAsync(ImapCommand.Create("STARTTLS"), null, null, cancellationToken).ConfigureAwait(false);
        if (r.IsOk)
        {
            await Connection.StartTlsAsync(cancellationToken).ConfigureAwait(false);
            _capabilities.Clear();
        }

        return r;
    }

    /// <summary>Synchronous twin of <see cref="StartTlsAsync"/>.</summary>
    /// <returns>The completion of the STARTTLS command.</returns>
    public ImapCommandResult StartTls()
    {
        ImapCommandResult r = Execute(ImapCommand.Create("STARTTLS"));
        if (r.IsOk)
        {
            Connection.StartTls();
            _capabilities.Clear();
        }

        return r;
    }

    /// <summary>Runs <c>COMPRESS DEFLATE</c> (RFC 4978) and, on OK, switches the stream to DEFLATE both ways.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The completion of the COMPRESS command.</returns>
    public async ValueTask<ImapCommandResult> CompressAsync(CancellationToken cancellationToken = default)
    {
        ImapCommandResult r = await ExecuteAsync(ImapCommand.Create("COMPRESS").Atom("DEFLATE"), null, null, cancellationToken).ConfigureAwait(false);
        if (r.IsOk)
        {
            await Connection.EnableCompressionAsync(cancellationToken).ConfigureAwait(false);
        }

        return r;
    }

    /// <summary>Synchronous twin of <see cref="CompressAsync"/>.</summary>
    /// <returns>The completion of the COMPRESS command.</returns>
    public ImapCommandResult Compress()
    {
        ImapCommandResult r = Execute(ImapCommand.Create("COMPRESS").Atom("DEFLATE"));
        if (r.IsOk)
        {
            Connection.EnableCompression();
        }

        return r;
    }

    /// <summary>Runs <c>LOGIN</c>. The password is redacted in the trace. Refuses to send it unencrypted unless <see cref="ProtocolConnectionOptions.AllowInsecureAuthentication"/>.</summary>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The completion. Capabilities are forgotten when it is OK.</returns>
    /// <exception cref="InvalidOperationException">The connection is not encrypted.</exception>
    public async ValueTask<ImapCommandResult> LoginAsync(string user, string password, CancellationToken cancellationToken = default)
    {
        CheckCleartext();
        ImapCommandResult r = await ExecuteAsync(ImapCommand.Create("LOGIN").Quoted(user, secret: true).Quoted(password, secret: true), null, null, cancellationToken).ConfigureAwait(false);
        AfterAuth(r);
        return r;
    }

    /// <summary>Synchronous twin of <see cref="LoginAsync"/>.</summary>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    /// <returns>The completion.</returns>
    public ImapCommandResult Login(string user, string password)
    {
        CheckCleartext();
        ImapCommandResult r = Execute(ImapCommand.Create("LOGIN").Quoted(user, secret: true).Quoted(password, secret: true));
        AfterAuth(r);
        return r;
    }

    private void CheckCleartext()
    {
        if (!Connection.IsEncrypted && !Connection.Options.AllowInsecureAuthentication)
        {
            throw new InvalidOperationException("Refusing to send credentials over an unencrypted connection; use STARTTLS or implicit TLS, or set AllowInsecureAuthentication.");
        }
    }

    private void AfterAuth(ImapCommandResult r)
    {
        // RFC 9051 §6.2.2: capabilities may change once authenticated. A [CAPABILITY] code on the OK was already taken.
        if (r.IsOk && !(r.Completion.Code?.Is("CAPABILITY") ?? false))
        {
            _capabilities.Clear();
        }
    }

    /// <summary>Runs <c>AUTHENTICATE</c> (RFC 9051 §6.2.2) with a SASL mechanism, using the initial-response form (SASL-IR, RFC 4959) when the server offers it.</summary>
    /// <param name="mechanism">The mechanism; its secrets are redacted in the trace.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The completion.</returns>
    /// <exception cref="InvalidOperationException">The mechanism needs an encrypted connection and this one is not.</exception>
    public async ValueTask<ImapCommandResult> AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken = default) =>
        await AuthenticateCore(false, mechanism, cancellationToken).ConfigureAwait(false);

    /// <summary>Synchronous twin of <see cref="AuthenticateAsync"/>.</summary>
    /// <param name="mechanism">The mechanism.</param>
    /// <returns>The completion.</returns>
    public ImapCommandResult Authenticate(SaslMechanism mechanism) =>
        SyncBridge.Run(AuthenticateCore(true, mechanism, default));

    private async ValueTask<ImapCommandResult> AuthenticateCore(bool sync, SaslMechanism mechanism, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        if (mechanism.RequiresEncryption && !Connection.IsEncrypted && !Connection.Options.AllowInsecureAuthentication)
        {
            throw new InvalidOperationException(mechanism.Name + " sends the password in the clear; use STARTTLS or implicit TLS, or set AllowInsecureAuthentication.");
        }

        ProtocolWriter w = Connection.Writer;
        string tag = NextTag();
        w.WriteAscii(tag + " AUTHENTICATE " + mechanism.Name);
        bool initialSent = false;
        if (mechanism.ClientFirst && HasCapability("SASL-IR"))
        {
            byte[] init = mechanism.GetInitialResponse() ?? [];
            w.WriteAscii(" ");
            if (init.Length == 0) { w.WriteAscii("="); } else { w.WriteSecretAscii(Convert.ToBase64String(init)); }
            initialSent = true;
        }

        w.WriteCrLf();
        if (sync) { w.Flush(); } else { await w.FlushAsync(ct).ConfigureAwait(false); }

        async ValueTask Continue(ImapResponse challenge, CancellationToken token)
        {
            byte[] decoded;
            try
            {
                decoded = challenge.Text.Length == 0 ? [] : Convert.FromBase64String(challenge.Text);
            }
            catch (FormatException)
            {
                // RFC 9051 §6.2.2: the client cancels an exchange it cannot continue with "*".
                w.WriteAscii("*");
                w.WriteCrLf();
                if (sync) { w.Flush(); } else { await w.FlushAsync(token).ConfigureAwait(false); }
                return;
            }

            byte[] response = !initialSent && mechanism.ClientFirst && decoded.Length == 0 ? mechanism.GetInitialResponse() ?? [] : mechanism.Respond(decoded);
            initialSent = true;
            w.WriteSecretAscii(Convert.ToBase64String(response));
            w.WriteCrLf();
            if (sync) { w.Flush(); } else { await w.FlushAsync(token).ConfigureAwait(false); }
        }

        ImapCommandResult r = await CompleteCore(sync, tag, null, null, null, Continue, ct).ConfigureAwait(false);
        AfterAuth(r);
        return r;
    }

    /// <summary>Starts <c>IDLE</c> (RFC 2177) and waits for the continuation. Afterwards read untagged responses with <see cref="ReadResponseAsync"/> (raise <see cref="ProtocolConnection.ReadTimeout"/> first) and finish with <see cref="IdleDoneAsync"/>.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The IDLE tag, or null when the server refused (its completion is then the next response).</returns>
    public async ValueTask<string?> IdleStartAsync(CancellationToken cancellationToken = default)
    {
        var cmd = ImapCommand.Create("IDLE");
        await SendAsync(cmd, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            // Read from the wire, not the queue: anything that arrives before the continuation is queued for the caller.
            ImapResponse r = await ReadFresh(cancellationToken).ConfigureAwait(false);
            if (r.Kind == ImapResponseKind.Continuation)
            {
                return cmd.Tag;
            }

            _pending.Enqueue(r);
            if (r.Kind == ImapResponseKind.Tagged)
            {
                return null;
            }
        }
    }

    private async ValueTask<ImapResponse> ReadFresh(CancellationToken ct)
    {
        ImapResponse r = await _responses.ReadResponseAsync(null, ct).ConfigureAwait(false);
        Observe(r);
        return r;
    }

    /// <summary>Ends IDLE: sends <c>DONE</c> and reads until the tagged completion.</summary>
    /// <param name="tag">The tag returned by <see cref="IdleStartAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The completion and the untagged responses that arrived meanwhile.</returns>
    public async ValueTask<ImapCommandResult> IdleDoneAsync(string tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        Connection.Writer.WriteAscii("DONE");
        Connection.Writer.WriteCrLf();
        await Connection.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteCore(false, tag, null, null, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the connection.</summary>
    public void Dispose() => Connection.Dispose();

    /// <summary>Closes the connection.</summary>
    /// <returns>A task that completes when closed.</returns>
    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
