using System.Buffers;
using System.Text.Unicode;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Mime.Reading;

/// <summary>What a <see cref="MimeReader"/> event is.</summary>
public enum MimeEventKind
{
    /// <summary>No event yet (before the first <see cref="MimeReader.Read"/>) or after the end.</summary>
    None = 0,

    /// <summary>An entity (the message, a body part or an embedded message) begins. Zero length.</summary>
    EntityStart,

    /// <summary>One header field, folded lines included. <see cref="MimeReader.Raw"/> is the whole field with its line end.</summary>
    Header,

    /// <summary>
    /// An mbox <c>From </c> line (<c>From sender date</c>) at the very start of the message. It is not a header field: it is kept
    /// in the raw bytes and the header block starts after it. <see cref="MimeReader.Raw"/> is the line with its line end.
    /// </summary>
    EnvelopeLine,

    /// <summary>
    /// The end of the header block. <see cref="MimeReader.Raw"/> is the empty separator line (empty when the input has no
    /// separator). <see cref="MimeReader.EntityType"/> says what follows.
    /// </summary>
    HeadersEnd,

    /// <summary>A chunk of the text before the first delimiter of a multipart (RFC 2046 §5.1.1).</summary>
    Preamble,

    /// <summary>
    /// A delimiter line of a multipart, including the line end that precedes it (RFC 2046 §5.1.1: that line end belongs to
    /// the delimiter) and its own line end. The kind of delimiter is in <see cref="MimeReader.IsCloseDelimiter"/>.
    /// </summary>
    Delimiter,

    /// <summary>A chunk of a leaf entity's body. Chunks are contiguous; the body is their concatenation.</summary>
    Body,

    /// <summary>A chunk of the text after a multipart's closing delimiter.</summary>
    Epilogue,

    /// <summary>An entity ends. Zero length. <see cref="MimeReader.EntityType"/> says which kind.</summary>
    EntityEnd,

    /// <summary>A repair, guess or refusal; see <see cref="MimeReader.Defect"/>.</summary>
    Defect,
}

/// <summary>What an entity's body is, as the reader decided from its headers.</summary>
public enum MimeEntityType
{
    /// <summary>A body of bytes (text, an attachment, or content the reader does not look into).</summary>
    Leaf = 0,

    /// <summary>A multipart: preamble, delimited child entities, epilogue.</summary>
    Multipart,

    /// <summary>An embedded message (<c>message/rfc822</c> or <c>message/global</c>): the body is one child entity.</summary>
    Message,
}

/// <summary>Options for <see cref="MimeReader"/>.</summary>
public sealed class MimeReaderOptions
{
    /// <summary>Resource limits. Defaults to <see cref="MimeLimits.Default"/>.</summary>
    public MimeLimits Limits { get; init; } = MimeLimits.Default;

    /// <summary>
    /// Whether <c>message/rfc822</c> bodies are read as nested entities. Default true. When false they are leaf bodies.
    /// </summary>
    public bool ParseEmbeddedMessages { get; init; } = true;

    /// <summary>Initial read-buffer size in bytes, 4096 or more. Default 65,536. The buffer grows only to hold one header field.</summary>
    public int BufferSize { get; init; } = 64 * 1024;
}

/// <summary>
/// A forward-only MIME reader: it walks a message as a stream of events (entity start and end, header fields, header end,
/// preamble, delimiters, body chunks, epilogue and defects), each with its byte offset and raw bytes.
/// </summary>
/// <remarks>
/// <para>
/// The events cover the input exactly once, in order: the concatenation of <see cref="Raw"/> over all events is the input
/// (unless <see cref="IsTruncated"/>), malformed input included. That is what makes untouched messages write back
/// byte-identically.
/// </para>
/// <para>
/// The reader allocates a pooled buffer when it is created and nothing per event. <see cref="Raw"/> and the other spans
/// are valid until the next <see cref="Read"/>. Over a non-seekable stream (a socket) it reads only what it needs, in
/// forward order. It never throws on malformed content; see <see cref="MimeDefectKind"/>. A reader can be reused for many
/// messages through the <c>Reset</c> methods.
/// </para>
/// <para>
/// Structure: a multipart is recognised by <c>Content-Type: multipart/*; boundary=...</c> with a usable boundary and a
/// 7bit/8bit/binary encoding; a message by <c>message/rfc822</c> or <c>message/global</c> (and the default type inside
/// <c>multipart/digest</c>). Anything else is a leaf. A delimiter line of any enclosing multipart ends the entities nested
/// inside it (RFC 2046 §5.1.2), reported as <see cref="MimeDefectKind.MissingFinalBoundary"/> for each multipart left open.
/// </para>
/// </remarks>
public sealed class MimeReader : IDisposable
{
    private const int MinBuffer = 4096;
    private const int MaxDelimiterPadding = 1024;

    private enum Phase : byte
    {
        Headers,
        ChildPending,
        Preamble,
        Body,
        Epilogue,
        AwaitDelimiter,
        Ended,
    }

    private enum Step : byte
    {
        Event,
        NeedData,
        Done,
    }

    private enum Classified : byte
    {
        NotDelimiter,
        Found,
        NeedMore,
    }

    private enum BoundaryState : byte
    {
        None,
        Ok,
        Empty,
        TooLong,
    }

