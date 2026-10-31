using System.Buffers;

namespace RecursiveAI.OneMail.Mime.Transfer;

/// <summary>Things a lenient transfer decoder had to repair. Decoding never fails; these say what was skipped or guessed.</summary>
[Flags]
public enum TransferIssues
{
    /// <summary>Nothing was repaired.</summary>
    None = 0,

    /// <summary>Base64 input contained a character outside the alphabet (other than whitespace). It was skipped.</summary>
    InvalidBase64Character = 1,

    /// <summary>A base64 <c>=</c> where it does not belong (after zero or one data characters). It was skipped.</summary>
    MisplacedBase64Padding = 2,

    /// <summary>Base64 data ended inside a four-character group (missing padding, or a lost character).</summary>
    TruncatedBase64Group = 4,

    /// <summary>Quoted-printable <c>=</c> not followed by two hex digits or a line break. Kept as a literal <c>=</c>.</summary>
    InvalidQuotedPrintableEscape = 8,

    /// <summary>Quoted-printable data ended right after a <c>=</c> or a half escape.</summary>
    TruncatedQuotedPrintableEscape = 16,
}

/// <summary>
/// A stateful, lenient base64 decoder (RFC 2045 §6.8) for bodies delivered in pieces. Whitespace and characters outside the
/// alphabet are skipped, padding ends a group, and data after padding starts a new group, so concatenated base64 blocks
/// decode too. A group cut short by the end of input still yields its complete bytes.
/// </summary>
public sealed class Base64Decoder
{
    private static readonly sbyte[] Table = BuildTable();

    private int _acc;
    private int _count;
    private bool _secondPadPending;

    /// <summary>What has been repaired so far.</summary>
    public TransferIssues Issues { get; private set; }

    /// <summary>The output space <see cref="Decode"/> may need for <paramref name="inputLength"/> input bytes.</summary>
    /// <param name="inputLength">Input bytes.</param>
    /// <returns>A safe output size.</returns>
    public static int MaxOutputLength(int inputLength) => inputLength + 4;

    private static sbyte[] BuildTable()
    {
        var t = new sbyte[256];
        Array.Fill(t, (sbyte)-1);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        for (int i = 0; i < alphabet.Length; i++)
        {
            t[alphabet[i]] = (sbyte)i;
        }

        return t;
    }

    /// <summary>Decodes a piece of input.</summary>
    /// <param name="input">Encoded bytes.</param>
    /// <param name="output">At least <see cref="MaxOutputLength"/> bytes of room.</param>
    /// <returns>Bytes written to <paramref name="output"/>.</returns>
    public int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        sbyte[] table = Table;
        int o = 0;
        int acc = _acc;
        int count = _count;
        foreach (byte c in input)
        {
            int v = table[c];
            if (v >= 0)
            {
                acc = (acc << 6) | v;
                if (++count == 4)
                {
                    output[o++] = (byte)(acc >> 16);
                    output[o++] = (byte)(acc >> 8);
                    output[o++] = (byte)acc;
                    count = 0;
                    acc = 0;
                }

                _secondPadPending = false;
            }
            else if (c == (byte)'=')
            {
                // RFC 2045 §6.8: "==" after two characters, "=" after three.
                if (count == 2)
                {
                    output[o++] = (byte)(acc >> 4);
                    count = 0;
                    acc = 0;
                    _secondPadPending = true;
                }
                else if (count == 3)
                {
                    output[o++] = (byte)(acc >> 10);
                    output[o++] = (byte)(acc >> 2);
                    count = 0;
                    acc = 0;
                }
                else if (_secondPadPending)
                {
                    _secondPadPending = false;
                }
                else
                {
                    Issues |= TransferIssues.MisplacedBase64Padding;
                    count = 0;
                    acc = 0;
                }
            }
            else if (c is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                Issues |= TransferIssues.InvalidBase64Character;
            }
        }

