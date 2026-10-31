using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Tnef;

/// <summary>
/// A TNEF stream read as a MAPI message: the encapsulated properties (attMsgProps, attRecipTable, attAttachment) fill the property bag, the legacy
/// attributes fill in what the properties leave out, and attachments and embedded messages become items of the shared MAPI model. The attribute layer
/// stays reachable through <see cref="Document"/>, and an untouched stream still writes back byte for byte through it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence</b> (MS-OXTNEF 2.1.3): an encapsulated property beats a legacy attribute for the same thing; a legacy attribute only adds a property the
/// list does not have, and disagreement is reported as <see cref="TnefDefectKind.AmbiguousLegacyConflict"/>. Text taken from legacy attributes is decoded
/// with attOemCodepage and stored as Unicode properties, so it never depends on which code page the shared model resolves for 8-bit strings.
/// </para>
/// <para>
/// The code page for the message's own 8-bit properties is attOemCodepage (MS-OXTNEF 2.3.3.2). When the stream has no PidTagMessageCodepage the reader
/// adds one with that value, because the shared model resolves 8-bit strings from that property first. Reading never throws on malformed content.
/// </para>
/// </remarks>
public sealed class TnefMessage : MapiMessage
{
    internal TnefMessage(MapiObject? parent, TnefDocument document, int depth)
        : base(parent)
    {
        Document = document;
        EmbeddingDepth = depth;
    }

    /// <summary>The attribute layer this message was built from.</summary>
    public TnefDocument Document { get; }

    /// <summary>The nesting level: 0 for the top-level message, 1 for a message embedded in it, and so on.</summary>
    public int EmbeddingDepth { get; }

    /// <summary>The primary code page of attOemCodepage, or null when the attribute is missing or zero.</summary>
    public int? OemCodePage { get; internal set; }

    /// <summary>Everything the TNEF layers found wrong with this message's own stream (attribute structure included, embedded messages excluded).</summary>
    public IReadOnlyList<TnefDefect> TnefDefects { get; internal set; } = [];

    /// <summary>Defects beyond <see cref="TnefReaderOptions.MaxDefects"/>, which are counted and not kept.</summary>
    public int DroppedDefects { get; internal set; }

    /// <summary>Reads a message from a TNEF stream.</summary>
    /// <param name="source">The stream bytes; keep the source open while the message's attachment data is used.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>The message. A stream without the TNEF signature gives an empty message whose <see cref="TnefDefects"/> start with <see cref="TnefDefectKind.NotTnef"/>.</returns>
    public static TnefMessage Read(IRandomAccessSource source, TnefReaderOptions? options = null) =>
        Read(TnefDocument.Read(source, options), options);

    /// <summary>Reads a message from TNEF bytes in memory.</summary>
    /// <param name="data">The stream; it must stay unchanged while the message is used.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>The message.</returns>
    public static TnefMessage Read(ReadOnlyMemory<byte> data, TnefReaderOptions? options = null) =>
        Read(TnefDocument.Read(data, options), options);

    /// <summary>Reads a message from a document already split into attributes.</summary>
    /// <param name="document">The document.</param>
    /// <param name="options">Limits, or null for the defaults. Use the options the document was read with.</param>
    /// <returns>The message.</returns>
    public static TnefMessage Read(TnefDocument document, TnefReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TnefMessageBuilder.Build(document, options ?? new TnefReaderOptions(), 0, null);
    }

    /// <summary>Writes the message as a TNEF stream; shorthand for <see cref="TnefMessageWriter.Write"/>.</summary>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="options">Writer choices, or null.</param>
    /// <returns>What could not be carried.</returns>
    public IReadOnlyList<string> WriteTo(Stream output, TnefWriterOptions? options = null) => TnefMessageWriter.Write(this, output, options);
}

/// <summary>An attachment of a <see cref="TnefMessage"/>. An embedded message is read when first asked for.</summary>
public sealed class TnefAttachment : MapiAttachment
{
    private readonly TnefReaderOptions _options;
    private readonly IRandomAccessSource? _source;
    private readonly long _embeddedOffset;
    private readonly long _embeddedLength;
    private readonly int _depth;