    private struct Frame
    {
        public MimeEntityType Type;
        public Phase Phase;
        public bool Closed;
        public bool SawContentType;
        public bool SawEncoding;
        public bool MediaMultipart;
        public bool MediaMessage;
        public bool MediaDigest;
        public bool Digest;
        public BoundaryState BoundaryState;
        public ContentTransferEncoding Encoding;
        public int BoundaryStart;
        public int BoundaryLen;
        public int HeaderBytes;
        public int HeaderCount;
        public bool SawDelimiter;
        public bool BoundaryTrimmed;
        public bool EpilogueReported;
    }

    private readonly MimeLimits _limits;
    private readonly bool _parseMessages;
    private readonly int _bufferSize;
    private readonly int _bufferCap;

    private Frame[] _frames = new Frame[8];
    private int _n;
    private byte[] _bnd = new byte[512];
    private int _bndLen;
    private MimeDefect[] _dq = new MimeDefect[8];
    private int _dqHead;
    private int _dqCount;

    private ReadOnlyMemory<byte> _buf;
    private byte[]? _rented;
    private Stream? _stream;
    private bool _leaveOpen;
    private IRandomAccessSource? _source;
    private long _srcPos;
    private int _start;
    private int _end;
    private long _bufBase;
    private bool _eof;
    private bool _truncated;
    private bool _done;
    private bool _flood;
    private bool _atLineStart;
    private int _entityCount;
    private long _lastAmbiguousBoundary = -1;

    private int _eolStyle; // 0 not seen yet, 1 LF, 2 CRLF: the style of the first line end in the message
    private bool _mixedReported;

    private static readonly System.Buffers.SearchValues<byte> ControlBytes = System.Buffers.SearchValues.Create(
        [1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 127]);

    private bool _pend;
    private int _pendIdx;
    private bool _pendClose;
    private int _pendLen;

    private int _consume;
    private MimeEventKind _kind;
    private int _evStart;
    private int _evLen;
    private int _evDepth;
    private MimeEntityType _evType;
    private int _nameStart;
    private int _nameLen;
    private int _valStart;
    private int _valLen;
    private bool _evClose;
    private MimeDefect _defect;
    private int _evBoundaryStart;
    private int _evBoundaryLen;

    /// <summary>Creates a reader over bytes in memory. The memory is read in place, without copying, and must not change.</summary>
    /// <param name="data">The message.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    public MimeReader(ReadOnlyMemory<byte> data, MimeReaderOptions? options = null)
        : this(options)
    {
        Reset(data);
    }

    /// <summary>Creates a reader over a stream, which is read forward only and may be non-seekable.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="leaveOpen">Whether disposing the reader leaves the stream open. Default true.</param>
    public MimeReader(Stream stream, MimeReaderOptions? options = null, bool leaveOpen = true)
        : this(options)
    {
        Reset(stream, leaveOpen);
    }

    /// <summary>Creates a reader over a random-access source, read from offset 0 forward.</summary>
    /// <param name="source">The source. The reader does not dispose it.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    public MimeReader(IRandomAccessSource source, MimeReaderOptions? options = null)
        : this(options)
    {
        Reset(source);
    }

    private MimeReader(MimeReaderOptions? options)
    {
        options ??= new MimeReaderOptions();
        _limits = options.Limits ?? MimeLimits.Default;
        _parseMessages = options.ParseEmbeddedMessages;
        _bufferSize = Math.Max(MinBuffer, options.BufferSize);
        _bufferCap = (int)Math.Min(int.MaxValue / 2, Math.Max(_bufferSize, (long)_limits.MaxHeaderBlockBytes * 2 + _bufferSize));
    }

    /// <summary>The kind of the current event. <see cref="MimeEventKind.None"/> before the first read and after the last.</summary>
    public MimeEventKind Kind => _kind;

    /// <summary>Absolute offset in the input of the current event's first byte.</summary>
    public long Offset => _bufBase + _evStart;

    /// <summary>Number of input bytes the current event covers (zero for start, end and defect events).</summary>
    public int Length => _evLen;

    /// <summary>The bytes the current event covers. Valid until the next read.</summary>
    public ReadOnlySpan<byte> Raw => _buf.Span.Slice(_evStart, _evLen);

    /// <summary>Nesting depth of the entity the current event belongs to; the message is 0.</summary>
    public int Depth => _evDepth;

    /// <summary>
    /// For <see cref="MimeEventKind.HeadersEnd"/> and <see cref="MimeEventKind.EntityEnd"/>: what the entity's body is.
    /// </summary>
    public MimeEntityType EntityType => _evType;

    /// <summary>For <see cref="MimeEventKind.Header"/>: the field name as written, without surrounding whitespace.</summary>
    public ReadOnlySpan<byte> HeaderName => _buf.Span.Slice(_nameStart, _nameLen);

    /// <summary>
    /// For <see cref="MimeEventKind.Header"/>: the raw value after the colon, up to but not including the field's last line
    /// end. Folded lines and leading whitespace are kept as written.
    /// </summary>
    public ReadOnlySpan<byte> HeaderValue => _buf.Span.Slice(_valStart, _valLen);

    /// <summary>For <see cref="MimeEventKind.Delimiter"/>: whether it is the closing delimiter (<c>--boundary--</c>).</summary>
    public bool IsCloseDelimiter => _evClose;

