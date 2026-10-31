using System.Buffers;
using System.Text;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Mbox;

/// <summary>Options for <see cref="MboxWriter"/>.</summary>
public sealed class MboxWriterOptions
{
    /// <summary>The variant to write. <see cref="MboxVariant.Unknown"/> (the default) writes <see cref="MboxVariant.Mboxrd"/>.</summary>
    public MboxVariant Variant { get; init; } = MboxVariant.Mboxrd;

    /// <summary>Whether envelope lines and separators end in CRLF instead of LF. Default false. Message bytes are never converted.</summary>
    public bool Crlf { get; init; }

    /// <summary>The sender written in envelope lines when the call gives none. Default <c>MAILER-DAEMON</c>.</summary>
    public string DefaultSender { get; init; } = "MAILER-DAEMON";

    /// <summary>
    /// The clock for envelope dates when the call gives no date. Default <see cref="TimeProvider.System"/>. Pass a fixed
    /// date to every call (or a fake provider) for reproducible output.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// True when the output stream already holds an mbox file and the writer appends to it. The stream must be readable,
    /// seekable and positioned anywhere: the writer looks at the last bytes, moves to the end and, if the file does not
    /// end with an empty line, adds the missing line ending and separator before the first message.
    /// </summary>
    public bool AppendToExisting { get; init; }
}

/// <summary>
/// Writes mbox files in any <see cref="MboxVariant"/>, quoting body lines as the variant requires and, for
/// <see cref="MboxVariant.Mboxcl"/> and <see cref="MboxVariant.Mboxcl2"/>, writing a <c>Content-Length</c> header.
/// </summary>
/// <remarks>
/// <para>
/// Each message is written as an envelope line, the message, a line ending if the message lacks one, and an empty line.
/// The file is therefore always a complete mbox between calls. Message bytes pass through unchanged except for quoting and,
/// for the <c>Content-Length</c> variants, the replaced <c>Content-Length</c> header (an existing one is removed first).
/// </para>
/// <para>
/// <see cref="AppendRecord"/> and <see cref="WritePreamble"/> copy bytes of an existing file verbatim: copying every entry
/// of a file reproduces it byte for byte.
/// </para>
/// The writer does not own the stream and is not thread-safe.
/// </remarks>
public sealed class MboxWriter
{
    private const int Chunk = 16384;
    private const int MaxHeaderBlock = 1 << 20;
    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private readonly Stream _out;
    private readonly MboxWriterOptions _options;
    private readonly byte[] _eol;
    private readonly MboxVariant _variant;
    private readonly byte[] _in = new byte[Chunk];
    private readonly byte[] _ob = new byte[MboxQuoter.MaxOutput(Chunk)];
    private byte _t1;
    private byte _t2;
    private byte _t3;
    private int _tailCount;

    /// <summary>Creates a writer over a stream.</summary>
    /// <param name="output">The stream to write to. Not disposed by the writer.</param>
    /// <param name="options">Options; null means the defaults.</param>
    /// <exception cref="ArgumentException"><see cref="MboxWriterOptions.AppendToExisting"/> is set and the stream is not readable and seekable.</exception>
    public MboxWriter(Stream output, MboxWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _out = output;
        _options = options ?? new MboxWriterOptions();
        _eol = _options.Crlf ? "\r\n"u8.ToArray() : "\n"u8.ToArray();
        _variant = _options.Variant == MboxVariant.Unknown ? MboxVariant.Mboxrd : _options.Variant;
        if (_options.AppendToExisting)
        {
            if (!output.CanRead || !output.CanSeek)
            {
                throw new ArgumentException("Appending needs a readable, seekable stream.", nameof(output));
            }

            long len = output.Length;
            int n = (int)Math.Min(3, len);
            if (n > 0)
            {
                output.Seek(len - n, SeekOrigin.Begin);
                Span<byte> t = stackalloc byte[3];
                output.ReadExactly(t[..n]);
                Track(t[..n]);
            }

            output.Seek(0, SeekOrigin.End);
        }
    }

    /// <summary>The variant being written.</summary>
    public MboxVariant Variant => _variant;

    /// <summary>Appends a message held in memory.</summary>
    /// <param name="message">The message bytes, headers first.</param>
    /// <param name="sender">The envelope sender; null means <see cref="MboxWriterOptions.DefaultSender"/>. Spaces become hyphens.</param>
    /// <param name="date">The envelope date; null means the options' clock.</param>
    /// <exception cref="ArgumentException">The sender contains a line break, or the header block of a <c>Content-Length</c> variant exceeds 1 MiB.</exception>
    public void Append(ReadOnlyMemory<byte> message, string? sender = null, DateTimeOffset? date = null) =>
        SyncWait.Complete(AppendCore<SyncMode>(new MemorySource(message), 0, message.Length, sender, date, default));

