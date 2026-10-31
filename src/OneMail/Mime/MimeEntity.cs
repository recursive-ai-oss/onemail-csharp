using System.Security.Cryptography;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Mime.Writing;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Mime;

/// <summary>A piece of output: literal bytes, or a range of a source copied lazily.</summary>
internal readonly record struct WriteSegment(byte[]? Bytes, IRandomAccessSource? Source, long Offset, long Length);

/// <summary>Bytes that are either a range of the source the tree was read from, or bytes the caller replaced them with.</summary>
internal sealed class RawContent
{
    private readonly IRandomAccessSource? _source;
    private readonly long _offset;
    private readonly byte[]? _bytes;

    private RawContent(IRandomAccessSource? source, long offset, long length, byte[]? bytes)
    {
        _source = source;
        _offset = offset;
        Length = length;
        _bytes = bytes;
    }

    public static RawContent Empty { get; } = new(null, 0, 0, []);

    public long Length { get; }

    public static RawContent FromRange(IRandomAccessSource source, long offset, long length) => new(source, offset, Math.Max(0, length), null);

    public static RawContent FromBytes(ReadOnlyMemory<byte> bytes) => new(null, 0, bytes.Length, bytes.ToArray());

    public Stream Open() => _bytes is not null ? new MemoryStream(_bytes, writable: false) : _source!.OpenRead(_offset, Length);

    public byte[] ToArray()
    {
        if (_bytes is not null)
        {
            return (byte[])_bytes.Clone();
        }

        if (Length > Array.MaxLength)
        {
            throw new InvalidOperationException("The content is too large for one array; read it as a stream.");
        }

        byte[] r = new byte[Length];
        int n = _source!.ReadFully(_offset, r);
        return n == r.Length ? r : r.AsSpan(0, n).ToArray();
    }

    public WriteSegment Segment() => new(_bytes, _source, _offset, Length);
}

/// <summary>
/// A node of the MIME entity tree: a message or body part with its header fields and body. The tree is raw-preserving:
/// parsing keeps every byte's place, and writing a tree nobody changed produces the input byte for byte, malformed input
/// included. Bodies are lazy streams over the source. Changing a header, body or child re-serialises only the entities on the
/// path to the change; everything else is copied from the source as it was.
/// </summary>
/// <remarks>
/// A tree read from memory, a file or a seekable stream keeps using that source until it is discarded: do not dispose or
/// change the source meanwhile. Entities are not thread-safe for modification.
/// </remarks>
public abstract partial class MimeEntity
{
    private static readonly byte[] Crlf = [(byte)'\r', (byte)'\n'];

    private readonly List<MimeDefect> _defects = [];
    private bool _modified;

    internal MimeEntity() => Headers = new MimeHeaderList(this);

    /// <summary>The header fields, in order, duplicates included.</summary>
    public MimeHeaderList Headers { get; }

    /// <summary>The enclosing multipart or message part, or null for the root.</summary>
    public MimeEntity? Parent { get; internal set; }

    /// <summary>What kind of body this entity has.</summary>
    public abstract MimeEntityType EntityType { get; }

    /// <summary>Whether anything in this entity or below it has been changed since it was parsed (always true for new entities).</summary>
    public bool IsModified => _modified;

    /// <summary>True on the root when the input was longer than <see cref="MimeLimits.MaxTotalBytes"/> and the tree holds only the part read.</summary>
    public bool IsTruncated { get; internal set; }

    /// <summary>Defects reported while this entity was being read, in order. Does not include children's; see <see cref="EnumerateDefects"/>.</summary>
    public IReadOnlyList<MimeDefect> Defects => _defects;

    /// <summary>Offset in the parsed input where this entity starts, or -1 for an entity that was not parsed.</summary>
    public long SourceStart { get; internal set; } = -1;

    /// <summary>Number of input bytes this entity covered when parsed, or 0 for an entity that was not parsed.</summary>
    public long SourceLength { get; internal set; }

    internal IRandomAccessSource? Source { get; set; }

    internal byte[] Separator { get; set; } = Crlf;

