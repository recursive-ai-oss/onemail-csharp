using System.Xml;

namespace RecursiveAI.OneMail.Stores.Olm;

/// <summary>An attribute of an <see cref="OlmElement"/>, exactly as written (entity references resolved).</summary>
/// <param name="Name">The attribute name, with its prefix (<c>xml:space</c>).</param>
/// <param name="Value">The attribute value.</param>
public readonly record struct OlmNameValue(string Name, string Value);

/// <summary>
/// One XML element of an OLM record, kept whole so that nothing the typed views do not map is lost: names, attributes, text and children, in
/// document order. The OLM files are machine-written (every value is an attribute or the text of a leaf element), so the tree is small and
/// plain; it is not a general XML DOM.
/// </summary>
public sealed class OlmElement
{
    private static readonly OlmNameValue[] NoAttributes = [];
    private static readonly OlmElement[] NoChildren = [];

    internal OlmElement(string name, OlmNameValue[]? attributes, string text, List<OlmElement>? children)
    {
        Name = name;
        Attributes = attributes ?? NoAttributes;
        Text = text;
        Children = children is null ? NoChildren : children;
    }

    /// <summary>The element name, with its prefix if it has one. The Outlook for Mac records use names like <c>OPFMessageCopySubject</c>.</summary>
    public string Name { get; }

    /// <summary>The attributes in document order (including <c>xml:space</c>).</summary>
    public IReadOnlyList<OlmNameValue> Attributes { get; }

    /// <summary>The element's own text (text and CDATA nodes directly inside it, joined), or an empty string. Whitespace is kept exactly.</summary>
    public string Text { get; }

    /// <summary>The child elements in document order.</summary>
    public IReadOnlyList<OlmElement> Children { get; }