    /// <summary>Appends a message read from a range of a source.</summary>
    /// <param name="source">Where the message bytes are.</param>
    /// <param name="offset">First byte of the message.</param>
    /// <param name="length">Length of the message.</param>
    /// <param name="sender">The envelope sender; null means the default.</param>
    /// <param name="date">The envelope date; null means the options' clock.</param>
    public void Append(IRandomAccessSource source, long offset, long length, string? sender = null, DateTimeOffset? date = null) =>
        SyncWait.Complete(AppendCore<SyncMode>(source, offset, length, sender, date, default));

    /// <summary>Asynchronous twin of <see cref="Append(IRandomAccessSource, long, long, string?, DateTimeOffset?)"/>.</summary>
    /// <param name="source">Where the message bytes are.</param>
    /// <param name="offset">First byte of the message.</param>
    /// <param name="length">Length of the message.</param>
    /// <param name="sender">The envelope sender; null means the default.</param>
    /// <param name="date">The envelope date; null means the options' clock.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the message is written.</returns>
    public ValueTask AppendAsync(IRandomAccessSource source, long offset, long length, string? sender = null, DateTimeOffset? date = null, CancellationToken cancellationToken = default) =>
        AppendCore<AsyncMode>(source, offset, length, sender, date, cancellationToken);

    /// <summary>Copies the bytes before the first message of another file, verbatim. Call before any message.</summary>
    /// <param name="source">The file the preamble comes from.</param>
    /// <param name="length">The reader's <see cref="MboxReader.PreambleLength"/>.</param>
    public void WritePreamble(IRandomAccessSource source, long length) =>
        SyncWait.Complete(CopyRaw<SyncMode>(source, 0, length, default));

    /// <summary>Copies a whole record (envelope line, message and separator) of another file, verbatim.</summary>
    /// <param name="source">The file the entry came from.</param>
    /// <param name="entry">The entry.</param>
    public void AppendRecord(IRandomAccessSource source, in MboxEntry entry) =>
        SyncWait.Complete(CopyRaw<SyncMode>(source, entry.Offset, entry.RecordLength, default));

    /// <summary>Asynchronous twin of <see cref="AppendRecord"/>.</summary>
    /// <param name="source">The file the entry came from.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A task that completes when the record is written.</returns>
    public ValueTask AppendRecordAsync(IRandomAccessSource source, MboxEntry entry, CancellationToken cancellationToken = default) =>
        CopyRaw<AsyncMode>(source, entry.Offset, entry.RecordLength, cancellationToken);

    private interface IMode
    {
        static abstract bool IsAsync { get; }
    }

    private readonly struct SyncMode : IMode
    {
        public static bool IsAsync => false;
    }

    private readonly struct AsyncMode : IMode
    {
        public static bool IsAsync => true;
    }

    private void Track(ReadOnlySpan<byte> written)
    {
        foreach (byte b in written.Length > 3 ? written[^3..] : written)
        {
            _t1 = _t2;
            _t2 = _t3;
            _t3 = b;
            if (_tailCount < 3)
            {
                _tailCount++;
            }
        }
    }

    private async ValueTask Emit<TMode>(ReadOnlyMemory<byte> data, CancellationToken ct)
        where TMode : struct, IMode
    {
        if (data.Length == 0)
        {
            return;
        }

        if (TMode.IsAsync)
        {
            await _out.WriteAsync(data, ct).ConfigureAwait(false);
        }
        else
        {
            _out.Write(data.Span);
        }

        Track(data.Span);
    }

    private async ValueTask CopyRaw<TMode>(IRandomAccessSource source, long offset, long length, CancellationToken ct)
        where TMode : struct, IMode
    {
        ArgumentNullException.ThrowIfNull(source);
        long done = 0;
        while (done < length)
        {
            int n = (int)Math.Min(Chunk, length - done);
            int got = TMode.IsAsync
                ? await source.ReadFullyAsync(offset + done, _in.AsMemory(0, n), ct).ConfigureAwait(false)
                : source.ReadFully(offset + done, _in.AsSpan(0, n));
            if (got == 0)
            {
                throw new EndOfStreamException("The source ended before the range did.");
            }

            await Emit<TMode>(_in.AsMemory(0, got), ct).ConfigureAwait(false);
            done += got;
        }
    }