    /// <summary>The mbox <c>From </c> line that began the message, with its line end, or empty. It is not a header field.</summary>
    public ReadOnlyMemory<byte> EnvelopeLine { get; internal set; }

    internal List<MimeDefect> DefectList => _defects;

    /// <summary>The nesting depth: 0 for the root.</summary>
    public int Depth
    {
        get
        {
            int d = 0;
            for (MimeEntity? p = Parent; p is not null; p = p.Parent)
            {
                d++;
            }

            return d;
        }
    }

    /// <summary>The media type from <c>Content-Type</c>, lower case, e.g. <c>text/plain</c>. Defaults per RFC 2045 §5.2 (and RFC 2046 §5.1.5 inside a digest).</summary>
    public string MediaType
    {
        get
        {
            ParseContentType(out string type, out string sub);
            return type + "/" + sub;
        }
    }

    /// <summary>The top-level type, lower case, e.g. <c>multipart</c>.</summary>
    public string MediaTopLevelType
    {
        get
        {
            ParseContentType(out string type, out _);
            return type;
        }
    }

    /// <summary>The subtype, lower case, e.g. <c>alternative</c>.</summary>
    public string MediaSubType
    {
        get
        {
            ParseContentType(out _, out string sub);
            return sub;
        }
    }

    /// <summary>The <c>Content-Transfer-Encoding</c>; 7bit when absent.</summary>
    public ContentTransferEncoding TransferEncoding
    {
        get
        {
            MimeHeader? h = Headers.GetFirst("Content-Transfer-Encoding");
            return h is null ? ContentTransferEncoding.SevenBit : StructuredHeader.ParseTransferEncoding(h.RawValue.Span);
        }
    }

    private void ParseContentType(out string type, out string sub)
    {
        MimeHeader? h = Headers.GetFirst("Content-Type");
        if (h is not null && StructuredHeader.TryParseMediaType(h.RawValue.Span, out ReadOnlySpan<byte> t, out ReadOnlySpan<byte> s, out _))
        {
            type = HeaderText.DecodeName(t).ToLowerInvariant();
            sub = HeaderText.DecodeName(s).ToLowerInvariant();
            return;
        }

        if (h is null && Parent is MimeMultipart p && p.MediaSubType == "digest")
        {
            type = "message";
            sub = "rfc822";
            return;
        }

        type = "text";
        sub = "plain";
    }

    /// <summary>This entity's defects and those of every entity below it, in document order.</summary>
    /// <returns>The defects.</returns>
    public IEnumerable<MimeDefect> EnumerateDefects()
    {
        foreach (MimeDefect d in _defects)
        {
            yield return d;
        }

        foreach (MimeEntity c in ChildEntities())
        {
            foreach (MimeDefect d in c.EnumerateDefects())
            {
                yield return d;
            }
        }
    }

    internal abstract IEnumerable<MimeEntity> ChildEntities();

    internal void MarkModified()
    {
        for (MimeEntity? e = this; e is not null; e = e.Parent)
        {
            e._modified = true;
        }
    }

    // ---- parsing ----

    /// <summary>Parses a message held in memory. Never throws on malformed content; see <see cref="EnumerateDefects"/>.</summary>
    /// <param name="data">The message. It is read lazily afterwards, so it must not change while the tree is in use.</param>
    /// <param name="options">Reader options (limits), or null for the defaults.</param>
    /// <returns>The root entity.</returns>
    public static MimeEntity Parse(ReadOnlyMemory<byte> data, MimeReaderOptions? options = null)
    {
        var source = new MemorySource(data);
        using var reader = new MimeReader(data, options);
        var builder = new MimeTreeBuilder(source);
        while (reader.Read())
        {
            builder.Handle(reader);
        }

        return builder.Finish(reader);
    }