    /// <summary>For <see cref="MimeEventKind.HeadersEnd"/> of a multipart: the boundary, unquoted, without the leading dashes.</summary>
    public ReadOnlySpan<byte> Boundary => _bnd.AsSpan(_evBoundaryStart, _evBoundaryLen);

    /// <summary>For <see cref="MimeEventKind.Defect"/>: the defect.</summary>
    public MimeDefect Defect => _defect;

    /// <summary>True when input beyond <see cref="MimeLimits.MaxTotalBytes"/> was left unread, so the events do not cover all of it.</summary>
    public bool IsTruncated => _truncated;

    /// <summary>Starts over on new bytes in memory, keeping the reader's buffers.</summary>
    /// <param name="data">The message.</param>
    public void Reset(ReadOnlyMemory<byte> data)
    {
        ResetState();
        _buf = data;
        _end = (int)Math.Min(data.Length, _limits.MaxTotalBytes);
        _truncated = data.Length > _end;
        _eof = true;
        if (_truncated)
        {
            AddDefect(MimeDefectKind.TotalSizeLimitExceeded, _end, 0);
        }
    }

    /// <summary>Starts over on a stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="leaveOpen">Whether disposing the reader leaves the stream open.</param>
    public void Reset(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ResetState();
        _stream = stream;
        _leaveOpen = leaveOpen;
        UseRentedBuffer();
    }

    /// <summary>Starts over on a random-access source, from offset 0.</summary>
    /// <param name="source">The source.</param>
    public void Reset(IRandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ResetState();
        _source = source;
        UseRentedBuffer();
    }

    private void UseRentedBuffer()
    {
        _rented ??= ArrayPool<byte>.Shared.Rent(_bufferSize);
        _buf = _rented;
    }

    private void ResetState()
    {
        if (_stream is not null && !_leaveOpen)
        {
            _stream.Dispose();
        }

        _stream = null;
        _source = null;
        _srcPos = 0;
        _n = 0;
        _bndLen = 0;
        _dqHead = _dqCount = 0;
        _buf = default;
        _start = _end = 0;
        _bufBase = 0;
        _eof = _truncated = _done = _flood = false;
        _atLineStart = false;
        _entityCount = 0;
        _lastAmbiguousBoundary = -1;
        _eolStyle = 0;
        _mixedReported = false;
        _pend = false;
        _consume = 0;
        _kind = MimeEventKind.None;
        _evStart = _evLen = 0;
        _evBoundaryLen = 0;
    }

    /// <summary>Returns the pooled buffer and, unless it was left open, closes the stream.</summary>
    public void Dispose()
    {
        if (_rented is not null)
        {
            ArrayPool<byte>.Shared.Return(_rented);
            _rented = null;
        }

        if (_stream is not null && !_leaveOpen)
        {
            _stream.Dispose();
        }

        _stream = null;
        _buf = default;
        _done = true;
    }

    /// <summary>Advances to the next event, reading more input when it needs it.</summary>
    /// <returns>True when an event is ready; false at the end of the message.</returns>
    /// <exception cref="NotSupportedException">The source is asynchronous only; use <see cref="ReadAsync"/>.</exception>
    /// <exception cref="IOException">The underlying stream or source failed.</exception>
    public bool Read()
    {
        while (true)
        {
            switch (Next())
            {
                case Step.Event:
                    return true;
                case Step.Done:
                    return false;
            }

            Fill();
        }
    }

    /// <summary>Asynchronous twin of <see cref="Read"/>.</summary>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>True when an event is ready; false at the end of the message.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            switch (Next())
            {
                case Step.Event:
                    return true;
                case Step.Done:
                    return false;
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- input ----

    private void Fill()
    {
        if (_eof)
        {
            return;
        }

        int free = PrepareBuffer();
        if (free < 0)
        {
            return;
        }

        int n = _stream is not null
            ? _stream.Read(_rented!, _end, free == 0 ? 1 : free)
            : _source!.Read(_srcPos, _rented.AsSpan(_end, free == 0 ? 1 : free));
        Filled(n, free == 0);
    }

    private async ValueTask FillAsync(CancellationToken ct)
    {
        if (_eof)
        {
            return;
        }

        int free = PrepareBuffer();
        if (free < 0)
        {
            return;
        }

        int want = free == 0 ? 1 : free;
        int n = _stream is not null
            ? await _stream.ReadAsync(_rented.AsMemory(_end, want), ct).ConfigureAwait(false)
            : await _source!.ReadAsync(_srcPos, _rented.AsMemory(_end, want), ct).ConfigureAwait(false);
        Filled(n, free == 0);
    }

    // Moves unread bytes to the front, grows the buffer if one header field needs more room, and returns the free space:
    // negative when no more can be read, zero when the total-size limit is reached (the caller probes for more input).
    private int PrepareBuffer()
    {
        byte[] r = _rented!;
        if (_start > 0)
        {
            if (_end > _start)
            {
                Buffer.BlockCopy(r, _start, r, 0, _end - _start);
            }

            _bufBase += _start;
            _end -= _start;
            _start = 0;
        }

        if (_end == r.Length)
        {
            if (r.Length >= _bufferCap)
            {
                // Nothing the reader decides needs this much lookahead; treat it as the end of usable input.
                _eof = true;
                _truncated = true;
                AddDefect(MimeDefectKind.HeaderLimitExceeded, _bufBase + _end, 0);
                return -1;
            }

            byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(_bufferCap, (long)r.Length * 2));
            Buffer.BlockCopy(r, 0, bigger, 0, _end);
            ArrayPool<byte>.Shared.Return(r);
            _rented = r = bigger;
        }

        _buf = r;
        long room = Math.Min(r.Length - _end, _limits.MaxTotalBytes - (_bufBase + _end));
        return (int)Math.Max(0, room);
    }