        _acc = acc;
        _count = count;
        return o;
    }

    /// <summary>Ends the input, writing the bytes of a final partial group.</summary>
    /// <param name="output">At least 2 bytes of room.</param>
    /// <returns>Bytes written.</returns>
    public int Finish(Span<byte> output)
    {
        int o = 0;
        if (_count == 2)
        {
            output[o++] = (byte)(_acc >> 4);
            Issues |= TransferIssues.TruncatedBase64Group;
        }
        else if (_count == 3)
        {
            output[o++] = (byte)(_acc >> 10);
            output[o++] = (byte)(_acc >> 2);
            Issues |= TransferIssues.TruncatedBase64Group;
        }
        else if (_count == 1)
        {
            Issues |= TransferIssues.TruncatedBase64Group;
        }

        _count = 0;
        _acc = 0;
        return o;
    }
}

/// <summary>
/// A stateful, lenient quoted-printable decoder (RFC 2045 §6.7). Soft line breaks (<c>=</c> then a line break) vanish,
/// <c>=XX</c> escapes (either hex case) become bytes, blanks before a line break are kept (they are data), and everything
/// else passes through. Hard line breaks are copied as they are (CRLF stays CRLF, LF stays LF). A malformed
/// escape stays literal.
/// </summary>
public sealed class QuotedPrintableDecoder
{
    private const int WhitespaceCapacity = 64;

    private enum State : byte
    {
        Normal,
        AfterEquals,
        AfterHex,
        SoftWhitespace,
        SoftCr,
    }

    private readonly byte[] _ws = new byte[WhitespaceCapacity];
    private int _wsCount;
    private State _state;
    private byte _hex;

    /// <summary>What has been repaired so far.</summary>
    public TransferIssues Issues { get; private set; }

    /// <summary>The output space <see cref="Decode"/> may need for <paramref name="inputLength"/> input bytes.</summary>
    /// <param name="inputLength">Input bytes.</param>
    /// <returns>A safe output size.</returns>
    public static int MaxOutputLength(int inputLength) => inputLength + WhitespaceCapacity + 4;

    private static int HexValue(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        _ => -1,
    };

    /// <summary>Decodes a piece of input.</summary>
    /// <param name="input">Encoded bytes.</param>
    /// <param name="output">At least <see cref="MaxOutputLength"/> bytes of room.</param>
    /// <returns>Bytes written to <paramref name="output"/>.</returns>
    public int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int o = 0;
        for (int i = 0; i < input.Length; i++)
        {
            byte c = input[i];
        again:
            switch (_state)
            {
                case State.Normal:
                    if (c == (byte)'=')
                    {
                        o = FlushWhitespace(output, o);
                        _state = State.AfterEquals;
                    }
                    else if (c is (byte)' ' or (byte)'\t')
                    {
                        if (_wsCount == WhitespaceCapacity)
                        {
                            o = FlushWhitespace(output, o);
                        }

                        _ws[_wsCount++] = c;
                    }
                    else if (c is (byte)'\r' or (byte)'\n')
                    {
                        // RFC 2045 §6.7 rule 3 says trailing blanks must not be produced, but the three reference parsers keep
                        // them when they are present, and dropping them would change the body: they are data.
                        o = FlushWhitespace(output, o);
                        output[o++] = c;
                    }
                    else
                    {
                        if (_wsCount > 0)
                        {
                            o = FlushWhitespace(output, o);
                        }

                        output[o++] = c;
                    }

                    break;

                case State.AfterEquals:
                    if (HexValue(c) >= 0)
                    {
                        _hex = c;
                        _state = State.AfterHex;
                    }
                    else if (c == (byte)'\n')
                    {
                        _state = State.Normal;
                    }
                    else if (c == (byte)'\r')
                    {
                        _state = State.SoftCr;
                    }
                    else if (c is (byte)' ' or (byte)'\t')
                    {
                        // "=" then blanks then the line break is a soft break with trailing blanks. If something else follows,
                        // the "=" and the blanks are literal, so the blanks are held until we know.
                        _wsCount = 0;
                        _ws[_wsCount++] = c;
                        _state = State.SoftWhitespace;
                    }
                    else
                    {
                        Issues |= TransferIssues.InvalidQuotedPrintableEscape;
                        output[o++] = (byte)'=';
                        _state = State.Normal;
                        goto again;
                    }

                    break;

                case State.AfterHex:
                    if (HexValue(c) >= 0)
                    {
                        output[o++] = (byte)((HexValue(_hex) << 4) | HexValue(c));
                        _state = State.Normal;
                    }
                    else
                    {
                        Issues |= TransferIssues.InvalidQuotedPrintableEscape;
                        output[o++] = (byte)'=';
                        output[o++] = _hex;
                        _state = State.Normal;
                        goto again;
                    }

                    break;

                case State.SoftWhitespace:
                    if (c == (byte)'\r')
                    {
                        _wsCount = 0;
                        _state = State.SoftCr;
                    }
                    else if (c == (byte)'\n')
                    {
                        _wsCount = 0;
                        _state = State.Normal;
                    }
                    else if (c is (byte)' ' or (byte)'\t')
                    {
                        if (_wsCount < WhitespaceCapacity)
                        {
                            _ws[_wsCount++] = c;
                        }
                    }
                    else
                    {
                        Issues |= TransferIssues.InvalidQuotedPrintableEscape;
                        output[o++] = (byte)'=';
                        o = FlushWhitespace(output, o);
                        _state = State.Normal;
                        goto again;
                    }

                    break;

                default: // SoftCr
                    _state = State.Normal;
                    if (c != (byte)'\n')
                    {
                        goto again;
                    }

                    break;
            }
        }