    /// <summary>
    /// Parses a message from a random-access source (a file, a seekable stream, ranged reads). Bodies stay in the source and are
    /// read when opened, so the source must stay alive and unchanged while the tree is used.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Reader options (limits), or null for the defaults.</param>
    /// <returns>The root entity.</returns>
    /// <exception cref="NotSupportedException">The source is asynchronous only; use <see cref="LoadAsync(IRandomAccessSource, MimeReaderOptions?, CancellationToken)"/>.</exception>
    public static MimeEntity Load(IRandomAccessSource source, MimeReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var reader = new MimeReader(source, options);
        var builder = new MimeTreeBuilder(source);
        while (reader.Read())
        {
            builder.Handle(reader);
        }

        return builder.Finish(reader);
    }

    /// <summary>Asynchronous twin of <see cref="Load(IRandomAccessSource, MimeReaderOptions?)"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Reader options (limits), or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The root entity.</returns>
    public static async Task<MimeEntity> LoadAsync(IRandomAccessSource source, MimeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var reader = new MimeReader(source, options);
        var builder = new MimeTreeBuilder(source);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            builder.Handle(reader);
        }

        return builder.Finish(reader);
    }

    /// <summary>
    /// Parses a message from a stream. A seekable stream is used as the lazy source and must stay open while the tree is used.
    /// A non-seekable stream (a socket, a pipe) is read into memory first, up to <see cref="MimeLimits.MaxTotalBytes"/>.
    /// </summary>
    /// <param name="stream">The stream, positioned at the start of the message.</param>
    /// <param name="options">Reader options (limits), or null for the defaults.</param>
    /// <returns>The root entity.</returns>
    public static MimeEntity Load(Stream stream, MimeReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            if (stream.Position != 0)
            {
                // Offsets in the tree are offsets in the source, so a stream not at its start is copied from where it is.
                return Parse(ReadAll(stream, options), options);
            }

            return Load(new StreamSource(stream), options);
        }

        return Parse(ReadAll(stream, options), options);
    }

    /// <summary>Asynchronous twin of <see cref="Load(Stream, MimeReaderOptions?)"/>.</summary>
    /// <param name="stream">The stream, positioned at the start of the message.</param>
    /// <param name="options">Reader options (limits), or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The root entity.</returns>
    public static async Task<MimeEntity> LoadAsync(Stream stream, MimeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Position == 0)
        {
            return await LoadAsync(new StreamSource(stream), options, cancellationToken).ConfigureAwait(false);
        }

        var ms = new MemoryStream();
        long limit = (options?.Limits ?? MimeLimits.Default).MaxTotalBytes;
        await CopyLimitedAsync(stream, ms, limit, cancellationToken).ConfigureAwait(false);
        return Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length), options);
    }

    private static byte[] ReadAll(Stream stream, MimeReaderOptions? options)
    {
        var ms = new MemoryStream();
        long limit = (options?.Limits ?? MimeLimits.Default).MaxTotalBytes;
        byte[] chunk = new byte[64 * 1024];
        while (ms.Length <= limit)
        {
            int n = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, limit + 1 - ms.Length));
            if (n <= 0)
            {
                break;
            }

            ms.Write(chunk, 0, n);
        }

        return ms.ToArray();
    }

    private static async Task CopyLimitedAsync(Stream from, MemoryStream to, long limit, CancellationToken ct)
    {
        byte[] chunk = new byte[64 * 1024];
        while (to.Length <= limit)
        {
            int n = await from.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1 - to.Length)), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            to.Write(chunk, 0, n);
        }
    }

    // ---- writing ----

    /// <summary>
    /// The output as pieces. An entity nobody changed is one piece: its range in the source. A changed entity is its header
    /// fields and separator followed by its body pieces, where unchanged children and bodies are again source ranges.
    /// <paramref name="forceAssemble"/> rebuilds even unchanged entities from their parts (tests use it to prove the parts
    /// reproduce the input).
    /// </summary>
    internal IEnumerable<WriteSegment> Segments(bool forceAssemble = false)
    {
        if (!_modified && !forceAssemble && Source is not null)
        {
            yield return new WriteSegment(null, Source, SourceStart, SourceLength);
            yield break;
        }

        if (!EnvelopeLine.IsEmpty)
        {
            yield return new WriteSegment(EnvelopeLine.ToArray(), null, 0, EnvelopeLine.Length);
        }

        foreach (MimeHeader h in Headers)
        {
            yield return new WriteSegment(h.Raw.ToArray(), null, 0, h.Raw.Length);
        }

        if (Separator.Length > 0)
        {
            yield return new WriteSegment(Separator, null, 0, Separator.Length);
        }

        foreach (WriteSegment s in BodySegments(forceAssemble))
        {
            yield return s;
        }
    }

    internal abstract IEnumerable<WriteSegment> BodySegments(bool forceAssemble);

    /// <summary>Writes the entity (header block, separator and body) to a stream. Unchanged parts are copied byte for byte from the source.</summary>
    /// <param name="output">The stream.</param>
    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var w = new MimeWriter(output);
        foreach (WriteSegment s in Segments())
        {
            if (s.Bytes is not null)
            {
                w.WriteRaw(s.Bytes);
            }
            else if (s.Length > 0)
            {
                w.CopyFrom(s.Source!, s.Offset, s.Length);
            }
        }
    }

    /// <summary>Asynchronous twin of <see cref="WriteTo(Stream)"/>.</summary>
    /// <param name="output">The stream.</param>
    /// <param name="cancellationToken">Cancels writing.</param>
    /// <returns>A task that completes when everything is written and flushed.</returns>
    public async Task WriteToAsync(Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var w = new MimeWriter(output);
        foreach (WriteSegment s in Segments())
        {
            if (s.Bytes is not null)
            {
                await w.WriteRawAsync(s.Bytes, cancellationToken).ConfigureAwait(false);
            }
            else if (s.Length > 0)
            {
                await w.CopyFromAsync(s.Source!, s.Offset, s.Length, cancellationToken).ConfigureAwait(false);
            }
        }

        await w.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The whole entity as one array. For large entities write to a stream instead.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToArray()
    {
        using var ms = new MemoryStream();
        WriteTo(ms);
        return ms.ToArray();
    }

    internal byte[] ToArrayAssembled()
    {
        using var ms = new MemoryStream();
        using (var w = new MimeWriter(ms))
        {
            foreach (WriteSegment s in Segments(forceAssemble: true))
            {
                if (s.Bytes is not null)
                {
                    w.WriteRaw(s.Bytes);
                }
                else if (s.Length > 0)
                {
                    w.CopyFrom(s.Source!, s.Offset, s.Length);
                }
            }
        }

        return ms.ToArray();
    }

    internal static byte[] NewBoundary()
    {
        // 18 random bytes, base64 without characters that need quoting; reproducible output passes its own boundary.
        byte[] r = RandomNumberGenerator.GetBytes(18);
        return System.Text.Encoding.ASCII.GetBytes("=_" + Convert.ToBase64String(r).Replace('+', '-').Replace('/', '_'));
    }
}

