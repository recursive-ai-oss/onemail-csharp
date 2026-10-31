using System.Collections;
using System.Text;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Calendar;

/// <summary>
/// The parameters of a <see cref="ContentProperty"/>. Changing the list marks the property as modified, so it is
/// written again from its parts instead of from the stored source bytes.
/// </summary>
public sealed class ParameterList : IReadOnlyList<ContentParameter>
{
    private readonly List<ContentParameter> _items = [];
    private readonly ContentProperty _owner;

    internal ParameterList(ContentProperty owner) => _owner = owner;

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public ContentParameter this[int index] => _items[index];

    /// <summary>Appends a parameter.</summary>
    /// <param name="parameter">The parameter.</param>
    public void Add(ContentParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        _items.Add(parameter);
        _owner.Touch();
    }

    /// <summary>Removes every parameter with this name (ASCII case-insensitive).</summary>
    /// <param name="name">The name.</param>
    /// <returns>How many were removed.</returns>
    public int Remove(string name)
    {
        int n = _items.RemoveAll(p => p.IsNamed(name));
        if (n > 0)
        {
            _owner.Touch();
        }

        return n;
    }

    /// <summary>Replaces every parameter of this name by one with the given values, keeping the position of the first.</summary>
    /// <param name="name">The name.</param>
    /// <param name="values">The values.</param>
    public void Set(string name, params string[] values)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(values);
        ContentParameter p = new(name, values);
        int i = _items.FindIndex(x => x.IsNamed(name));
        if (i < 0)
        {
            _items.Add(p);
        }
        else
        {
            _items[i] = p;
            for (int j = _items.Count - 1; j > i; j--)
            {
                if (_items[j].IsNamed(name))
                {
                    _items.RemoveAt(j);
                }
            }
        }

        _owner.Touch();
    }

    /// <summary>Finds the first parameter with this name.</summary>
    /// <param name="name">The name (ASCII case-insensitive).</param>
    /// <returns>The parameter, or null.</returns>
    public ContentParameter? Find(string name)
    {
        foreach (ContentParameter p in _items)
        {
            if (p.IsNamed(name))
            {
                return p;
            }
        }

        return null;
    }

    internal void AddParsed(ContentParameter p) => _items.Add(p);

    /// <inheritdoc />
    public IEnumerator<ContentParameter> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>An item inside a component: a property line, a blank or malformed line, or a nested component.</summary>
public abstract class ComponentItem
{
    private protected ComponentItem()
    {
    }

    /// <summary>Byte offset of the item's first line in the input it was read from, or -1 if it was created in memory.</summary>
    public long Offset { get; internal set; } = -1;
}

/// <summary>
/// One content line of a component tree. A property read from input remembers its source bytes and is written back
/// exactly as read until something about it is changed; after that it is written from its parts.
/// </summary>
public sealed class ContentProperty : ComponentItem
{
    private string? _group;
    private string _name;
    private string _value;
    private ReadOnlyMemory<byte> _raw;
    private bool _modified;

    /// <summary>Creates a property in memory.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value, already escaped.</param>
    /// <param name="group">The group (<c>item1</c>), or null.</param>
    /// <exception cref="ArgumentException">The name is empty, or the name or group contains a colon, semicolon or line break.</exception>
    public ContentProperty(string name, string value, string? group = null)
    {
        CheckName(name);
        CheckGroup(group);
        ArgumentNullException.ThrowIfNull(value);
        _name = name;
        _value = value;
        _group = group;
        Kind = ContentLineKind.Property;
        Parameters = new ParameterList(this);
        _modified = true;
    }

    internal ContentProperty(ContentLineKind kind, string? group, string name, string value, ReadOnlyMemory<byte> raw, long offset)
    {
        Kind = kind;
        _group = group;
        _name = name;
        _value = value;
        _raw = raw;
        Offset = offset;
        Parameters = new ParameterList(this);
    }

    /// <summary>Whether this is a real property, or a blank or malformed line kept for fidelity. The latter two cannot be edited.</summary>
    public ContentLineKind Kind { get; }

    /// <summary>The group (<c>item1</c> in <c>item1.EMAIL</c>), or null. Setting it throws <see cref="ArgumentException"/> for an empty group or one with a colon, semicolon or line break.</summary>
    public string? Group
    {
        get => _group;
        set
        {
            EnsureEditable();
            CheckGroup(value);
            _group = value;
            Touch();
        }
    }

