using System.Text;
using RecursiveAI.OneMail.Client.Protocol;
using RecursiveAI.OneMail.Client.Sasl;
using RecursiveAI.OneMail.Client.Smtp.Protocol;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Client.Smtp;

/// <summary>
/// An SMTP submission session (RFC 5321, RFC 6409): connects, secures the connection, authenticates, and sends any number of
/// messages over it. Each send returns a <see cref="SmtpSendResult"/> with the server's answer for every recipient; a rejected recipient is
/// data, not an exception. Everything is asynchronous with a synchronous twin and takes a cancellation token.
/// </summary>
/// <remarks>
/// Not thread-safe: one send at a time. Cancelling or timing out leaves the stream mid-reply, so the client marks itself unusable
/// (<see cref="IsUsable"/> false) and should be disposed. Messages are streamed to the server; only <see cref="BdatSink"/>'s chunk and
/// the copy made for an 8-bit downgrade are held in memory. The wire protocol is available through <see cref="Raw"/>.
/// </remarks>
public sealed class SmtpClient : IAsyncDisposable, IDisposable
{
    private readonly SmtpClientOptions _options;
    private SmtpCapabilities _caps = null!;
    private bool _dirty;
    private bool _broken;
    private bool _disposed;

    private SmtpClient(SmtpRawConnection raw, SmtpClientOptions options)
    {
        Raw = raw;
        _options = options;
    }

    /// <summary>The raw session under the client, for commands the client does not cover. Using it between sends is allowed when it leaves the session idle.</summary>
    public SmtpRawConnection Raw { get; }

    /// <summary>The extensions the server announced in its latest EHLO.</summary>
    public SmtpCapabilities Capabilities => _caps;

    /// <summary>Whether the connection is encrypted.</summary>
    public bool IsEncrypted => Raw.Connection.IsEncrypted;

    /// <summary>Whether authentication succeeded on this session.</summary>
    public bool IsAuthenticated { get; private set; }

    /// <summary>Whether the session can take another message: not disposed, not cut off by a timeout, cancellation or a 421.</summary>
    public bool IsUsable => !_disposed && !_broken;

    // ---- connecting ----

    /// <summary>Connects, reads the greeting, says EHLO, secures the connection as <see cref="SmtpClientOptions.Security"/> says and authenticates.</summary>
    /// <param name="options">The server and policy.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <returns>A ready session.</returns>
    /// <exception cref="SmtpGreetingException">The server refused the session.</exception>
    /// <exception cref="SmtpFeatureRequiredException">STARTTLS is required and not offered.</exception>
    /// <exception cref="SmtpAuthenticationException">Authentication failed.</exception>
    public static async ValueTask<SmtpClient> ConnectAsync(SmtpClientOptions options, CancellationToken cancellationToken = default) =>
        await ConnectCore(false, options, cancellationToken).ConfigureAwait(false);

    /// <summary>Synchronous twin of <see cref="ConnectAsync"/>.</summary>
    /// <param name="options">The server and policy.</param>
    /// <returns>A ready session.</returns>
    public static SmtpClient Connect(SmtpClientOptions options) => SyncBridge.Run(ConnectCore(true, options, default));

    private static ProtocolConnectionOptions TransportOptions(SmtpClientOptions o)
    {
        ProtocolConnectionOptions? c = o.Connection;
        return new ProtocolConnectionOptions
        {
            ImplicitTls = o.Security == SmtpSecurity.ImplicitTls,
            TlsTargetHost = c?.TlsTargetHost,
            ServerCertificateValidationCallback = c?.ServerCertificateValidationCallback,
            ClientCertificates = c?.ClientCertificates,
            ApplicationProtocols = c?.ApplicationProtocols,
            EnabledSslProtocols = c?.EnabledSslProtocols ?? System.Security.Authentication.SslProtocols.None,
            ConfigureTls = c?.ConfigureTls,
            ConnectTimeout = c?.ConnectTimeout ?? TimeSpan.FromSeconds(30),
            ReadTimeout = c?.ReadTimeout ?? TimeSpan.FromMinutes(2),
            WriteTimeout = c?.WriteTimeout ?? TimeSpan.FromMinutes(2),
            Limits = c?.Limits ?? ProtocolLimits.Default,
            Trace = c?.Trace,
            AllowInsecureAuthentication = (c?.AllowInsecureAuthentication ?? false) || (o.Credentials?.AllowInsecure ?? false),
        };
    }

