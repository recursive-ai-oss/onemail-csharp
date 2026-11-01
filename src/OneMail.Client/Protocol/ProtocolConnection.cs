using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>
/// A connection to a mail server: the socket (or any stream), the TLS and compression layers on top of it, and the buffered
/// <see cref="Reader"/> and <see cref="Writer"/> every protocol uses. It knows nothing about IMAP, SMTP or POP3.
/// </summary>
/// <remarks>
/// <para>Every I/O method has an asynchronous form taking a <see cref="CancellationToken"/> and a synchronous twin. Cancellation
/// or a timeout leaves the connection in an undefined state; dispose it.</para>
/// <para>STARTTLS and COMPRESS=DEFLATE are layered after the protocol's own exchange succeeded: the caller sends the command, reads
/// the tagged OK, then calls <see cref="StartTlsAsync"/> or <see cref="EnableCompressionAsync"/>. Both refuse to switch if the reader already holds bytes the server sent after
/// the acknowledgement, because those bytes belong to the old layer (for TLS this is the STARTTLS plaintext-injection attack).</para>
/// </remarks>
public sealed class ProtocolConnection : IAsyncDisposable, IDisposable
{
    private readonly Socket? _socket;
    private readonly string _host;
    private Stream _stream;
    private bool _disposed;

    private ProtocolConnection(Stream stream, Socket? socket, string host, ProtocolConnectionOptions options)
    {
        _stream = stream;
        _socket = socket;
        _host = host;
        Options = options;
        ReadTimeout = options.ReadTimeout;
        WriteTimeout = options.WriteTimeout;
        Reader = new ProtocolReader(this);
        Writer = new ProtocolWriter(this);
    }

    /// <summary>The options the connection was made with.</summary>
    public ProtocolConnectionOptions Options { get; }

    /// <summary>The buffered reader over the current stream.</summary>
    public ProtocolReader Reader { get; }

    /// <summary>The buffered writer over the current stream.</summary>
    public ProtocolWriter Writer { get; }

    /// <summary>The current bottom of the stack: the socket, or TLS over it, or compression over that.</summary>
    public Stream Stream => _stream;

    /// <summary>Whether TLS is active.</summary>
    public bool IsEncrypted => _tls is not null;

    /// <summary>Whether COMPRESS=DEFLATE is active.</summary>
    public bool IsCompressed { get; private set; }

    /// <summary>Time one read may take before <see cref="TimeoutException"/>. Initially from the options; settable at any time.</summary>
    public TimeSpan ReadTimeout { get; set; }

    /// <summary>Time one write may take before <see cref="TimeoutException"/>.</summary>
    public TimeSpan WriteTimeout { get; set; }

    private SslStream? _tls;

    /// <summary>The negotiated TLS version, or null without TLS.</summary>
    public SslProtocols? TlsProtocol => _tls?.SslProtocol;

    /// <summary>The ALPN protocol the handshake chose, or default when none.</summary>
    public SslApplicationProtocol NegotiatedApplicationProtocol => _tls?.NegotiatedApplicationProtocol ?? default;

    /// <summary>The server certificate, or null without TLS.</summary>
    public X509Certificate? RemoteCertificate => _tls?.RemoteCertificate;

    /// <summary>Connects to a server (TCP, then TLS when <see cref="ProtocolConnectionOptions.ImplicitTls"/>).</summary>
    /// <param name="host">Host name or address.</param>
    /// <param name="port">TCP port.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the connect and handshake.</param>
    /// <returns>The open connection.</returns>
    /// <exception cref="SocketException">The connect failed.</exception>
    /// <exception cref="TimeoutException">The connect timed out.</exception>
    /// <exception cref="AuthenticationException">The TLS handshake failed.</exception>
    public static async ValueTask<ProtocolConnection> ConnectAsync(string host, int port, ProtocolConnectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new ProtocolConnectionOptions();
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options.ConnectTimeout != Timeout.InfiniteTimeSpan)
            {
                cts.CancelAfter(options.ConnectTimeout);
            }

            try
            {
                await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Connecting to " + host + ":" + port + " timed out.");
            }

            var conn = new ProtocolConnection(new NetworkStream(socket, ownsSocket: false), socket, host, options);
            if (options.ImplicitTls)
            {
                await conn.StartTlsAsync(cancellationToken).ConfigureAwait(false);
            }