        return o;
    }

    /// <summary>Ends the input, writing anything held back.</summary>
    /// <param name="output">At least <see cref="MaxOutputLength"/>(0) bytes of room.</param>
    /// <returns>Bytes written.</returns>
    public int Finish(Span<byte> output)
    {
        int o = 0;
        if (_state == State.AfterEquals || _state == State.SoftWhitespace)
        {
            Issues |= TransferIssues.TruncatedQuotedPrintableEscape;
            output[o++] = (byte)'=';
        }
        else if (_state == State.AfterHex)
        {
            Issues |= TransferIssues.TruncatedQuotedPrintableEscape;
            output[o++] = (byte)'=';
            output[o++] = _hex;
        }

        _state = State.Normal;
        return FlushWhitespace(output, o);
    }

    private int FlushWhitespace(Span<byte> output, int o)
    {
        _ws.AsSpan(0, _wsCount).CopyTo(output[o..]);
        o += _wsCount;
        _wsCount = 0;
        return o;
    }
}

/// <summary>
/// A stateful, lenient uudecoder for bodies delivered in pieces. Text before the <c>begin</c> line is ignored, each data line
/// starts with a length character (<c>' '</c> + n, or <c>`</c> for zero) and decoding stops at the <c>end</c> line. Characters
/// from space to <c>`</c> all decode (space and backquote are both zero), as old encoders differ on that. A line whose length
/// character claims more bytes than the line holds yields what it holds.
/// </summary>
public sealed class UuDecoder
{
    private const int MaxLine = 256;
    private readonly byte[] _line = new byte[MaxLine];
    private int _length;
    private bool _begun;
    private bool _ended;

    /// <summary>The output space <see cref="Decode"/> may need for <paramref name="inputLength"/> input bytes.</summary>
    /// <param name="inputLength">Input bytes.</param>
    /// <returns>A safe output size.</returns>
    public static int MaxOutputLength(int inputLength) => inputLength + MaxLine;

    /// <summary>Decodes a piece of input.</summary>
    /// <param name="input">Encoded bytes.</param>
    /// <param name="output">At least <see cref="MaxOutputLength"/> bytes of room.</param>
    /// <returns>Bytes written to <paramref name="output"/>.</returns>
    public int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int o = 0;
        foreach (byte c in input)
        {
            if (c == (byte)'\n')
            {
                o += Line(output[o..]);
                _length = 0;
            }
            else if (_length < MaxLine)
            {
                _line[_length++] = c;
            }
        }