    private static async ValueTask<SmtpClient> ConnectCore(bool sync, SmtpClientOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        int port = options.Port ?? options.Security switch { SmtpSecurity.ImplicitTls => 465, SmtpSecurity.None => 25, _ => 587 };
        ProtocolConnectionOptions transport = TransportOptions(options);
        SmtpRawConnection raw = sync
            ? SmtpRawConnection.Connect(options.Host, port, transport)
            : await SmtpRawConnection.ConnectAsync(options.Host, port, transport, ct).ConfigureAwait(false);
        var client = new SmtpClient(raw, options);
        try
        {
            await client.StartSession(sync, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (sync) { raw.Dispose(); } else { await raw.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    private async ValueTask<SmtpReply> Read(bool sync, CancellationToken ct) => sync ? Raw.ReadReply() : await Raw.ReadReplyAsync(ct).ConfigureAwait(false);

    private async ValueTask<SmtpReply> Cmd(bool sync, string verb, string? args, CancellationToken ct) =>
        sync ? Raw.Command(verb, args) : await Raw.CommandAsync(verb, args, ct).ConfigureAwait(false);

    private async ValueTask Hello(bool sync, CancellationToken ct)
    {
        SmtpReply r = sync ? Raw.Ehlo(_options.ClientDomain) : await Raw.EhloAsync(_options.ClientDomain, ct).ConfigureAwait(false);
        if (r.Code == 250 && Raw.Capabilities is { } caps)
        {
            _caps = caps;
            return;
        }

        if (r.Code is >= 500 and < 600)
        {
            // RFC 5321 §3.2: a server that does not know EHLO is spoken to with HELO and has no extensions.
            SmtpReply h = await Cmd(sync, "HELO", _options.ClientDomain, ct).ConfigureAwait(false);
            if (h.Code == 250)
            {
                _caps = new SmtpCapabilities(new SmtpReply(250, [_options.Host], null, []));
                return;
            }

            throw new SmtpGreetingException(h);
        }

        throw new SmtpGreetingException(r);
    }

    private async ValueTask StartSession(bool sync, CancellationToken ct)
    {
        SmtpReply greeting = await Read(sync, ct).ConfigureAwait(false);
        if (greeting.Code != 220)
        {
            throw new SmtpGreetingException(greeting);
        }

        await Hello(sync, ct).ConfigureAwait(false);
        if (_options.Security is SmtpSecurity.StartTls or SmtpSecurity.StartTlsIfOffered)
        {
            if (_caps.Has("STARTTLS"))
            {
                SmtpReply r = sync ? Raw.StartTls() : await Raw.StartTlsAsync(ct).ConfigureAwait(false);
                if (r.Code != 220)
                {
                    throw new SmtpGreetingException(r);
                }

                // RFC 3207 §4.2: what the server said before the handshake no longer counts.
                await Hello(sync, ct).ConfigureAwait(false);
            }
            else if (_options.Security == SmtpSecurity.StartTls)
            {
                throw new SmtpFeatureRequiredException("STARTTLS");
            }
        }

        if (_options.Credentials is { } credentials)
        {
            await Authenticate(sync, credentials, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask Authenticate(bool sync, SaslCredentials credentials, CancellationToken ct)
    {
        IReadOnlyList<string> offered = _caps.AuthMechanisms;
        if (offered.Count == 0)
        {
            throw new SmtpAuthenticationException("The server does not offer authentication" + (IsEncrypted ? "." : " on an unencrypted connection (it may offer it after STARTTLS)."));
        }

        int port = _options.Port ?? (_options.Security == SmtpSecurity.ImplicitTls ? 465 : _options.Security == SmtpSecurity.None ? 25 : 587);
        IReadOnlyList<SaslMechanism> candidates = _options.MechanismSelector is { } custom
            ? custom(offered, Raw.Connection)
            : SaslMechanisms.Select(offered, credentials, Raw.Connection, "smtp", _options.Host, port);
        if (candidates.Count == 0)
        {
            throw new SmtpAuthenticationException("No usable SASL mechanism. The server offers " + string.Join(", ", offered)
                + (IsEncrypted ? "." : "; PLAIN and LOGIN are not used without TLS unless SaslCredentials.AllowInsecure is set."));
        }

        SmtpReply? last = null;
        foreach (SaslMechanism m in candidates)
        {
            SmtpReply r;
            try
            {
                r = sync ? Raw.Authenticate(m) : await Raw.AuthenticateAsync(m, ct).ConfigureAwait(false);
            }
            catch (SaslException ex)
            {
                // The server is waiting for a response we cannot give; the session cannot be resynchronised.
                _broken = true;
                throw new SmtpAuthenticationException(m.Name + ": " + ex.Message, null, ex);
            }

            if (r.Code == 235)
            {
                IsAuthenticated = true;
                await Hello(sync, ct).ConfigureAwait(false);
                return;
            }

            last = r;
            // 504 (mechanism not implemented) and 534 (too weak) are the cases where another mechanism can still work.
            if (r.Code is not (504 or 534))
            {
                break;
            }
        }

        throw new SmtpAuthenticationException("Authentication failed: " + last, last);
    }

    // ---- other commands ----

    /// <summary>Sends <c>NOOP</c> (keeps the session alive).</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply.</returns>
    public async ValueTask<SmtpReply> NoopAsync(CancellationToken cancellationToken = default) => await Guard(Cmd(false, "NOOP", null, cancellationToken)).ConfigureAwait(false);

    /// <summary>Synchronous twin of <see cref="NoopAsync"/>.</summary>
    /// <returns>The reply.</returns>
    public SmtpReply Noop() => SyncBridge.Run(Guard(Cmd(true, "NOOP", null, default)));

    /// <summary>Says <c>QUIT</c> and closes the connection. Errors on the way are ignored.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when closed.</returns>
    public async ValueTask QuitAsync(CancellationToken cancellationToken = default)
    {
        if (IsUsable)
        {
            try { await Cmd(false, "QUIT", null, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Synchronous twin of <see cref="QuitAsync"/>.</summary>
    public void Quit()
    {
        if (IsUsable)
        {
            try { Raw.Command("QUIT"); }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException) { }
        }

        Dispose();
    }

    private async ValueTask<SmtpReply> Guard(ValueTask<SmtpReply> reply)
    {
        ThrowIfUnusable();
        try
        {
            SmtpReply r = await reply.ConfigureAwait(false);
            if (r.Code == 421)
            {
                _broken = true;
            }

            return r;
        }
        catch
        {
            _broken = true;
            throw;
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_broken)
        {
            throw new InvalidOperationException("The SMTP session was cut off (timeout, cancellation, 421 or an error); connect again.");
        }
    }

    // ---- sending ----

    /// <summary>Sends one message.</summary>
    /// <param name="message">The message and, optionally, its envelope.</param>
    /// <param name="cancellationToken">Cancels the send. The session is unusable afterwards.</param>
    /// <returns>What the server said, per recipient and for the data. Rejections are in the result.</returns>
    /// <exception cref="SmtpFeatureRequiredException">The message needs an extension the server lacks (SMTPUTF8, DSN, REQUIRETLS).</exception>
    /// <exception cref="SmtpMessageTooLargeException">The message exceeds the server's SIZE limit.</exception>
    /// <exception cref="SmtpDowngradeRefusedException">8-bit content cannot be sent to this server and cannot be re-encoded.</exception>
    /// <exception cref="ArgumentException">The envelope cannot be worked out (no sender or no recipients).</exception>
    public async ValueTask<SmtpSendResult> SendAsync(SmtpMessage message, CancellationToken cancellationToken = default) =>
        await SendCore(false, message, cancellationToken).ConfigureAwait(false);

    /// <summary>Synchronous twin of <see cref="SendAsync"/>.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The result.</returns>
    public SmtpSendResult Send(SmtpMessage message) => SyncBridge.Run(SendCore(true, message, default));

    private static IEnumerable<string> Mailboxes(MimeEntity e, string header) =>
        e.Headers.GetAll(header).SelectMany(h => AddressParser.Flatten(AddressParser.Parse(h.RawValue.Span)).Select(m => m.Address));

    private async ValueTask<SmtpSendResult> SendCore(bool sync, SmtpMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfUnusable();
        try
        {
            return await SendChecked(sync, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SmtpClientException && ex is IOException or OperationCanceledException or TimeoutException)
        {
            _broken = true;
            throw;
        }
    }

    private async ValueTask<SmtpSendResult> SendChecked(bool sync, SmtpMessage message, CancellationToken ct)
    {
        SmtpCapabilities caps = _caps;

        // The tree that carries the headers: the message itself, or a parse of the bytes. A stream has none, so its envelope must be given.
        MimeEntity? tree = message.Entity ?? (message.Stream is null ? MimeEntity.Parse(message.Bytes) : null);
        string? sender = message.Sender ?? (tree is null ? null : (Mailboxes(tree, "Sender").FirstOrDefault() ?? Mailboxes(tree, "From").FirstOrDefault()));
        if (sender is null)
        {
            throw new ArgumentException("No sender: set SmtpMessage.Sender or give the message a From header.");
        }

        List<string> recipients = [.. (message.Recipients ?? (tree is null ? [] : Mailboxes(tree, "To").Concat(Mailboxes(tree, "Cc")).Concat(Mailboxes(tree, "Bcc")))).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (recipients.Count == 0)
        {
            throw new ArgumentException("No recipients: set SmtpMessage.Recipients or give the message To, Cc or Bcc headers.");
        }

        // What the content needs from the server.
        ContentFacts facts = message.Entity is not null ? SmtpContent.Inspect(message.Entity)
            : message.Stream is null ? SmtpContent.Inspect(message.Bytes.Span) : new ContentFacts();
        bool addressUtf8 = sender.Any(c => c > 0x7F) || recipients.Any(r => r.Any(c => c > 0x7F));
        bool hasUtf8 = caps.Has("SMTPUTF8");
        if (addressUtf8 && !hasUtf8)
        {
            throw new SmtpFeatureRequiredException("SMTPUTF8");
        }

        bool utf8Param = (addressUtf8 || facts.HeaderNonAscii) && hasUtf8;
        bool has8 = caps.Has("8BITMIME");
        bool chunking = caps.Has("CHUNKING") && _options.UseChunking;
        bool binaryOk = chunking && caps.Has("BINARYMIME");
        bool headerTrouble = facts.HeaderNonAscii && !utf8Param && !has8;
        bool downgrade = (facts.BodyNonAscii && !has8) || (facts.HasBinaryPart && !binaryOk) || headerTrouble;
        if (downgrade)
        {
            if (headerTrouble)
            {
                throw new SmtpDowngradeRefusedException("The header fields contain non-ASCII text and the server offers neither SMTPUTF8 nor 8BITMIME; encode the headers (RFC 2047) first.");
            }

            if (facts.Signed)
            {
                throw new SmtpDowngradeRefusedException("The message is signed (S/MIME, PGP or DKIM/ARC), so its 8-bit parts cannot be re-encoded for this server without breaking the signature.");
            }

            if (!message.AllowDowngrade)
            {
                throw new SmtpDowngradeRefusedException("The message has 8-bit parts, the server lacks 8BITMIME and downgrading is not allowed.");
            }
        }

        SmtpBodyType body = facts.HasBinaryPart && !downgrade ? SmtpBodyType.BinaryMime
            : (facts.BodyNonAscii || facts.HeaderNonAscii || message.Stream is not null) && has8 && !downgrade ? SmtpBodyType.EightBitMime
            : SmtpBodyType.Default;
        if (message.RequireTls && (!caps.Has("REQUIRETLS") || !IsEncrypted))
        {
            throw new SmtpFeatureRequiredException("REQUIRETLS");
        }

        if (message.Dsn is not null && !caps.Has("DSN"))
        {
            throw new SmtpFeatureRequiredException("DSN");
        }

        // The tree to write. A downgrade works on a private copy so the caller's tree is never changed.
        MimeEntity? toWrite = message.Entity;
        if (downgrade)
        {
            byte[] original = message.Entity?.ToArray() ?? message.Bytes.ToArray();
            toWrite = MimeEntity.Parse(original);
            if (!SmtpContent.Downgrade(toWrite))
            {
                throw new SmtpDowngradeRefusedException("A message/rfc822 part is 8-bit encoded and cannot be re-encoded.");
            }
        }

        // RFC 5322 §3.6.3: Bcc recipients are used for the envelope and the header is not transmitted. Taken out of the tree while sending.
        (MimeHeader Header, int Index)[] removedBcc = [];
        if (toWrite is not null && toWrite.Headers.GetAll("Bcc").Any())
        {
            removedBcc = [.. toWrite.Headers.Select((h, i) => (h, i)).Where(t => t.h.IsNamed("Bcc"))];
            foreach ((MimeHeader h, _) in removedBcc)
            {
                toWrite.Headers.Remove(h);
            }
        }

        try
        {
            long? size = toWrite is not null && message.Stream is null
                ? (caps.Has("SIZE") ? await MeasureAsync(sync, toWrite, ct).ConfigureAwait(false) : (long?)null)
                : message.Stream is not null ? message.StreamLength : message.Bytes.Length;
            if (caps.MaxMessageSize is > 0 and { } limit && size > limit)
            {
                throw new SmtpMessageTooLargeException(size.Value, limit);
            }

            var result = await Transact(sync, message, toWrite, sender, recipients, body, utf8Param, size, chunking || body == SmtpBodyType.BinaryMime, ct).ConfigureAwait(false);
            result.Downgraded = downgrade;
            result.UsedSmtpUtf8 = utf8Param;
            result.Size = size;
            return result;
        }
        finally
        {
            // Put the Bcc headers back where they were (ascending order keeps the indexes valid).
            foreach ((MimeHeader h, int index) in removedBcc)
            {
                toWrite!.Headers.Insert(Math.Min(index, toWrite.Headers.Count), h);
            }
        }
    }

    private static async ValueTask<long> MeasureAsync(bool sync, MimeEntity entity, CancellationToken ct)
    {
        var counter = new CountingStream();
        if (sync) { entity.WriteTo(counter); } else { await entity.WriteToAsync(counter, ct).ConfigureAwait(false); }
        return counter.Count;
    }

    private async ValueTask<SmtpSendResult> Transact(bool sync, SmtpMessage message, MimeEntity? toWrite, string sender, List<string> recipients, SmtpBodyType body, bool utf8, long? size, bool bdat, CancellationToken ct)
    {
        SmtpCapabilities caps = _caps;
        if (_dirty)
        {
            // The previous send ended in a state the server may still hold; RFC 5321 §4.1.1.5.
            await Cmd(sync, "RSET", null, ct).ConfigureAwait(false);
            _dirty = false;
        }

        var mailOptions = new SmtpMailOptions
        {
            Size = caps.Has("SIZE") ? size : null,
            Body = body,
            SmtpUtf8 = utf8,
            RequireTls = message.RequireTls,
            DsnReturn = message.Dsn?.Return,
            DsnEnvelopeId = message.Dsn?.EnvelopeId,
        };
        var rcptOptions = message.Dsn?.Notify is { } notify ? new SmtpRcptOptions { Notify = notify } : null;
        var commands = new List<string> { "MAIL " + SmtpRawConnection.MailFromArguments(sender, mailOptions) };
        commands.AddRange(recipients.Select(r => "RCPT " + SmtpRawConnection.RcptToArguments(r, rcptOptions)));

        bool pipelining = _options.UsePipelining && caps.Has("PIPELINING");
        bool pipeData = pipelining && !bdat && !message.RequireAllRecipients;
        var replies = new List<SmtpReply>();
        if (pipeData) { commands.Add("DATA"); }

        if (pipelining)
        {
            replies.AddRange(sync ? Raw.Pipeline(commands) : await Raw.PipelineAsync(commands, ct).ConfigureAwait(false));
        }
        else
        {
            foreach (string c in commands)
            {
                int space = c.IndexOf(' ', StringComparison.Ordinal);
                SmtpReply r = await Cmd(sync, c[..space], c[(space + 1)..], ct).ConfigureAwait(false);
                replies.Add(r);
                if (replies.Count == 1 && !r.IsPositiveCompletion)
                {
                    break; // nothing else makes sense after a refused sender
                }
            }
        }

        if (replies.Any(r => r.Code == 421)) { _broken = true; }

        var rcptResults = new List<SmtpRecipientResult>();
        for (int i = 0; i < recipients.Count && i + 1 < replies.Count; i++)
        {
            rcptResults.Add(new SmtpRecipientResult(recipients[i], replies[i + 1]));
        }

        var result = new SmtpSendResult(rcptResults) { SenderReply = replies[0], UsedChunking = bdat };
        SmtpReply? dataReply = pipeData && replies.Count == commands.Count ? replies[^1] : null;
        bool anyAccepted = rcptResults.Any(r => r.Accepted);
        bool abort = !replies[0].IsPositiveCompletion || !anyAccepted || (message.RequireAllRecipients && rcptResults.Any(r => !r.Accepted));

        if (abort)
        {
            result.UsedChunking = false;
            if (dataReply is { Code: 354 })
            {
                // The server opened DATA although it took no recipient (not allowed by RFC 5321 §3.3, but be tidy): end it so the session stays in step.
                SmtpDataStream stream = Raw.OpenDataStream(false);
                result.DataReply = sync ? stream.Complete() : await stream.CompleteAsync(ct).ConfigureAwait(false);
            }
            else if (replies[0].IsPositiveCompletion)
            {
                await Cmd(sync, "RSET", null, ct).ConfigureAwait(false);
            }

            return result;
        }

        // RFC 5321 §4.5.2 / RFC 3030: now the data.
        try
        {
            SmtpReply final;
            if (bdat)
            {
                await using var sink = new BdatSink(Raw, Math.Clamp(_options.ChunkSize, 1024, 16 * 1024 * 1024), _options.NormalizeLineEndings);
                await WriteBody(sync, message, toWrite, sink, ct).ConfigureAwait(false);
                final = sync ? sink.Complete() : await sink.CompleteAsync(ct).ConfigureAwait(false);
            }
            else
            {
                SmtpDataStream? stream;
                if (dataReply is null)
                {
                    (SmtpReply r, stream) = sync ? Raw.BeginData(_options.NormalizeLineEndings) : await Raw.BeginDataAsync(_options.NormalizeLineEndings, ct).ConfigureAwait(false);
                    dataReply = r;
                }
                else
                {
                    stream = dataReply.Code == 354 ? Raw.OpenDataStream(_options.NormalizeLineEndings) : null;
                }

                if (stream is null)
                {
                    result.DataReply = dataReply;
                    _dirty = true;
                    return result;
                }

                await using (stream.ConfigureAwait(false))
                {
                    await WriteBody(sync, message, toWrite, stream, ct).ConfigureAwait(false);
                    final = sync ? stream.Complete() : await stream.CompleteAsync(ct).ConfigureAwait(false);
                }
            }

            result.DataReply = final;
            if (final.Code == 421) { _broken = true; }
            _dirty = !final.IsPositiveCompletion && bdat;
            return result;
        }
        catch
        {
            // Mid-data failures (source read error, cancellation) leave the stream out of step.
            _broken = true;
            throw;
        }
    }

    private static async ValueTask WriteBody(bool sync, SmtpMessage message, MimeEntity? entity, Stream sink, CancellationToken ct)
    {
        if (entity is not null)
        {
            if (sync) { entity.WriteTo(sink); } else { await entity.WriteToAsync(sink, ct).ConfigureAwait(false); }
        }
        else if (message.Stream is { } source)
        {
            long remaining = message.StreamLength ?? long.MaxValue;
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, remaining);
                    int n = sync ? source.Read(buffer, 0, want) : await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    if (sync) { sink.Write(buffer, 0, n); } else { await sink.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false); }
                    remaining -= n;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else if (sync)
        {
            sink.Write(message.Bytes.Span);
        }
        else
        {
            await sink.WriteAsync(message.Bytes, ct).ConfigureAwait(false);
        }
    }

    private sealed class CountingStream : Stream
    {
        public long Count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Count += buffer.Length; return ValueTask.CompletedTask; }
    }

    // ---- closing ----

    /// <summary>Closes the connection without saying QUIT (use <see cref="QuitAsync"/> for a clean end).</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Raw.Dispose();
        }
    }

    /// <summary>Closes the connection without saying QUIT.</summary>
    /// <returns>A task that completes when closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await Raw.DisposeAsync().ConfigureAwait(false);
        }
    }
}