            return conn;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Synchronous twin of <see cref="ConnectAsync"/>.</summary>
    /// <param name="host">Host name or address.</param>
    /// <param name="port">TCP port.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The open connection.</returns>
    public static ProtocolConnection Connect(string host, int port, ProtocolConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new ProtocolConnectionOptions();
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Socket.Connect has no timeout; a bounded async connect, waited on here, gives it one.
            using var cts = new CancellationTokenSource();
            if (options.ConnectTimeout != Timeout.InfiniteTimeSpan)
            {
                cts.CancelAfter(options.ConnectTimeout);
            }

            try
            {
                socket.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Connecting to " + host + ":" + port + " timed out.");
            }

            var conn = new ProtocolConnection(new NetworkStream(socket, ownsSocket: false), socket, host, options);
            if (options.ImplicitTls)
            {
                conn.StartTls();
            }

            return conn;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Wraps a stream the caller already has (a test double, a tunnel, a socket made elsewhere). Disposing the connection disposes
    /// the stream.
    /// </summary>
    /// <param name="stream">The transport.</param>
    /// <param name="host">The host name used for TLS when it is started later.</param>
    /// <param name="options">Options, or null for the defaults. <see cref="ProtocolConnectionOptions.ImplicitTls"/> is not applied; call <see cref="StartTls"/>.</param>
    /// <returns>The connection.</returns>
    public static ProtocolConnection FromStream(Stream stream, string host = "localhost", ProtocolConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ProtocolConnection(stream, null, host, options ?? new ProtocolConnectionOptions());
    }

    private SslClientAuthenticationOptions BuildTlsOptions()
    {
        var o = new SslClientAuthenticationOptions
        {
            TargetHost = Options.TlsTargetHost ?? _host,
            RemoteCertificateValidationCallback = Options.ServerCertificateValidationCallback,
            ClientCertificates = Options.ClientCertificates,
            ApplicationProtocols = Options.ApplicationProtocols is { } alpn ? [.. alpn] : null,
            EnabledSslProtocols = Options.EnabledSslProtocols,
        };
        Options.ConfigureTls?.Invoke(o);
        return o;
    }

    private void CheckLayerSwitch(string what)
    {
        if (Reader.Buffered != 0)
        {
            throw new MailProtocolViolationException("The server sent data after the " + what + " acknowledgement and before the switch; refusing to continue.");
        }

        if (Writer.Pending != 0)
        {
            throw new InvalidOperationException("Flush the writer before switching to " + what + ".");
        }
    }

    /// <summary>Runs the TLS handshake over the current stream (after STARTTLS/STLS was acknowledged, or at once for implicit TLS).</summary>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <exception cref="MailProtocolViolationException">The reader held unread bytes (see the class remarks).</exception>
    /// <exception cref="InvalidOperationException">TLS is already active.</exception>
    /// <exception cref="AuthenticationException">The handshake or certificate validation failed.</exception>
    public async ValueTask StartTlsAsync(CancellationToken cancellationToken = default)
    {
        PrepareTls();
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        using var cts = TimeoutSource(ReadTimeout, cancellationToken);
        try
        {
            await ssl.AuthenticateAsClientAsync(BuildTlsOptions(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ssl.Dispose();
            throw new TimeoutException("The TLS handshake timed out.");
        }

        _tls = ssl;
        _stream = ssl;
    }

    /// <summary>Synchronous twin of <see cref="StartTlsAsync"/>.</summary>
    public void StartTls()
    {
        PrepareTls();
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        SetSocketTimeouts();
        ssl.AuthenticateAsClient(BuildTlsOptions());
        _tls = ssl;
        _stream = ssl;
    }

    private void PrepareTls()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tls is not null)
        {
            throw new InvalidOperationException("TLS is already active.");
        }

        CheckLayerSwitch("STARTTLS");
    }

    /// <summary>
    /// Switches both directions to raw DEFLATE (RFC 4978 §3) after the server acknowledged COMPRESS=DEFLATE. Each flush is a
    /// sync flush, so the server can read a command as soon as it is sent.
    /// </summary>
    /// <param name="cancellationToken">Not used; present for symmetry.</param>
    /// <exception cref="MailProtocolViolationException">The reader held unread bytes.</exception>
    /// <exception cref="InvalidOperationException">Compression is already active.</exception>
    public ValueTask EnableCompressionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnableCompression();
        return ValueTask.CompletedTask;
    }

    /// <summary>Synchronous twin of <see cref="EnableCompressionAsync"/>.</summary>
    public void EnableCompression()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCompressed)
        {
            throw new InvalidOperationException("Compression is already active.");
        }

        CheckLayerSwitch("COMPRESS");
        _stream = new DeflateDuplexStream(_stream);
        IsCompressed = true;
    }

    internal static CancellationTokenSource TimeoutSource(TimeSpan timeout, CancellationToken token)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout != Timeout.InfiniteTimeSpan && timeout > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout);
        }

        return cts;
    }

    internal void SetSocketTimeouts()
    {
        if (_socket is null)
        {
            return;
        }

        _socket.ReceiveTimeout = ToMs(ReadTimeout);
        _socket.SendTimeout = ToMs(WriteTimeout);
    }

    private static int ToMs(TimeSpan t) => t == Timeout.InfiniteTimeSpan || t <= TimeSpan.Zero ? 0 : (int)Math.Min(int.MaxValue, t.TotalMilliseconds);

    /// <summary>Closes the connection and returns the buffers to the pool.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reader.Dispose();
        Writer.Dispose();
        try
        {
            _stream.Dispose();
        }
        finally
        {
            _socket?.Dispose();
        }
    }

    /// <summary>Asynchronous twin of <see cref="Dispose"/>.</summary>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reader.Dispose();
        Writer.Dispose();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _socket?.Dispose();
        }
    }

    /// <summary>One stream that reads through an inflater and writes through a deflater over the same transport.</summary>
    private sealed class DeflateDuplexStream : Stream
    {
        private readonly Stream _inner;
        private readonly DeflateStream _in;
        private readonly DeflateStream _out;

        public DeflateDuplexStream(Stream inner)
        {
            _inner = inner;
            _in = new DeflateStream(inner, CompressionMode.Decompress, leaveOpen: true);
            _out = new DeflateStream(inner, CompressionLevel.Fastest, leaveOpen: true);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { _out.Flush(); _inner.Flush(); }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _out.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _in.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _in.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _out.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _out.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _in.Dispose();
                    _out.Dispose();
                }
                catch (IOException)
                {
                    // The transport may already be gone; closing it below is what matters.
                }

                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