        return o;
    }

    /// <summary>Ends the input, decoding a last line that had no line break.</summary>
    /// <param name="output">At least <see cref="MaxOutputLength"/>(0) bytes of room.</param>
    /// <returns>Bytes written.</returns>
    public int Finish(Span<byte> output)
    {
        int o = Line(output);
        _length = 0;
        return o;
    }

    private int Line(Span<byte> output)
    {
        ReadOnlySpan<byte> line = _line.AsSpan(0, _length);
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (_ended)
        {
            return 0;
        }

        if (!_begun)
        {
            _begun = line.StartsWith("begin "u8);
            return 0;
        }

        if (line.Length == 0)
        {
            return 0;
        }

        if (line.SequenceEqual("end"u8))
        {
            _ended = true;
            return 0;
        }

        int n = (line[0] - 32) & 63;
        int o = 0;
        int i = 1;
        while (o < n)
        {
            // Four characters carry three bytes; a short line is padded with zero characters.
            int a = Sextet(line, i), b = Sextet(line, i + 1), c = Sextet(line, i + 2), d = Sextet(line, i + 3);
            if (i >= line.Length)
            {
                break;
            }

            i += 4;
            int v = (a << 18) | (b << 12) | (c << 6) | d;
            if (o < n)
            {
                output[o++] = (byte)(v >> 16);
            }

            if (o < n)
            {
                output[o++] = (byte)(v >> 8);
            }

            if (o < n)
            {
                output[o++] = (byte)v;
            }
        }

        return o;
    }

    private static int Sextet(ReadOnlySpan<byte> line, int i) => i < line.Length ? (line[i] - 32) & 63 : 0;
}

/// <summary>Encoders for the transfer encodings, for writing bodies.</summary>
public static class TransferEncoder
{
    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    /// <summary>Encodes bytes as base64 in lines of at most 76 characters (RFC 2045 §6.8), each ended by <paramref name="newline"/>.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="newline">Line end; defaults to CRLF. Pass empty for one unbroken line.</param>
    /// <param name="trailingNewline">Whether the last line also ends with the line end. Default false: a body's last line break belongs to the delimiter.</param>
    /// <returns>The encoded bytes.</returns>
    public static byte[] Base64(ReadOnlySpan<byte> data, ReadOnlySpan<byte> newline = default, bool trailingNewline = false)
    {
        if (newline.IsEmpty)
        {
            newline = Crlf;
        }

        int lines = (data.Length + 56) / 57;
        int encodedLength = (data.Length + 2) / 3 * 4;
        byte[] result = new byte[encodedLength + (lines * newline.Length)];
        int o = 0;
        for (int i = 0; i < data.Length; i += 57)
        {
            ReadOnlySpan<byte> chunk = data.Slice(i, Math.Min(57, data.Length - i));
            _ = System.Buffers.Text.Base64.EncodeToUtf8(chunk, result.AsSpan(o), out _, out int written);
            o += written;
            if (i + 57 < data.Length || trailingNewline)
            {
                newline.CopyTo(result.AsSpan(o));
                o += newline.Length;
            }
        }

        return result.AsSpan(0, o).ToArray();
    }

    /// <summary>
    /// Encodes bytes as quoted-printable (RFC 2045 §6.7) with lines of at most 76 characters.
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <param name="binary">When true, CR and LF are encoded as escapes; when false (text) CRLF and bare LF are line breaks.</param>
    /// <param name="newline">Line end; defaults to CRLF.</param>
    /// <returns>The encoded bytes.</returns>
    public static byte[] QuotedPrintable(ReadOnlySpan<byte> data, bool binary = false, ReadOnlySpan<byte> newline = default)
    {
        if (newline.IsEmpty)
        {
            newline = Crlf;
        }

        var result = new ArrayBufferWriter<byte>(data.Length + (data.Length / 8) + 16);
        int col = 0;
        ReadOnlySpan<byte> hex = "0123456789ABCDEF"u8;
        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];
            if (!binary && (c == (byte)'\n' || (c == (byte)'\r' && i + 1 < data.Length && data[i + 1] == (byte)'\n')))
            {
                if (c == (byte)'\r')
                {
                    i++;
                }

                Append(result, newline);
                col = 0;
                continue;
            }

