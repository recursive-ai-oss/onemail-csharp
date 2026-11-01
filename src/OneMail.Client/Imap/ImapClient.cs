using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Imap;

/// <summary>A command the server answered with NO or BAD (or a BYE in place of a result).</summary>
public sealed class ImapCommandException : MailProtocolException
{
    internal ImapCommandException(ImapResponse completion)
        : base("IMAP " + completion.Status.ToString().ToUpperInvariant() + ": " + completion.Text)
    {
        Completion = completion;
    }

    /// <summary>The tagged response.</summary>
    public ImapResponse Completion { get; }

    /// <summary>NO or BAD.</summary>
    public ImapStatus Status => Completion.Status;

    /// <summary>The bracketed response code, e.g. <c>TOOBIG</c>, <c>OVERQUOTA</c>, <c>AUTHENTICATIONFAILED</c>, <c>NONEXISTENT</c>, or null.</summary>
    public string? ResponseCode => Completion.Code?.Name;
}

/// <summary>Whether to upgrade a plain connection with STARTTLS.</summary>
public enum ImapStartTlsMode
{
    /// <summary>Upgrade when the connection is not already encrypted and the server offers STARTTLS. Passwords are never sent unencrypted either way unless the connection options allow it.</summary>
    Auto,

    /// <summary>Never upgrade.</summary>
    Never,

    /// <summary>Upgrade, and fail when the server does not offer STARTTLS.</summary>
    Required,
}

/// <summary>Options for <see cref="ImapClient"/>.</summary>
public sealed class ImapClientOptions
{
    /// <summary>Transport options: implicit TLS, certificate validation, timeouts, limits, trace. Default: a plain connection with platform certificate validation.</summary>
    public ProtocolConnectionOptions? Connection { get; init; }

    /// <summary>STARTTLS policy. Default <see cref="ImapStartTlsMode.Auto"/>.</summary>
    public ImapStartTlsMode StartTls { get; init; }

    /// <summary>Send <c>ENABLE UTF8=ACCEPT</c> after authentication when offered, so names and strings travel as UTF-8 (RFC 6855). Default false.</summary>
    public bool Utf8 { get; init; }

    /// <summary>Switch to COMPRESS=DEFLATE after authentication when offered. Default false.</summary>
    public bool Compress { get; init; }

    /// <summary>Client identification sent with <c>ID</c> (RFC 2971) after the greeting when the server offers it. Default none (nothing is sent).</summary>
    public IReadOnlyDictionary<string, string>? ClientId { get; init; }
}

/// <summary>
/// An IMAP4rev1/rev2 client over the raw protocol layer (<see cref="Raw"/>), with the selected mailbox tracked as a state model.
/// </summary>
/// <remarks>
/// <para>One command runs at a time: starting another while a <see cref="FetchAsync"/> enumeration or <see cref="IdleAsync"/> is open throws
/// <see cref="InvalidOperationException"/>. Not thread-safe. Cancelling a token while a command is in flight leaves the connection mid-response; dispose it.</para>
/// <para>Methods that fail on the server throw <see cref="ImapCommandException"/>; malformed server output never throws, it is a defect on the response.
/// Synchronous twins block on the asynchronous implementation. <see cref="IdleAsync"/> has no synchronous twin.</para>
/// </remarks>
public sealed partial class ImapClient : IAsyncDisposable, IDisposable
{
    private readonly ImapRawConnection _raw;
    private readonly ImapClientOptions _options;
    private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private ImapMailbox? _pendingMailbox;
    private List<ImapEvent>? _collector;
    private bool _busy;

    private ImapClient(ImapRawConnection raw, ImapClientOptions options)
    {
        _raw = raw;
        _options = options;
    }

    /// <summary>The raw protocol connection, for extensions this client does not cover. Using it while a command of this client is in flight corrupts the exchange.</summary>
    public ImapRawConnection Raw => _raw;

    /// <summary>The server greeting.</summary>
    public ImapResponse Greeting => _raw.Greeting!;

    /// <summary>The capabilities last announced (refreshed after STARTTLS and authentication).</summary>
    public IReadOnlyCollection<string> Capabilities => _raw.Capabilities;

    /// <summary>Whether the capability was announced.</summary>
    /// <param name="capability">For example <c>CONDSTORE</c>, <c>MOVE</c>, <c>AUTH=PLAIN</c>.</param>
    /// <returns>True if announced.</returns>
    public bool HasCapability(string capability) => _raw.HasCapability(capability);

    /// <summary>Whether the session is authenticated (login done or PREAUTH greeting).</summary>
    public bool IsAuthenticated { get; private set; }