/// <summary>A leaf entity: headers and a body of bytes.</summary>
public sealed class MimePart : MimeEntity
{
    private RawContent _body = RawContent.Empty;

    /// <summary>Creates a new, empty part with the given <c>Content-Type</c>.</summary>
    /// <param name="mediaType">For example <c>text/plain; charset=utf-8</c>. Null adds no Content-Type header (text/plain by default).</param>
    public MimePart(string? mediaType = null)
    {
        if (mediaType is not null)
        {
            Headers.Add("Content-Type", mediaType);
        }
        else
        {
            MarkModified();
        }
    }

    internal MimePart(bool parsed)
    {
        _ = parsed;
    }

    /// <inheritdoc/>
    public override MimeEntityType EntityType => MimeEntityType.Leaf;

    /// <summary>Length in bytes of the body as transported (still encoded).</summary>
    public long RawContentLength => _body.Length;

    internal void SetParsedBody(RawContent body) => _body = body;

    /// <summary>Opens the body as transported, encoded as its <c>Content-Transfer-Encoding</c> says. Reads are lazy.</summary>
    /// <returns>A read-only stream. Dispose it.</returns>
    public Stream OpenRawContent() => _body.Open();

    /// <summary>Opens the body decoded from its transfer encoding. Reads are lazy; decoding is lenient (see <see cref="TransferDecodingStream.Issues"/>).</summary>
    /// <returns>A read-only stream. Dispose it.</returns>
    public Stream OpenDecodedContent() => new TransferDecodingStream(_body.Open(), TransferEncoding, leaveOpen: false);

