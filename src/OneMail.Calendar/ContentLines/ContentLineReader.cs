using System.Text;

namespace RecursiveAI.OneMail.Calendar.ContentLines;

/// <summary>What a physical-or-logical line turned out to be.</summary>
public enum ContentLineKind
{
    /// <summary>A content line: <c>[group.]name[;parameters]:value</c>.</summary>
    Property = 0,

    /// <summary>An empty line.</summary>
    Blank,

    /// <summary>A line that is not a content line (no colon, or no name). The raw bytes are all it has.</summary>
    Malformed,
}

/// <summary>Options for <see cref="ContentLineReader"/>.</summary>
public sealed record ContentLineReaderOptions
{
    /// <summary>The defaults: quoted-printable soft breaks and unindented base64 continuation both handled.</summary>
    public static ContentLineReaderOptions Default { get; } = new();

    /// <summary>
    /// The longest logical line whose value is exposed, in bytes. Longer lines are cut there, flagged by
    /// <see cref="ContentLine.IsTruncated"/>, and still consumed in full. Default 16 MiB.
    /// </summary>
    public int MaxLineBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Join the next physical line when a <c>QUOTED-PRINTABLE</c> line ends with a soft break (<c>=</c>), as vCard 2.1
    /// writers produce. The soft break is removed from the value. Default true.
    /// </summary>
    public bool QuotedPrintableSoftBreaks { get; init; } = true;

    /// <summary>
    /// Join following unindented lines that hold only base64 characters when the line carries <c>ENCODING=BASE64</c>
    /// (or the bare <c>BASE64</c> / <c>B</c> forms), and the empty line that ends such a block, as vCard 2.1 writers
    /// produce. Default true.
    /// </summary>
    public bool Base64Continuation { get; init; } = true;
}

/// <summary>
/// One content line (RFC 5545 §3.1, RFC 6350 §3.3) after unfolding. The spans are valid until the next call to
/// <see cref="ContentLineReader.TryRead"/>.
/// </summary>
public readonly ref struct ContentLine
{
    internal ContentLine(
        ContentLineKind kind, long offset, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> group, ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> value, bool isFolded, bool isTruncated)
    {
        Kind = kind;
        Offset = offset;
        Raw = raw;
        Group = group;
        Name = name;
        Parameters = parameters;
        Value = value;
        IsFolded = isFolded;
        IsTruncated = isTruncated;
    }

    /// <summary>What the line is.</summary>
    public ContentLineKind Kind { get; }

    /// <summary>Byte offset of the line's first byte in the input.</summary>
    public long Offset { get; }

    /// <summary>The line exactly as it appears in the input, folds and line end included.</summary>
    public ReadOnlySpan<byte> Raw { get; }

    /// <summary>The group before the dot (<c>item1</c> in <c>item1.EMAIL</c>), or empty.</summary>
    public ReadOnlySpan<byte> Group { get; }

    /// <summary>The property name as written (case preserved), or empty for blank and malformed lines.</summary>
    public ReadOnlySpan<byte> Name { get; }

    /// <summary>The parameter text between the first semicolon and the colon, without the leading semicolon; empty if none.</summary>
    public ReadOnlySpan<byte> Parameters { get; }

    /// <summary>The value after the colon, unfolded, still escaped. For malformed lines, the whole unfolded line.</summary>
    public ReadOnlySpan<byte> Value { get; }

    /// <summary>True when physical lines were joined (a fold, a quoted-printable soft break or base64 continuation).</summary>
    public bool IsFolded { get; }

    /// <summary>True when the logical line exceeded <see cref="ContentLineReaderOptions.MaxLineBytes"/> and <see cref="Value"/> was cut.</summary>
    public bool IsTruncated { get; }

    /// <summary>Enumerates the parameters.</summary>
    /// <returns>An enumerator over <see cref="Parameters"/>.</returns>
    public ContentParameterEnumerator EnumerateParameters() => new(Parameters);
}

