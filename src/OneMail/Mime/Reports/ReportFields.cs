using System.Collections;
using System.Text;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Mime.Reports;

/// <summary>
/// One <c>Name: value</c> line of a report body (RFC 3464 §2.1.1, RFC 3798, RFC 5965): a header-style field, possibly folded.
/// </summary>
public sealed class ReportField
{
    private readonly ReadOnlyMemory<byte> _rawValue;
    private string? _value;

    internal ReportField(string name, ReadOnlyMemory<byte> rawValue, long offset)
    {
        Name = name;
        _rawValue = rawValue;
        Offset = offset;
    }

    internal ReportField(string name, string value)
    {
        Name = name;
        _value = value;
        _rawValue = Encoding.UTF8.GetBytes(value);
        Offset = -1;
    }

    /// <summary>The field name as written.</summary>
    public string Name { get; }

    /// <summary>
    /// The value as written after the colon, folds included, without the final line end. Empty for a field created in code
    /// from text (use <see cref="Value"/>).
    /// </summary>
    public ReadOnlyMemory<byte> RawValue => _rawValue;

    /// <summary>
    /// The value unfolded (RFC 5322 §2.2.3: a line break before white space is removed) and trimmed, as text. Bytes that are
    /// valid UTF-8 are read as UTF-8 (RFC 6533); anything else is read as windows-1252. Encoded words (RFC 2047) are not decoded:
    /// the report formats do not use them.
    /// </summary>
    public string Value => _value ??= ReportText.Unfold(_rawValue.Span);

    /// <summary>Offset of the field's first byte in the report body it was read from, or -1 for a field created in code.</summary>
    public long Offset { get; }

    /// <summary>Whether the field name equals <paramref name="name"/>, ignoring ASCII case.</summary>
    /// <param name="name">The name to compare.</param>
    /// <returns>True when they match.</returns>
    public bool IsNamed(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string ToString() => Name + ": " + Value;
}

/// <summary>
/// A block of report fields: the run of field lines between blank lines. A <c>message/delivery-status</c> body is one per-message
/// block followed by one block per recipient (RFC 3464 §2.1); an MDN or feedback report is one block.
/// </summary>
/// <remarks>
/// A block read from a body is a copy of what was read; changing it does not change the tree. A new block is built with
/// <see cref="Add(string, string)"/> and written with <see cref="ToBytes(string)"/>.
/// </remarks>
public sealed class ReportFieldBlock : IReadOnlyList<ReportField>
{
    private readonly List<ReportField> _fields = [];

    /// <summary>Creates an empty block.</summary>
    public ReportFieldBlock()
    {
    }

    /// <summary>Offset of the block's first field in the body it was read from, or -1 for a block created in code.</summary>
    public long Offset { get; internal set; } = -1;

    /// <inheritdoc/>
    public int Count => _fields.Count;

    /// <inheritdoc/>
    public ReportField this[int index] => _fields[index];

    /// <summary>The value of the first field called <paramref name="name"/> (ASCII case-insensitive), or null.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The unfolded, trimmed value, or null when the field is absent.</returns>
    public string? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (ReportField f in _fields)
        {
            if (f.IsNamed(name))
            {
                return f.Value;
            }
        }

        return null;
    }

    /// <summary>The values of every field called <paramref name="name"/>, in order.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The values; empty when there is none.</returns>
    public IReadOnlyList<string> GetAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var r = new List<string>();
        foreach (ReportField f in _fields)
        {
            if (f.IsNamed(name))
            {
                r.Add(f.Value);
            }
        }

