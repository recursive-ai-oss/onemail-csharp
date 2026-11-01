using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Client.Pop3.Protocol;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Client.Pop3;

/// <summary>A server answered a POP3 command with <c>-ERR</c>.</summary>
public sealed class Pop3CommandException : MailProtocolException
{
    internal Pop3CommandException(Pop3Response response) : base("POP3 -ERR: " + response.Text)
    {
        Response = response;
    }

    /// <summary>The negative response.</summary>
    public Pop3Response Response { get; }

    /// <summary>The bracketed response code (RFC 3206), e.g. <c>AUTH</c>, <c>IN-USE</c>, <c>LOGIN-DELAY</c>, or null.</summary>
    public string? ResponseCode => Response.Code;
}

/// <summary>A message in the maildrop as LIST and UIDL describe it.</summary>
/// <param name="Number">The message number (valid for this session only).</param>
/// <param name="Size">Size in octets as LIST reports it; -1 when not asked for.</param>
/// <param name="Uid">The unique id from UIDL, or null when not asked for.</param>
public readonly record struct Pop3MessageInfo(int Number, long Size, string? Uid);

/// <summary>Whether to upgrade a plain connection with STLS.</summary>
public enum Pop3StartTlsMode
{
    /// <summary>Upgrade when not encrypted and the server offers STLS.</summary>
    Auto,

    /// <summary>Never upgrade.</summary>
    Never,

    /// <summary>Upgrade, and fail when the server does not offer STLS.</summary>
    Required,
}

/// <summary>Options for <see cref="Pop3Client"/>.</summary>
public sealed class Pop3ClientOptions
{
    /// <summary>Transport options. Default: a plain connection with platform certificate validation.</summary>
    public ProtocolConnectionOptions? Connection { get; init; }

    /// <summary>STLS policy. Default <see cref="Pop3StartTlsMode.Auto"/>.</summary>
    public Pop3StartTlsMode StartTls { get; init; }

    /// <summary>How many commands to keep in flight when the server offers PIPELINING (RFC 2449). Default 16.</summary>
    public int PipelineWindow { get; init; } = 16;
}

/// <summary>A POP3 client (RFC 1939, 2449, 2595, 5034) over <see cref="Pop3RawConnection"/>.</summary>
/// <remarks>
/// Not thread-safe; one command at a time. Cancelling a token mid-command leaves the stream mid-response; dispose the client.
/// Message numbers are valid for the session only; use <see cref="UidlAsync"/> for ids that survive it. Synchronous twins block on the asynchronous implementation.
/// </remarks>
public sealed class Pop3Client : IAsyncDisposable, IDisposable
{
    private readonly Pop3RawConnection _raw;
    private readonly Pop3ClientOptions _options;

    private Pop3Client(Pop3RawConnection raw, Pop3ClientOptions options)
    {
        _raw = raw;
        _options = options;
    }

    /// <summary>The raw protocol connection, for commands this client does not cover.</summary>
    public Pop3RawConnection Raw => _raw;

    /// <summary>The server capabilities (<c>CAPA</c>), or null when the server does not support it.</summary>
    public Pop3Capabilities? Capabilities { get; private set; }

    /// <summary>Whether the session is in the transaction state.</summary>
    public bool IsAuthenticated { get; private set; }

    /// <summary>Whether the transport is encrypted.</summary>
    public bool IsEncrypted => _raw.Connection.IsEncrypted;