    private void Filled(int n, bool probe)
    {
        if (probe)
        {
            // At the size limit: any further byte means input is being left unread.
            _eof = true;
            if (n > 0)
            {
                _truncated = true;
                AddDefect(MimeDefectKind.TotalSizeLimitExceeded, _bufBase + _end, 0);
            }

            return;
        }

        if (n <= 0)
        {
            _eof = true;
            return;
        }

        _end += n;
        _srcPos += n;
    }

    // ---- events ----

    private Step Next()
    {
        _start += _consume;
        _consume = 0;
        if (_dqCount > 0)
        {
            _defect = _dq[_dqHead];
            _dqHead = (_dqHead + 1) % _dq.Length;
            _dqCount--;
            _kind = MimeEventKind.Defect;
            _evStart = _start;
            _evLen = 0;
            return Step.Event;
        }

        if (_done)
        {
            _kind = MimeEventKind.None;
            _evStart = _evLen = 0;
            return Step.Done;
        }

        Step r = Advance();
        if (r == Step.NeedData && _eof)
        {
            // Cannot happen: every path that needs data checks the end of input first. Stop rather than loop.
            _done = true;
            return Next();
        }

        return r;
    }

    private void AddDefect(MimeDefectKind kind, long offset, int length)
    {
        if (_dqCount == _dq.Length)
        {
            var bigger = new MimeDefect[_dq.Length * 2];
            for (int i = 0; i < _dqCount; i++)
            {
                bigger[i] = _dq[(_dqHead + i) % _dq.Length];
            }

            _dq = bigger;
            _dqHead = 0;
        }

        _dq[(_dqHead + _dqCount) % _dq.Length] = new MimeDefect(kind, offset, length, Math.Max(0, _n - 1));
        _dqCount++;
    }

    private Step Emit(MimeEventKind kind, int start, int len)
    {
        _kind = kind;
        _evStart = start;
        _evLen = len;
        _evDepth = Math.Max(0, _n - 1);
        _consume = len;
        return Step.Event;
    }

    private Step Advance()
    {
        if (_n == 0)
        {
            if (_entityCount > 0)
            {
                _done = true;
                return Step.Done;
            }

            return PushEntity();
        }

        if (_pend)
        {
            return _pendIdx < _n - 1 ? EndTop() : EmitDelimiter();
        }

        ref Frame f = ref _frames[_n - 1];
        switch (f.Phase)
        {
            case Phase.Headers:
                return StepHeaders(ref f);
            case Phase.ChildPending:
                return PushEntity();
            case Phase.Preamble:
            case Phase.Body:
            case Phase.Epilogue:
                return StepBody(ref f);
            default:
                return EndTop();
        }
    }

    private Step PushEntity()
    {
        if (_n == _frames.Length)
        {
            Array.Resize(ref _frames, _n * 2);
        }

        _frames[_n] = new Frame { BoundaryStart = _bndLen, Phase = Phase.Headers };
        _n++;
        _entityCount++;
        _atLineStart = true;
        return Emit(MimeEventKind.EntityStart, _start, 0);
    }

    private Step EndTop()
    {
        ref Frame f = ref _frames[_n - 1];
        if (f.Type == MimeEntityType.Multipart && !f.Closed)
        {
            // With no delimiter at all the body is all preamble (a multipart that never started); otherwise it never ended.
            AddDefect(f.SawDelimiter ? MimeDefectKind.MissingFinalBoundary : MimeDefectKind.MissingStartBoundary, _bufBase + _start, 0);
        }

        _evType = f.Type;
        Step r = Emit(MimeEventKind.EntityEnd, _start, 0);
        _bndLen = f.BoundaryStart;
        _n--;
        if (_n > 0)
        {
            ref Frame p = ref _frames[_n - 1];
            p.Phase = p.Type == MimeEntityType.Message ? Phase.Ended : Phase.AwaitDelimiter;
        }

        return r;
    }

    private Step EmitDelimiter()
    {
        ref Frame f = ref _frames[_n - 1];
        _pend = false;
        f.SawDelimiter = true;
        NoteLineEnd(_buf.Span.Slice(_start, _pendLen), _bufBase + _start);
        _evClose = _pendClose;
        if (_pendClose)
        {
            f.Closed = true;
            f.Phase = Phase.Epilogue;
        }
        else
        {
            f.Phase = Phase.ChildPending;
        }

        _atLineStart = true;
        return Emit(MimeEventKind.Delimiter, _start, _pendLen);
    }

    // RFC 5322 §2.2: lines end in CRLF, but LF-only mail is common. Mixing the two in one message is reported once.
    private void NoteLineEnd(ReadOnlySpan<byte> endsWithLineEnd, long offset)
    {
        if (endsWithLineEnd.IsEmpty || endsWithLineEnd[^1] != (byte)'\n')
        {
            return;
        }

        int style = endsWithLineEnd.Length > 1 && endsWithLineEnd[^2] == (byte)'\r' ? 2 : 1;
        if (_eolStyle == 0)
        {
            _eolStyle = style;
        }
        else if (style != _eolStyle && !_mixedReported)
        {
            _mixedReported = true;
            AddDefect(MimeDefectKind.MixedLineEndings, offset + endsWithLineEnd.Length - style, style);
        }
    }