        return r;
    }

    /// <summary>Whether a field called <paramref name="name"/> exists.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>True when it does.</returns>
    public bool Contains(string name) => Get(name) is not null;

    /// <summary>Appends a field.</summary>
    /// <param name="name">Printable ASCII without a colon.</param>
    /// <param name="value">The value as one logical line. It may hold any text (RFC 6533 allows UTF-8).</param>
    /// <exception cref="ArgumentException">The name is empty or has a character that cannot be in a name, or the value holds a line break.</exception>
    public void Add(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (name.Length == 0 || name.Any(c => c <= ' ' || c > '~' || c == ':'))
        {
            throw new ArgumentException("A field name must be printable ASCII without a colon.", nameof(name));
        }

        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("A field value cannot contain a line break; it would start a new field.", nameof(value));
        }

        _fields.Add(new ReportField(name, value.Trim(' ', '\t')));
    }

    internal void AddParsed(ReportField f) => _fields.Add(f);

    /// <summary>Whether any field holds a character above U+007F, so the block needs the <c>global</c> media types of RFC 6533.</summary>
    public bool HasNonAscii => _fields.Any(f => !Ascii.IsValid(f.Value));

    /// <summary>
    /// Writes the block: each field as <c>Name: value</c>, folded at white space to 78 columns, followed by the blank line that ends
    /// the block. Non-ASCII text is written as UTF-8 (RFC 6533).
    /// </summary>
    /// <param name="newline">The line end; default CRLF.</param>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes(string newline = "\r\n")
    {
        using var ms = new MemoryStream();
        WriteFields(ms, newline);
        ms.Write(Encoding.ASCII.GetBytes(newline));
        return ms.ToArray();
    }

    internal void WriteFields(Stream to, string newline)
    {
        byte[] nl = Encoding.ASCII.GetBytes(newline);
        foreach (ReportField f in _fields)
        {
            byte[] value = Encoding.UTF8.GetBytes(f.Value);
            byte[] head = Encoding.ASCII.GetBytes(f.Name + ": ");
            to.Write(head);
            int col = head.Length;
            int i = 0;
            bool first = true;
            while (i < value.Length)
            {
                int j = i;
                while (j < value.Length && value[j] is not ((byte)' ' or (byte)'\t'))
                {
                    j++;
                }

                int wordLen = j - i;
                if (!first)
                {
                    if (col + 1 + wordLen > 78)
                    {
                        // RFC 5322 §2.2.3: a fold is a line break before existing white space.
                        to.Write(nl);
                        to.WriteByte((byte)' ');
                        col = 1;
                    }
                    else
                    {
                        to.WriteByte((byte)' ');
                        col++;
                    }
                }

                to.Write(value.AsSpan(i, wordLen));
                col += wordLen;
                first = false;
                while (j < value.Length && value[j] is (byte)' ' or (byte)'\t')
                {
                    j++;
                }

                i = j;
            }

            to.Write(nl);
        }
    }

    /// <inheritdoc/>
    public IEnumerator<ReportField> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _fields.GetEnumerator();
}

/// <summary>
/// Reads the header-style bodies of the report media types into <see cref="ReportFieldBlock"/>s. Lenient: nothing here throws on
/// malformed bytes; what was repaired is a typed defect with the byte offset in the body.
/// </summary>
public static class ReportFieldParser
{
    /// <summary>The most fields read from one body. Further lines are ignored and reported as <see cref="MimeDefectKind.ReportLimitExceeded"/>.</summary>
    public const int MaxFields = 100_000;