    /// <summary>The decoded body as one array. For large parts use <see cref="OpenDecodedContent"/>.</summary>
    /// <returns>The bytes.</returns>
    public byte[] GetDecodedContent()
    {
        using Stream s = OpenDecodedContent();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Replaces the body with bytes that are already encoded as the headers say.</summary>
    /// <param name="encodedBytes">The body as it goes on the wire.</param>
    public void SetRawContent(ReadOnlyMemory<byte> encodedBytes)
    {
        _body = RawContent.FromBytes(encodedBytes);
        MarkModified();
    }

    /// <summary>Replaces the body with <paramref name="data"/>, encoding it and setting <c>Content-Transfer-Encoding</c> to match.</summary>
    /// <param name="data">The decoded content.</param>
    /// <param name="encoding">Base64 (default), quoted-printable, or an identity encoding (7bit, 8bit, binary: bytes as given).</param>
    public void SetDecodedContent(ReadOnlySpan<byte> data, ContentTransferEncoding encoding = ContentTransferEncoding.Base64)
    {
        byte[] encoded = encoding switch
        {
            ContentTransferEncoding.Base64 => TransferEncoder.Base64(data),
            ContentTransferEncoding.QuotedPrintable => TransferEncoder.QuotedPrintable(data),
            _ => data.ToArray(),
        };
        _body = RawContent.FromBytes(encoded);
        if (encoding == ContentTransferEncoding.SevenBit)
        {
            Headers.RemoveAll("Content-Transfer-Encoding");
        }
        else
        {
            Headers.Set("Content-Transfer-Encoding", encoding switch
            {
                ContentTransferEncoding.EightBit => "8bit",
                ContentTransferEncoding.Binary => "binary",
                ContentTransferEncoding.Base64 => "base64",
                ContentTransferEncoding.QuotedPrintable => "quoted-printable",
                _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
            });
        }

        MarkModified();
    }

    internal override IEnumerable<MimeEntity> ChildEntities() => [];

    internal override IEnumerable<WriteSegment> BodySegments(bool forceAssemble)
    {
        if (_body.Length > 0)
        {
            yield return _body.Segment();
        }
    }
}

/// <summary>An embedded message (<c>message/rfc822</c>): the body is one child entity.</summary>
public sealed class MimeMessagePart : MimeEntity
{
    private MimeEntity? _message;

    /// <summary>Creates a new part holding no message yet.</summary>
    /// <param name="mediaType">The <c>Content-Type</c> value. Default <c>message/rfc822</c>.</param>
    public MimeMessagePart(string mediaType = "message/rfc822") => Headers.Add("Content-Type", mediaType);

    internal MimeMessagePart(bool parsed)
    {
        _ = parsed;
    }

    /// <inheritdoc/>
    public override MimeEntityType EntityType => MimeEntityType.Message;

    /// <summary>The embedded message, or null when there is none.</summary>
    public MimeEntity? Message
    {
        get => _message;
        set
        {
            if (value?.Parent is not null)
            {
                throw new ArgumentException("The entity already has a parent.", nameof(value));
            }

            if (_message is not null)
            {
                _message.Parent = null;
            }

            _message = value;
            if (value is not null)
            {
                value.Parent = this;
            }

            MarkModified();
        }
    }

    internal void SetParsedMessage(MimeEntity message)
    {
        _message = message;
        message.Parent = this;
    }

    internal override IEnumerable<MimeEntity> ChildEntities() => _message is null ? [] : [_message];