    /// <summary>Connects, reads the greeting, learns the capabilities and upgrades with STLS as the options say.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port: 110, or 995 with implicit TLS in the connection options.</param>
    /// <param name="options">Client options, or null.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The connected client; call <see cref="LoginAsync"/> next.</returns>
    /// <exception cref="Pop3CommandException">The greeting was <c>-ERR</c>, or STLS was refused.</exception>
    /// <exception cref="MailProtocolException">STLS was required and not offered.</exception>
    public static async ValueTask<Pop3Client> ConnectAsync(string host, int port, Pop3ClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new Pop3ClientOptions();
        var client = new Pop3Client(await Pop3RawConnection.ConnectAsync(host, port, options.Connection, cancellationToken).ConfigureAwait(false), options);
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

    /// <summary>Synchronous twin of <see cref="ConnectAsync(string, int, Pop3ClientOptions?, CancellationToken)"/>.</summary>
    /// <param name="host">Host name.</param>
    /// <param name="port">Port.</param>
    /// <param name="options">Client options, or null.</param>
    /// <returns>The connected client.</returns>
    public static Pop3Client Connect(string host, int port, Pop3ClientOptions? options = null) =>
        ConnectAsync(host, port, options).AsTask().GetAwaiter().GetResult();

    /// <summary>Wraps a connection the caller opened (a stream, a proxy, a test), reads its greeting and continues as <see cref="ConnectAsync(string, int, Pop3ClientOptions?, CancellationToken)"/> does.</summary>
    /// <param name="connection">The transport; the client owns it from here.</param>
    /// <param name="options">Client options, or null.</param>
    /// <param name="cancellationToken">Cancels the greeting.</param>
    /// <returns>The client.</returns>
    public static async ValueTask<Pop3Client> ConnectAsync(ProtocolConnection connection, Pop3ClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = new Pop3Client(new Pop3RawConnection(connection), options ?? new Pop3ClientOptions());
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
        Pop3Response g = await _raw.ReadGreetingAsync(ct).ConfigureAwait(false);
        if (!g.IsOk)
        {
            throw new Pop3CommandException(g);
        }

        await RefreshCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!IsEncrypted && _options.StartTls != Pop3StartTlsMode.Never)
        {
            if (Capabilities?.Has("STLS") ?? false)
            {
                Pop3Response r = await _raw.StartTlsAsync(ct).ConfigureAwait(false);
                if (!r.IsOk)
                {
                    throw new Pop3CommandException(r);
                }

                await RefreshCapabilitiesAsync(ct).ConfigureAwait(false);
            }
            else if (_options.StartTls == Pop3StartTlsMode.Required)
            {
                throw new MailProtocolException("The server does not offer STLS.");
            }
        }
    }

    private async ValueTask RefreshCapabilitiesAsync(CancellationToken ct)
    {
        (Pop3Response r, Pop3Capabilities? caps) = await _raw.CapaAsync(ct).ConfigureAwait(false);
        Capabilities = r.IsOk ? caps : null;
    }

    private static Pop3Response Check(Pop3Response r) => r.IsOk ? r : throw new Pop3CommandException(r);

    // ---- authentication ----