    /// <summary>The most bytes read from one report part by the typed readers (<see cref="DeliveryStatusReport.From(MimePart, List{MimeDefect}?)"/> and the others).</summary>
    public const int MaxBodyBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Splits a body into blocks of fields. Blocks are separated by one or more empty lines. A line that starts with a space or tab
    /// continues the field before it (RFC 5322 §2.2.3); a line with neither a colon nor a fold is skipped
    /// (<see cref="MimeDefectKind.ReportMalformedLine"/>). Lines may end in CRLF or LF. A line holding only white space is a fold, not a blank
    /// line, as in a header block.
    /// </summary>
    /// <param name="body">The decoded body.</param>
    /// <param name="defects">Receives the defects; may be null. Offsets are in <paramref name="body"/>, plus <paramref name="baseOffset"/>.</param>
    /// <param name="baseOffset">Added to defect offsets and block offsets.</param>
    /// <param name="depth">The entity depth recorded in defects.</param>
    /// <param name="singleBlock">True for bodies that hold one block (a disposition notification, a feedback report). An empty first line then
    /// makes an empty first block and the fields after it a second one, which is how Python and MimeKit read it. For a delivery status
    /// (false) blank lines before the first block, after the last and between blocks are skipped, as MimeKit reads it, and
    /// <see cref="MimeDefectKind.AmbiguousReportBlankLines"/> is reported for the second and later blank line of a run.</param>
    /// <returns>The blocks in order; empty when the body has no field.</returns>
    public static List<ReportFieldBlock> Parse(ReadOnlyMemory<byte> body, List<MimeDefect>? defects = null, long baseOffset = 0, int depth = 0, bool singleBlock = false)
    {
        var blocks = new List<ReportFieldBlock>();
        ReadOnlySpan<byte> s = body.Span;
        ReportFieldBlock? block = null;
        int fieldCount = 0;
        bool blankRunReported = false;
        bool leadingBlankSeen = false;

        // Current field being accumulated.
        string? name = null;
        int fieldStart = 0;
        int valueStart = 0;
        int valueEnd = 0;

        void EndField()
        {
            if (name is not null && block is not null)
            {
                block.AddParsed(new ReportField(name, body.Slice(valueStart, valueEnd - valueStart), baseOffset + fieldStart));
            }

            name = null;
        }

        int pos = 0;
        while (pos < s.Length)
        {
            int eol = s[pos..].IndexOf((byte)'\n');
            int lineEnd = eol < 0 ? s.Length : pos + eol; // index of LF or end
            int next = eol < 0 ? s.Length : lineEnd + 1;
            int contentEnd = lineEnd > pos && s[lineEnd - 1] == (byte)'\r' && eol >= 0 ? lineEnd - 1 : lineEnd;

            if (contentEnd == pos)
            {
                // An empty line ends the field and the block.
                EndField();
                if (block is not null)
                {
                    blocks.Add(block);
                    block = null;
                    blankRunReported = false;
                }
                else if (singleBlock && blocks.Count == 0 && !leadingBlankSeen)
                {
                    blocks.Add(new ReportFieldBlock { Offset = baseOffset + pos });
                    leadingBlankSeen = true;
                }
                else if (!blankRunReported)
                {
                    // Python reads each extra blank line as an empty block; MimeKit skips them. OneMail skips them and says so.
                    defects?.Add(new MimeDefect(MimeDefectKind.AmbiguousReportBlankLines, baseOffset + pos, 0, depth));
                    blankRunReported = true;
                }

                pos = next;
                continue;
            }

            blankRunReported = false;

            if (s[pos] is (byte)' ' or (byte)'\t')
            {
                if (name is not null)
                {
                    valueEnd = contentEnd;
                }
                else
                {
                    defects?.Add(new MimeDefect(MimeDefectKind.ReportMalformedLine, baseOffset + pos, contentEnd - pos, depth));
                }

                pos = next;
                continue;
            }

            int colon = s[pos..contentEnd].IndexOf((byte)':');
            if (colon < 0)
            {
                EndField();
                defects?.Add(new MimeDefect(MimeDefectKind.ReportMalformedLine, baseOffset + pos, contentEnd - pos, depth));
                pos = next;
                continue;
            }

            EndField();
            if (fieldCount >= MaxFields)
            {
                defects?.Add(new MimeDefect(MimeDefectKind.ReportLimitExceeded, baseOffset + pos, s.Length - pos, depth));
                break;
            }

            int nameEnd = pos + colon;
            int trimmedEnd = nameEnd;
            while (trimmedEnd > pos && s[trimmedEnd - 1] is (byte)' ' or (byte)'\t')
            {
                trimmedEnd--;
            }

            if (trimmedEnd != nameEnd)
            {
                defects?.Add(new MimeDefect(MimeDefectKind.WhitespaceBeforeHeaderColon, baseOffset + pos, nameEnd - pos, depth));
            }

            ReadOnlySpan<byte> nameBytes = s[pos..trimmedEnd];
            if (nameBytes.Length == 0 || HasInvalidNameByte(nameBytes))
            {
                defects?.Add(new MimeDefect(MimeDefectKind.InvalidHeaderName, baseOffset + pos, nameBytes.Length, depth));
            }

            block ??= new ReportFieldBlock { Offset = baseOffset + pos };
            name = Encoding.Latin1.GetString(nameBytes);
            fieldStart = pos;
            valueStart = nameEnd + 1;
            valueEnd = contentEnd;
            fieldCount++;
            pos = next;
        }

        EndField();
        if (block is not null)
        {
            blocks.Add(block);
        }

        return blocks;
    }

    private static bool HasInvalidNameByte(ReadOnlySpan<byte> name)
    {
        foreach (byte b in name)
        {
            if (b <= (byte)' ' || b > (byte)'~')
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Text helpers shared by the report and authentication parsers.</summary>
internal static class ReportText
{
    /// <summary>Bytes to text: valid UTF-8 as UTF-8, else windows-1252 (the reading clients use for undeclared 8-bit text).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return Text.MailEncoding.Windows1252.GetString(bytes);
    }

    /// <summary>Removes line breaks that precede white space (unfolding), then trims blanks.</summary>
    public static string Unfold(ReadOnlySpan<byte> raw)
    {
        var buf = new List<byte>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (b == (byte)'\r' && i + 1 < raw.Length && raw[i + 1] == (byte)'\n')
            {
                continue;
            }

            if (b == (byte)'\n')
            {
                continue;
            }

            buf.Add(b);
        }

        return Decode([.. buf]).Trim(' ', '\t');
    }
}