    /// <summary>The first child with the given name, or null.</summary>
    /// <param name="name">The element name, compared exactly.</param>
    /// <returns>The child, or null.</returns>
    public OlmElement? Child(string name)
    {
        foreach (OlmElement c in Children)
        {
            if (c.Name == name)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>The children with the given name, in order.</summary>
    /// <param name="name">The element name, compared exactly.</param>
    /// <returns>The children.</returns>
    public IEnumerable<OlmElement> ChildrenNamed(string name)
    {
        foreach (OlmElement c in Children)
        {
            if (c.Name == name)
            {
                yield return c;
            }
        }
    }

    /// <summary>The text of the first child with the given name, or null when there is no such child.</summary>
    /// <param name="name">The element name.</param>
    /// <returns>The text (possibly empty), or null.</returns>
    public string? ChildText(string name) => Child(name)?.Text;

    /// <summary>The value of an attribute, or null.</summary>
    /// <param name="name">The attribute name.</param>
    /// <returns>The value, or null when the element has no such attribute.</returns>
    public string? Attribute(string name)
    {
        foreach (OlmNameValue a in Attributes)
        {
            if (a.Name == name)
            {
                return a.Value;
            }
        }

        return null;
    }

    /// <summary>The number of elements in this subtree, this one included.</summary>
    /// <returns>The count.</returns>
    public int CountElements()
    {
        int n = 1;
        foreach (OlmElement c in Children)
        {
            n += c.CountElements();
        }

        return n;
    }

    /// <inheritdoc />
    public override string ToString() => "<" + Name + ">";
}

/// <summary>Reads the records of one OLM XML entry one at a time, so a large address book or calendar never sits in memory whole.</summary>
internal sealed class OlmRecordReader : IDisposable
{
    private readonly XmlReader _xml;
    private readonly CountingStream _counter;
    private readonly OlmReaderOptions _options;
    private readonly string _entry;
    private readonly Action<OlmDefect> _report;
    private bool _rootRead;
    private bool _done;
    private int _rootDepth;

    public OlmRecordReader(Stream input, OlmReaderOptions options, string entry, Action<OlmDefect> report)
    {
        _options = options;
        _entry = entry;
        _report = report;
        _counter = new CountingStream(input, options.MaxXmlBytes);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            // The files are machine-written and mail bodies can hold control characters; reading them is better than refusing the entry.
            CheckCharacters = false,
            CloseInput = false,
            MaxCharactersFromEntities = 1 << 20,
        };
        _xml = XmlReader.Create(_counter, settings);
    }

    /// <summary>The root element's name, or null when the entry is empty or not XML.</summary>
    public string? RootName { get; private set; }

    /// <summary>The root's <c>elementCount</c> attribute, or -1.</summary>
    public long DeclaredCount { get; private set; } = -1;

    /// <summary>Records returned so far.</summary>
    public long Count { get; private set; }

    /// <summary>True when a record was dropped or the entry cut short, so a count mismatch is expected.</summary>
    public bool Damaged { get; private set; }

    public bool ReadRoot()
    {
        if (_rootRead)
        {
            return RootName is not null;
        }

        _rootRead = true;
        try
        {
            while (_xml.Read())
            {
                if (_xml.NodeType == XmlNodeType.Element)
                {
                    RootName = _xml.Name;
                    _rootDepth = _xml.Depth;
                    string? c = _xml.GetAttribute("elementCount");
                    if (c is not null && long.TryParse(c, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long n))
                    {
                        DeclaredCount = n;
                    }
                    else if (c is not null && double.TryParse(c, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d >= 0 && d < 1e15)
                    {
                        DeclaredCount = (long)d;
                    }

                    if (_xml.IsEmptyElement)
                    {
                        _done = true;
                    }

                    return true;
                }
            }
        }
        catch (XmlException)
        {
            Fail();
        }

        _done = true;
        return false;
    }

    /// <summary>The next record, or null at the end of the entry (or after damage).</summary>
    public OlmElement? Next()
    {
        if (!ReadRoot() || _done)
        {
            return null;
        }

        while (true)
        {
            try
            {
                if (!_xml.Read())
                {
                    _done = true;
                    return null;
                }

                if (_xml.NodeType == XmlNodeType.EndElement && _xml.Depth == _rootDepth)
                {
                    _done = true;
                    return null;
                }

                if (_xml.NodeType != XmlNodeType.Element || _xml.Depth != _rootDepth + 1)
                {
                    continue;
                }

                int recordDepth = _xml.Depth;
                int budget = _options.MaxElementsPerItem;
                try
                {
                    OlmElement e = Build(ref budget, 2);
                    Count++;
                    return e;
                }
                catch (OlmLimitException ex)
                {
                    _report(new OlmDefect(ex.Kind, _entry, ex.Detail));
                    Damaged = true;
                    SkipRecord(recordDepth);
                }
            }
            catch (XmlException)
            {
                Fail();
                return null;
            }
        }
    }

    private void Fail()
    {
        _done = true;
        Damaged = true;
        _report(new OlmDefect(_counter.Exceeded ? OlmDefectKind.XmlTooLarge : OlmDefectKind.XmlMalformed, _entry));
    }

    // After a limit stops the builder somewhere inside a record, read on to the end of that record.
    private void SkipRecord(int recordDepth)
    {
        while (!(_xml.NodeType == XmlNodeType.EndElement && _xml.Depth == recordDepth) && !(_xml.NodeType == XmlNodeType.Element && _xml.Depth == recordDepth && _xml.IsEmptyElement))
        {
            if (!_xml.Read())
            {
                _done = true;
                return;
            }
        }
    }

    // The reader is on a start tag. Returns with the reader on the matching end tag (or on the tag itself when it is empty).
    private OlmElement Build(ref int budget, int depth)
    {
        if (depth > _options.MaxXmlDepth)
        {
            throw new OlmLimitException(OlmDefectKind.XmlTooDeep, _xml.Name);
        }

        if (--budget < 0)
        {
            throw new OlmLimitException(OlmDefectKind.XmlTooManyElements, _xml.Name);
        }

        string name = _xml.Name;
        OlmNameValue[]? attrs = null;
        if (_xml.HasAttributes)
        {
            attrs = new OlmNameValue[_xml.AttributeCount];
            for (int i = 0; i < attrs.Length; i++)
            {
                _xml.MoveToAttribute(i);
                if (_xml.Value.Length > _options.MaxTextLength)
                {
                    throw new OlmLimitException(OlmDefectKind.XmlTooLarge, _xml.Name);
                }

                attrs[i] = new OlmNameValue(_xml.Name, _xml.Value);
            }

            _xml.MoveToElement();
        }

        if (_xml.IsEmptyElement)
        {
            return new OlmElement(name, attrs, string.Empty, null);
        }

        int myDepth = _xml.Depth;
        string text = string.Empty;
        System.Text.StringBuilder? sb = null;
        List<OlmElement>? kids = null;
        while (_xml.Read())
        {
            switch (_xml.NodeType)
            {
                case XmlNodeType.Element:
                    (kids ??= []).Add(Build(ref budget, depth + 1));
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    string v = _xml.Value;
                    if (v.Length + (sb?.Length ?? text.Length) > _options.MaxTextLength)
                    {
                        throw new OlmLimitException(OlmDefectKind.XmlTooLarge, name);
                    }

                    if (sb is not null)
                    {
                        sb.Append(v);
                    }
                    else if (text.Length == 0)
                    {
                        text = v;
                    }
                    else
                    {
                        sb = new System.Text.StringBuilder(text).Append(v);
                    }

                    break;
                case XmlNodeType.EndElement when _xml.Depth == myDepth:
                    return new OlmElement(name, attrs, sb?.ToString() ?? text, kids);
            }
        }

        throw new XmlException("Unexpected end of file.");
    }

    public void Dispose() => _xml.Dispose();

    private sealed class OlmLimitException(OlmDefectKind kind, string detail) : Exception
    {
        public OlmDefectKind Kind { get; } = kind;

        public string Detail { get; } = detail;
    }

    // Ends the stream after a byte budget, so a bomb cannot make the parser run forever. Exceeded says why the parse then failed.
    private sealed class CountingStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public bool Exceeded { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long room = limit - _read;
            if (room <= 0)
            {
                Exceeded = true;
                return 0;
            }

            int n = inner.Read(buffer.Length > room ? buffer[..(int)room] : buffer);
            _read += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