    // Brings the output to the start of a line after an empty line, so the next envelope line is a valid delimiter.
    private async ValueTask EnsureSeparator<TMode>(CancellationToken ct)
        where TMode : struct, IMode
    {
        if (_tailCount == 0)
        {
            return;
        }

        if (_t3 != (byte)'\n')
        {
            await Emit<TMode>(_eol, ct).ConfigureAwait(false);
        }

        bool blank = _tailCount >= 2 && _t2 == (byte)'\n' && _t3 == (byte)'\n'
            || _tailCount >= 3 && _t1 == (byte)'\n' && _t2 == (byte)'\r' && _t3 == (byte)'\n';
        if (!blank)
        {
            await Emit<TMode>(_eol, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask AppendCore<TMode>(IRandomAccessSource source, long offset, long length, string? sender, DateTimeOffset? date, CancellationToken ct)
        where TMode : struct, IMode
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        byte[] envelope = BuildEnvelope(sender ?? _options.DefaultSender, date ?? _options.TimeProvider.GetUtcNow());

        await EnsureSeparator<TMode>(ct).ConfigureAwait(false);
        await Emit<TMode>(envelope, ct).ConfigureAwait(false);

        if (_variant is MboxVariant.Mboxcl or MboxVariant.Mboxcl2)
        {
            await AppendWithContentLength<TMode>(source, offset, length, ct).ConfigureAwait(false);
        }
        else
        {
            MboxQuoter q = new(quote: true, reversible: _variant == MboxVariant.Mboxrd);
            await PumpBody<TMode>(source, offset, length, q, ct).ConfigureAwait(false);
        }

        if (_t3 != (byte)'\n')
        {
            await Emit<TMode>(_eol, ct).ConfigureAwait(false);
        }

        await Emit<TMode>(_eol, ct).ConfigureAwait(false);
    }

    // Streams a range through the quoter to the output; returns the number of bytes written.
    private async ValueTask<long> PumpBody<TMode>(IRandomAccessSource source, long offset, long length, MboxQuoter q, CancellationToken ct)
        where TMode : struct, IMode
    {
        long written = 0;
        long done = 0;
        while (done < length)
        {
            int n = (int)Math.Min(Chunk, length - done);
            int got = TMode.IsAsync
                ? await source.ReadFullyAsync(offset + done, _in.AsMemory(0, n), ct).ConfigureAwait(false)
                : source.ReadFully(offset + done, _in.AsSpan(0, n));
            if (got == 0)
            {
                throw new EndOfStreamException("The source ended before the range did.");
            }

            int o = q.Process(_in.AsSpan(0, got), _ob);
            await Emit<TMode>(_ob.AsMemory(0, o), ct).ConfigureAwait(false);
            written += o;
            done += got;
        }

        int tail = q.Finish(_ob);
        await Emit<TMode>(_ob.AsMemory(0, tail), ct).ConfigureAwait(false);
        return written + tail;
    }

    private async ValueTask AppendWithContentLength<TMode>(IRandomAccessSource source, long offset, long length, CancellationToken ct)
        where TMode : struct, IMode
    {
        bool quote = _variant == MboxVariant.Mboxcl;

        // Read the header block: through the first empty line.
        int headScan = (int)Math.Min(length, MaxHeaderBlock + 1);
        byte[] head = new byte[headScan];
        int got = TMode.IsAsync
            ? await source.ReadFullyAsync(offset, head, ct).ConfigureAwait(false)
            : source.ReadFully(offset, head);
        int headerEnd = FindHeaderEnd(head.AsSpan(0, got), out int blankLen);
        if (headerEnd < 0 && length > MaxHeaderBlock)
        {
            throw new ArgumentException("The header block is larger than 1 MiB.", nameof(length));
        }

        bool noSeparator = headerEnd < 0;
        int headerBytes = noSeparator ? got : headerEnd;       // header lines, each with its line ending
        long bodyOffset = offset + (noSeparator ? got : headerEnd + blankLen);
        long bodyLength = length - (bodyOffset - offset);

        // Header lines without any existing Content-Length (and its continuation lines).
        using MemoryStream hs = new();
        RemoveContentLength(head.AsSpan(0, headerBytes), hs);
        byte[] kept = hs.ToArray();
        byte[] eol = kept.Length > 1 && kept[^2] == (byte)'\r' ? "\r\n"u8.ToArray() : _eol;

        // Count the stored body bytes: after quoting, plus a line ending if the body does not end with one.
        long stored = 0;
        byte last = (byte)'\n';
        if (bodyLength > 0)
        {
            if (quote)
            {
                MboxQuoter q = new(quote: true, reversible: false);
                long done = 0;
                while (done < bodyLength)
                {
                    int n = (int)Math.Min(Chunk, bodyLength - done);
                    int g = TMode.IsAsync
                        ? await source.ReadFullyAsync(bodyOffset + done, _in.AsMemory(0, n), ct).ConfigureAwait(false)
                        : source.ReadFully(bodyOffset + done, _in.AsSpan(0, n));
                    if (g == 0)
                    {
                        throw new EndOfStreamException("The source ended before the range did.");
                    }

                    int o = q.Process(_in.AsSpan(0, g), _ob);
                    if (o > 0)
                    {
                        last = _ob[o - 1];
                    }

                    stored += o;
                    done += g;
                }

                int tail = q.Finish(_ob);
                if (tail > 0)
                {
                    last = _ob[tail - 1];
                }

                stored += tail;
            }
            else
            {
                stored = bodyLength;
                byte[] one = new byte[1];
                int g = TMode.IsAsync
                    ? await source.ReadFullyAsync(bodyOffset + bodyLength - 1, one, ct).ConfigureAwait(false)
                    : source.ReadFully(bodyOffset + bodyLength - 1, one);
                last = g == 1 ? one[0] : (byte)'\n';
            }
        }

        bool addEol = bodyLength > 0 && last != (byte)'\n';
        if (addEol)
        {
            stored += eol.Length;
        }

        // Headers, the new Content-Length, the blank line, the body.
        if (kept.Length > 0 && kept[^1] != (byte)'\n')
        {
            await Emit<TMode>(kept, ct).ConfigureAwait(false);
            await Emit<TMode>(eol, ct).ConfigureAwait(false);
        }
        else
        {
            await Emit<TMode>(kept, ct).ConfigureAwait(false);
        }

        await Emit<TMode>(Encoding.ASCII.GetBytes("Content-Length: " + stored.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
        await Emit<TMode>(eol, ct).ConfigureAwait(false);
        await Emit<TMode>(eol, ct).ConfigureAwait(false);

        if (bodyLength > 0)
        {
            MboxQuoter q = new(quote: quote, reversible: false);
            if (quote)
            {
                await PumpBody<TMode>(source, bodyOffset, bodyLength, q, ct).ConfigureAwait(false);
            }
            else
            {
                await CopyRaw<TMode>(source, bodyOffset, bodyLength, ct).ConfigureAwait(false);
            }

            if (addEol)
            {
                await Emit<TMode>(eol, ct).ConfigureAwait(false);
            }
        }
    }

    // Offset just past the last header line, and the length of the empty line that ends the block; -1 when none.
    private static int FindHeaderEnd(ReadOnlySpan<byte> m, out int blankLen)
    {
        blankLen = 0;
        int pos = 0;
        while (pos < m.Length)
        {
            if (m[pos] == (byte)'\n')
            {
                blankLen = 1;
                return pos;
            }

            if (m[pos] == (byte)'\r' && pos + 1 < m.Length && m[pos + 1] == (byte)'\n')
            {
                blankLen = 2;
                return pos;
            }

            int nl = m[pos..].IndexOf((byte)'\n');
            if (nl < 0)
            {
                return -1;
            }

            pos += nl + 1;
        }

        return -1;
    }

    private static void RemoveContentLength(ReadOnlySpan<byte> headers, Stream dest)
    {
        int pos = 0;
        bool skipping = false;
        while (pos < headers.Length)
        {
            int nl = headers[pos..].IndexOf((byte)'\n');
            int len = nl < 0 ? headers.Length - pos : nl + 1;
            ReadOnlySpan<byte> line = headers.Slice(pos, len);
            bool continuation = line[0] == (byte)' ' || line[0] == (byte)'\t';
            if (!continuation)
            {
                skipping = IsContentLength(line);
            }

            if (!skipping)
            {
                dest.Write(line);
            }

            pos += len;
        }
    }

    private static bool IsContentLength(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> name = "content-length:"u8;
        if (line.Length < name.Length)
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            byte c = line[i];
            if (c is >= (byte)'A' and <= (byte)'Z')
            {
                c += 32;
            }

            if (c != name[i])
            {
                return false;
            }
        }

        return true;
    }

    private byte[] BuildEnvelope(string sender, DateTimeOffset date)
    {
        if (sender.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("The envelope sender contains a line break.", nameof(sender));
        }

        DateTimeOffset u = date.ToUniversalTime();
        string line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"From {sender.Replace(' ', '-')} {Days[(int)u.DayOfWeek]} {Months[u.Month - 1]} {u.Day,2} {u.Hour:D2}:{u.Minute:D2}:{u.Second:D2} {u.Year:D4}");
        byte[] text = Encoding.UTF8.GetBytes(line);
        byte[] result = new byte[text.Length + _eol.Length];
        text.CopyTo(result, 0);
        _eol.CopyTo(result, text.Length);
        return result;
    }
}
