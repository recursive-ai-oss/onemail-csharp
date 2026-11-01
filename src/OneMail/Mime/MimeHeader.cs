using System.Text;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Mime;

/// <summary>
/// Header text decoding. This is the one place the tree turns header bytes into text: unstructured values go through
/// RFC 2047 (<see cref="Rfc2047"/>), which also reads undeclared 8-bit text as UTF-8 when valid and windows-1252 otherwise.
/// </summary>
internal static class HeaderText
{
    /// <summary>Unfolds (RFC 5322 §2.2.3), decodes encoded words, trims. A lone CR is data, not a line end, and is dropped from the text.</summary>
    public static string Decode(ReadOnlySpan<byte> rawValue, List<TextIssue>? issues = null)
    {
        string s = Rfc2047.DecodeText(rawValue, issues, EncodedWordContext.Unstructured);
        if (s.Contains('\r', StringComparison.Ordinal))
        {
            s = s.Replace("\r", string.Empty, StringComparison.Ordinal);
        }

        return s.Trim(' ', '\t');
    }

    /// <summary>Header names and media types are ASCII; a byte above 0x7F in one is kept as the Latin-1 character so that nothing is lost.</summary>
    public static string DecodeName(ReadOnlySpan<byte> name) => Encoding.Latin1.GetString(name);
}

/// <summary>One header field of an entity, kept as the bytes it was read from until it is changed.</summary>
public sealed class MimeHeader
{
    private byte[] _raw;
    private int _nameLength;
    private int _valueOffset;
    private int _valueLength;
    private string? _name;

    /// <summary>Creates a new header field. It is written folded, with CRLF.</summary>
    /// <param name="name">The field name: printable ASCII without a colon.</param>
    /// <param name="value">The value as one logical line (no line breaks).</param>
    /// <exception cref="ArgumentException">The name or the value is not allowed; see <see cref="Writing.MimeWriter.WriteHeader(string, string)"/>.</exception>
    public MimeHeader(string name, string value)
        : this(name, value, "\r\n"u8)
    {
    }

    internal MimeHeader(string name, string value, ReadOnlySpan<byte> newline, bool utf8Text = false)
    {
        // utf8Text keeps non-ASCII free text as raw UTF-8 (RFC 6532) instead of encoded-words.
        _raw = Writing.MimeWriterSupport.FormatHeader(name, value, newline, 78, encodeFreeText: !utf8Text);
        Reparse(name.Length);
    }

    internal MimeHeader(byte[] raw, int nameLength, int valueOffset, int valueLength)
    {
        _raw = raw;
        _nameLength = nameLength;
        _valueOffset = valueOffset;
        _valueLength = valueLength;
    }

    internal MimeHeaderList? Owner { get; set; }

    /// <summary>Offset of the field in the parsed input, or -1 for a field that was created.</summary>
    public long SourceOffset { get; internal set; } = -1;

    /// <summary>Offset in the parsed input of the first byte after the colon, or -1 for a field that was created.</summary>
    public long ValueSourceOffset => SourceOffset < 0 ? -1 : SourceOffset + _valueOffset;

    /// <summary>The field name as written (case preserved), without surrounding whitespace.</summary>
    public string Name => _name ??= HeaderText.DecodeName(NameBytes());

    // A UTF-8 byte-order mark in front of the first field name is not part of the name (the raw bytes keep it).
    private ReadOnlySpan<byte> NameBytes()
    {
        ReadOnlySpan<byte> n = _raw.AsSpan(0, _nameLength);
        return n.Length > 3 && n.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? n[3..] : n;
    }

    /// <summary>The whole field exactly as read or generated: name, colon, value with folds, and line end.</summary>
    public ReadOnlyMemory<byte> Raw => _raw;

    /// <summary>The raw value after the colon, up to but not including the line end, with folds as written.</summary>
    public ReadOnlyMemory<byte> RawValue => _raw.AsMemory(_valueOffset, _valueLength);

    /// <summary>The value unfolded and trimmed, as text. Encoded words (RFC 2047) are not decoded at this layer.</summary>
    public string Value => HeaderText.Decode(_raw.AsSpan(_valueOffset, _valueLength));

    /// <summary>Like <see cref="Value"/>, and reports what decoding repaired or guessed.</summary>
    /// <param name="defects">Receives one defect per repair, with offsets in the parsed input (0-based from the value for fields that were created).</param>
    /// <param name="depth">The nesting depth to record in the defects.</param>
    /// <returns>The decoded text.</returns>
    public string GetValue(List<MimeDefect> defects, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(defects);
        var issues = new List<TextIssue>();
        string s = HeaderText.Decode(_raw.AsSpan(_valueOffset, _valueLength), issues);
        HeaderDecoding.Report(issues, defects, Math.Max(0, ValueSourceOffset), depth);
        return s;
    }

