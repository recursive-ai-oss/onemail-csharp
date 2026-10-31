namespace RecursiveAI.OneMail.Rtf;

/// <summary>The kind of token an <see cref="RtfReader"/> is positioned on.</summary>
public enum RtfTokenKind
{
    /// <summary>No token: <see cref="RtfReader.Read"/> has not been called yet, or the input is exhausted.</summary>
    None = 0,

    /// <summary><c>{</c>.</summary>
    GroupStart,

    /// <summary><c>}</c>.</summary>
    GroupEnd,

    /// <summary>A control word such as <c>\par</c> or <c>\uc1</c>: <see cref="RtfReader.Name"/> and the optional parameter.</summary>
    ControlWord,

    /// <summary>
    /// A control symbol: a backslash and one non-letter byte (<c>\*</c>, <c>\~</c>, <c>\{</c>, <c>\\</c>…).
    /// <see cref="RtfReader.Name"/> is that one byte. A backslash before a line break is reported with the line-break byte.
    /// </summary>
    ControlSymbol,

    /// <summary><c>\'hh</c>: <see cref="RtfReader.HexValue"/> is the byte.</summary>
    HexEscape,

    /// <summary>A run of text bytes (never containing <c>\</c>, <c>{</c>, <c>}</c>, CR or LF): <see cref="RtfReader.Text"/>.</summary>
    Text,

    /// <summary>The raw data after a <c>\bin</c> control word: <see cref="RtfReader.Text"/>.</summary>
    Binary,
}

/// <summary>Limits for <see cref="RtfReader"/>.</summary>
public sealed class RtfReaderOptions
{
    /// <summary>The deepest group nesting accepted. Default 1000. Reading stops there with <see cref="RtfDefectKind.NestingLimitExceeded"/>.</summary>
    public int MaxGroupDepth { get; init; } = 1000;
}