    internal override IEnumerable<WriteSegment> BodySegments(bool forceAssemble) =>
        _message is null ? [] : _message.Segments(forceAssemble);
}

/// <summary>
/// A multipart entity: preamble, child entities separated by delimiter lines, and epilogue (RFC 2046 §5.1). The exact
/// delimiter lines are kept, so an unchanged multipart reproduces its input, including a missing closing delimiter.
/// </summary>
public sealed class MimeMultipart : MimeEntity
{
    private readonly List<MimeEntity> _children = [];
    private readonly List<byte[]?> _delimiters = [];
    private byte[] _boundary;
    private byte[]? _closeDelimiter;
    private bool _generateClose;
    private RawContent _preamble = RawContent.Empty;
    private RawContent _epilogue = RawContent.Empty;

    /// <summary>Creates a new multipart.</summary>
    /// <param name="subtype">The subtype, e.g. <c>mixed</c> or <c>alternative</c>.</param>
    /// <param name="boundary">The boundary; null generates a random one. Pass one for reproducible output.</param>
    public MimeMultipart(string subtype = "mixed", string? boundary = null)
    {
        _boundary = boundary is null ? NewBoundary() : System.Text.Encoding.Latin1.GetBytes(boundary);
        _generateClose = true;
        Headers.Add("Content-Type", "multipart/" + subtype + "; boundary=\"" + System.Text.Encoding.Latin1.GetString(_boundary) + "\"");
    }

    internal MimeMultipart(byte[] boundary)
    {
        _boundary = boundary;
    }

    /// <inheritdoc/>
    public override MimeEntityType EntityType => MimeEntityType.Multipart;

    /// <summary>The child entities in order.</summary>
    public IReadOnlyList<MimeEntity> Children => _children;

    /// <summary>The boundary string (without the leading dashes).</summary>
    public string Boundary => System.Text.Encoding.Latin1.GetString(_boundary);

    /// <summary>Whether the input had a closing delimiter (<c>--boundary--</c>). New multiparts always get one when written.</summary>
    public bool HasCloseDelimiter => _closeDelimiter is not null || _generateClose;

    /// <summary>Appends a child. Its delimiter is generated when the multipart is written.</summary>
    /// <param name="child">An entity with no parent.</param>
    /// <exception cref="ArgumentException">The entity already has a parent.</exception>
    public void Add(MimeEntity child) => Insert(_children.Count, child);

    /// <summary>Inserts a child at <paramref name="index"/>.</summary>
    /// <param name="index">Position.</param>
    /// <param name="child">An entity with no parent.</param>
    /// <exception cref="ArgumentException">The entity already has a parent.</exception>
    public void Insert(int index, MimeEntity child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent is not null)
        {
            throw new ArgumentException("The entity already has a parent.", nameof(child));
        }

        child.Parent = this;
        _children.Insert(index, child);
        _delimiters.Insert(index, null);
        StructureChanged();
    }

    /// <summary>Removes the child at <paramref name="index"/> together with its delimiter.</summary>
    /// <param name="index">Position.</param>
    public void RemoveAt(int index)
    {
        _children[index].Parent = null;
        _children.RemoveAt(index);
        _delimiters.RemoveAt(index);
        StructureChanged();
    }

    private void StructureChanged()
    {
        if (_closeDelimiter is null)
        {
            _generateClose = true;
        }

        MarkModified();
    }

    /// <summary>The text before the first delimiter.</summary>
    /// <returns>The bytes.</returns>
    public byte[] GetPreamble() => _preamble.ToArray();

    /// <summary>Replaces the text before the first delimiter.</summary>
    /// <param name="bytes">The bytes (no trailing line end: it belongs to the delimiter).</param>
    public void SetPreamble(ReadOnlyMemory<byte> bytes)
    {
        _preamble = RawContent.FromBytes(bytes);
        MarkModified();
    }

    /// <summary>The text after the closing delimiter.</summary>
    /// <returns>The bytes.</returns>
    public byte[] GetEpilogue() => _epilogue.ToArray();