    internal TnefAttachment(MapiObject parent, TnefReaderOptions options, int depth, IRandomAccessSource? source, long embeddedOffset, long embeddedLength)
        : base(parent)
    {
        _options = options;
        _depth = depth;
        _source = source;
        _embeddedOffset = embeddedOffset;
        _embeddedLength = embeddedLength;
    }

    /// <summary>Offset in the stream of the attribute that started this attachment (its attAttachRendData, or the first attachment attribute when that is missing).</summary>
    public long StreamOffset { get; internal set; }

    /// <inheritdoc/>
    protected override MapiMessage? LoadEmbeddedMessage()
    {
        if (_source is null)
        {
            return null;
        }

        var slice = new TnefSliceSource(_source, _embeddedOffset, _embeddedLength);
        return TnefMessageBuilder.Build(TnefDocument.Read(slice, _options), _options, _depth, this);
    }
}

/// <summary>A window onto part of another source, used to read a nested TNEF stream where it lies.</summary>
internal sealed class TnefSliceSource : IRandomAccessSource
{
    private readonly IRandomAccessSource _inner;
    private readonly long _offset;

    public TnefSliceSource(IRandomAccessSource inner, long offset, long length)
    {
        _inner = inner;
        _offset = offset;
        Length = length;
    }

    public long Length { get; }

    public bool SupportsSyncRead => _inner.SupportsSyncRead;

    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > Length - offset)
        {
            buffer = buffer[..(int)(Length - offset)];
        }

        return _inner.Read(_offset + offset, buffer);
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
        {
            return ValueTask.FromResult(0);
        }

        if (buffer.Length > Length - offset)
        {
            buffer = buffer[..(int)(Length - offset)];
        }

        return _inner.ReadAsync(_offset + offset, buffer, cancellationToken);
    }

    public void Dispose()
    {
    }
}