/// <summary>
/// Forward-only reader of content lines over a byte span. It never throws on malformed input, reports repairs to the
/// optional defect list, and allocates only when a line is folded (one reusable unfold buffer, grown as needed).
/// </summary>
public ref struct ContentLineReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _maxLine;
    private readonly bool _qp;
    private readonly bool _b64;
    private readonly List<CalendarDefect>? _defects;
    private int _pos;
    private byte[]? _buffer;
    private bool _reportedLf;
    private bool _reportedCr;
    private bool _reportedDoubleCr;

    /// <summary>Starts reading.</summary>
    /// <param name="data">The bytes. A leading UTF-8 byte order mark is skipped (see <see cref="Position"/>).</param>
    /// <param name="options">Reader options, or null for the defaults.</param>
    /// <param name="defects">Receives defects, or null to ignore them.</param>
    public ContentLineReader(ReadOnlySpan<byte> data, ContentLineReaderOptions? options = null, List<CalendarDefect>? defects = null)
    {
        options ??= ContentLineReaderOptions.Default;
        _data = data;
        _maxLine = options.MaxLineBytes;
        _qp = options.QuotedPrintableSoftBreaks;
        _b64 = options.Base64Continuation;
        _defects = defects;
        _pos = 0;
        _buffer = null;
        _reportedLf = false;
        _reportedCr = false;
        _reportedDoubleCr = false;
        Depth = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            _pos = 3;
            Report(CalendarDefectKind.ByteOrderMark, 0, 3);
        }
    }

    /// <summary>The component depth recorded in the defects this reader reports; the caller keeps it current. Default 0.</summary>
    public int Depth { get; set; }

    /// <summary>Offset of the next unread byte.</summary>
    public readonly long Position => _pos;

    private readonly void Report(CalendarDefectKind kind, long offset, int length) =>
        _defects?.Add(new CalendarDefect(kind, offset, length, Depth));

    // Finds the end of the physical line starting at 'from'. A line ends at CRLF, LF or a lone CR (RFC 5545 §3.1 says
    // CRLF; LF and CR come from clients that write native line ends).
    private int FindEol(int from, out int next)
    {
        ReadOnlySpan<byte> d = _data;
        int rel = d[from..].IndexOfAny((byte)'\r', (byte)'\n');
        if (rel < 0)
        {
            next = d.Length;
            return d.Length;
        }

        int ce = from + rel;
        if (d[ce] == '\r')
        {
            // A run of carriage returns then a line feed is one line end: a CRLF file passed through a text-mode copy comes out
            // CR CR LF, and six of the seven readers we compared join a fold after it (docs/ambiguities.md K1).
            int run = ce + 1;
            while (run < d.Length && d[run] == '\r')
            {
                run++;
            }

            if (run < d.Length && d[run] == '\n')
            {
                next = run + 1;
                if (run > ce + 1 && !_reportedDoubleCr)
                {
                    _reportedDoubleCr = true;
                    Report(CalendarDefectKind.AmbiguousDoubleCarriageReturn, ce, run + 1 - ce);
                }
            }
            else
            {
                next = ce + 1;
                if (!_reportedCr)
                {
                    _reportedCr = true;
                    Report(CalendarDefectKind.BareCarriageReturn, ce, 1);
                }
            }
        }
        else
        {
            next = ce + 1;
            if (!_reportedLf)
            {
                _reportedLf = true;
                Report(CalendarDefectKind.BareLineFeed, ce, 1);
            }
        }

        return ce;
    }

    /// <summary>Reads the next logical line.</summary>
    /// <param name="line">The line; valid until the next call.</param>
    /// <returns>False at the end of the input.</returns>
    public bool TryRead(out ContentLine line)
    {
        ReadOnlySpan<byte> d = _data;
        if (_pos >= d.Length)
        {
            line = default;
            return false;
        }

        int start = _pos;
        int contentStart = start;
        if (d[start] is (byte)' ' or (byte)'\t')
        {
            // A continuation with nothing before it: read the rest as a line of its own.
            contentStart++;
            Report(CalendarDefectKind.OrphanContinuation, start, 1);
        }

        int ce = FindEol(contentStart, out int next);
        int segStart = contentStart;
        int segEnd = ce;
        int bufLen = -1; // -1: the logical line is d[segStart..segEnd]; otherwise it is _buffer[..bufLen]
        bool folded = false;
        bool truncated = false;
        bool sawUnindentedBase64 = false;
        sbyte qpState = -1; // -1 unknown, 0 no, 1 yes
        sbyte b64State = -1;

        while (next < d.Length)
        {
            ReadOnlySpan<byte> cur = bufLen < 0 ? d[segStart..segEnd] : _buffer.AsSpan(0, bufLen);
            byte nb = d[next];
            int pieceStart;
            bool dropEquals = false;

            if (_qp && cur.Length > 0 && cur[^1] == (byte)'=' && IsEncoding(cur, ref qpState, "QUOTED-PRINTABLE", null))
            {
                // vCard 2.1: a quoted-printable line ending in '=' continues on the next line, whitespace and all.
                dropEquals = true;
                pieceStart = next;
                Report(CalendarDefectKind.QuotedPrintableSoftBreak, next, 0);
            }
            else if (nb is (byte)' ' or (byte)'\t')
            {
                // RFC 5545 §3.1: CRLF followed by one space or tab is removed.
                pieceStart = next + 1;
            }
            else if (_b64 && nb is not ((byte)'\r' or (byte)'\n') && IsBase64Line(next, out int ne) && IsEncoding(cur, ref b64State, "BASE64", "B"))
            {
                _ = ne;
                pieceStart = next;
                sawUnindentedBase64 = true;
                Report(CalendarDefectKind.Base64ContinuationWithoutFold, next, 0);
            }
            else if (_b64 && sawUnindentedBase64 && nb is (byte)'\r' or (byte)'\n')
            {
                // The empty line that ends an unindented base64 block belongs to the property.
                _ = FindEol(next, out int afterBlank);
                next = afterBlank;
                break;
            }
            else
            {
                break;
            }

            folded = true;
            int pe = FindEol(pieceStart, out int nnext);
            if (bufLen < 0)
            {
                int curLen = segEnd - segStart - (dropEquals ? 1 : 0);
                EnsureBuffer(Math.Min(curLen + (pe - pieceStart), _maxLine) + 16);
                d.Slice(segStart, Math.Min(curLen, _maxLine)).CopyTo(_buffer);
                bufLen = Math.Min(curLen, _maxLine);
                truncated |= curLen > _maxLine;
            }
            else if (dropEquals)
            {
                bufLen--;
            }

            int room = _maxLine - bufLen;
            int take = Math.Min(pe - pieceStart, room);
            if (take < pe - pieceStart)
            {
                truncated = true;
            }

            if (take > 0)
            {
                EnsureBuffer(bufLen + take + 16);
                d.Slice(pieceStart, take).CopyTo(_buffer.AsSpan(bufLen));
                bufLen += take;
            }

            ce = pe;
            next = nnext;
        }

        _pos = next;
        ReadOnlySpan<byte> logical;
        if (bufLen < 0)
        {
            logical = d[segStart..segEnd];
            if (logical.Length > _maxLine)
            {
                logical = logical[.._maxLine];
                truncated = true;
            }
        }
        else
        {
            logical = _buffer.AsSpan(0, bufLen);
        }

        if (truncated)
        {
            Report(CalendarDefectKind.LineTooLong, start, next - start);
        }

        ReadOnlySpan<byte> raw = d[start..next];
        if (logical.Length == 0 && !folded)
        {
            line = new ContentLine(ContentLineKind.Blank, start, raw, default, default, default, default, false, false);
            return true;
        }

        line = Classify(start, raw, logical, folded, truncated);
        return true;
    }

    private void EnsureBuffer(int size)
    {
        if (_buffer is null)
        {
            _buffer = new byte[Math.Max(size, 256)];
        }
        else if (_buffer.Length < size)
        {
            byte[] bigger = new byte[Math.Max(size, _buffer.Length * 2)];
            _buffer.CopyTo(bigger, 0);
            _buffer = bigger;
        }
    }

    // True when the physical line at 'at' is non-empty, holds no colon or semicolon and only base64 characters: the shape
    // of a vCard 2.1 base64 data line. A real content line always has a colon.
    private readonly bool IsBase64Line(int at, out int end)
    {
        ReadOnlySpan<byte> d = _data;
        int rel = d[at..].IndexOfAny((byte)'\r', (byte)'\n');
        end = rel < 0 ? d.Length : at + rel;
        if (end == at)
        {
            return false;
        }

        foreach (byte b in d[at..end])
        {
            if (!(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'+' or (byte)'/' or (byte)'=' or (byte)' ' or (byte)'\t'))
            {
                return false;
            }
        }

        return true;
    }

    // Does this (partial) logical line carry ENCODING=<a> (or the bare token <a> / <b>)? Cached in 'state'.
    private static bool IsEncoding(ReadOnlySpan<byte> logical, ref sbyte state, string a, string? b)
    {
        if (state >= 0)
        {
            return state == 1;
        }

        state = 0;
        if (!TryFindParameters(logical, out ReadOnlySpan<byte> parameters, out _, out _))
        {
            return false;
        }

        ContentParameterEnumerator e = new(parameters);
        while (e.MoveNext())
        {
            ContentParameterSpan p = e.Current;
            bool match;
            if (p.HasValue)
            {
                match = p.Name.Length == 8 && Ascii.EqualsIgnoreCase(p.Name, "ENCODING"u8)
                    && (EqualsAscii(Unquote(p.RawValue), a) || (b is not null && EqualsAscii(Unquote(p.RawValue), b)));
            }
            else
            {
                match = EqualsAscii(p.Name, a);
            }

            if (match)
            {
                state = 1;
                return true;
            }
        }

        return false;
    }

    private static bool EqualsAscii(ReadOnlySpan<byte> span, string text)
    {
        if (span.Length != text.Length)
        {
            return false;
        }

        for (int i = 0; i < span.Length; i++)
        {
            if (char.ToUpperInvariant((char)span[i]) != char.ToUpperInvariant(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<byte> Unquote(ReadOnlySpan<byte> v) =>
        v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;

    // Splits "name;params:value" and reports where the parameters are and where the value starts.
    internal static bool TryFindParameters(ReadOnlySpan<byte> logical, out ReadOnlySpan<byte> parameters, out int valueStart, out bool unterminatedQuote)
    {
        unterminatedQuote = false;
        parameters = default;
        valueStart = -1;
        int idx = logical.IndexOfAny((byte)';', (byte)':');
        if (idx < 0)
        {
            return false;
        }

        if (logical[idx] == ':')
        {
            valueStart = idx + 1;
            return true;
        }

        // Parameters: a colon inside double quotes does not end them.
        int j = idx + 1;
        bool inQuote = false;
        for (; j < logical.Length; j++)
        {
            byte c = logical[j];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == ':' && !inQuote)
            {
                break;
            }
        }

        if (j >= logical.Length)
        {
            if (!inQuote)
            {
                return false;
            }

            // An unclosed quote: read the quote as an ordinary character and take the first colon.
            unterminatedQuote = true;
            int colon = logical[(idx + 1)..].IndexOf((byte)':');
            if (colon < 0)
            {
                return false;
            }

            j = idx + 1 + colon;
        }

        parameters = logical[(idx + 1)..j];
        valueStart = j + 1;
        return true;
    }

    private readonly ContentLine Classify(long offset, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> logical, bool folded, bool truncated)
    {
        if (!TryFindParameters(logical, out ReadOnlySpan<byte> parameters, out int valueStart, out bool unterminated))
        {
            Report(CalendarDefectKind.MissingColon, offset, raw.Length);
            return new ContentLine(ContentLineKind.Malformed, offset, raw, default, default, default, logical, folded, truncated);
        }

        if (unterminated)
        {
            Report(CalendarDefectKind.UnterminatedQuote, offset, raw.Length);
        }

        int nameEnd = logical.IndexOfAny((byte)';', (byte)':');
        ReadOnlySpan<byte> full = logical[..nameEnd];
        ReadOnlySpan<byte> trimmed = full.TrimEnd((byte)' ').TrimEnd((byte)'\t');
        if (trimmed.Length != full.Length)
        {
            Report(CalendarDefectKind.WhitespaceAroundDelimiter, offset + nameEnd, full.Length - trimmed.Length);
        }

        ReadOnlySpan<byte> group = default;
        ReadOnlySpan<byte> name = trimmed;
        int dot = trimmed.LastIndexOf((byte)'.');
        if (dot > 0 && dot < trimmed.Length - 1)
        {
            group = trimmed[..dot];
            name = trimmed[(dot + 1)..];
        }

        if (name.Length == 0)
        {
            Report(CalendarDefectKind.EmptyName, offset, raw.Length);
            return new ContentLine(ContentLineKind.Malformed, offset, raw, default, default, default, logical, folded, truncated);
        }

        foreach (byte b in name)
        {
            if (!(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-'))
            {
                Report(CalendarDefectKind.InvalidNameCharacter, offset, name.Length);
                break;
            }
        }

        return new ContentLine(ContentLineKind.Property, offset, raw, group, name, parameters, logical[valueStart..], folded, truncated);
    }
}

/// <summary>One parameter as it sits in the input: a name and, unless it is a bare vCard 2.1 type, the raw value text.</summary>
public readonly ref struct ContentParameterSpan
{
    internal ContentParameterSpan(ReadOnlySpan<byte> name, ReadOnlySpan<byte> rawValue, bool hasValue)
    {
        Name = name;
        RawValue = rawValue;
        HasValue = hasValue;
    }

    /// <summary>The parameter name, or for a bare parameter the token itself (<c>WORK</c> in <c>TEL;WORK:</c>).</summary>
    public ReadOnlySpan<byte> Name { get; }

    /// <summary>Everything after the equals sign: one or more values separated by commas, possibly quoted. Empty for a bare parameter.</summary>
    public ReadOnlySpan<byte> RawValue { get; }

    /// <summary>False for a bare parameter (vCard 2.1 types such as <c>HOME</c>, <c>QUOTED-PRINTABLE</c>).</summary>
    public bool HasValue { get; }

    /// <summary>Enumerates the comma-separated values of <see cref="RawValue"/>.</summary>
    /// <returns>An enumerator.</returns>
    public ContentParameterValueEnumerator EnumerateValues() => new(RawValue);
}

/// <summary>Enumerates the parameters in the text between a line's first semicolon and its colon (RFC 5545 §3.2).</summary>
public ref struct ContentParameterEnumerator
{
    private ReadOnlySpan<byte> _rest;
    private ContentParameterSpan _current;

    /// <summary>Starts enumerating.</summary>
    /// <param name="parameters">The parameter text, without the leading semicolon.</param>
    public ContentParameterEnumerator(ReadOnlySpan<byte> parameters)
    {
        _rest = parameters;
        _current = default;
    }

    /// <summary>The current parameter.</summary>
    public readonly ContentParameterSpan Current => _current;

    /// <summary>Returns this enumerator, so it can be used in <c>foreach</c>.</summary>
    /// <returns>This enumerator.</returns>
    public readonly ContentParameterEnumerator GetEnumerator() => this;

    /// <summary>Moves to the next parameter; empty segments (<c>;;</c>) are skipped.</summary>
    /// <returns>False when there are no more.</returns>
    public bool MoveNext()
    {
        while (_rest.Length > 0)
        {
            int end = FindSegmentEnd(_rest);
            ReadOnlySpan<byte> seg = _rest[..end];
            _rest = end < _rest.Length ? _rest[(end + 1)..] : default;
            seg = Trim(seg);
            if (seg.Length == 0)
            {
                continue;
            }

            int eq = seg.IndexOf((byte)'=');
            if (eq < 0)
            {
                _current = new ContentParameterSpan(seg, default, false);
            }
            else
            {
                _current = new ContentParameterSpan(Trim(seg[..eq]), Trim(seg[(eq + 1)..]), true);
            }

            return true;
        }

        return false;
    }

    // A semicolon inside double quotes does not separate parameters; a quote that never closes is an ordinary character.
    private static int FindSegmentEnd(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (i < s.Length)
        {
            byte c = s[i];
            if (c == ';')
            {
                return i;
            }

            if (c == '"')
            {
                int close = s[(i + 1)..].IndexOf((byte)'"');
                if (close >= 0)
                {
                    i += close + 2;
                    continue;
                }
            }

            i++;
        }

        return s.Length;
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s) => s.Trim((byte)' ').Trim((byte)'\t');
}

/// <summary>Splits a parameter's raw value at commas outside quotes and strips the quotes.</summary>
public ref struct ContentParameterValueEnumerator
{
    private ReadOnlySpan<byte> _rest;
    private bool _finished;
    private ReadOnlySpan<byte> _current;
    private bool _quoted;

    /// <summary>Starts enumerating.</summary>
    /// <param name="rawValue">The text after the equals sign.</param>
    public ContentParameterValueEnumerator(ReadOnlySpan<byte> rawValue)
    {
        _rest = rawValue;
        _finished = false;
        _current = default;
        _quoted = false;
    }

    /// <summary>The current value without surrounding quotes.</summary>
    public readonly ReadOnlySpan<byte> Current => _current;

    /// <summary>True if the current value was quoted.</summary>
    public readonly bool IsQuoted => _quoted;

    /// <summary>Returns this enumerator, so it can be used in <c>foreach</c>.</summary>
    /// <returns>This enumerator.</returns>
    public readonly ContentParameterValueEnumerator GetEnumerator() => this;

    /// <summary>Moves to the next value. An empty raw value yields one empty value (<c>X=</c>).</summary>
    /// <returns>False when there are no more.</returns>
    public bool MoveNext()
    {
        if (_finished)
        {
            return false;
        }

        ReadOnlySpan<byte> s = _rest;
        int i = 0;
        int end = s.Length;
        bool hasMore = false;
        while (i < s.Length)
        {
            byte c = s[i];
            if (c == ',')
            {
                end = i;
                hasMore = true;
                break;
            }

            if (c == '"')
            {
                int close = s[(i + 1)..].IndexOf((byte)'"');
                if (close >= 0)
                {
                    i += close + 2;
                    continue;
                }
            }

            i++;
        }

        ReadOnlySpan<byte> v = s[..end].Trim((byte)' ').Trim((byte)'\t');
        _rest = hasMore ? s[(end + 1)..] : default;
        _finished = !hasMore; // "a," still owes one empty value

        _quoted = v.Length >= 2 && v[0] == '"' && v[^1] == '"';
        _current = _quoted ? v[1..^1] : v;
        return true;
    }
}