    // ---- headers ----

    private Step StepHeaders(ref Frame f)
    {
        ReadOnlySpan<byte> b = _buf.Span;
        int s = _start;
        int e = _end;
        if (s >= e)
        {
            return _eof ? EndHeaders(ref f, 0, MimeDefectKind.MissingHeaderBodySeparator) : Step.NeedData;
        }

        byte c0 = b[s];
        if (c0 == (byte)'\n')
        {
            return EndHeaders(ref f, 1, MimeDefectKind.None);
        }

        if (c0 == (byte)'\r')
        {
            if (s + 1 >= e && !_eof)
            {
                return Step.NeedData;
            }

            if (s + 1 < e && b[s + 1] == (byte)'\n')
            {
                return EndHeaders(ref f, 2, MimeDefectKind.None);
            }
        }

        if (c0 is (byte)' ' or (byte)'\t')
        {
            return EndHeaders(ref f, 0, MimeDefectKind.HeaderContinuationWithoutHeader);
        }

        int budget = Math.Min(_limits.MaxHeaderBlockBytes - f.HeaderBytes, int.MaxValue);
        int fieldMax = Math.Min(_limits.MaxHeaderFieldBytes, budget);
        if (_bufBase + s == 0 && _n == 1 && b[s..e].StartsWith("From "u8))
        {
            // Python, Go and MimeKit all skip an mbox "From " line at the start; no header field is ever named "From ".
            int envLf = b[s..e].IndexOf((byte)'\n');
            if (envLf >= 0 || _eof || e - s > fieldMax)
            {
                return Emit(MimeEventKind.EnvelopeLine, s, envLf >= 0 ? envLf + 1 : e - s);
            }

            return Step.NeedData;
        }

        if (!FindFieldEnd(b, s, e, out int end))
        {
            if (e - s > fieldMax)
            {
                return EndHeaders(ref f, 0, fieldMax == budget ? MimeDefectKind.HeaderLimitExceeded : MimeDefectKind.HeaderFieldLimitExceeded);
            }

            return Step.NeedData;
        }

        int len = end - s;
        if (len > _limits.MaxHeaderFieldBytes)
        {
            return EndHeaders(ref f, 0, MimeDefectKind.HeaderFieldLimitExceeded);
        }

        if (len > budget || f.HeaderCount >= _limits.MaxHeadersPerEntity)
        {
            return EndHeaders(ref f, 0, MimeDefectKind.HeaderLimitExceeded);
        }

        // RFC 5322 §2.2: a field name is printable ASCII up to the colon; trailing whitespace before it is obsolete syntax (§4.2).
        int lf = b[s..end].IndexOf((byte)'\n');
        int firstLineEnd = lf < 0 ? end : s + lf;
        int colon = b[s..firstLineEnd].IndexOf((byte)':');
        if (colon < 0)
        {
            return EndHeaders(ref f, 0, MimeDefectKind.MissingHeaderBodySeparator);
        }

        int nameEnd = s + colon;
        int trimmed = nameEnd;
        while (trimmed > s && b[trimmed - 1] is (byte)' ' or (byte)'\t')
        {
            trimmed--;
        }

        // RFC 5322 §2.2 allows only printable ASCII in a field name, but MimeKit and Go accept anything before the colon, so a line
        // like "X Bad: v" stays a header (reported), and no line with a colon silently becomes body text.
        bool badName = trimmed == s; // an empty name (": text") is a header too, as in the three reference parsers
        for (int i = s; i < trimmed; i++)
        {
            if (b[i] is <= 32 or > 126)
            {
                badName = true;
                break;
            }
        }

        int valEnd = end;
        if (valEnd > s && b[valEnd - 1] == (byte)'\n')
        {
            valEnd--;
            if (valEnd > s && b[valEnd - 1] == (byte)'\r')
            {
                valEnd--;
            }
        }

        long abs = _bufBase + s;
        if (badName)
        {
            AddDefect(MimeDefectKind.InvalidHeaderName, abs, trimmed - s);
        }

        if (trimmed != nameEnd)
        {
            AddDefect(MimeDefectKind.WhitespaceBeforeHeaderColon, abs, nameEnd - trimmed);
        }

        ReadOnlySpan<byte> field = b[s..valEnd];
        if (!System.Text.Ascii.IsValid(field))
        {
            AddDefect(Utf8.IsValid(field) ? MimeDefectKind.Utf8Header : MimeDefectKind.AmbiguousEightBitHeader, abs, len);
        }

        if (field.IndexOf((byte)0) >= 0)
        {
            AddDefect(MimeDefectKind.NulInHeader, abs, len);
        }

        // The field's own line end is outside `field`, so any CR here must be followed by a LF inside it.
        for (int from = 0; ;)
        {
            int cr = field[from..].IndexOf((byte)'\r');
            if (cr < 0)
            {
                break;
            }

            int at = from + cr;
            if (at + 1 >= field.Length || field[at + 1] != (byte)'\n')
            {
                AddDefect(MimeDefectKind.BareCarriageReturnInHeader, abs + at, 1);
                break;
            }

            from = at + 1;
        }

        if (field.IndexOfAny(ControlBytes) >= 0)
        {
            AddDefect(MimeDefectKind.ControlCharacterInHeader, abs, len);
        }

        NoteLineEnd(b.Slice(s, len), abs);
        _nameStart = s;
        _nameLen = trimmed - s;
        _valStart = nameEnd + 1;
        _valLen = valEnd - _valStart;
        ProcessHeader(ref f, b.Slice(_nameStart, _nameLen), b.Slice(_valStart, _valLen), abs, len);
        f.HeaderBytes += len;
        f.HeaderCount++;
        return Emit(MimeEventKind.Header, s, len);
    }

