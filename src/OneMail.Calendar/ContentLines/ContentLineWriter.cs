using System.Text;

namespace RecursiveAI.OneMail.Calendar.ContentLines;

/// <summary>Options for <see cref="ContentLineWriter"/>.</summary>
public sealed record ContentLineWriterOptions
{
    /// <summary>The defaults: fold at 75 octets, CRLF, caret encoding, UTF-8.</summary>
    public static ContentLineWriterOptions Default { get; } = new();

    /// <summary>
    /// Longest physical line in octets, line end excluded (RFC 5545 §3.1 says 75). Zero writes lines unfolded.
    /// Folds never split a UTF-8 sequence. Default 75.
    /// </summary>
    public int FoldWidth { get; init; } = 75;

    /// <summary>The line end written after each line. Default CRLF.</summary>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>Encode <c>^</c>, line breaks and double quotes in parameter values with RFC 6868 carets; when false a double quote becomes an apostrophe. Default true.</summary>
    public bool CaretEncoding { get; init; } = true;

    /// <summary>The encoding for values; null means UTF-8. vCard 2.1 text with a <c>CHARSET</c> uses that charset.</summary>
    public Encoding? ValueEncoding { get; init; }
}

/// <summary>
/// Streaming writer of content lines. It folds long lines, quotes parameter values that need it and encodes them per
/// RFC 6868. It writes what it is given: it does not escape values (see <see cref="ContentValues.Escape"/>).
/// </summary>
public sealed class ContentLineWriter
{
    private readonly Stream _output;
    private readonly ContentLineWriterOptions _options;
    private readonly byte[] _newLine;
    private byte[] _line = new byte[256];
    private int _len;

    /// <summary>Creates a writer.</summary>
    /// <param name="output">Where the bytes go. Wrap it in a buffered stream for many small lines.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    public ContentLineWriter(Stream output, ContentLineWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _options = options ?? ContentLineWriterOptions.Default;
        _newLine = Encoding.ASCII.GetBytes(_options.NewLine);
    }

    /// <summary>Writes <c>BEGIN:name</c>.</summary>
    /// <param name="component">The component name.</param>
    public void WriteBegin(string component) => WriteLine(null, "BEGIN", null, component);

    /// <summary>Writes <c>END:name</c>.</summary>
    /// <param name="component">The component name.</param>
    public void WriteEnd(string component) => WriteLine(null, "END", null, component);

    /// <summary>Writes one content line, folded.</summary>
    /// <param name="group">The group (<c>item1</c>), or null.</param>
    /// <param name="name">The property name.</param>
    /// <param name="parameters">The parameters, or null.</param>
    /// <param name="value">The value, already escaped; it must not contain a line break.</param>
    /// <param name="valueEncoding">The encoding for the value, or null for the writer's default (UTF-8).</param>
    /// <exception cref="ArgumentException">The name is empty or has a delimiter, or the value has a line break.</exception>
    public void WriteLine(string? group, string name, IReadOnlyList<ContentParameter>? parameters, string value, Encoding? valueEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (name.Length == 0 || name.AsSpan().IndexOfAny(";:\r\n") >= 0 || (group is not null && group.AsSpan().IndexOfAny(";:\r\n") >= 0))
        {
            throw new ArgumentException("The name or group is empty or contains a delimiter.", nameof(name));
        }

        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("A value cannot contain a line break; escape it first.", nameof(value));
        }

        _len = 0;
        if (!string.IsNullOrEmpty(group))
        {
            Append(group);
            Append(".");
        }

        Append(name);
        if (parameters is not null)
        {
            foreach (ContentParameter p in parameters)
            {
                Append(";");
                Append(p.Name);
                if (!p.IsBare)
                {
                    Append("=");
                    for (int i = 0; i < p.Values.Count; i++)
                    {
                        if (i > 0)
                        {
                            Append(",");
                        }

                        Append(EncodeParameterValue(p.Values[i]));
                    }
                }
            }
        }

        Append(":");
        Encoding enc = valueEncoding ?? _options.ValueEncoding ?? Encoding.UTF8;
        int max = enc.GetMaxByteCount(value.Length);
        Grow(_len + max);
        _len += enc.GetBytes(value, 0, value.Length, _line, _len);
        FlushLine(enc.CodePage == Encoding.UTF8.CodePage);
    }

    /// <summary>Writes bytes as they are (a stored line, a byte order mark).</summary>
    /// <param name="raw">The bytes.</param>
    public void WriteRaw(ReadOnlySpan<byte> raw) => _output.Write(raw);

    /// <summary>Writes the configured line end.</summary>
    public void WriteLineEnd() => _output.Write(_newLine);

    private string EncodeParameterValue(string v)
    {
        string t = _options.CaretEncoding ? ContentValues.EncodeCaret(v) : v.Replace('"', '\'').Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        bool quote = false;
        if (t.Length > 0 && (t.AsSpan().IndexOfAny(",;:") >= 0 || t[0] == ' ' || t[^1] == ' '))
        {
            quote = true;
        }

        return quote ? "\"" + t + "\"" : t;
    }

    private void Append(string s)
    {
        Grow(_len + (s.Length * 3) + 4);
        _len += Encoding.UTF8.GetBytes(s, 0, s.Length, _line, _len);
    }

    private void Grow(int size)
    {
        if (_line.Length < size)
        {
            Array.Resize(ref _line, Math.Max(size, _line.Length * 2));
        }
    }

    // RFC 5545 §3.1: lines are folded by inserting CRLF and one whitespace octet; a fold must not split a multi-byte
    // UTF-8 sequence.
    private void FlushLine(bool utf8)
    {
        ReadOnlySpan<byte> l = _line.AsSpan(0, _len);
        int width = _options.FoldWidth;
        int pos = 0;
        int limit = width;
        while (true)
        {
            int take = width <= 0 ? l.Length - pos : Math.Min(limit, l.Length - pos);
            if (pos + take < l.Length && utf8)
            {
                int t = take;
                while (t > 0 && (l[pos + t] & 0xC0) == 0x80)
                {
                    t--;
                }

                if (t > 0)
                {
                    take = t;
                }
            }

            if (pos + take < l.Length && take > 1 && l[pos + take - 1] == (byte)'=')
            {
                // A physical line ending in '=' is a quoted-printable soft break to a vCard 2.1 reader, so the fold moves one octet earlier.
                take--;
            }

            _output.Write(l.Slice(pos, take));
            pos += take;
            if (pos >= l.Length)
            {
                break;
            }

            _output.Write(_newLine);
            _output.WriteByte((byte)' ');
            limit = width - 1;
        }

        _output.Write(_newLine);
    }
}