/// <summary>
/// A forward-only RTF tokenizer over a byte span: groups, control words and symbols, hex escapes, text runs and
/// <c>\bin</c> data, with the byte offset of each token. It follows the RTF specification's lexical rules: a control
/// word is letters, an optional signed number, and one optional space that belongs to the word; carriage returns and
/// line feeds in the file are not text. It never throws on malformed input; <see cref="Defects"/> lists what it repaired.
/// </summary>
/// <remarks>It does not interpret anything (no code pages, destinations or Unicode skipping): that is the caller's job.</remarks>
public ref struct RtfReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _maxDepth;
    private readonly RtfDefectList _defects;
    private int _pos;
    private int _depth;
    private bool _stopped;
    private bool _reportedUnclosed;
    private int _binaryPending; // byte count of the \bin data to deliver next, or -1

    /// <summary>Creates a reader positioned before the first token.</summary>
    /// <param name="data">The RTF bytes.</param>
    /// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
    public RtfReader(ReadOnlySpan<byte> data, RtfReaderOptions? options = null)
    {
        _data = data;
        _maxDepth = options?.MaxGroupDepth ?? 1000;
        _defects = new RtfDefectList();
        _binaryPending = -1;
        Name = default;
        Text = default;
    }

    /// <summary>The kind of the current token.</summary>
    public RtfTokenKind Kind { get; private set; }

    /// <summary>The offset of the current token's first byte (its backslash, brace or first text byte).</summary>
    public int Offset { get; private set; }

    /// <summary>The offset just after the current token, including the space that ended a control word.</summary>
    public int End => _pos;

    /// <summary>The group depth after the current token: 0 outside the outermost group, 1 inside it.</summary>
    public int Depth => _depth;

    /// <summary>The control word's letters, or the control symbol's one byte.</summary>
    public ReadOnlySpan<byte> Name { get; private set; }

    /// <summary><see langword="true"/> when the control word has a numeric parameter.</summary>
    public bool HasParameter { get; private set; }

    /// <summary>The control word's parameter (clamped to the <see cref="int"/> range); 0 when there is none.</summary>
    public int Parameter { get; private set; }

    /// <summary>The byte of a <see cref="RtfTokenKind.HexEscape"/>.</summary>
    public byte HexValue { get; private set; }

    /// <summary>The bytes of a <see cref="RtfTokenKind.Text"/> or <see cref="RtfTokenKind.Binary"/> token.</summary>
    public ReadOnlySpan<byte> Text { get; private set; }

    /// <summary>Repairs made so far; complete once <see cref="Read"/> has returned <see langword="false"/>.</summary>
    public readonly IReadOnlyList<RtfDefect> Defects => _defects.Items;

    /// <summary>Advances to the next token.</summary>
    /// <returns><see langword="false"/> at the end of the input (or after the nesting limit), with <see cref="Kind"/> <see cref="RtfTokenKind.None"/>.</returns>
    public bool Read()
    {
        Name = default;
        Text = default;
        HasParameter = false;
        Parameter = 0;

        if (_stopped)
        {
            Kind = RtfTokenKind.None;
            return false;
        }

        if (_binaryPending >= 0)
        {
            int n = _binaryPending;
            _binaryPending = -1;
            if (n > 0)
            {
                Offset = _pos;
                Text = _data.Slice(_pos, n);
                _pos += n;
                Kind = RtfTokenKind.Binary;
                return true;
            }
        }

        // CR and LF between tokens carry no meaning in RTF.
        while (_pos < _data.Length && (_data[_pos] == '\r' || _data[_pos] == '\n'))
        {
            _pos++;
        }

        if (_pos >= _data.Length)
        {
            if (_depth > 0 && !_reportedUnclosed)
            {
                _reportedUnclosed = true;
                _defects.Add(RtfDefectKind.UnclosedGroups, _pos, _depth + " group(s) not closed");
            }

            Kind = RtfTokenKind.None;
            _stopped = true;
            return false;
        }

        Offset = _pos;
        byte c = _data[_pos];
        switch (c)
        {
            case (byte)'{':
                if (_depth >= _maxDepth)
                {
                    _defects.Add(RtfDefectKind.NestingLimitExceeded, _pos, "group nesting limit reached");
                    _stopped = true;
                    Kind = RtfTokenKind.None;
                    return false;
                }

                _pos++;
                _depth++;
                Kind = RtfTokenKind.GroupStart;
                return true;
            case (byte)'}':
                _pos++;
                if (_depth == 0)
                {
                    _defects.Add(RtfDefectKind.UnbalancedGroupEnd, Offset, "} without {");
                }
                else
                {
                    _depth--;
                }

                Kind = RtfTokenKind.GroupEnd;
                return true;
            case (byte)'\\':
                ReadControl();
                return true;
            default:
                int start = _pos;
                while (_pos < _data.Length && _data[_pos] is not ((byte)'\\' or (byte)'{' or (byte)'}' or (byte)'\r' or (byte)'\n'))
                {
                    _pos++;
                }

                Text = _data[start.._pos];
                Kind = RtfTokenKind.Text;
                return true;
        }
    }

    // RTF 1.9.1, "Control words": \ + letters + [-]digits + optional delimiting space. "Control symbols": \ + one non-letter.
    private void ReadControl()
    {
        int start = _pos;
        _pos++; // the backslash
        if (_pos >= _data.Length)
        {
            _defects.Add(RtfDefectKind.TruncatedToken, start, "backslash at the end of the input");
            Kind = RtfTokenKind.ControlSymbol;
            Name = _data.Slice(start, 1);
            return;
        }

        byte c = _data[_pos];
        if (IsLetter(c))
        {
            int nameStart = _pos;
            while (_pos < _data.Length && IsLetter(_data[_pos]))
            {
                _pos++;
            }

            Name = _data[nameStart.._pos];
            Kind = RtfTokenKind.ControlWord;
            int p = _pos;
            bool negative = false;
            if (p < _data.Length && _data[p] == '-')
            {
                negative = true;
                p++;
            }

            if (p < _data.Length && IsDigit(_data[p]))
            {
                long value = 0;
                bool overflow = false;
                while (p < _data.Length && IsDigit(_data[p]))
                {
                    if (value <= int.MaxValue)
                    {
                        value = (value * 10) + (_data[p] - '0');
                    }
                    else
                    {
                        overflow = true;
                    }

                    p++;
                }

                if (overflow || value > int.MaxValue)
                {
                    _defects.Add(RtfDefectKind.ParameterOverflow, start, "control word parameter does not fit in 32 bits");
                    value = int.MaxValue;
                }

                HasParameter = true;
                Parameter = negative ? (int)-value : (int)value;
                _pos = p;
            }

            if (_pos < _data.Length && _data[_pos] == ' ')
            {
                _pos++; // the one space that ends a control word is part of it
            }

            if (HasParameter && Name.SequenceEqual("bin"u8))
            {
                // RTF 1.9.1: \binN is followed by N bytes of raw data that are not parsed.
                int available = _data.Length - _pos;
                int n = Parameter;
                if (n < 0 || n > available)
                {
                    _defects.Add(RtfDefectKind.BinaryDataTruncated, start, "\\bin length is negative or past the end of the input");
                    n = n < 0 ? 0 : available;
                }

                _binaryPending = n;
            }

            return;
        }

        Kind = RtfTokenKind.ControlSymbol;
        Name = _data.Slice(_pos, 1);
        _pos++;
        if (c == '\'')
        {
            if (_pos + 1 < _data.Length && HexDigit(_data[_pos]) >= 0 && HexDigit(_data[_pos + 1]) >= 0)
            {
                HexValue = (byte)((HexDigit(_data[_pos]) << 4) | HexDigit(_data[_pos + 1]));
                _pos += 2;
                Kind = RtfTokenKind.HexEscape;
                Name = default;
            }
            else
            {
                _defects.Add(RtfDefectKind.InvalidHexEscape, start, "\\' without two hexadecimal digits");
            }
        }
        else if (c == '\r' && _pos < _data.Length && _data[_pos] == '\n')
        {
            _pos++; // a backslash before CRLF is one \par
        }
    }

    private static bool IsLetter(byte b) => (uint)((b | 0x20) - 'a') <= 'z' - 'a';

    private static bool IsDigit(byte b) => (uint)(b - '0') <= 9;

    private static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