    /// <summary>The name as written; compare with <see cref="IsNamed"/>. Setting it throws <see cref="ArgumentException"/> for an empty name or one with a colon, semicolon or line break.</summary>
    public string Name
    {
        get => _name;
        set
        {
            EnsureEditable();
            CheckName(value);
            _name = value;
            Touch();
        }
    }

    /// <summary>The value after unfolding, still escaped. Use <see cref="GetText"/> for the plain text.</summary>
    public string Value
    {
        get => _value;
        set
        {
            EnsureEditable();
            ArgumentNullException.ThrowIfNull(value);
            _value = value;
            Touch();
        }
    }

    /// <summary>The parameters.</summary>
    public ParameterList Parameters { get; }

    /// <summary>True once the property has been changed (or was created in memory) and will be written from its parts.</summary>
    public bool IsModified => _modified;

    internal ReadOnlyMemory<byte> Raw => _raw;

    internal void Touch()
    {
        _modified = true;
        _raw = default;
    }

    // A name or group with a delimiter would not read back as the same line.
    private static void CheckName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.AsSpan().IndexOfAny(";:\r\n") >= 0)
        {
            throw new ArgumentException("A property name cannot contain a colon, semicolon or line break.", nameof(name));
        }
    }

    private static void CheckGroup(string? group)
    {
        if (group is not null && (group.Length == 0 || group.AsSpan().IndexOfAny(";:\r\n") >= 0))
        {
            throw new ArgumentException("A group cannot be empty or contain a colon, semicolon or line break.", nameof(group));
        }
    }

    private void EnsureEditable()
    {
        if (Kind != ContentLineKind.Property)
        {
            throw new InvalidOperationException("Blank and malformed lines are kept as they are and cannot be edited.");
        }
    }

    /// <summary>Compares the name to <paramref name="name"/>, ignoring ASCII case.</summary>
    /// <param name="name">The name to compare.</param>
    /// <returns>True when equal.</returns>
    public bool IsNamed(string name) => Kind == ContentLineKind.Property && string.Equals(_name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the first value of a parameter.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>The first value, or null when the parameter is missing or bare.</returns>
    public string? GetParameter(string name) => Parameters.Find(name)?.Value;

    /// <summary>The <c>ENCODING</c> parameter, or a bare <c>QUOTED-PRINTABLE</c>/<c>BASE64</c>/<c>B</c> token (vCard 2.1), upper-cased; null if none.</summary>
    public string? Encoding
    {
        get
        {
            string? e = GetParameter("ENCODING");
            if (e is not null)
            {
                return e.ToUpperInvariant();
            }

            foreach (ContentParameter p in Parameters)
            {
                if (p.IsBare && (p.IsNamed("QUOTED-PRINTABLE") || p.IsNamed("BASE64") || p.IsNamed("B") || p.IsNamed("8BIT") || p.IsNamed("7BIT")))
                {
                    return p.Name.ToUpperInvariant();
                }
            }

            return null;
        }
    }

    /// <summary>The <c>CHARSET</c> parameter, or null.</summary>
    public string? Charset => GetParameter("CHARSET");

    /// <summary>
    /// The value as plain text: quoted-printable decoded (using <c>CHARSET</c>, UTF-8 by default), then text escapes
    /// resolved. Use <see cref="GetBytes"/> for binary values.
    /// </summary>
    /// <param name="style">The escaping rules; use <see cref="TextEscapeStyle.VCard21"/> for vCard 2.1.</param>
    /// <returns>The text.</returns>
    public string GetText(TextEscapeStyle style = TextEscapeStyle.Rfc5545) => ContentValues.Unescape(GetDecodedValue(), style);

    /// <summary>The value with its transfer encoding removed (quoted-printable decoded, using <c>CHARSET</c>, UTF-8 by default) but its escapes intact, for splitting structured values before unescaping the parts.</summary>
    /// <returns>The decoded, still escaped value.</returns>
    public string GetDecodedValue() =>
        Encoding == "QUOTED-PRINTABLE" ? ContentCharsets.Decode(ContentValues.DecodeQuotedPrintable(_value), Charset, Offset, 0, null) : _value;

    /// <summary>The value as bytes: base64 or quoted-printable decoded, otherwise the UTF-8 bytes of the value.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>False when the value claims base64 and is not.</returns>
    public bool TryGetBytes(out byte[] bytes)
    {
        switch (Encoding)
        {
            case "BASE64" or "B":
                return ContentValues.TryDecodeBase64(_value, out bytes);
            case "QUOTED-PRINTABLE":
                bytes = ContentValues.DecodeQuotedPrintable(_value);
                return true;
            default:
                bytes = System.Text.Encoding.UTF8.GetBytes(_value);
                return true;
        }
    }

    /// <summary>The value as bytes (see <see cref="TryGetBytes"/>); empty when the value is not valid base64.</summary>
    /// <returns>The bytes.</returns>
    public byte[] GetBytes() => TryGetBytes(out byte[] b) ? b : [];

    /// <inheritdoc />
    public override string ToString() => Kind == ContentLineKind.Property ? (_group is null ? string.Empty : _group + ".") + _name + ":" + _value : _value;
}