    /// <summary>Replaces the text after the closing delimiter.</summary>
    /// <param name="bytes">The bytes.</param>
    public void SetEpilogue(ReadOnlyMemory<byte> bytes)
    {
        _epilogue = RawContent.FromBytes(bytes);
        MarkModified();
    }

    internal void SetParsedRanges(RawContent preamble, RawContent epilogue)
    {
        _preamble = preamble;
        _epilogue = epilogue;
    }

    internal void AddParsed(byte[] delimiter, MimeEntity child)
    {
        child.Parent = this;
        _children.Add(child);
        _delimiters.Add(delimiter);
    }

    internal void SetParsedClose(byte[] delimiter) => _closeDelimiter = delimiter;

    // Takes the last parsed child out of the list and returns the delimiter that introduced it.
    internal byte[] RemoveLastParsed()
    {
        int last = _children.Count - 1;
        byte[] delimiter = _delimiters[last]!;
        _children[last].Parent = null;
        _children.RemoveAt(last);
        _delimiters.RemoveAt(last);
        return delimiter;
    }

    internal override IEnumerable<MimeEntity> ChildEntities() => _children;

    internal override IEnumerable<WriteSegment> BodySegments(bool forceAssemble)
    {
        if (_preamble.Length > 0)
        {
            yield return _preamble.Segment();
        }

        for (int i = 0; i < _children.Count; i++)
        {
            byte[] delimiter = _delimiters[i] ?? MimeWriterSupport.FormatDelimiter(_boundary, close: false, leadingNewline: i > 0 || _preamble.Length > 0, "\r\n"u8);
            yield return new WriteSegment(delimiter, null, 0, delimiter.Length);
            foreach (WriteSegment s in _children[i].Segments(forceAssemble))
            {
                yield return s;
            }
        }

        if (_closeDelimiter is not null)
        {
            yield return new WriteSegment(_closeDelimiter, null, 0, _closeDelimiter.Length);
        }
        else if (_generateClose)
        {
            byte[] close = MimeWriterSupport.FormatDelimiter(_boundary, close: true, leadingNewline: _children.Count > 0 || _preamble.Length > 0, "\r\n"u8);
            yield return new WriteSegment(close, null, 0, close.Length);
        }

        if (_epilogue.Length > 0)
        {
            yield return _epilogue.Segment();
        }
    }
}

/// <summary>Builds the entity tree from reader events.</summary>
internal sealed class MimeTreeBuilder
{
    private sealed class Pending
    {
        public long Start;
        public byte[]? Envelope;
        public readonly List<MimeHeader> Headers = [];
        public List<MimeDefect> Defects = [];
        public MimeEntity? Entity;
        public long BodyStart;
        public byte[]? PendingDelimiter;
        public MimeEntity? GhostCandidate;
        public List<MimeDefect>? DefectsOfGhost;
        public long PreambleEnd = -1;
        public long EpilogueStart = -1;
    }

    private readonly IRandomAccessSource _source;
    private readonly Stack<Pending> _stack = new();
    private MimeEntity? _root;

    public MimeTreeBuilder(IRandomAccessSource source) => _source = source;