    // RFC 5322 §2.2.3: a line starting with space or tab continues the field before it.
    private bool FindFieldEnd(ReadOnlySpan<byte> b, int s, int e, out int end)
    {
        int p = s;
        while (true)
        {
            int lf = b[p..e].IndexOf((byte)'\n');
            if (lf < 0)
            {
                end = e;
                return _eof;
            }

            int lineEnd = p + lf + 1;
            if (lineEnd >= e)
            {
                end = lineEnd;
                return _eof;
            }

            if (b[lineEnd] is (byte)' ' or (byte)'\t')
            {
                p = lineEnd;
                continue;
            }

            end = lineEnd;
            return true;
        }
    }

    private void ProcessHeader(ref Frame f, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, long abs, int len)
    {
        if (name.Length == 12 && StructuredHeader.EqualsIgnoreCase(name, "content-type"u8))
        {
            if (f.SawContentType)
            {
                AddDefect(MimeDefectKind.AmbiguousDuplicateContentType, abs, len);
                return;
            }

            f.SawContentType = true;
            if (!StructuredHeader.TryParseMediaType(value, out ReadOnlySpan<byte> type, out ReadOnlySpan<byte> sub, out int next))
            {
                AddDefect(MimeDefectKind.InvalidContentType, abs, len);
                return;
            }

            if (StructuredHeader.EqualsIgnoreCase(type, "multipart"u8))
            {
                f.MediaMultipart = true;
                f.MediaDigest = StructuredHeader.EqualsIgnoreCase(sub, "digest"u8);
                ReadBoundary(ref f, value, next);
            }
            else if (StructuredHeader.EqualsIgnoreCase(type, "message"u8)
                && (StructuredHeader.EqualsIgnoreCase(sub, "rfc822"u8) || StructuredHeader.EqualsIgnoreCase(sub, "global"u8)))
            {
                f.MediaMessage = true;
            }
        }
        else if (name.Length == 25 && StructuredHeader.EqualsIgnoreCase(name, "content-transfer-encoding"u8))
        {
            if (f.SawEncoding)
            {
                AddDefect(MimeDefectKind.AmbiguousDuplicateTransferEncoding, abs, len);
                return;
            }

            f.SawEncoding = true;
            f.Encoding = StructuredHeader.ParseTransferEncoding(value);
            if (f.Encoding == ContentTransferEncoding.Unknown)
            {
                AddDefect(MimeDefectKind.UnknownTransferEncoding, abs, len);
            }
        }
    }

    // RFC 2046 §5.1.1: the boundary parameter; the delimiter is "--" followed by it, so the dashes are stored with it.
    private void ReadBoundary(ref Frame f, ReadOnlySpan<byte> value, int from)
    {
        var en = new StructuredHeader.ParameterEnumerator(value, from);
        while (en.MoveNext())
        {
            if (!StructuredHeader.EqualsIgnoreCase(en.Name, "boundary"u8))
            {
                continue;
            }

            ReadOnlySpan<byte> v = en.Value;
            if (v.Length > _limits.MaxBoundaryLength + 64 * 4)
            {
                f.BoundaryState = BoundaryState.TooLong;
                return;
            }

            if (_bnd.Length < f.BoundaryStart + 2 + v.Length)
            {
                Array.Resize(ref _bnd, f.BoundaryStart + 2 + v.Length + 256);
            }

            Span<byte> dest = _bnd.AsSpan(f.BoundaryStart + 2);
            int n;
            if (en.Quoted)
            {
                n = StructuredHeader.Unquote(v, dest);
            }
            else
            {
                v.CopyTo(dest);
                n = v.Length;
            }

            // A delimiter may be followed by padding, so trimming trailing blanks off the boundary matches both readings.
            int trimmedLength = n;
            while (trimmedLength > 0 && _bnd[f.BoundaryStart + 2 + trimmedLength - 1] is (byte)' ' or (byte)'\t')
            {
                trimmedLength--;
            }

            if (trimmedLength != n && trimmedLength > 0)
            {
                f.BoundaryTrimmed = true;
                n = trimmedLength;
            }

            if (n == 0)
            {
                f.BoundaryState = BoundaryState.Empty;
            }
            else if (n > _limits.MaxBoundaryLength)
            {
                f.BoundaryState = BoundaryState.TooLong;
            }
            else
            {
                _bnd[f.BoundaryStart] = (byte)'-';
                _bnd[f.BoundaryStart + 1] = (byte)'-';
                f.BoundaryLen = n + 2;
                f.BoundaryState = BoundaryState.Ok;
            }

            return;
        }
    }