    /// <summary>Whether the field name equals <paramref name="name"/>, ignoring ASCII case.</summary>
    /// <param name="name">The name to compare.</param>
    /// <returns>True when they match.</returns>
    public bool IsNamed(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Replaces the value, regenerating the field's bytes (folded, with the same line end style). The name keeps its case.</summary>
    /// <param name="value">The new value, one logical line.</param>
    /// <exception cref="ArgumentException">The value contains a line break.</exception>
    public void SetValue(string value)
    {
        ReadOnlySpan<byte> nl = _raw.AsSpan().EndsWith("\r\n"u8) || !_raw.AsSpan().EndsWith("\n"u8) ? "\r\n"u8 : "\n"u8;
        _raw = Writing.MimeWriterSupport.FormatHeader(Name, value, nl, 78);
        Reparse(_nameLength);
        Owner?.Changed();
    }

    internal void EnsureLineEnd(ReadOnlySpan<byte> newline)
    {
        if (_raw.Length == 0 || _raw[^1] != (byte)'\n')
        {
            byte[] r = new byte[_raw.Length + newline.Length];
            _raw.CopyTo(r, 0);
            newline.CopyTo(r.AsSpan(_raw.Length));
            _raw = r;
        }
    }

    private void Reparse(int nameLength)
    {
        _nameLength = nameLength;
        _valueOffset = nameLength + 1;
        int end = _raw.Length;
        if (end > 0 && _raw[end - 1] == (byte)'\n')
        {
            end--;
            if (end > 0 && _raw[end - 1] == (byte)'\r')
            {
                end--;
            }
        }

        _valueLength = end - _valueOffset;
        _name = null;
    }

    /// <summary>The field as <c>Name: value</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Name + ": " + Value;
}

/// <summary>The header fields of an entity, in order, duplicates included. Any change marks the entity (and its parents) modified.</summary>
public sealed class MimeHeaderList : IReadOnlyList<MimeHeader>
{
    private readonly List<MimeHeader> _items = [];
    private readonly MimeEntity _owner;

    internal MimeHeaderList(MimeEntity owner) => _owner = owner;

    /// <inheritdoc/>
    public int Count => _items.Count;

    /// <inheritdoc/>
    public MimeHeader this[int index] => _items[index];

    /// <summary>The first field named <paramref name="name"/> (ASCII case-insensitive), or null.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The field or null.</returns>
    public MimeHeader? GetFirst(string name)
    {
        foreach (MimeHeader h in _items)
        {
            if (h.IsNamed(name))
            {
                return h;
            }
        }

        return null;
    }

    /// <summary>All fields named <paramref name="name"/>, in order.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The fields.</returns>
    public IEnumerable<MimeHeader> GetAll(string name) => _items.Where(h => h.IsNamed(name));

    /// <summary>Appends a field.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value, one logical line.</param>
    /// <returns>The new field.</returns>
    public MimeHeader Add(string name, string value) => Add(new MimeHeader(name, value, LineEnd()));

    /// <summary>Appends a field. The previous last field gets a line end if it lacked one (a message cut off mid-header).</summary>
    /// <param name="header">The field; it must not belong to a list already.</param>
    /// <returns>The field.</returns>
    public MimeHeader Add(MimeHeader header) => Insert(_items.Count, header);

    /// <summary>Inserts a field at <paramref name="index"/>.</summary>
    /// <param name="index">Position.</param>
    /// <param name="header">The field; it must not belong to a list already.</param>
    /// <returns>The field.</returns>
    public MimeHeader Insert(int index, MimeHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.Owner is not null)
        {
            throw new ArgumentException("The header already belongs to a header list.", nameof(header));
        }

        if (index == _items.Count && index > 0)
        {
            _items[index - 1].EnsureLineEnd(LineEnd());
        }

        header.Owner = this;
        _items.Insert(index, header);
        Changed();
        return header;
    }

    /// <summary>Removes a field.</summary>
    /// <param name="header">The field.</param>
    /// <returns>True if it was in the list.</returns>
    public bool Remove(MimeHeader header)
    {
        if (!_items.Remove(header))
        {
            return false;
        }

        header.Owner = null;
        Changed();
        return true;
    }

    /// <summary>Removes every field named <paramref name="name"/>.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>How many were removed.</returns>
    public int RemoveAll(string name)
    {
        int n = 0;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].IsNamed(name))
            {
                _items[i].Owner = null;
                _items.RemoveAt(i);
                n++;
            }
        }

        if (n > 0)
        {
            Changed();
        }

        return n;
    }

    /// <summary>Sets a field: changes the first one named <paramref name="name"/> in place, removes the other duplicates, or appends one.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value, one logical line.</param>
    public void Set(string name, string value)
    {
        MimeHeader? first = GetFirst(name);
        if (first is null)
        {
            Add(name, value);
            return;
        }

        first.SetValue(value);
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i] != first && _items[i].IsNamed(name))
            {
                _items[i].Owner = null;
                _items.RemoveAt(i);
            }
        }
    }

    /// <inheritdoc/>
    public IEnumerator<MimeHeader> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

    internal void AddParsed(MimeHeader h)
    {
        h.Owner = this;
        _items.Add(h);
    }

    internal void Changed() => _owner.MarkModified();

    private ReadOnlySpan<byte> LineEnd()
    {
        if (_items.Count > 0)
        {
            ReadOnlyMemory<byte> raw = _items[^1].Raw;
            if (raw.Span.EndsWith("\r\n"u8))
            {
                return "\r\n"u8;
            }

            if (raw.Span.EndsWith("\n"u8))
            {
                return "\n"u8;
            }
        }

        return "\r\n"u8;
    }
}
