using System.Buffers;
using System.Text;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Mime.Writing;

/// <summary>Options for <see cref="MimeWriter"/>.</summary>
public sealed class MimeWriterOptions
{
    /// <summary>Line end for lines the writer generates (headers, delimiters). Default CRLF. Bytes passed in are never changed.</summary>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>Column at which <see cref="MimeWriter.WriteHeader(string, string)"/> folds long values (RFC 5322 §2.1.1: 78). Default 78.</summary>
    public int FoldWidth { get; init; } = 78;

    /// <summary>Size of the write buffer in bytes. Default 16,384.</summary>
    public int BufferSize { get; init; } = 16 * 1024;
}

/// <summary>
/// A streaming MIME writer that mirrors <see cref="MimeReader"/>: the reader's events can be copied through it
/// (<see cref="WriteEvent"/>) to reproduce the input byte for byte, and it can also produce new headers, delimiters and
/// bodies.
/// </summary>
/// <remarks>
/// Output is buffered. The synchronous methods write to the stream when the buffer fills; asynchronous code uses the
/// <c>Async</c> methods and <see cref="FlushAsync"/>. The writer checks nothing about the order of calls: it writes the
/// bytes it is told to write, with the structure helpers generating correct delimiters and header folding.
/// </remarks>
public sealed class MimeWriter : IDisposable
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly byte[] _newline;
    private readonly int _foldWidth;
    private byte[]? _buf;
    private int _len;

    /// <summary>Creates a writer over <paramref name="output"/>.</summary>
    /// <param name="output">A writable stream.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="leaveOpen">Whether disposing the writer leaves the stream open. Default true.</param>
    public MimeWriter(Stream output, MimeWriterOptions? options = null, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= new MimeWriterOptions();
        _output = output;
        _leaveOpen = leaveOpen;
        _newline = Encoding.ASCII.GetBytes(options.NewLine);
        _foldWidth = Math.Max(20, options.FoldWidth);
        _buf = ArrayPool<byte>.Shared.Rent(Math.Max(1024, options.BufferSize));
    }

    /// <summary>The number of bytes written so far, buffered or not.</summary>
    public long Position { get; private set; }

    private byte[] Buffer => _buf ?? throw new ObjectDisposedException(nameof(MimeWriter));

    /// <summary>Writes bytes exactly as given.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        byte[] buf = Buffer;
        Position += bytes.Length;
        if (bytes.Length > buf.Length - _len)
        {
            Flush();
            if (bytes.Length >= buf.Length)
            {
                _output.Write(bytes);
                return;
            }
        }

        bytes.CopyTo(buf.AsSpan(_len));
        _len += bytes.Length;
    }

    /// <summary>Asynchronous twin of <see cref="WriteRaw"/>.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are buffered or written.</returns>
    public async ValueTask WriteRawAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        byte[] buf = Buffer;
        Position += bytes.Length;
        if (bytes.Length > buf.Length - _len)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length >= buf.Length)
            {
                await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        bytes.Span.CopyTo(buf.AsSpan(_len));
        _len += bytes.Length;
    }

    /// <summary>Copies whatever bytes the reader's current event covers (headers, bodies, delimiters, separators).</summary>
    /// <param name="reader">A reader positioned on an event.</param>
    public void WriteEvent(MimeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.Length > 0)
        {
            WriteRaw(reader.Raw);
        }
    }

    /// <summary>Copies a range of a source, reading it lazily in pieces.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">First byte.</param>
    /// <param name="length">Number of bytes; clamped to the source.</param>
    public void CopyFrom(IRandomAccessSource source, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        Flush();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            length = Math.Min(length, source.Length - offset);
            while (length > 0)
            {
                int n = source.Read(offset, chunk.AsSpan(0, (int)Math.Min(chunk.Length, length)));
                if (n <= 0)
                {
                    throw new EndOfStreamException("The source ended before the requested range.");
                }

                _output.Write(chunk, 0, n);
                offset += n;
                length -= n;
                Position += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Asynchronous twin of <see cref="CopyFrom"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">First byte.</param>
    /// <param name="length">Number of bytes; clamped to the source.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A task that completes when the range is written.</returns>
    public async ValueTask CopyFromAsync(IRandomAccessSource source, long offset, long length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            length = Math.Min(length, source.Length - offset);
            while (length > 0)
            {
                int n = await source.ReadAsync(offset, chunk.AsMemory(0, (int)Math.Min(chunk.Length, length)), cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    throw new EndOfStreamException("The source ended before the requested range.");
                }

                await _output.WriteAsync(chunk.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                offset += n;
                length -= n;
                Position += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// Writes one header field: <c>Name: value</c>, folded at word boundaries before <see cref="MimeWriterOptions.FoldWidth"/>
    /// columns, ended by the writer's line end. Non-ASCII text in free-text fields (Subject, Comments, Keywords,
    /// Content-Description and unknown fields) is written as RFC 2047 encoded words; the fields with their own syntax (addresses,
    /// identifiers, dates, Content-*) keep it as UTF-8 (RFC 6532), and parameters are built with <see cref="MimeHeaderValue.Parameter"/>.
    /// </summary>
    /// <param name="name">The field name: printable ASCII without a colon.</param>
    /// <param name="value">The value as one logical line. It must not contain a line break.</param>
    /// <exception cref="ArgumentException">The name is not a valid field name, or the value contains CR or LF (which would let a value inject headers).</exception>
    public void WriteHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        WriteRaw(MimeWriterSupport.FormatHeader(name, value, _newline, _foldWidth));
    }

    /// <summary>Writes the empty line that ends a header block.</summary>
    public void WriteHeadersEnd() => WriteRaw(_newline);

    /// <summary>
    /// Writes a multipart delimiter line (RFC 2046 §5.1.1): the line end that belongs to the delimiter, <c>--boundary</c>,
    /// <c>--</c> if it closes the multipart, and the line end.
    /// </summary>
    /// <param name="boundary">The boundary, without dashes.</param>
    /// <param name="close">True for the closing delimiter.</param>
    /// <param name="leadingNewline">False for the first delimiter when there is no preamble.</param>
    public void WriteDelimiter(ReadOnlySpan<byte> boundary, bool close, bool leadingNewline = true)
    {
        WriteRaw(MimeWriterSupport.FormatDelimiter(boundary, close, leadingNewline, _newline));
    }

    /// <summary>Writes buffered bytes to the stream and flushes it.</summary>
    public void Flush()
    {
        if (_len > 0)
        {
            _output.Write(Buffer, 0, _len);
            _len = 0;
        }

        _output.Flush();
    }

    /// <summary>Asynchronous twin of <see cref="Flush"/>.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the stream has the bytes.</returns>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_len > 0)
        {
            int n = _len;
            _len = 0;
            await _output.WriteAsync(Buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
        }

        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Flushes, returns the buffer, and closes the stream unless it was left open.</summary>
    public void Dispose()
    {
        if (_buf is null)
        {
            return;
        }

        Flush();
        ArrayPool<byte>.Shared.Return(_buf);
        _buf = null;
        if (!_leaveOpen)
        {
            _output.Dispose();
        }
    }
}

/// <summary>Byte formatting shared by the writer and the entity tree.</summary>
internal static class MimeWriterSupport
{
    public static byte[] FormatDelimiter(ReadOnlySpan<byte> boundary, bool close, bool leadingNewline, ReadOnlySpan<byte> newline)
    {
        int n = (leadingNewline ? newline.Length : 0) + 2 + boundary.Length + (close ? 2 : 0) + newline.Length;
        byte[] r = new byte[n];
        int o = 0;
        if (leadingNewline)
        {
            newline.CopyTo(r);
            o = newline.Length;
        }

        r[o++] = (byte)'-';
        r[o++] = (byte)'-';
        boundary.CopyTo(r.AsSpan(o));
        o += boundary.Length;
        if (close)
        {
            r[o++] = (byte)'-';
            r[o++] = (byte)'-';
        }

        newline.CopyTo(r.AsSpan(o));
        return r;
    }

    // Headers whose value is not free text keep non-ASCII characters as UTF-8 (RFC 6532): addresses and identifiers have their own
    // syntax, and Content-Type, Content-Disposition and the like carry parameters, which MimeHeaderValue.Parameter encodes.
    private static bool IsStructured(string name) =>
        name.StartsWith("Resent-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && !name.Equals("Content-Description", StringComparison.OrdinalIgnoreCase)
        || name.Equals("From", StringComparison.OrdinalIgnoreCase) || name.Equals("To", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cc", StringComparison.OrdinalIgnoreCase) || name.Equals("Bcc", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Reply-To", StringComparison.OrdinalIgnoreCase) || name.Equals("Sender", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase) || name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase)
        || name.Equals("References", StringComparison.OrdinalIgnoreCase) || name.Equals("Date", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase) || name.Equals("Return-Path", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Received", StringComparison.OrdinalIgnoreCase);

    // RFC 2047 §5(1): free text (Subject, Comments, Keywords, Content-Description, X- fields ...) carries non-ASCII text as encoded words.
    private static string EncodeText(string name, string value) =>
        IsStructured(name) || System.Text.Ascii.IsValid(value) ? value : Text.Rfc2047.Encode(value);

    // RFC 5322 §2.2.3: fold only at whitespace, so the unfolded value is the value given.
    public static byte[] FormatHeader(string name, string value, ReadOnlySpan<byte> newline, int foldWidth)
    {
        if (name.Length == 0)
        {
            throw new ArgumentException("A header name cannot be empty.", nameof(name));
        }

        foreach (char c in name)
        {
            if (c <= ' ' || c > '~' || c == ':')
            {
                throw new ArgumentException("A header name must be printable ASCII without a colon.", nameof(name));
            }
        }

        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("A header value cannot contain a line break; it would start a new header.", nameof(value));
        }

        byte[] v = Encoding.UTF8.GetBytes(EncodeText(name, value.Trim(' ', '\t')));
        var w = new ArrayBufferWriter<byte>(name.Length + v.Length + 16);
        w.Write(Encoding.ASCII.GetBytes(name));
        w.Write(": "u8);
        int col = name.Length + 2;
        int i = 0;
        bool first = true;
        while (i < v.Length)
        {
            int j = i;
            while (j < v.Length && v[j] is not ((byte)' ' or (byte)'\t'))
            {
                j++;
            }

            int wordLen = j - i;
            if (!first)
            {
                if (col + 1 + wordLen > foldWidth)
                {
                    w.Write(newline);
                    w.Write(" "u8);
                    col = 1;
                }
                else
                {
                    w.Write(" "u8);
                    col++;
                }
            }

            w.Write(v.AsSpan(i, wordLen));
            col += wordLen;
            first = false;
            while (j < v.Length && v[j] is (byte)' ' or (byte)'\t')
            {
                j++;
            }

            i = j;
        }

        w.Write(newline);
        return w.WrittenSpan.ToArray();
    }
}