    // Closes the header block (consuming the separator line, if any) and decides what the body is.
    private Step EndHeaders(ref Frame f, int separatorLength, MimeDefectKind defect)
    {
        long pos = _bufBase + _start;
        if (defect != MimeDefectKind.None)
        {
            AddDefect(defect, pos, 0);
        }

        NoteLineEnd(_buf.Span.Slice(_start, separatorLength), pos);
        bool multipart = f.MediaMultipart;
        bool message = f.MediaMessage;
        if (!f.SawContentType && _n >= 2 && _frames[_n - 2].Type == MimeEntityType.Multipart && _frames[_n - 2].Digest)
        {
            message = true; // RFC 2046 §5.1.5: the default type inside multipart/digest is message/rfc822
        }

        // RFC 2045 §6.4: multipart and message entities must not be base64 or quoted-printable encoded. Two of the three
        // reference libraries parse a multipart's parts from the raw body anyway, and so do we; an embedded message is
        // kept as a leaf whose decoded content the caller can parse.
        if ((multipart || message) && f.Encoding is ContentTransferEncoding.Base64 or ContentTransferEncoding.QuotedPrintable)
        {
            AddDefect(MimeDefectKind.AmbiguousEncodedComposite, pos, 0);
            message = false;
        }

        if (multipart && f.BoundaryState != BoundaryState.Ok)
        {
            AddDefect(
                f.BoundaryState switch
                {
                    BoundaryState.Empty => MimeDefectKind.EmptyBoundary,
                    BoundaryState.TooLong => MimeDefectKind.BoundaryTooLong,
                    _ => MimeDefectKind.MultipartWithoutBoundary,
                },
                pos,
                0);
            multipart = false;
        }

        if ((multipart || message) && _n > _limits.MaxDepth)
        {
            AddDefect(MimeDefectKind.DepthLimitExceeded, pos, 0);
            multipart = message = false;
        }

        if (message && !_parseMessages)
        {
            message = false;
        }

        if (message && _entityCount >= _limits.MaxEntityCount)
        {
            AddDefect(MimeDefectKind.EntityLimitExceeded, pos, 0);
            _flood = true;
            message = false;
        }

        _evBoundaryLen = 0;
        if (multipart)
        {
            f.Type = MimeEntityType.Multipart;
            f.Digest = f.MediaDigest;
            f.Phase = Phase.Preamble;
            _bndLen = f.BoundaryStart + f.BoundaryLen;
            for (int i = 0; i < _n - 1; i++)
            {
                if (_frames[i].Type == MimeEntityType.Multipart && _frames[i].BoundaryLen == f.BoundaryLen
                    && _bnd.AsSpan(_frames[i].BoundaryStart, f.BoundaryLen).SequenceEqual(_bnd.AsSpan(f.BoundaryStart, f.BoundaryLen)))
                {
                    AddDefect(MimeDefectKind.AmbiguousDuplicateBoundary, pos, 0);
                    break;
                }
            }

            if (f.BoundaryTrimmed)
            {
                AddDefect(MimeDefectKind.BoundaryHasTrailingSpace, pos, 0);
            }

            _evBoundaryStart = f.BoundaryStart + 2;
            _evBoundaryLen = f.BoundaryLen - 2;
        }
        else
        {
            f.BoundaryLen = 0;
            f.Type = message ? MimeEntityType.Message : MimeEntityType.Leaf;
            f.Phase = message ? Phase.ChildPending : Phase.Body;
        }

        _atLineStart = true;
        _evType = f.Type;
        return Emit(MimeEventKind.HeadersEnd, _start, separatorLength);
    }

    // ---- bodies and delimiters ----

    private bool AnyBoundary()
    {
        for (int i = 0; i < _n; i++)
        {
            if (_frames[i].Type == MimeEntityType.Multipart && !_frames[i].Closed)
            {
                return true;
            }
        }

        return false;
    }

    private Step StepBody(ref Frame f)
    {
        MimeEventKind kind = f.Phase switch
        {
            Phase.Preamble => MimeEventKind.Preamble,
            Phase.Epilogue => MimeEventKind.Epilogue,
            _ => MimeEventKind.Body,
        };
        ReadOnlySpan<byte> b = _buf.Span;
        int s = _start;
        int e = _end;
        if (s >= e)
        {
            if (!_eof)
            {
                return Step.NeedData;
            }

            f.Phase = Phase.Ended;
            return EndTop();
        }

        bool atStart = _atLineStart;
        if (_flood || !AnyBoundary())
        {
            CheckEpilogue(ref f, kind, b, s, e - s, atStart);
            return Emit(kind, s, e - s);
        }

        if (_atLineStart)
        {
            if (b[s] == (byte)'-')
            {
                if (e - s < 2 && !_eof)
                {
                    return Step.NeedData;
                }

                if (e - s >= 2 && b[s + 1] == (byte)'-')
                {
                    Classified c = Classify(b, s);
                    if (c == Classified.NeedMore)
                    {
                        return Step.NeedData;
                    }

                    if (c == Classified.Found)
                    {
                        return Advance();
                    }
                }
            }

            _atLineStart = false;
        }

        // Delimiters start a line, so the candidates are the places where "\n--" occurs.
        int from = s;
        while (true)
        {
            int idx = b[from..e].IndexOf("\n--"u8);
            if (idx < 0)
            {
                break;
            }

            int lf = from + idx;
            int eolStart = lf > s && b[lf - 1] == (byte)'\r' ? lf - 1 : lf;
            Classified c = Classify(b, lf + 1, eolStart);
            if (c == Classified.Found || c == Classified.NeedMore)
            {
                if (eolStart > s)
                {
                    return Emit(kind, s, eolStart - s);
                }

                return c == Classified.Found ? Advance() : Step.NeedData;
            }

            from = lf + 1;
        }

        // No delimiter in sight. Hold back a trailing fragment that could be the start of one in the next read.
        int ce = e;
        if (!_eof)
        {
            if (ce - s >= 2 && b[ce - 1] == (byte)'-' && b[ce - 2] == (byte)'\n')
            {
                ce -= 2;
            }
            else if (b[ce - 1] is (byte)'\n' or (byte)'\r')
            {
                ce--;
            }

            if (ce > s && ce < e && b[ce - 1] == (byte)'\r' && b[ce] == (byte)'\n')
            {
                ce--;
            }

            if (ce <= s)
            {
                return Step.NeedData;
            }
        }

        _atLineStart = false;
        CheckEpilogue(ref f, kind, b, s, ce - s, atStart);
        return Emit(kind, s, ce - s);
    }