/// <summary>
/// A component (<c>BEGIN:name</c> … <c>END:name</c>) holding properties, blank and malformed lines and nested
/// components in source order. The tree keeps every byte: stored lines are written back as read.
/// </summary>
public sealed class Component : ComponentItem
{
    private readonly List<ComponentItem> _items = [];
    private string _name;
    private ReadOnlyMemory<byte> _beginRaw;
    private ReadOnlyMemory<byte> _endRaw;
    private bool _modifiedName;

    /// <summary>Creates a component in memory.</summary>
    /// <param name="name">The component name, for example <c>VEVENT</c>.</param>
    public Component(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _name = name;
        IsClosed = true;
        _modifiedName = true;
    }

    internal Component(string name, ReadOnlyMemory<byte> beginRaw, long offset)
    {
        _name = name;
        _beginRaw = beginRaw;
        Offset = offset;
    }

    /// <summary>The name as written (case preserved).</summary>
    public string Name
    {
        get => _name;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value;
            _modifiedName = true;
            _beginRaw = default;
            _endRaw = default;
        }
    }

    /// <summary>False when the input ended, or an enclosing <c>END</c> came, before this component's own <c>END</c>. Such a component is written without one.</summary>
    public bool IsClosed { get; set; }

    /// <summary>Everything inside, in source order.</summary>
    public IReadOnlyList<ComponentItem> Items => _items;

    /// <summary>The real properties, in order (blank and malformed lines excluded).</summary>
    public IEnumerable<ContentProperty> Properties => _items.OfType<ContentProperty>().Where(p => p.Kind == ContentLineKind.Property);

    /// <summary>The nested components, in order.</summary>
    public IEnumerable<Component> Components => _items.OfType<Component>();

    /// <summary>Compares the name to <paramref name="name"/>, ignoring ASCII case.</summary>
    /// <param name="name">The name to compare.</param>
    /// <returns>True when equal.</returns>
    public bool IsNamed(string name) => string.Equals(_name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the first property with this name.</summary>
    /// <param name="name">The property name (ASCII case-insensitive).</param>
    /// <returns>The property, or null.</returns>
    public ContentProperty? FindProperty(string name)
    {
        foreach (ComponentItem i in _items)
        {
            if (i is ContentProperty p && p.IsNamed(name))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>Finds all properties with this name.</summary>
    /// <param name="name">The property name (ASCII case-insensitive).</param>
    /// <returns>The properties in order.</returns>
    public IEnumerable<ContentProperty> FindProperties(string name) => _items.OfType<ContentProperty>().Where(p => p.IsNamed(name));

    /// <summary>Finds the nested components with this name.</summary>
    /// <param name="name">The component name (ASCII case-insensitive).</param>
    /// <returns>The components in order.</returns>
    public IEnumerable<Component> FindComponents(string name) => _items.OfType<Component>().Where(c => c.IsNamed(name));

    /// <summary>Appends an item.</summary>
    /// <param name="item">A property or component.</param>
    public void Add(ComponentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    /// <summary>Inserts an item.</summary>
    /// <param name="index">Where to insert.</param>
    /// <param name="item">A property or component.</param>
    public void Insert(int index, ComponentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);
    }

    /// <summary>Removes an item.</summary>
    /// <param name="item">The item.</param>
    /// <returns>True if it was there.</returns>
    public bool Remove(ComponentItem item) => _items.Remove(item);

    /// <summary>Removes every property with this name.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>How many were removed.</returns>
    public int RemoveProperties(string name) => _items.RemoveAll(i => i is ContentProperty p && p.IsNamed(name));

    internal ReadOnlyMemory<byte> BeginRaw => _modifiedName ? default : _beginRaw;

    internal ReadOnlyMemory<byte> EndRaw => _modifiedName ? default : _endRaw;

    internal void SetEnd(ReadOnlyMemory<byte> raw)
    {
        _endRaw = raw;
        IsClosed = true;
    }

    /// <inheritdoc />
    public override string ToString() => "BEGIN:" + _name;
}

/// <summary>Options for <see cref="ContentDocument.Parse(ReadOnlyMemory{byte}, ContentDocumentOptions?)"/>.</summary>
public sealed record ContentDocumentOptions
{
    /// <summary>The defaults.</summary>
    public static ContentDocumentOptions Default { get; } = new();

    /// <summary>Resource limits. Default <see cref="CalendarLimits.Default"/>.</summary>
    public CalendarLimits Limits { get; init; } = CalendarLimits.Default;

    /// <summary>Reader options for line joining. The line length limit comes from <see cref="Limits"/>.</summary>
    public ContentLineReaderOptions Reader { get; init; } = ContentLineReaderOptions.Default;
}

/// <summary>
/// A parsed ICS or vCard file as a lossless tree of components and properties. The root holds the top-level
/// components (<c>VCALENDAR</c>, <c>VCARD</c>, possibly several) and any stray lines around them. Writing an untouched
/// document gives back the input byte for byte, malformed input included.
/// </summary>
public sealed class ContentDocument
{
    private readonly bool _hasBom;

    /// <summary>Creates an empty document.</summary>
    public ContentDocument()
    {
        Root = new Component(string.Empty) { IsClosed = true };
        Defects = [];
    }

    private ContentDocument(Component root, List<CalendarDefect> defects, bool hasBom, string newLine)
    {
        Root = root;
        Defects = defects;
        _hasBom = hasBom;
        NewLine = newLine;
    }

    /// <summary>The unnamed root. Its items are the top-level components and stray lines.</summary>
    public Component Root { get; }

    /// <summary>The top-level components.</summary>
    public IEnumerable<Component> Components => Root.Components;

    /// <summary>What the parser repaired or guessed, in input order.</summary>
    public IReadOnlyList<CalendarDefect> Defects { get; }

    /// <summary>The line end used for lines written from parts: the first one found in the input, CRLF by default.</summary>
    public string NewLine { get; set; } = "\r\n";

    /// <summary>Parses bytes. The memory is kept (not copied) and must not change while the document is in use.</summary>
    /// <param name="data">The bytes of an ICS or vCard file.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The document. Never throws on malformed content.</returns>
    public static ContentDocument Parse(ReadOnlyMemory<byte> data, ContentDocumentOptions? options = null)
    {
        options ??= ContentDocumentOptions.Default;
        CalendarLimits limits = options.Limits;
        List<CalendarDefect> defects = [];
        Component root = new(string.Empty) { IsClosed = true };
        ReadOnlySpan<byte> all = data.Span;
        int parseLength = all.Length;
        if (parseLength > limits.MaxTotalBytes)
        {
            // Cut at a line end so no line is split between the parsed part and the kept block.
            parseLength = all[..limits.MaxTotalBytes].LastIndexOfAny((byte)'\r', (byte)'\n') + 1;
            defects.Add(new CalendarDefect(CalendarDefectKind.TotalSizeLimitExceeded, parseLength, all.Length - parseLength, 0));
        }

        string newLine = DetectNewLine(all[..parseLength]);
        ContentLineReaderOptions ro = options.Reader with { MaxLineBytes = Math.Min(options.Reader.MaxLineBytes, limits.MaxLineBytes) };
        ContentLineReader reader = new(all[..parseLength], ro, defects);
        bool hasBom = reader.Position == 3;

        List<Component> stack = [root];
        int overflow = 0;
        int components = 0;
        int properties = 0;
        int stoppedAt = -1;

        while (reader.TryRead(out ContentLine line))
        {
            Component cur = stack[^1];
            reader.Depth = stack.Count - 1;
            ReadOnlyMemory<byte> raw = data.Slice((int)line.Offset, line.Raw.Length);

            if (line.Kind != ContentLineKind.Property)
            {
                string text = line.Kind == ContentLineKind.Blank ? string.Empty : ContentCharsets.Decode(line.Value, null, line.Offset, stack.Count - 1, defects);
                cur.Add(new ContentProperty(line.Kind, null, string.Empty, text, raw, line.Offset));
                continue;
            }

            if (++properties > limits.MaxProperties)
            {
                defects.Add(new CalendarDefect(CalendarDefectKind.PropertyLimitExceeded, line.Offset, 0, stack.Count - 1));
                stoppedAt = (int)line.Offset;
                break;
            }

            bool isBegin = line.Group.IsEmpty && Ascii.EqualsIgnoreCase(line.Name, "BEGIN"u8);
            bool isEnd = line.Group.IsEmpty && Ascii.EqualsIgnoreCase(line.Name, "END"u8);

            if (isBegin)
            {
                string cname = Encoding.UTF8.GetString(line.Value).Trim();
                if (cname.Length == 0)
                {
                    defects.Add(new CalendarDefect(CalendarDefectKind.BeginWithoutName, line.Offset, line.Raw.Length, stack.Count - 1));
                }
                else if (stack.Count - 1 >= limits.MaxDepth || components >= limits.MaxComponents)
                {
                    defects.Add(new CalendarDefect(
                        stack.Count - 1 >= limits.MaxDepth ? CalendarDefectKind.DepthLimitExceeded : CalendarDefectKind.ComponentLimitExceeded,
                        line.Offset, line.Raw.Length, stack.Count - 1));
                    overflow++;
                }
                else
                {
                    components++;
                    Component c = new(cname, raw, line.Offset);
                    cur.Add(c);
                    stack.Add(c);
                    continue;
                }
            }
            else if (isEnd)
            {
                if (overflow > 0)
                {
                    overflow--;
                }
                else
                {
                    string cname = Encoding.UTF8.GetString(line.Value).Trim();
                    int k = stack.Count - 1;
                    while (k >= 1 && !stack[k].IsNamed(cname))
                    {
                        k--;
                    }

                    if (k < 1)
                    {
                        defects.Add(new CalendarDefect(CalendarDefectKind.UnmatchedEnd, line.Offset, line.Raw.Length, stack.Count - 1));
                        cur.Add(new ContentProperty(ContentLineKind.Malformed, null, string.Empty, Encoding.UTF8.GetString(line.Raw).TrimEnd('\r', '\n'), raw, line.Offset));
                    }
                    else
                    {
                        for (int j = stack.Count - 1; j > k; j--)
                        {
                            defects.Add(new CalendarDefect(CalendarDefectKind.MissingEnd, stack[j].Offset, 0, j));
                        }

                        stack[k].SetEnd(raw);
                        stack.RemoveRange(k, stack.Count - k);
                    }

                    continue;
                }
            }

            cur.Add(BuildProperty(in line, raw, stack.Count - 1, defects));
        }

        for (int j = stack.Count - 1; j >= 1; j--)
        {
            defects.Add(new CalendarDefect(CalendarDefectKind.MissingEnd, stack[j].Offset, 0, j));
        }

        // Whatever the parser did not read (past a size or count limit) stays in the tree as one block so the file still
        // writes back whole.
        int consumed = stoppedAt >= 0 ? stoppedAt : (int)reader.Position;
        if (consumed < all.Length && (stoppedAt >= 0 || parseLength < all.Length))
        {
            ReadOnlyMemory<byte> rest = data[consumed..];
            stack[^1].Add(new ContentProperty(ContentLineKind.Malformed, null, string.Empty, string.Empty, rest, consumed));
        }

        return new ContentDocument(root, defects, hasBom, newLine);
    }

    /// <summary>Parses text as UTF-8.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The document.</returns>
    public static ContentDocument Parse(string text, ContentDocumentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(Encoding.UTF8.GetBytes(text), options);
    }

    /// <summary>Reads a stream to its end and parses it.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The document.</returns>
    public static ContentDocument Parse(Stream stream, ContentDocumentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return Parse(ms.ToArray(), options);
    }

    /// <summary>Reads a stream to its end and parses it.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document.</returns>
    public static async Task<ContentDocument> ParseAsync(Stream stream, ContentDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream ms = new();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return Parse(ms.ToArray(), options);
    }

    private static string DetectNewLine(ReadOnlySpan<byte> data)
    {
        int i = data.IndexOfAny((byte)'\r', (byte)'\n');
        if (i < 0)
        {
            return "\r\n";
        }

        if (data[i] == '\n')
        {
            return "\n";
        }

        return i + 1 < data.Length && data[i + 1] == '\n' ? "\r\n" : "\r";
    }

    private static ContentProperty BuildProperty(in ContentLine line, ReadOnlyMemory<byte> raw, int depth, List<CalendarDefect> defects)
    {
        List<ContentParameter> ps = [];
        string? charset = null;
        foreach (ContentParameterSpan p in line.EnumerateParameters())
        {
            string pname = Encoding.UTF8.GetString(p.Name);
            if (!p.HasValue)
            {
                ps.Add(ContentParameter.Bare(pname));
                continue;
            }

            if (pname.Length == 0)
            {
                defects.Add(new CalendarDefect(CalendarDefectKind.EmptyParameterName, line.Offset, 0, depth));
            }

            List<string> values = [];
            foreach (ReadOnlySpan<byte> v in p.EnumerateValues())
            {
                string s = ContentCharsets.Decode(v, null, line.Offset, depth, null);
                if (ContentValues.HasCaretSequence(s))
                {
                    defects.Add(new CalendarDefect(CalendarDefectKind.AmbiguousCaretEncoding, line.Offset, 0, depth));
                    s = ContentValues.DecodeCaret(s);
                }

                values.Add(s);
            }

            ps.Add(new ContentParameter(pname, values));
        }

        foreach (ContentParameter p in ps)
        {
            if (p.IsNamed("CHARSET") && p.Value is not null)
            {
                charset = p.Value;
                break;
            }
        }

        string value = ContentCharsets.Decode(line.Value, charset, line.Offset, depth, defects);
        string? group = line.Group.IsEmpty ? null : Encoding.UTF8.GetString(line.Group);
        ContentProperty prop = new(ContentLineKind.Property, group, Encoding.UTF8.GetString(line.Name), value, raw, line.Offset);
        foreach (ContentParameter p in ps)
        {
            prop.Parameters.AddParsed(p);
        }

        return prop;
    }

    /// <summary>Writes the document. Stored lines go out as read; changed or new lines are written from their parts.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="options">Options for lines written from parts (folding, caret encoding); null for the defaults. The line end is <see cref="NewLine"/>.</param>
    public void Write(Stream output, ContentLineWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ContentLineWriterOptions o = (options ?? ContentLineWriterOptions.Default) with { NewLine = NewLine };
        ContentLineWriter w = new(output, o);
        if (_hasBom)
        {
            w.WriteRaw([0xEF, 0xBB, 0xBF]);
        }

        bool pending = false;
        WriteItems(Root, w, ref pending);
    }

    /// <summary>Writes the document to a byte array.</summary>
    /// <param name="options">See <see cref="Write"/>.</param>
    /// <returns>The bytes.</returns>
    public byte[] ToArray(ContentLineWriterOptions? options = null)
    {
        using MemoryStream ms = new();
        Write(ms, options);
        return ms.ToArray();
    }

    private static void WriteItems(Component c, ContentLineWriter w, ref bool pending)
    {
        foreach (ComponentItem item in c.Items)
        {
            if (item is ContentProperty p)
            {
                if (!p.IsModified)
                {
                    EmitRaw(p.Raw.Span, w, ref pending);
                }
                else
                {
                    EndPending(w, ref pending);
                    Encoding? enc = p.Charset is { } cs ? ContentCharsets.Find(cs) : null;
                    w.WriteLine(p.Group, p.Name, p.Parameters, p.Value, enc);
                }
            }
            else if (item is Component child)
            {
                if (child.BeginRaw.IsEmpty)
                {
                    EndPending(w, ref pending);
                    w.WriteBegin(child.Name);
                }
                else
                {
                    EmitRaw(child.BeginRaw.Span, w, ref pending);
                }

                WriteItems(child, w, ref pending);
                if (child.IsClosed)
                {
                    if (child.EndRaw.IsEmpty)
                    {
                        EndPending(w, ref pending);
                        w.WriteEnd(child.Name);
                    }
                    else
                    {
                        EmitRaw(child.EndRaw.Span, w, ref pending);
                    }
                }
            }
        }
    }

    private static void EndPending(ContentLineWriter w, ref bool pending)
    {
        if (pending)
        {
            w.WriteLineEnd();
            pending = false;
        }
    }

    // A stored line is written as it was. If the previous stored line was the last one in the input and had no line
    // end, one is added only when something follows it.
    private static void EmitRaw(ReadOnlySpan<byte> raw, ContentLineWriter w, ref bool pending)
    {
        EndPending(w, ref pending);
        w.WriteRaw(raw);
        pending = raw.Length > 0 && raw[^1] is not ((byte)'\n' or (byte)'\r');
    }
}