            bool atLineEnd = i + 1 == data.Length || (!binary && (data[i + 1] == (byte)'\n' || data[i + 1] == (byte)'\r'));
            bool literal = c is >= 33 and <= 126 and not (byte)'='
                || (c is (byte)' ' or (byte)'\t' && !atLineEnd);
            int width = literal ? 1 : 3;
            if (col + width > 75)
            {
                result.Write("="u8);
                Append(result, newline);
                col = 0;
            }

            if (literal)
            {
                result.Write([c]);
            }
            else
            {
                result.Write([(byte)'=', hex[c >> 4], hex[c & 15]]);
            }

            col += width;
        }

        return result.WrittenSpan.ToArray();
    }

    private static void Append(ArrayBufferWriter<byte> w, ReadOnlySpan<byte> bytes) => w.Write(bytes);
}

/// <summary>
/// A read-only stream that decodes a body from its transfer encoding as it is read. 7bit, 8bit, binary and unknown encodings
/// pass through. Decoding is lenient; see <see cref="Issues"/> for what was repaired once the stream has been read to the end.
/// </summary>
public sealed class TransferDecodingStream : Stream
{
    private const int ChunkSize = 8192;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly ContentTransferEncoding _encoding;
    private readonly Base64Decoder? _b64;
    private readonly QuotedPrintableDecoder? _qp;
    private readonly UuDecoder? _uu;
    private readonly byte[]? _in;
    private readonly byte[]? _out;
    private int _outPos;
    private int _outLen;
    private bool _finished;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The encoded bytes, positioned at the start of the body.</param>
    /// <param name="encoding">How they are encoded.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves <paramref name="inner"/> open.</param>
    public TransferDecodingStream(Stream inner, ContentTransferEncoding encoding, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _leaveOpen = leaveOpen;
        _encoding = encoding;
        if (encoding == ContentTransferEncoding.Base64)
        {
            _b64 = new Base64Decoder();
        }
        else if (encoding == ContentTransferEncoding.QuotedPrintable)
        {
            _qp = new QuotedPrintableDecoder();
        }

        else if (encoding == ContentTransferEncoding.UUEncode)
        {
            _uu = new UuDecoder();
        }

        if (_b64 is not null || _qp is not null || _uu is not null)
        {
            _in = new byte[ChunkSize];
            _out = new byte[ChunkSize + 128];
        }
    }

    /// <summary>What the decoder repaired so far; complete once the stream returned 0.</summary>
    public TransferIssues Issues => _b64?.Issues ?? _qp?.Issues ?? TransferIssues.None;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        if (_in is null)
        {
            return _inner.Read(buffer);
        }

        while (_outPos == _outLen)
        {
            if (_finished)
            {
                return 0;
            }

            int n = _inner.Read(_in, 0, _in.Length);
            Decode(n);
        }

        return CopyOut(buffer);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_in is null)
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        while (_outPos == _outLen)
        {
            if (_finished)
            {
                return 0;
            }

            int n = await _inner.ReadAsync(_in.AsMemory(), cancellationToken).ConfigureAwait(false);
            Decode(n);
        }

        return CopyOut(buffer.Span);
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Decode(int n)
    {
        _outPos = 0;
        if (n <= 0)
        {
            _finished = true;
            _outLen = _b64 is not null ? _b64.Finish(_out) : _qp is not null ? _qp.Finish(_out) : _uu!.Finish(_out);
            return;
        }

        _outLen = _b64 is not null ? _b64.Decode(_in.AsSpan(0, n), _out) : _qp is not null ? _qp.Decode(_in.AsSpan(0, n), _out) : _uu!.Decode(_in.AsSpan(0, n), _out);
    }

    private int CopyOut(Span<byte> buffer)
    {
        int k = Math.Min(buffer.Length, _outLen - _outPos);
        _out.AsSpan(_outPos, k).CopyTo(buffer);
        _outPos += k;
        return k;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