internal static class TnefMessageBuilder
{
    // MS-OXTNEF 2.1.3.4: the interface identifier of a Message object, {00020307-0000-0000-C000-000000000046}.
    internal static readonly byte[] MessageIid = [0x07, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    // MS-OXTNEF 2.3.3.9: PidTagAttachTag for an OLE attachment, and PidTagAttachEncoding for a MacBinary one.
    private static readonly byte[] OleAttachTag = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x14, 0x03, 0x0A, 0x03, 0x02, 0x01];
    private static readonly byte[] MacBinaryEncoding = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x14, 0x03, 0x0B, 0x01];

    private sealed class AttachmentParts
    {
        public long Offset;
        public readonly List<TnefParsedProperty> Props = [];
        public readonly Dictionary<ushort, TnefAttributeRecord> Legacy = [];
    }

    internal sealed class Context
    {
        public required TnefDocument Doc;
        public required TnefReaderOptions Options;
        public required TnefMessage Message;
        public readonly List<TnefDefect> Defects = [];
        public int Dropped;

        public void Add(TnefDefectKind kind, long offset, string detail)
        {
            if (Defects.Count < Options.MaxDefects)
            {
                Defects.Add(new TnefDefect(kind, offset, detail));
            }
            else
            {
                Dropped++;
            }

            Message.AddDefect(new MapiDefect(MapiDefectKind.LowerLayerDefect, "tnef", offset, kind + ": " + detail));
        }
    }

    public static TnefMessage Build(TnefDocument doc, TnefReaderOptions o, int depth, MapiObject? parent)
    {
        var msg = new TnefMessage(parent, doc, depth);
        var cx = new Context { Doc = doc, Options = o, Message = msg };
        foreach (TnefDefect d in doc.Defects)
        {
            cx.Defects.Add(d);
            msg.AddDefect(new MapiDefect(MapiDefectKind.LowerLayerDefect, "tnef", d.Offset, d.Kind + ": " + d.Detail));
        }

        cx.Dropped = doc.DroppedDefects;
        msg.TnefDefects = cx.Defects;
        try
        {
            Fill(cx, depth);
        }
        finally
        {
            msg.DroppedDefects = cx.Dropped;
        }

        return msg;
    }

    private static void Fill(Context cx, int depth)
    {
        TnefMessage msg = cx.Message;
        TnefReaderOptions o = cx.Options;
        IRandomAccessSource src = cx.Doc.Source;
        var reader = new TnefPropertyReader(src, o, cx.Add);
        var legacy = new Dictionary<ushort, TnefAttributeRecord>();
        var msgProps = new List<TnefParsedProperty>();
        var rows = new List<List<TnefParsedProperty>>();
        var attachments = new List<AttachmentParts>();
        AttachmentParts? current = null;
        TnefAttributeRecord? version = null;
        TnefAttributeRecord? oem = null;

        foreach (TnefAttributeRecord a in cx.Doc.Attributes)
        {
            ushort low = (ushort)(a.Id & 0xFFFF);
            if (a.LevelValue == 1)
            {
                switch (low)
                {
                    case 0x9006:
                        version = a;
                        break;
                    case 0x9007:
                        oem = a;
                        break;
                    case 0x9003:
                        using (var c = new TnefCursor(src, a.DataOffset, a.AvailableLength))
                        {
                            reader.ReadList(c, msgProps);
                        }

                        break;
                    case 0x9004:
                        ReadRecipientTable(cx, reader, a, rows);
                        break;
                    case 0x8000 or 0x8004 or 0x8005 or 0x8006 or 0x8007 or 0x8008 or 0x8009 or 0x800C or 0x800D or 0x8020
                        or 0x0600 or 0x0000 or 0x0001 or 0x0002 or 0x0006 or 0x0007 or 0x0008 or 0x0009:
                        if (!legacy.TryAdd(low, a))
                        {
                            cx.Add(TnefDefectKind.AmbiguousDuplicateAttribute, a.Offset, "attribute 0x" + a.Id.ToString("X8") + " appears again; the last one is used");
                            legacy[low] = a;
                        }

                        break;
                    default:
                        cx.Add(TnefDefectKind.UnknownAttribute, a.Offset, "message-level attribute 0x" + a.Id.ToString("X8"));
                        break;
                }
            }
            else if (a.LevelValue == 2)
            {
                if (low == 0x9002)
                {
                    current = new AttachmentParts { Offset = a.Offset };
                    attachments.Add(current);
                    current.Legacy[low] = a;
                    if (attachments.Count > o.MaxAttachments)
                    {
                        cx.Add(TnefDefectKind.LimitReached, a.Offset, "more than " + o.MaxAttachments + " attachments");
                        attachments.RemoveAt(attachments.Count - 1);
                        break;
                    }

                    continue;
                }

                if (low is not (0x800F or 0x8010 or 0x8011 or 0x8012 or 0x8013 or 0x9001 or 0x9005))
                {
                    cx.Add(TnefDefectKind.UnknownAttribute, a.Offset, "attachment-level attribute 0x" + a.Id.ToString("X8"));
                    continue;
                }

                if (current is null)
                {
                    // MS-OXTNEF 2.1.3: each set of attachment attributes must begin with attAttachRendData.
                    cx.Add(TnefDefectKind.AttachmentWithoutRendData, a.Offset, "attachment attribute 0x" + a.Id.ToString("X8") + " before any attAttachRendData");
                    current = new AttachmentParts { Offset = a.Offset };
                    attachments.Add(current);
                }

                if (low == 0x9005)
                {
                    using var c = new TnefCursor(src, a.DataOffset, a.AvailableLength);
                    reader.ReadList(c, current.Props);
                }
                else
                {
                    current.Legacy[low] = a;
                }
            }
        }

        CheckVersion(cx, version);
        ReadCodePage(cx, oem, out MailEncoding? oemEncoding);

        // The encapsulated properties first, in stream order.
        var seen = new HashSet<uint>();
        foreach (TnefParsedProperty p in msgProps)
        {
            if (!seen.Add(p.Property.Tag.Value))
            {
                msg.AddDefect(new MapiDefect(MapiDefectKind.AmbiguousDuplicateProperty, "tnef", 0, "property " + p.Property.Tag + " appears more than once; lookups return the last"));
            }

            msg.Properties.Add(p.Property);
        }

        if (msg.OemCodePage is int cp && msg.Properties.Get(MapiPid.MessageCodepage) is null && oemEncoding is not null)
        {
            // The shared model resolves 8-bit strings from PidTagMessageCodepage first, and MS-OXTNEF 2.3.3.2 makes attOemCodepage the writer's code page.
            msg.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.MessageCodepage, MapiType.Integer32), BitConverter.GetBytes(cp)));
        }
        else if (msg.OemCodePage is int cp2 && msg.Properties.Get(MapiPid.MessageCodepage)?.AsInt32() is int pcp && pcp != cp2)
        {
            cx.Add(TnefDefectKind.AmbiguousCodePage, 0, "attOemCodepage " + cp2 + " but PidTagMessageCodepage " + pcp + "; the property decides how 8-bit strings are read");
        }

        MessageLegacy.Apply(cx, legacy, oemEncoding);

        foreach (List<TnefParsedProperty> row in rows)
        {
            var r = new MapiRecipient(msg);
            foreach (TnefParsedProperty p in row)
            {
                r.Properties.Add(p.Property);
            }

            msg.AddRecipient(r);
        }

        foreach (AttachmentParts parts in attachments)
        {
            msg.AddAttachment(BuildAttachment(cx, parts, depth, oemEncoding));
        }

        CheckRtf(cx, msg);
    }

    private static void CheckVersion(Context cx, TnefAttributeRecord? version)
    {
        if (version is null)
        {
            cx.Add(TnefDefectKind.UnexpectedVersion, 0, "no attTnefVersion attribute");
            return;
        }

        byte[] v = version.ReadData();
        if (v.Length != 4 || BinaryPrimitives.ReadUInt32LittleEndian(v) != 0x00010000)
        {
            cx.Add(TnefDefectKind.UnexpectedVersion, version.Offset, "attTnefVersion is not 0x00010000");
        }
    }

    private static void ReadCodePage(Context cx, TnefAttributeRecord? oem, out MailEncoding? encoding)
    {
        encoding = null;
        if (oem is null || oem.AvailableLength < 4)
        {
            cx.Add(TnefDefectKind.MissingCodePage, oem?.Offset ?? 0, oem is null ? "no attOemCodepage attribute" : "attOemCodepage is shorter than 4 bytes");
            return;
        }

        Span<byte> b = stackalloc byte[4];
        oem.ReadData(0, b);
        int cp = (int)BinaryPrimitives.ReadUInt32LittleEndian(b);
        if (cp <= 0)
        {
            cx.Add(TnefDefectKind.MissingCodePage, oem.Offset, "attOemCodepage is zero");
            return;
        }

        cx.Message.OemCodePage = cp;
        encoding = MailEncoding.ForCodePage(cp);
        if (encoding is null)
        {
            cx.Add(TnefDefectKind.UnsupportedCodePage, oem.Offset, "code page " + cp + " has no decoder; windows-1252 stands in");
        }
    }

    private static void ReadRecipientTable(Context cx, TnefPropertyReader reader, TnefAttributeRecord a, List<List<TnefParsedProperty>> rows)
    {
        using var c = new TnefCursor(cx.Doc.Source, a.DataOffset, a.AvailableLength);
        if (!c.TryReadUInt32(out uint count))
        {
            cx.Add(TnefDefectKind.PropertyListTruncated, a.DataOffset, "recipient table has no row count");
            return;
        }

        // Each row holds at least its own 4-byte count.
        if (count > c.Remaining / 4)
        {
            cx.Add(TnefDefectKind.PropertyListTruncated, a.DataOffset, count + " rows promised, " + c.Remaining + " bytes left");
            count = (uint)(c.Remaining / 4);
        }

        if (count > cx.Options.MaxRecipients)
        {
            cx.Add(TnefDefectKind.LimitReached, a.DataOffset, count + " recipient rows, limit " + cx.Options.MaxRecipients);
            count = (uint)cx.Options.MaxRecipients;
        }

        for (uint i = 0; i < count; i++)
        {
            var row = new List<TnefParsedProperty>();
            bool ok = reader.ReadList(c, row);
            rows.Add(row);
            if (!ok)
            {
                break;
            }
        }
    }

    private static TnefAttachment BuildAttachment(Context cx, AttachmentParts parts, int depth, MailEncoding? oemEncoding)
    {
        TnefMessage msg = cx.Message;
        TnefReaderOptions o = cx.Options;

        // An embedded message is a PtypObject value: a 16-byte interface id, then a complete TNEF stream (MS-OXTNEF 2.1.3.4).
        long embeddedOffset = 0;
        long embeddedLength = 0;
        bool embedded = false;
        TnefParsedProperty? objectProp = null;
        foreach (TnefParsedProperty p in parts.Props)
        {
            if (p.Property.Id == MapiPid.AttachDataBinary && p.Property.Type == MapiType.EmbeddedObject)
            {
                objectProp = p;
            }
        }

        if (objectProp is { } op)
        {
            Span<byte> head = stackalloc byte[20];
            int n = op.ValueLength >= 20 ? cx.Doc.Source.ReadFully(op.ValueOffset, head) : 0;
            if (n == 20 && head[..16].SequenceEqual(MessageIid) && TnefDocument.HasTnefSignature(head[16..]))
            {
                if (depth + 1 > o.MaxEmbeddingDepth)
                {
                    cx.Add(TnefDefectKind.EmbeddingTooDeep, op.ValueOffset, "embedded messages nested deeper than " + o.MaxEmbeddingDepth + "; kept as a raw object property");
                }
                else
                {
                    embedded = true;
                    embeddedOffset = op.ValueOffset + 16;
                    embeddedLength = op.ValueLength - 16;
                }
            }
            else if (n == 20 && head[..16].SequenceEqual(MessageIid))
            {
                cx.Add(TnefDefectKind.EmbeddedMessageInvalid, op.ValueOffset, "the message object does not hold a TNEF stream; kept as a raw object property");
            }
        }

        var att = new TnefAttachment(msg, o, depth + 1, embedded ? cx.Doc.Source : null, embeddedOffset, embeddedLength) { StreamOffset = parts.Offset };
        var seen = new HashSet<uint>();
        foreach (TnefParsedProperty p in parts.Props)
        {
            if (embedded && objectProp is { } ob && ReferenceEquals(ob.Property, p.Property))
            {
                continue;
            }

            if (!seen.Add(p.Property.Tag.Value))
            {
                att.AddDefect(new MapiDefect(MapiDefectKind.AmbiguousDuplicateProperty, "tnef", parts.Offset, "property " + p.Property.Tag + " appears more than once; lookups return the last"));
            }

            att.Properties.Add(p.Property);
        }

        AttachmentLegacy.Apply(cx, att, parts.Legacy, oemEncoding, embedded);
        return att;
    }

    private static void CheckRtf(Context cx, TnefMessage msg)
    {
        MapiProperty? rtf = msg.Properties.Get(MapiPid.RtfCompressed, MapiType.Binary);
        if (rtf is null || rtf.GetByteLength() < 16)
        {
            return;
        }

        // MS-OXRTFCP 2.1.3.1: the header is compressed size, raw size, a signature and a CRC. Nothing is decompressed here; the header is checked so a
        // caller that decompresses can refuse a bomb. A dictionary reference is 2 bytes and stands for at most 17, so output is at most 8.5 bytes per input byte.
        Span<byte> h = stackalloc byte[16];
        using Stream s = rtf.OpenRead();
        int n = 0;
        while (n < 16)
        {
            int r = s.Read(h[n..]);
            if (r <= 0)
            {
                break;
            }

            n += r;
        }

        if (n < 16)
        {
            return;
        }

        uint comp = BinaryPrimitives.ReadUInt32LittleEndian(h);
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        if (raw > cx.Options.MaxRtfDecompressedBytes)
        {
            cx.Add(TnefDefectKind.RtfExceedsLimit, 0, "compressed RTF announces " + raw + " bytes, limit " + cx.Options.MaxRtfDecompressedBytes);
        }
        else if ((ulong)raw > ((ulong)comp + 16) * 9 + 4096)
        {
            cx.Add(TnefDefectKind.RtfExceedsLimit, 0, "compressed RTF announces " + raw + " bytes from " + comp + " compressed bytes, more than the format can expand to");
        }
    }
}