    // After the closing delimiter everything is epilogue, even a line that looks like a delimiter of the same multipart
    // (RFC 2046 §5.1.1); that is reported once.
    private void CheckEpilogue(ref Frame f, MimeEventKind kind, ReadOnlySpan<byte> b, int s, int length, bool atLineStart)
    {
        if (kind != MimeEventKind.Epilogue || f.EpilogueReported || f.BoundaryLen == 0)
        {
            return;
        }

        ReadOnlySpan<byte> chunk = b.Slice(s, length);
        ReadOnlySpan<byte> bnd = _bnd.AsSpan(f.BoundaryStart, f.BoundaryLen);
        for (int from = 0; from < chunk.Length;)
        {
            int i = chunk[from..].IndexOf(bnd);
            if (i < 0)
            {
                return;
            }

            int at = from + i;
            if ((at == 0 && atLineStart) || (at > 0 && chunk[at - 1] == (byte)'\n'))
            {
                f.EpilogueReported = true;
                AddDefect(MimeDefectKind.EpilogueHasDelimiter, _bufBase + s + at, bnd.Length);
                return;
            }

            from = at + 1;
        }
    }

    private Classified Classify(ReadOnlySpan<byte> b, int ls) => Classify(b, ls, ls);

    // RFC 2046 §5.1.1: a delimiter line is "--" boundary, optionally "--" for the close delimiter, optional transport
    // padding (spaces and tabs), then the line end. Active boundaries are tried innermost first; RFC 2046 §5.1.2 lets a
    // delimiter of any enclosing multipart end the ones nested inside it.
    private Classified Classify(ReadOnlySpan<byte> b, int ls, int eolStart)
    {
        if (_flood)
        {
            return Classified.NotDelimiter;
        }

        int e = _end;
        bool needMore = false;
        for (int i = _n - 1; i >= 0; i--)
        {
            ref Frame fr = ref _frames[i];
            if (fr.Type != MimeEntityType.Multipart || fr.Closed)
            {
                continue;
            }

            ReadOnlySpan<byte> bnd = _bnd.AsSpan(fr.BoundaryStart, fr.BoundaryLen);
            int avail = e - ls;
            if (avail < bnd.Length)
            {
                if (b.Slice(ls, avail).SequenceEqual(bnd[..avail]) && !_eof)
                {
                    needMore = true;
                }

                continue;
            }

            if (!b.Slice(ls, bnd.Length).SequenceEqual(bnd))
            {
                continue;
            }

            int q = ls + bnd.Length;
            if (q + 2 > e && !_eof)
            {
                needMore = true;
                continue;
            }

            bool close = false;
            if (q + 1 < e && b[q] == (byte)'-' && b[q + 1] == (byte)'-')
            {
                close = true;
                q += 2;
            }

            int padStart = q;
            while (q < e && (b[q] is (byte)' ' or (byte)'\t') && q - padStart <= MaxDelimiterPadding)
            {
                q++;
            }

            int lineEnd;
            if (q >= e)
            {
                if (!_eof)
                {
                    needMore = true;
                    continue;
                }

                lineEnd = q;
            }
            else if (b[q] == (byte)'\n')
            {
                lineEnd = q + 1;
            }
            else if (b[q] == (byte)'\r' && q + 1 >= e && !_eof)
            {
                needMore = true;
                continue;
            }
            else if (b[q] == (byte)'\r' && q + 1 < e && b[q + 1] == (byte)'\n')
            {
                lineEnd = q + 2;
            }
            else
            {
                long at = _bufBase + ls;
                if (at > _lastAmbiguousBoundary)
                {
                    _lastAmbiguousBoundary = at;
                    AddDefect(MimeDefectKind.AmbiguousBoundaryLikeLine, at, bnd.Length);
                }

                continue;
            }

            if (!close && _entityCount >= _limits.MaxEntityCount)
            {
                AddDefect(MimeDefectKind.EntityLimitExceeded, _bufBase + ls, 0);
                _flood = true;
                return Classified.NotDelimiter;
            }

            _pend = true;
            _pendIdx = i;
            _pendClose = close;
            _pendLen = lineEnd - eolStart;
            return Classified.Found;
        }

        return needMore ? Classified.NeedMore : Classified.NotDelimiter;
    }
}
