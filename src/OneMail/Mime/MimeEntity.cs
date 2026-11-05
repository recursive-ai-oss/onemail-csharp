using System.Buffers;
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

    public static RawContent FromSource(IRandomAccessSource source) => new(source, 0, source.Length, null);

    /// <summary>Takes the array without copying; the caller must not use it afterwards.</summary>
    public static RawContent FromOwnedBytes(byte[] bytes) => new(null, 0, bytes.Length, bytes);

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

    /// <summary>The bytes as a view of memory they already live in (the in-memory source the tree was parsed from, or bytes set by the caller), or false when they sit in a file, a stream or a generated source.</summary>
    public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        if (_bytes is not null)
        {
            memory = _bytes;
            return true;
        }

        if (_source is MemorySource m && _offset >= 0 && _offset <= m.Memory.Length && Length <= m.Memory.Length - _offset)
        {
            memory = m.Memory.Slice((int)_offset, (int)Length);
            return true;
        }

        memory = default;
        return false;
    }

    public WriteSegment Segment() => new(_bytes, _source, _offset, Length);

    /// <summary>
    /// A guess at what this body decodes to as base64: the first line's length and line end give the layout, the last bytes the
    /// padding. It is exact for bodies wrapped at one width, which nearly all are; anything else returns -1 or a close number and
    /// the caller copes with either.
    /// </summary>
    public long EstimateBase64Decoded()
    {
        const int Head = 256;
        if (Length < 2 * Head)
        {
            return -1;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Head);
        try
        {
            Span<byte> head = buffer.AsSpan(0, Head);
            if (Peek(0, head) != Head)
            {
                return -1;
            }

            int lf = head.IndexOf((byte)'\n');
            if (lf < 0)
            {
                return -1;
            }

            int eol = lf > 0 && head[lf - 1] == (byte)'\r' ? 2 : 1;
            long line = lf + 1 - eol;
            if (line < 4 || line % 4 != 0)
            {
                return -1;
            }

            // The body usually ends with the line end of its last line, which is not data.
            Span<byte> tail = buffer.AsSpan(0, 8);
            int end = Peek(Length - tail.Length, tail);
            long dataLength = Length;
            while (end > 0 && tail[end - 1] is (byte)'\r' or (byte)'\n')
            {
                end--;
                dataLength--;
            }

            long stride = line + eol;
            long full = dataLength / stride;
            long rest = dataLength - (full * stride);
            if (rest > line)
            {
                return -1; // a partial line end: not the layout the first line promised
            }

            long chars = (full * line) + rest;
            if (chars % 4 != 0)
            {
                return -1;
            }

            int pad = 0;
            while (end > 0 && pad < 2 && tail[end - 1] == (byte)'=')
            {
                pad++;
                end--;
            }

            return (chars / 4 * 3) - pad;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private int Peek(long offset, Span<byte> destination)
    {
        if (_bytes is not null)
        {
            int n = (int)Math.Min(destination.Length, Math.Max(0, _bytes.Length - offset));
            _bytes.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        return _source!.Read(_offset + offset, destination);
    }
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
        ContentTransferEncoding encoding = TransferEncoding;
        if (encoding is not (ContentTransferEncoding.Base64 or ContentTransferEncoding.QuotedPrintable or ContentTransferEncoding.UUEncode))
        {
            return _body.ToArray(); // 7bit, 8bit, binary and unknown encodings are the bytes as they are
        }

        using Stream s = OpenDecodedContent();
        // Start from the size the decoded body will most likely have, so that the usual case fills one array without growing a
        // buffer or trimming it. Base64 decodes to at most three bytes per four and the others to at most their own length.
        long guess = encoding == ContentTransferEncoding.Base64 ? _body.EstimateBase64Decoded() : -1;
        if (guess < 0)
        {
            guess = encoding == ContentTransferEncoding.Base64 ? _body.Length / 4 * 3 + 3 : _body.Length;
        }

        // Every byte up to the returned count is written before it is read, so the array need not be cleared.
        byte[] buf = GC.AllocateUninitializedArray<byte>((int)Math.Clamp(guess, 16, Array.MaxLength - 64));
        int n = 0;
        while (true)
        {
            if (n == buf.Length)
            {
                // Full: one byte tells whether that was the end.
                int next = s.ReadByte();
                if (next < 0)
                {
                    break;
                }

                Array.Resize(ref buf, (int)Math.Min((long)buf.Length * 2, Array.MaxLength));
                buf[n++] = (byte)next;
                continue;
            }

            int r = s.Read(buf, n, buf.Length - n);
            if (r == 0)
            {
                break;
            }

            n += r;
        }

        return n == buf.Length ? buf : buf.AsSpan(0, n).ToArray();
    }

    /// <summary>
    /// Gives the body as transported (still encoded) as a view of memory, without copying, when the part lives in memory: a
    /// tree parsed from a <see cref="ReadOnlyMemory{T}"/> or <see cref="Sources.MemorySource"/>, or a body the caller set.
    /// </summary>
    /// <param name="content">The bytes. Valid while the source the tree was parsed from is alive and unchanged.</param>
    /// <returns>False when the body sits in a file, a stream, a ranged source or is generated while read; use <see cref="OpenRawContent"/> then.</returns>
    public bool TryGetRawContentMemory(out ReadOnlyMemory<byte> content) => _body.TryGetMemory(out content);

    /// <summary>
    /// Gives the decoded body as a view of the source, without copying, when no decoding is needed: the transfer encoding is
    /// 7bit, 8bit, binary or unknown (the bytes are the content) and the part lives in memory.
    /// </summary>
    /// <param name="content">The bytes. Valid while the source the tree was parsed from is alive and unchanged.</param>
    /// <returns>False for base64, quoted-printable and uuencode bodies, and for parts that are not in memory; use <see cref="GetDecodedContent"/> or <see cref="CopyDecodedContentTo(Span{byte})"/> then.</returns>
    public bool TryGetDecodedContentMemory(out ReadOnlyMemory<byte> content)
    {
        if (IsDecodedAsIs(TransferEncoding))
        {
            return _body.TryGetMemory(out content);
        }

        content = default;
        return false;
    }

    private static bool IsDecodedAsIs(ContentTransferEncoding encoding) =>
        encoding is not (ContentTransferEncoding.Base64 or ContentTransferEncoding.QuotedPrintable or ContentTransferEncoding.UUEncode);

    /// <summary>
    /// The room <see cref="CopyDecodedContentTo(Span{byte})"/> needs: an upper bound on the decoded size, exact for 7bit, 8bit and
    /// binary bodies and a little over the true size (up to about a third more for base64) for decoded ones.
    /// </summary>
    /// <returns>Bytes. Saturates at <see cref="int.MaxValue"/>; larger bodies cannot be copied to one span, use <see cref="OpenDecodedContent"/> or <see cref="CopyDecodedContentTo(System.Buffers.IBufferWriter{byte})"/>.</returns>
    public int GetMaxDecodedContentLength()
    {
        // The decoders' own bounds, so the span is always enough for Decode plus Finish.
        long raw = _body.Length;
        if (raw > int.MaxValue / 2)
        {
            return int.MaxValue;
        }

        int length = (int)raw;
        long max = TransferEncoding switch
        {
            ContentTransferEncoding.Base64 => Base64Decoder.MaxOutputLength(length) + 4,
            ContentTransferEncoding.QuotedPrintable => QuotedPrintableDecoder.MaxOutputLength(length) + 4,
            ContentTransferEncoding.UUEncode => UuDecoder.MaxOutputLength(length) + 4,
            _ => raw,
        };
        return (int)Math.Min(max, int.MaxValue);
    }

    /// <summary>Decodes the body into <paramref name="destination"/> without allocating a result array.</summary>
    /// <param name="destination">At least <see cref="GetMaxDecodedContentLength"/> bytes.</param>
    /// <returns>The decoded bytes written: the same bytes <see cref="GetDecodedContent"/> returns.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="GetMaxDecodedContentLength"/>.</exception>
    public int CopyDecodedContentTo(Span<byte> destination)
    {
        if (destination.Length < GetMaxDecodedContentLength())
        {
            throw new ArgumentException("The destination is shorter than GetMaxDecodedContentLength().", nameof(destination));
        }

        ContentTransferEncoding encoding = TransferEncoding;
        if (_body.TryGetMemory(out ReadOnlyMemory<byte> raw))
        {
            if (IsDecodedAsIs(encoding))
            {
                raw.Span.CopyTo(destination);
                return raw.Length;
            }

            // One Decode call over the whole body decodes the same bytes as the stream does piece by piece.
            return DecodeWhole(encoding, raw.Span, destination);
        }

        using Stream s = OpenDecodedContent();
        int n = 0;
        while (n < destination.Length)
        {
            int r = s.Read(destination.Slice(n));
            if (r == 0)
            {
                break;
            }

            n += r;
        }

        return n;
    }

    /// <summary>Decodes the body into <paramref name="writer"/>, in chunks for bodies outside memory.</summary>
    /// <param name="writer">Receives the bytes <see cref="GetDecodedContent"/> returns.</param>
    /// <returns>The number of bytes written.</returns>
    public long CopyDecodedContentTo(System.Buffers.IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ContentTransferEncoding encoding = TransferEncoding;
        if (_body.TryGetMemory(out ReadOnlyMemory<byte> raw))
        {
            if (IsDecodedAsIs(encoding))
            {
                writer.Write(raw.Span);
                return raw.Length;
            }

            Span<byte> room = writer.GetSpan(GetMaxDecodedContentLength());
            int w = DecodeWhole(encoding, raw.Span, room);
            writer.Advance(w);
            return w;
        }

        long total = 0;
        using Stream s = OpenDecodedContent();
        while (true)
        {
            Span<byte> room = writer.GetSpan(8192);
            int r = s.Read(room);
            if (r == 0)
            {
                return total;
            }

            writer.Advance(r);
            total += r;
        }
    }

    private static int DecodeWhole(ContentTransferEncoding encoding, ReadOnlySpan<byte> input, Span<byte> output)
    {
        switch (encoding)
        {
            case ContentTransferEncoding.Base64:
                {
                    var d = new Base64Decoder();
                    int w = d.Decode(input, output);
                    return w + d.Finish(output.Slice(w));
                }

            case ContentTransferEncoding.QuotedPrintable:
                {
                    var d = new QuotedPrintableDecoder();
                    int w = d.Decode(input, output);
                    return w + d.Finish(output.Slice(w));
                }

            default:
                {
                    var d = new UuDecoder();
                    int w = d.Decode(input, output);
                    return w + d.Finish(output.Slice(w));
                }
        }
    }

    /// <summary>Replaces the body with bytes that are already encoded as the headers say.</summary>
    /// <param name="encodedBytes">The body as it goes on the wire.</param>
    public void SetRawContent(ReadOnlyMemory<byte> encodedBytes)
    {
        _body = RawContent.FromBytes(encodedBytes);
        MarkModified();
    }

    /// <summary>Replaces the body with <paramref name="data"/>, encoding it and setting <c>Content-Transfer-Encoding</c> to match.</summary>
    /// <param name="data">The decoded content. It is copied.</param>
    /// <param name="encoding">Base64 (default), quoted-printable, or an identity encoding (7bit, 8bit, binary: bytes as given).</param>
    public void SetDecodedContent(ReadOnlySpan<byte> data, ContentTransferEncoding encoding = ContentTransferEncoding.Base64)
    {
        // Base64 is encoded as the body is read or written, so a large attachment is held once, not twice.
        _body = encoding switch
        {
            ContentTransferEncoding.Base64 => RawContent.FromSource(new Base64WrappedSource(CopyOf(data))),
            ContentTransferEncoding.QuotedPrintable => RawContent.FromOwnedBytes(TransferEncoder.QuotedPrintable(data)),
            _ => RawContent.FromOwnedBytes(data.ToArray()),
        };
        SetTransferEncodingHeader(encoding);
    }

    /// <summary>
    /// Like <see cref="SetDecodedContent"/>, but keeps <paramref name="data"/> itself instead of a copy: base64 and the identity
    /// encodings (7bit, 8bit, binary) read it in place whenever the body is written or read, so a large attachment is never
    /// duplicated.
    /// </summary>
    /// <param name="data">The decoded content. <b>Do not change it, and keep it alive, until the part has been written (or the
    /// part is discarded or its body replaced).</b> Changing it earlier changes what is written. Quoted-printable encodes
    /// eagerly, so that form holds a new array and does not borrow.</param>
    /// <param name="encoding">Base64 (default), quoted-printable, or an identity encoding (7bit, 8bit, binary: bytes as given).</param>
    public void SetDecodedContentBorrowed(ReadOnlyMemory<byte> data, ContentTransferEncoding encoding = ContentTransferEncoding.Base64)
    {
        _body = encoding switch
        {
            ContentTransferEncoding.Base64 => RawContent.FromSource(new Base64WrappedSource(data)),
            ContentTransferEncoding.QuotedPrintable => RawContent.FromOwnedBytes(TransferEncoder.QuotedPrintable(data.Span)),
            _ => RawContent.FromSource(new MemorySource(data)),
        };
        SetTransferEncodingHeader(encoding);
    }

    private void SetTransferEncodingHeader(ContentTransferEncoding encoding)
    {
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

    private static byte[] CopyOf(ReadOnlySpan<byte> data)
    {
        byte[] copy = GC.AllocateUninitializedArray<byte>(data.Length);
        data.CopyTo(copy);
        return copy;
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
    private byte[] _scratch = [];
    private int _scratchLength;

    public MimeTreeBuilder(IRandomAccessSource source) => _source = source;

    // The scratch buffer is only needed between an entity's first field and its end of headers, so it goes back to the pool there.
    private void ReturnScratch()
    {
        if (_scratch.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = [];
        }
    }

    public void Handle(MimeReader r)
    {
        switch (r.Kind)
        {
            case MimeEventKind.EntityStart:
                _scratchLength = 0;
                _stack.Push(new Pending { Start = r.Offset });
                break;

            case MimeEventKind.Header:
            {
                // The fields of one entity are collected into one block, which becomes their shared array at HeadersEnd. Only one
                // entity is ever between its first field and its end of headers, so one scratch buffer serves the whole tree.
                ReadOnlySpan<byte> raw = r.Raw;
                int valueOffset = raw.IndexOf((byte)':') + 1;
                if (_scratch.Length - _scratchLength < raw.Length)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(1024, Math.Max(_scratch.Length * 2, _scratchLength + raw.Length)));
                    _scratch.AsSpan(0, _scratchLength).CopyTo(bigger);
                    ReturnScratch();
                    _scratch = bigger;
                }

                raw.CopyTo(_scratch.AsSpan(_scratchLength));
                _stack.Peek().Headers.Add(new MimeHeader(_scratchLength, raw.Length, r.HeaderName.Length, valueOffset, r.HeaderValue.Length) { SourceOffset = r.Offset });
                _scratchLength += raw.Length;
                break;
            }

            case MimeEventKind.EnvelopeLine:
                _stack.Peek().Envelope = r.Raw.ToArray();
                break;

            case MimeEventKind.HeadersEnd:
                if (_scratchLength > 0)
                {
                    byte[] block = _scratch.AsSpan(0, _scratchLength).ToArray();
                    foreach (MimeHeader h in _stack.Peek().Headers)
                    {
                        h.Attach(block);
                    }

                    _scratchLength = 0;
                }

                ReturnScratch();
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
        e.Headers.AdoptParsed(p.Headers);

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