    public void Handle(MimeReader r)
    {
        switch (r.Kind)
        {
            case MimeEventKind.EntityStart:
                _stack.Push(new Pending { Start = r.Offset });
                break;

            case MimeEventKind.Header:
            {
                ReadOnlySpan<byte> raw = r.Raw;
                int valueOffset = raw.IndexOf((byte)':') + 1;
                _stack.Peek().Headers.Add(new MimeHeader(raw.ToArray(), r.HeaderName.Length, valueOffset, r.HeaderValue.Length) { SourceOffset = r.Offset });
                break;
            }

            case MimeEventKind.EnvelopeLine:
                _stack.Peek().Envelope = r.Raw.ToArray();
                break;

            case MimeEventKind.HeadersEnd:
                StartEntity(r);
                break;

            case MimeEventKind.Delimiter:
            {
                Pending p = _stack.Peek();
                var m = (MimeMultipart)p.Entity!;
                byte[] bytes = r.Raw.ToArray();
                if (p.GhostCandidate is not null)
                {
                    // Two delimiter lines in a row: Python and MimeKit read them as one, so the empty part between them is
                    // not a child. Its (empty) bytes and delimiter stay in the next delimiter, so writing is unchanged.
                    byte[] carried = m.RemoveLastParsed();
                    byte[] merged = new byte[carried.Length + bytes.Length];
                    carried.CopyTo(merged, 0);
                    bytes.CopyTo(merged, carried.Length);
                    bytes = merged;
                    p.DefectsOfGhost?.ForEach(d => p.Defects.Add(d));
                    p.GhostCandidate = null;
                    p.DefectsOfGhost = null;
                }

                if (p.PreambleEnd < 0)
                {
                    p.PreambleEnd = r.Offset;
                }

                if (r.IsCloseDelimiter)
                {
                    m.SetParsedClose(bytes);
                    p.EpilogueStart = r.Offset + r.Length;
                }
                else
                {
                    p.PendingDelimiter = bytes;
                }

                break;
            }

            case MimeEventKind.EntityEnd:
                EndEntity(r);
                break;

            case MimeEventKind.Defect:
                if (_stack.Count > 0)
                {
                    _stack.Peek().Defects.Add(r.Defect);
                }
                else
                {
                    _root?.DefectList.Add(r.Defect);
                }

                break;
        }
    }

    private void StartEntity(MimeReader r)
    {
        Pending p = _stack.Peek();
        MimeEntity e = r.EntityType switch
        {
            MimeEntityType.Multipart => new MimeMultipart(r.Boundary.ToArray()),
            MimeEntityType.Message => new MimeMessagePart(parsed: true),
            _ => new MimePart(parsed: true),
        };
        foreach (MimeHeader h in p.Headers)
        {
            e.Headers.AddParsed(h);
        }

        e.Separator = r.Raw.ToArray();
        e.EnvelopeLine = p.Envelope ?? default;
        e.Source = _source;
        e.SourceStart = p.Start;
        e.DefectList.AddRange(p.Defects);
        p.Defects = e.DefectList;
        p.Entity = e;
        p.BodyStart = r.Offset + r.Length;

        if (_stack.Count == 1)
        {
            _root = e;
        }
        else
        {
            Pending parent = _stack.ElementAt(1);
            if (parent.Entity is MimeMultipart parentMultipart)
            {
                parentMultipart.AddParsed(parent.PendingDelimiter!, e);
            }
            else
            {
                ((MimeMessagePart)parent.Entity!).SetParsedMessage(e);
            }
        }
    }

    private void EndEntity(MimeReader r)
    {
        Pending p = _stack.Pop();
        MimeEntity e = p.Entity!;
        long end = r.Offset;
        e.SourceLength = end - p.Start;
        if (e is MimePart && e.Headers.Count == 0 && e.Separator.Length == 0 && end == p.BodyStart && _stack.Count > 0
            && _stack.Peek().Entity is MimeMultipart)
        {
            _stack.Peek().GhostCandidate = e;
            _stack.Peek().DefectsOfGhost = [.. e.DefectList];
        }
        else if (_stack.Count > 0)
        {
            _stack.Peek().GhostCandidate = null;
            _stack.Peek().DefectsOfGhost = null;
        }

        switch (e)
        {
            case MimePart leaf:
                leaf.SetParsedBody(RawContent.FromRange(_source, p.BodyStart, end - p.BodyStart));
                break;
            case MimeMultipart m:
            {
                long preambleEnd = p.PreambleEnd >= 0 ? p.PreambleEnd : end;
                long epilogueStart = p.EpilogueStart >= 0 ? p.EpilogueStart : end;
                m.SetParsedRanges(
                    RawContent.FromRange(_source, p.BodyStart, preambleEnd - p.BodyStart),
                    RawContent.FromRange(_source, epilogueStart, end - epilogueStart));
                break;
            }
        }
    }

    public MimeEntity Finish(MimeReader r)
    {
        MimeEntity root = _root!;
        root.IsTruncated = r.IsTruncated;
        return root;
    }
}