    /// <summary>
    /// Logs in with a user name and password: SASL <c>PLAIN</c> when the server lists it, else <c>USER</c>/<c>PASS</c>.
    /// The password is never sent over an unencrypted connection unless the connection options allow it (<see cref="InvalidOperationException"/>).
    /// </summary>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the transaction state is reached.</returns>
    /// <exception cref="Pop3CommandException">The credentials were rejected.</exception>
    public async ValueTask LoginAsync(string user, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        Pop3Response r = Capabilities?.SaslMechanisms.Contains("PLAIN") ?? false
            ? await _raw.AuthenticateAsync(new SaslPlain(user, password), cancellationToken).ConfigureAwait(false)
            : await _raw.LoginAsync(user, password, cancellationToken).ConfigureAwait(false);
        await AuthenticatedAsync(r, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="LoginAsync"/>.</summary>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    public void Login(string user, string password) => LoginAsync(user, password).AsTask().GetAwaiter().GetResult();

    /// <summary>Authenticates with a SASL mechanism (<c>AUTH</c>, RFC 5034).</summary>
    /// <param name="mechanism">The mechanism.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when authenticated.</returns>
    /// <exception cref="Pop3CommandException">The exchange failed.</exception>
    public async ValueTask AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken = default) =>
        await AuthenticatedAsync(await _raw.AuthenticateAsync(mechanism, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>Synchronous twin of <see cref="AuthenticateAsync"/>.</summary>
    /// <param name="mechanism">The mechanism.</param>
    public void Authenticate(SaslMechanism mechanism) => AuthenticateAsync(mechanism).AsTask().GetAwaiter().GetResult();

    /// <summary>Logs in with <c>APOP</c> (RFC 1939 §7), which proves the password without sending it but is defined on MD5. Use only on connections that cannot use TLS.</summary>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when authenticated.</returns>
    /// <exception cref="NotSupportedException">The greeting carried no timestamp.</exception>
    /// <exception cref="Pop3CommandException">The server rejected the digest.</exception>
    public async ValueTask ApopAsync(string user, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        string stamp = _raw.ApopTimestamp ?? throw new NotSupportedException("The server's greeting has no APOP timestamp.");
        // RFC 1939 §7: digest = MD5(timestamp-with-brackets + shared secret), as 32 lower-case hex digits.
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Encoding.UTF8.GetBytes(stamp + password));
        string digest = Convert.ToHexStringLower(md5.GetHashAndReset());
        Pop3Response r = await _raw.CommandAsync("APOP", user + " " + digest, cancellationToken).ConfigureAwait(false);
        await AuthenticatedAsync(r, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AuthenticatedAsync(Pop3Response r, CancellationToken ct)
    {
        Check(r);
        IsAuthenticated = true;
        await RefreshCapabilitiesAsync(ct).ConfigureAwait(false);
    }

    // ---- maildrop ----

    /// <summary>The number of messages and their total size (<c>STAT</c>).</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>Count and octets.</returns>
    public async ValueTask<(int Count, long Size)> StatAsync(CancellationToken cancellationToken = default)
    {
        (Pop3Response r, int c, long s) = await _raw.StatAsync(cancellationToken).ConfigureAwait(false);
        Check(r);
        return (c, s);
    }

    /// <summary>Synchronous twin of <see cref="StatAsync"/>.</summary>
    /// <returns>Count and octets.</returns>
    public (int Count, long Size) Stat() => StatAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Lists messages with their sizes (<c>LIST</c>); deleted messages are not listed.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The messages; <see cref="Pop3MessageInfo.Uid"/> is null.</returns>
    public async ValueTask<IReadOnlyList<Pop3MessageInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        (Pop3Response r, IReadOnlyList<Pop3ListEntry> entries) = await _raw.ListAsync(false, cancellationToken).ConfigureAwait(false);
        Check(r);
        return entries.Select(e => new Pop3MessageInfo(e.Number, long.TryParse(e.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long s) ? s : -1, null)).ToList();
    }

    /// <summary>Synchronous twin of <see cref="ListAsync"/>.</summary>
    /// <returns>The messages.</returns>
    public IReadOnlyList<Pop3MessageInfo> List() => ListAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Lists unique ids (<c>UIDL</c>, RFC 1939 §7): ids that stay the same across sessions.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The messages; <see cref="Pop3MessageInfo.Size"/> is -1.</returns>
    /// <exception cref="Pop3CommandException">The server does not support UIDL.</exception>
    public async ValueTask<IReadOnlyList<Pop3MessageInfo>> UidlAsync(CancellationToken cancellationToken = default)
    {
        (Pop3Response r, IReadOnlyList<Pop3ListEntry> entries) = await _raw.ListAsync(true, cancellationToken).ConfigureAwait(false);
        Check(r);
        return entries.Select(e => new Pop3MessageInfo(e.Number, -1, e.Value)).ToList();
    }

    /// <summary>Synchronous twin of <see cref="UidlAsync"/>.</summary>
    /// <returns>The messages.</returns>
    public IReadOnlyList<Pop3MessageInfo> Uidl() => UidlAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Streams a message to a stream (<c>RETR</c>), dot-unstuffed, line ends as the server sent them.</summary>
    /// <param name="number">The message number.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="Pop3CommandException">No such message.</exception>
    public async ValueTask<long> RetrieveAsync(int number, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        (Pop3Response r, Pop3DotStream? body) = await _raw.MultilineAsync("RETR", Number(number), cancellationToken).ConfigureAwait(false);
        Check(r);
        await body!.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return body.BytesRead;
    }

    /// <summary>Synchronous twin of <see cref="RetrieveAsync"/>.</summary>
    /// <param name="number">The message number.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns>Bytes written.</returns>
    public long Retrieve(int number, Stream destination) => RetrieveAsync(number, destination).AsTask().GetAwaiter().GetResult();

    /// <summary>Fetches a message (<c>RETR</c>) and parses it into the MIME entity tree.</summary>
    /// <param name="number">The message number.</param>
    /// <param name="options">MIME reader options, or null.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The root entity.</returns>
    /// <exception cref="Pop3CommandException">No such message.</exception>
    public async ValueTask<MimeEntity> GetMessageAsync(int number, MimeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        (Pop3Response r, Pop3DotStream? body) = await _raw.MultilineAsync("RETR", Number(number), cancellationToken).ConfigureAwait(false);
        Check(r);
        return await MimeEntity.LoadAsync(body!, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="GetMessageAsync"/>.</summary>
    /// <param name="number">The message number.</param>
    /// <param name="options">MIME reader options.</param>
    /// <returns>The root entity.</returns>
    public MimeEntity GetMessage(int number, MimeReaderOptions? options = null) => GetMessageAsync(number, options).AsTask().GetAwaiter().GetResult();

    /// <summary>Fetches the header and the first lines of the body (<c>TOP</c>) and parses them. The tree is of a truncated message.</summary>
    /// <param name="number">The message number.</param>
    /// <param name="bodyLines">How many body lines to include; 0 for the header alone.</param>
    /// <param name="options">MIME reader options, or null.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The root entity.</returns>
    /// <exception cref="Pop3CommandException">No such message, or no TOP support.</exception>
    public async ValueTask<MimeEntity> GetTopAsync(int number, int bodyLines = 0, MimeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLines);
        (Pop3Response r, Pop3DotStream? body) = await _raw.MultilineAsync("TOP", Number(number) + " " + bodyLines.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        Check(r);
        return await MimeEntity.LoadAsync(body!, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches several messages, keeping up to <see cref="Pop3ClientOptions.PipelineWindow"/> RETR commands in flight when the server offers PIPELINING,
    /// and yields each parsed message as it arrives.
    /// </summary>
    /// <param name="numbers">The message numbers.</param>
    /// <param name="options">MIME reader options, or null.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>Pairs of message number and tree, in the order requested.</returns>
    /// <exception cref="Pop3CommandException">A message could not be retrieved (the enumeration stops there).</exception>
    public async IAsyncEnumerable<(int Number, MimeEntity Message)> GetMessagesAsync(IReadOnlyList<int> numbers, MimeReaderOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        int window = (Capabilities?.Has("PIPELINING") ?? false) ? Math.Max(1, _options.PipelineWindow) : 1;
        int sent = 0;
        for (int i = 0; i < numbers.Count; i++)
        {
            // Keep the window full: commands for later messages go out while earlier bodies are still coming in.
            while (sent < numbers.Count && sent - i < window)
            {
                await _raw.SendAsync("RETR", Number(numbers[sent]), flush: false, cancellationToken).ConfigureAwait(false);
                sent++;
            }

            await _raw.FlushAsync(cancellationToken).ConfigureAwait(false);
            (Pop3Response r, Pop3DotStream? body) = await _raw.ReadMultilineAsync(cancellationToken).ConfigureAwait(false);
            Check(r);
            yield return (numbers[i], await MimeEntity.LoadAsync(body!, options, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>Marks a message for deletion (<c>DELE</c>); it goes when the session ends with <see cref="QuitAsync"/>.</summary>
    /// <param name="number">The message number.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask DeleteAsync(int number, CancellationToken cancellationToken = default) =>
        Check(await _raw.CommandAsync("DELE", Number(number), cancellationToken).ConfigureAwait(false));

    /// <summary>Marks several messages for deletion, pipelined when the server offers PIPELINING.</summary>
    /// <param name="numbers">The message numbers.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when all replies are read.</returns>
    /// <exception cref="Pop3CommandException">A deletion was refused (the first refusal; the replies already in flight are read first).</exception>
    public async ValueTask DeleteAsync(IReadOnlyList<int> numbers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        int window = (Capabilities?.Has("PIPELINING") ?? false) ? Math.Max(1, _options.PipelineWindow) : 1;
        Pop3Response? failed = null;
        int sent = 0;
        for (int i = 0; i < numbers.Count; i++)
        {
            while (sent < numbers.Count && sent - i < window)
            {
                await _raw.SendAsync("DELE", Number(numbers[sent]), flush: false, cancellationToken).ConfigureAwait(false);
                sent++;
            }

            await _raw.FlushAsync(cancellationToken).ConfigureAwait(false);
            Pop3Response r = await _raw.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            failed ??= r.IsOk ? null : r;
        }

        if (failed is not null)
        {
            throw new Pop3CommandException(failed);
        }
    }

    /// <summary>Unmarks every message marked for deletion in this session (<c>RSET</c>).</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default) =>
        Check(await _raw.CommandAsync("RSET", null, cancellationToken).ConfigureAwait(false));

    /// <summary>Does nothing (<c>NOOP</c>); keeps the session alive.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes with the command.</returns>
    public async ValueTask NoopAsync(CancellationToken cancellationToken = default) =>
        Check(await _raw.CommandAsync("NOOP", null, cancellationToken).ConfigureAwait(false));

    /// <summary>Ends the session (<c>QUIT</c>), which applies the deletions, and closes the connection.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the server has answered.</returns>
    /// <exception cref="Pop3CommandException">The server could not apply the deletions (RFC 1939 §6: the reply is <c>-ERR</c>).</exception>
    public async ValueTask QuitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Check(await _raw.CommandAsync("QUIT", null, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            IsAuthenticated = false;
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous twin of <see cref="QuitAsync"/>.</summary>
    public void Quit() => QuitAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Closes the connection without QUIT; pending deletions are not applied.</summary>
    public void Dispose() => _raw.Dispose();

    /// <summary>Closes the connection without QUIT.</summary>
    /// <returns>A task that completes when closed.</returns>
    public ValueTask DisposeAsync() => _raw.DisposeAsync();

    private static string Number(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        return n.ToString(CultureInfo.InvariantCulture);
    }
}