    /// <summary>Whether the transport is encrypted.</summary>
    public bool IsEncrypted => _raw.Connection.IsEncrypted;

    /// <summary>Extensions the server confirmed with ENABLE, upper case.</summary>
    public IReadOnlyCollection<string> Enabled => _enabled;

    /// <summary>The selected mailbox, or null.</summary>
    public ImapMailbox? Mailbox { get; private set; }

    /// <summary>Raised for each unsolicited change (EXISTS, EXPUNGE, VANISHED, FETCH, BYE ...) as responses are read, on the thread that reads them.</summary>
    public event Action<ImapEvent>? EventReceived;

    // ---- connect ----

    /// <summary>Connects, reads the greeting, learns the capabilities and upgrades with STARTTLS as the options say.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port: 143, or 993 with implicit TLS in <see cref="ImapClientOptions.Connection"/>.</param>
    /// <param name="options">Client options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The connected client; call <see cref="LoginAsync"/> next.</returns>
    /// <exception cref="ImapCommandException">The server greeted with BYE, or refused STARTTLS.</exception>
    /// <exception cref="MailProtocolException">STARTTLS was required and not offered.</exception>
    public static async ValueTask<ImapClient> ConnectAsync(string host, int port, ImapClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ImapClientOptions();
        ImapRawConnection raw = await ImapRawConnection.ConnectAsync(host, port, options.Connection, cancellationToken).ConfigureAwait(false);
        var client = new ImapClient(raw, options);
        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Synchronous twin of <see cref="ConnectAsync"/>.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port.</param>
    /// <param name="options">Client options, or null.</param>
    /// <returns>The connected client.</returns>
    public static ImapClient Connect(string host, int port, ImapClientOptions? options = null) =>
        ConnectAsync(host, port, options).AsTask().GetAwaiter().GetResult();

    /// <summary>Wraps a connection the caller opened (a stream, a proxy, a test), reads its greeting and continues as <see cref="ConnectAsync"/> does.</summary>
    /// <param name="connection">The transport; the client owns it from here.</param>
    /// <param name="options">Client options, or null.</param>
    /// <param name="cancellationToken">Cancels the greeting.</param>
    /// <returns>The client.</returns>
    public static async ValueTask<ImapClient> FromConnectionAsync(ProtocolConnection connection, ImapClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = new ImapClient(new ImapRawConnection(connection), options ?? new ImapClientOptions());
        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask StartAsync(CancellationToken ct)
    {
        ImapResponse g = await _raw.ReadGreetingAsync(ct).ConfigureAwait(false);
        if (g.Status == ImapStatus.Bye || g.Is("BYE"))
        {
            throw new MailProtocolException("The server refused the connection: " + g.Text);
        }

        IsAuthenticated = g.Status == ImapStatus.PreAuth;
        if (_raw.Capabilities.Count == 0)
        {
            await RefreshCapabilitiesAsync(ct).ConfigureAwait(false);
        }

        if (!IsEncrypted && _options.StartTls != ImapStartTlsMode.Never && !IsAuthenticated)
        {
            if (HasCapability("STARTTLS"))
            {
                ImapCommandResult r = await _raw.StartTlsAsync(ct).ConfigureAwait(false);
                if (!r.IsOk)
                {
                    throw new ImapCommandException(r.Completion);
                }

                await RefreshCapabilitiesAsync(ct).ConfigureAwait(false);
            }
            else if (_options.StartTls == ImapStartTlsMode.Required)
            {
                throw new MailProtocolException("The server does not offer STARTTLS.");
            }
        }

        if (_options.ClientId is { } id && HasCapability("ID"))
        {
            await IdAsync(id, ct).ConfigureAwait(false);
        }

        if (IsAuthenticated)
        {
            await AfterAuthenticationAsync(ct).ConfigureAwait(false);
        }
    }

    private async ValueTask RefreshCapabilitiesAsync(CancellationToken ct) =>
        await RunAsync(ImapCommand.Create("CAPABILITY"), ct: ct).ConfigureAwait(false);

    // ---- authentication ----

    /// <summary>
    /// Logs in with a user name and password: SASL <c>PLAIN</c> when offered, else <c>LOGIN</c>. Refuses to send the password over an unencrypted connection
    /// unless the connection options allow it (<see cref="InvalidOperationException"/>).
    /// </summary>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when authenticated.</returns>
    /// <exception cref="ImapCommandException">The server rejected the credentials (<see cref="ImapCommandException.ResponseCode"/> is often <c>AUTHENTICATIONFAILED</c>).</exception>
    /// <exception cref="NotSupportedException">The server offers neither PLAIN nor LOGIN (<c>LOGINDISABLED</c> without <c>AUTH=PLAIN</c>).</exception>
    public async ValueTask LoginAsync(string user, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        EnterBusy();
        try
        {
            ImapCommandResult r;
            if (HasCapability("AUTH=PLAIN"))
            {
                r = await _raw.AuthenticateAsync(new Sasl.SaslPlain(user, password), cancellationToken).ConfigureAwait(false);
            }
            else if (!HasCapability("LOGINDISABLED"))
            {
                r = await _raw.LoginAsync(user, password, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException("The server offers neither AUTH=PLAIN nor LOGIN.");
            }

            await FinishAuthenticationAsync(r, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Synchronous twin of <see cref="LoginAsync"/>.</summary>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    public void Login(string user, string password) => LoginAsync(user, password).AsTask().GetAwaiter().GetResult();

    /// <summary>Authenticates with a SASL mechanism (RFC 4422).</summary>
    /// <param name="mechanism">The mechanism, ready to run.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when authenticated.</returns>
    /// <exception cref="ImapCommandException">The server rejected the exchange.</exception>
    public async ValueTask AuthenticateAsync(Sasl.SaslMechanism mechanism, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        EnterBusy();
        try
        {
            ImapCommandResult r = await _raw.AuthenticateAsync(mechanism, cancellationToken).ConfigureAwait(false);
            await FinishAuthenticationAsync(r, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Synchronous twin of <see cref="AuthenticateAsync(Sasl.SaslMechanism, CancellationToken)"/>.</summary>
    /// <param name="mechanism">The mechanism.</param>
    public void Authenticate(Sasl.SaslMechanism mechanism) => AuthenticateAsync(mechanism).AsTask().GetAwaiter().GetResult();

    /// <summary>Authenticates with the first mechanism in <paramref name="preferences"/> that the server announced (<c>AUTH=name</c>).</summary>
    /// <param name="preferences">Candidate mechanisms in order of preference.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The name of the mechanism used.</returns>
    /// <exception cref="NotSupportedException">None of them is offered.</exception>
    public async ValueTask<string> AuthenticateAsync(IEnumerable<Sasl.SaslMechanism> preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        foreach (Sasl.SaslMechanism m in preferences)
        {
            if (HasCapability("AUTH=" + m.Name))
            {
                await AuthenticateAsync(m, cancellationToken).ConfigureAwait(false);
                return m.Name;
            }
        }

        throw new NotSupportedException("The server offers none of the requested SASL mechanisms: " + string.Join(' ', Capabilities.Where(c => c.StartsWith("AUTH=", StringComparison.OrdinalIgnoreCase))));
    }

    private async ValueTask FinishAuthenticationAsync(ImapCommandResult r, CancellationToken ct)
    {
        if (!r.IsOk)
        {
            throw new ImapCommandException(r.Completion);
        }

        IsAuthenticated = true;
        _busy = false;
        if (_raw.Capabilities.Count == 0)
        {
            await RefreshCapabilitiesAsync(ct).ConfigureAwait(false);
        }

        await AfterAuthenticationAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask AfterAuthenticationAsync(CancellationToken ct)
    {
        if (_options.Utf8 && HasCapability("UTF8=ACCEPT"))
        {
            await EnableAsync(["UTF8=ACCEPT"], ct).ConfigureAwait(false);
        }

        if (_options.Compress && HasCapability("COMPRESS=DEFLATE"))
        {
            await CompressAsync(ct).ConfigureAwait(false);
        }
    }

    // ---- session commands ----

    /// <summary>Asks for extensions to be switched on (<c>ENABLE</c>, RFC 5161). Needs authentication.</summary>
    /// <param name="extensions">For example <c>CONDSTORE</c>, <c>QRESYNC</c>, <c>UTF8=ACCEPT</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The extensions the server confirmed now (also in <see cref="Enabled"/>).</returns>
    /// <exception cref="NotSupportedException">The server does not offer ENABLE.</exception>
    public async ValueTask<IReadOnlyList<string>> EnableAsync(IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (!HasCapability("ENABLE") && !HasCapability("IMAP4rev2"))
        {
            throw new NotSupportedException("The server does not offer ENABLE.");
        }

        var cmd = ImapCommand.Create("ENABLE");
        foreach (string e in extensions)
        {
            cmd.Atom(e);
        }

        var got = new List<string>();
        await RunAsync(cmd, each: r =>
        {
            if (r.Is("ENABLED"))
            {
                foreach (ImapValue v in r.Data)
                {
                    if (v.Text is { } t)
                    {
                        got.Add(t.ToUpperInvariant());
                    }
                }
            }
        }, ct: cancellationToken).ConfigureAwait(false);
        return got;
    }

    /// <summary>Exchanges client and server identification (<c>ID</c>, RFC 2971).</summary>
    /// <param name="clientInfo">Fields to send (name, version, ...), or null to send <c>NIL</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The server's fields; empty when it sent <c>NIL</c>.</returns>
    public async ValueTask<IReadOnlyDictionary<string, string>> IdAsync(IReadOnlyDictionary<string, string>? clientInfo = null, CancellationToken cancellationToken = default)
    {
        var cmd = ImapCommand.Create("ID");
        if (clientInfo is null || clientInfo.Count == 0)
        {
            cmd.Atom("NIL");
        }
        else
        {
            cmd.Open();
            foreach (KeyValuePair<string, string> kv in clientInfo)
            {
                cmd.Quoted(kv.Key).Quoted(kv.Value);
            }

            cmd.Close();
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await RunAsync(cmd, each: r =>
        {
            if (r.Is("ID") && r.Data.Count > 0 && r.Data[0].IsList)
            {
                IReadOnlyList<ImapValue> l = r.Data[0].Items;
                for (int i = 0; i + 1 < l.Count; i += 2)
                {
                    if (l[i].Text is { } k)
                    {
                        result[k] = l[i + 1].Text ?? "";
                    }
                }
            }
        }, ct: cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Switches the connection to COMPRESS=DEFLATE (RFC 4978).</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when compression is on.</returns>
    /// <exception cref="ImapCommandException">The server refused (e.g. already active).</exception>
    public async ValueTask CompressAsync(CancellationToken cancellationToken = default)
    {
        EnterBusy();
        try
        {
            ImapCommandResult r = await _raw.CompressAsync(cancellationToken).ConfigureAwait(false);
            if (!r.IsOk)
            {
                throw new ImapCommandException(r.Completion);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Reads the namespaces (<c>NAMESPACE</c>, RFC 2342).</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The prefixes and delimiters.</returns>
    public async ValueTask<ImapNamespaces> NamespaceAsync(CancellationToken cancellationToken = default)
    {
        ImapNamespaceData? ns = null;
        await RunAsync(ImapCommand.Create("NAMESPACE"), each: r =>
        {
            if (r.TryGetNamespace(out ImapNamespaceData d))
            {
                ns = d;
            }
        }, ct: cancellationToken).ConfigureAwait(false);
        static IReadOnlyList<ImapNamespace> Map(IReadOnlyList<KeyValuePair<string, string?>>? l) =>
            l is null ? [] : l.Select(kv => new ImapNamespace(kv.Key, kv.Value)).ToList();
        return new ImapNamespaces(Map(ns?.Personal), Map(ns?.OtherUsers), Map(ns?.Shared));
    }

    /// <summary>Does nothing, and lets the server report changes (<c>NOOP</c>). Updates <see cref="Mailbox"/>.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask NoopAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(ImapCommand.Create("NOOP"), ct: cancellationToken).ConfigureAwait(false);

    /// <summary>Synchronous twin of <see cref="NoopAsync"/>.</summary>
    public void Noop() => NoopAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Ends the session (<c>LOGOUT</c>) and closes the connection.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the server has said goodbye.</returns>
    public async ValueTask LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RunAsync(ImapCommand.Create("LOGOUT"), ct: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsAuthenticated = false;
            Mailbox = null;
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous twin of <see cref="LogoutAsync"/>.</summary>
    public void Logout() => LogoutAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Closes the connection without LOGOUT.</summary>
    public void Dispose() => _raw.Dispose();

    /// <summary>Closes the connection without LOGOUT.</summary>
    /// <returns>A task that completes when closed.</returns>
    public ValueTask DisposeAsync() => _raw.DisposeAsync();

    // ---- plumbing ----

    private void EnterBusy()
    {
        if (_busy)
        {
            throw new InvalidOperationException("Another command is in progress on this connection (a FetchAsync or IdleAsync enumeration is still open, or calls overlap).");
        }

        _busy = true;
    }

    private async ValueTask<ImapCommandResult> RunAsync(ImapCommand cmd, ImapLiteralHandler? literals = null, Action<ImapResponse>? each = null, bool throwOnFailure = true, CancellationToken ct = default)
    {
        EnterBusy();
        try
        {
            ImapCommandResult r = await _raw.ExecuteAsync(cmd, literals, resp =>
            {
                Observe(resp);
                each?.Invoke(resp);
            }, ct).ConfigureAwait(false);
            if (r.Completion.Code is { } code)
            {
                ObserveCode(code);
            }

            if (throwOnFailure && !r.IsOk)
            {
                throw new ImapCommandException(r.Completion);
            }

            return r;
        }
        finally
        {
            _busy = false;
        }
    }

    // The state model: every untagged response passes through here, whichever command is running.
    private void Observe(ImapResponse r)
    {
        if (r.Kind != ImapResponseKind.Untagged)
        {
            return;
        }

        ImapMailbox? mb = _pendingMailbox ?? Mailbox;
        if (r.Number is { } n)
        {
            switch (r.Name.ToUpperInvariant())
            {
                case "EXISTS":
                    if (mb is not null) { mb.Exists = n; }
                    Raise(new ImapEvent(ImapEventKind.Exists) { Count = n });
                    return;
                case "RECENT":
                    if (mb is not null) { mb.Recent = n; }
                    Raise(new ImapEvent(ImapEventKind.Recent) { Count = n });
                    return;
                case "EXPUNGE":
                    if (mb is not null && mb.Exists > 0) { mb.Exists--; }
                    Raise(new ImapEvent(ImapEventKind.Expunge) { Number = n });
                    return;
                case "FETCH":
                    if (!_inFetch && r.TryGetFetch(out ImapFetchData f))
                    {
                        Raise(new ImapEvent(ImapEventKind.Fetch) { Number = n, Fetch = f });
                    }

                    return;
                default:
                    return;
            }
        }

        switch (r.Name.ToUpperInvariant())
        {
            case "FLAGS":
                if (mb is not null && r.Data.Count > 0 && r.Data[0].IsList)
                {
                    mb.Flags = r.Data[0].Items.Select(v => v.Text ?? "").ToList();
                }

                break;
            case "VANISHED":
                if (r.TryGetVanished(out ImapVanishedData v))
                {
                    var set = new ImapSet(v.UidSet);
                    if (!v.Earlier && mb is not null)
                    {
                        mb.Exists = Math.Max(0, mb.Exists - set.Expand().Take(10_000_000).LongCount());
                    }

                    Raise(new ImapEvent(ImapEventKind.Vanished) { Uids = set, Earlier = v.Earlier });
                }

                break;
            case "ENABLED":
                foreach (ImapValue e in r.Data)
                {
                    if (e.Text is { } t)
                    {
                        _enabled.Add(t);
                    }
                }

                break;
            case "BYE":
                Raise(new ImapEvent(ImapEventKind.Bye) { Text = r.Text });
                break;
            case "OK":
                if (r.Code is { } code)
                {
                    ObserveCode(code);
                }

                break;
            default:
                break;
        }
    }

    private bool _inFetch;

    // Response codes that describe the selected mailbox (RFC 9051 §7.1), on untagged OK or on the tagged completion.
    private void ObserveCode(ImapResponseCode code)
    {
        ImapMailbox? mb = _pendingMailbox ?? Mailbox;
        if (mb is null)
        {
            return;
        }

        IReadOnlyList<ImapValue> a = code.Arguments;
        long Num() => a.Count > 0 && a[0].TryGetNumber(out long x) ? x : 0;
        switch (code.Name.ToUpperInvariant())
        {
            case "UIDVALIDITY": mb.UidValidity = Num(); break;
            case "UIDNEXT": mb.UidNext = Num(); break;
            case "HIGHESTMODSEQ": mb.HighestModSeq = Num(); mb.NoModSeq = false; break;
            case "NOMODSEQ": mb.NoModSeq = true; mb.HighestModSeq = 0; break;
            case "UNSEEN": mb.FirstUnseen = Num(); break;
            case "READ-ONLY": mb.ReadOnly = true; break;
            case "READ-WRITE": mb.ReadOnly = false; break;
            case "MAILBOXID": mb.MailboxId = a.Count > 0 ? (a[0].IsList && a[0].Items.Count > 0 ? a[0].Items[0].Text : a[0].Text) : null; break;
            case "PERMANENTFLAGS":
                if (a.Count > 0 && a[0].IsList)
                {
                    mb.PermanentFlags = a[0].Items.Select(x => x.Text ?? "").ToList();
                }

                break;
            default: break;
        }
    }

    private void Raise(ImapEvent e)
    {
        _collector?.Add(e);
        EventReceived?.Invoke(e);
    }

    private void RequireCapability(string capability, string? alternative = null)
    {
        if (!HasCapability(capability) && (alternative is null || !HasCapability(alternative)))
        {
            throw new NotSupportedException("The server does not offer " + capability + ".");
        }
    }

    private static T Wait<T>(ValueTask<T> t) => t.AsTask().GetAwaiter().GetResult();
}
