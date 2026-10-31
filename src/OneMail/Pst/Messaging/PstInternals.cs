using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>State shared by everything read from one open store: the file, the limits, the name map and the store's defect list.</summary>
internal sealed class PstContext
{
    private readonly List<MapiDefect> _defects = [];

    public PstContext(NdbFile file, PstReaderOptions options)
    {
        File = file;
        Options = options;
        Ltp = options.Ltp ?? new LtpOptions();
    }

    public NdbFile File { get; }

    public PstReaderOptions Options { get; }

    public LtpOptions Ltp { get; }

    public PstNameMap Names { get; set; } = PstNameMap.Empty;

    public IReadOnlyList<MapiDefect> Defects => _defects;

    public int Dropped { get; private set; }

    public void Report(MapiObject? owner, MapiDefectKind kind, string location, long offset, string detail)
    {
        var d = new MapiDefect(kind, location, offset, detail);
        owner?.AddDefect(d);
        if (_defects.Count >= Options.MaxDefects)
        {
            Dropped++;
            return;
        }

        _defects.Add(d);
        if (_defects.Count == Options.MaxDefects)
        {
            _defects.Add(new MapiDefect(MapiDefectKind.DefectLimitReached, string.Empty, 0, $"more than {Options.MaxDefects} defects; the rest are only counted"));
        }
    }

    /// <summary>Passes on what the lists-tables-properties layer found in a heap, once the object that used it is read.</summary>
    public void ReportHeap(MapiObject? owner, string location, HeapNode heap)
    {
        foreach (LtpDefect d in heap.Defects)
        {
            Report(owner, MapiDefectKind.LowerLayerDefect, location, d.Offset, d.Kind + ": " + d.Detail);
        }
    }

    public void ReportLtp(MapiObject? owner, string location, IReadOnlyList<LtpDefect> defects)
    {
        foreach (LtpDefect d in defects)
        {
            Report(owner, MapiDefectKind.LowerLayerDefect, location, d.Offset, d.Kind + ": " + d.Detail);
        }
    }
}

/// <summary>Where a heap-on-node lives: the data and subnode blocks of a node or subnode.</summary>
internal readonly record struct PstNode(Bid Data, Bid Sub)
{
    public bool TryOpenHeap(PstContext ctx, out HeapNode? heap, out IReadOnlyList<LtpDefect> defects) =>
        HeapNode.TryOpen(ctx.File, Data, Sub, out heap, out defects, ctx.Ltp);

    public static PstNode Of(in NodeEntry e) => new(e.Data, e.Sub);

    public static PstNode Of(in SubnodeEntry e) => new(e.Data, e.Sub);
}

/// <summary>
/// The name-to-ID map (MS-PST 2.4.7): a property context at NID_NAME_TO_ID_MAP whose entry, GUID and string streams give the
/// (property set, number or string) behind each named property ID. The hash buckets only speed up searches and are not read.
/// </summary>
internal sealed class PstNameMap
{
    public const uint NidNameToIdMap = 0x61;
    private const ushort PidTagNameidStreamGuid = 0x0002;
    private const ushort PidTagNameidStreamEntry = 0x0003;
    private const ushort PidTagNameidStreamString = 0x0004;

    public static readonly PstNameMap Empty = new([]);

    private readonly Dictionary<ushort, MapiNamedProperty> _names;

    private PstNameMap(Dictionary<ushort, MapiNamedProperty> names) => _names = names;

    public int Count => _names.Count;

    public MapiNamedProperty? Get(ushort id) => _names.TryGetValue(id, out MapiNamedProperty n) ? n : null;

    public IEnumerable<KeyValuePair<ushort, MapiNamedProperty>> Entries => _names;

    public static PstNameMap Read(PstContext ctx)
    {
        if (!ctx.File.Nodes.TryGet(new Nid(NidNameToIdMap), out _))
        {
            return Empty;
        }

        if (!PropertyContext.TryOpen(ctx.File, new Nid(NidNameToIdMap), out PropertyContext? pc, out IReadOnlyList<LtpDefect> why, ctx.Ltp) || pc is null)
        {
            ctx.ReportLtp(null, "nid 0x61", why);
            ctx.Report(null, MapiDefectKind.ContextUnreadable, "nid 0x61", 0, "the name-to-ID map is not a readable property context; named properties stay unresolved");
            return Empty;
        }

        using (pc)
        {
            byte[] guids = ReadStream(pc, PidTagNameidStreamGuid);
            byte[] entries = ReadStream(pc, PidTagNameidStreamEntry);
            byte[] strings = ReadStream(pc, PidTagNameidStreamString);
            var names = new Dictionary<ushort, MapiNamedProperty>();
            int count = Math.Min(entries.Length / 8, ctx.Options.MaxNamedProperties);
            if ((entries.Length % 8) != 0)
            {
                ctx.Report(null, MapiDefectKind.NamedPropertyEntryStreamInvalid, "nid 0x61", entries.Length, "the entry stream's length is not a multiple of eight; the partial entry is ignored");
            }

            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> e = entries.AsSpan(i * 8, 8);
                uint dw = BinaryPrimitives.ReadUInt32LittleEndian(e);
                ushort word = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
                ushort propIdx = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
                if (propIdx >= 0x7FFF)
                {
                    ctx.Report(null, MapiDefectKind.NamedPropertyEntryStreamInvalid, "nid 0x61", i * 8L, $"entry {i} has property index 0x{propIdx:X4}, which would leave the named range");
                    continue;
                }

                ushort id = (ushort)(0x8000 + propIdx);
                bool isString = (word & 1) != 0;
                int g = word >> 1;
                Guid set;
                if (g == 1)
                {
                    set = MapiPropertySets.Mapi;
                }
                else if (g == 2)
                {
                    set = MapiPropertySets.PublicStrings;
                }
                else if (g >= 3)
                {
                    long at = (g - 3L) * 16;
                    if (at + 16 > guids.Length)
                    {
                        ctx.Report(null, MapiDefectKind.NamedPropertyGuidStreamInvalid, "nid 0x61", i * 8L, $"entry {i} names GUID index {g - 3}, past the end of the GUID stream");
                        continue;
                    }

                    set = new Guid(guids.AsSpan((int)at, 16));
                }
                else
                {
                    set = Guid.Empty;
                }

                MapiNamedProperty name;
                if (isString)
                {
                    // MS-PST 2.4.7.4: dwPropertyID is the offset of a 4-byte byte length followed by UTF-16LE characters.
                    if (dw > strings.Length - 4L || BinaryPrimitives.ReadUInt32LittleEndian(strings.AsSpan((int)dw)) > strings.Length - dw - 4L)
                    {
                        ctx.Report(null, MapiDefectKind.NamedPropertyReferenceInvalid, "nid 0x61", i * 8L, $"entry {i} names a string at offset {dw}, outside the string stream");
                        continue;
                    }

                    int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(strings.AsSpan((int)dw)) & ~1;
                    name = new MapiNamedProperty(set, Encoding.Unicode.GetString(strings, (int)dw + 4, len));
                }
                else
                {
                    name = new MapiNamedProperty(set, dw);
                }

                if (!names.TryAdd(id, name))
                {
                    ctx.Report(null, MapiDefectKind.NamedPropertyIndexMismatch, "nid 0x61", i * 8L, $"property index 0x{propIdx:X4} is named twice; the first name is kept");
                }
            }

            ctx.ReportHeap(null, "nid 0x61", pc.Heap);
            return new PstNameMap(names);
        }
    }

    private static byte[] ReadStream(PropertyContext pc, ushort id) =>
        pc.TryGet(id, out PcProperty p) && pc.ReadValue(p) is { } b ? b : [];
}

/// <summary>Turns property context entries and table cells into <see cref="MapiProperty"/> values.</summary>
internal static class PstProperties
{
    /// <summary>Property IDs 0x67F2 and 0x67F3 are the table's own row identifier and row version (MS-PST 2.3.4.1), not properties of the object.</summary>
    public static bool IsRowBookkeeping(ushort id) => id is 0x67F2 or 0x67F3;

    /// <summary>
    /// Makes the property for one reference. Values held in a heap allocation (at most a few KiB) are copied; values held in a
    /// subnode are read only when asked for. Multi-valued values are split into their elements.
    /// </summary>
    public static MapiProperty Make(PstContext ctx, PstNode node, HeapNode heap, MapiObject owner, string location, ushort id, ushort type, in LtpValueRef v)
    {
        var tag = new MapiTag(id, type);
        MapiNamedProperty? name = null;
        if (tag.IsNamed)
        {
            name = ctx.Names.Get(id);
            if (name is null)
            {
                ctx.Report(owner, MapiDefectKind.NamedPropertyUnmapped, location, 0, $"property ID 0x{id:X4} has no entry in the name-to-ID map; it is kept without a name");
            }
        }

        bool multi = LtpPropertyTypes.IsMultiValue(type);
        byte[] bytes;
        switch (v.Kind)
        {
            case LtpValueKind.Empty:
                bytes = [];
                break;
            case LtpValueKind.Inline:
                bytes = new byte[Math.Clamp(v.InlineSize, 0, 8)];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)(v.InlineValue >> (8 * i));
                }

                break;
            case LtpValueKind.Heap:
                byte[]? read = heap.ReadValue(v);
                if (read is null)
                {
                    ctx.Report(owner, MapiDefectKind.ValueUnreadable, location, 0, $"the value of property 0x{id:X4}{type:X4} cannot be read; it is kept empty");
                }

                bytes = read ?? [];
                break;
            default:
                return MapiProperty.CreateLazy(tag, new PstLazyValue(ctx, node, v, type, owner, location), name);
        }

        if (!multi)
        {
            return MapiProperty.CreateOwned(tag, bytes, name);
        }

        return MapiProperty.CreateMulti(tag, PstLazyValue.Split(ctx, owner, location, type, bytes), name);
    }
}

/// <summary>The bytes of a value that lives in a subnode: read when first asked for, through a heap opened just for the read.</summary>
internal sealed class PstLazyValue(PstContext ctx, PstNode node, LtpValueRef value, ushort type, MapiObject owner, string location) : MapiValueSource
{
    private byte[][]? _elements;
    private long _length = -1;

    private bool IsMulti => LtpPropertyTypes.IsMultiValue(type);

    public override int Count => IsMulti ? Elements().Length : 1;

    public override long GetLength(int index)
    {
        if (IsMulti)
        {
            return Elements()[index].Length;
        }

        if (_length < 0)
        {
            if (!node.TryOpenHeap(ctx, out HeapNode? h, out _) || h is null)
            {
                return _length = 0;
            }

            using (h)
            {
                _length = Math.Max(0, h.GetValueLength(value));
            }
        }

        return _length;
    }

    public override byte[] Read(int index)
    {
        if (IsMulti)
        {
            return Elements()[index];
        }

        if (!node.TryOpenHeap(ctx, out HeapNode? h, out _) || h is null)
        {
            ctx.Report(owner, MapiDefectKind.ValueUnreadable, location, 0, "the heap holding a subnode value cannot be reopened; the value is empty");
            return [];
        }

        using (h)
        {
            byte[]? b = h.ReadValue(value);
            if (b is null)
            {
                ctx.Report(owner, MapiDefectKind.ValueUnreadable, location, 0, "a subnode value cannot be read (missing, damaged or over the size limit); it is empty");
            }

            return b ?? [];
        }
    }

    public override Stream OpenRead(int index)
    {
        if (IsMulti)
        {
            return new MemoryStream(Elements()[index], writable: false);
        }

        if (!node.TryOpenHeap(ctx, out HeapNode? h, out _) || h is null)
        {
            return new MemoryStream([], writable: false);
        }

        return new PstValueStream(h, value, Math.Max(0, h.GetValueLength(value)));
    }

    private byte[][] Elements()
    {
        if (_elements is null)
        {
            byte[] whole = [];
            if (node.TryOpenHeap(ctx, out HeapNode? h, out _) && h is not null)
            {
                using (h)
                {
                    whole = h.ReadValue(value) ?? [];
                }
            }

            _elements = Split(ctx, owner, location, type, whole);
        }

        return _elements;
    }

    /// <summary>Splits a multi-valued blob (MS-PST 2.3.3.4) into its elements. A blob that does not hold what its header says keeps the elements that fit.</summary>
    public static byte[][] Split(PstContext ctx, MapiObject owner, string location, ushort type, byte[] blob)
    {
        if (blob.Length == 0)
        {
            return [];
        }

        if (!LtpValues.TryGetMultiValueCount(type, blob, out int count))
        {
            ctx.Report(owner, MapiDefectKind.MultiValueLengthMismatch, location, 0, "a multi-valued property's header does not fit its value; it has no elements");
            return [];
        }

        count = Math.Min(count, Math.Max(0, ctx.Options.MaxProperties));
        var list = new List<byte[]>(Math.Min(count, 1024));
        for (int i = 0; i < count; i++)
        {
            if (!LtpValues.TryGetMultiValueElement(type, blob, i, out int off, out int len))
            {
                ctx.Report(owner, MapiDefectKind.MultiValueLengthMismatch, location, i, $"element {i} of a multi-valued property lies outside its value; the later elements are dropped");
                break;
            }

            list.Add(blob.AsSpan(off, len).ToArray());
        }

        return [.. list];
    }
}

/// <summary>A read-only seekable stream over one value held by a heap. It owns the heap and closes it on dispose.</summary>
internal sealed class PstValueStream(HeapNode heap, LtpValueRef value, long length) : Stream
{
    private long _position;
    private HeapNode? _heap = heap;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_heap is null, this);
        if (_position >= length || buffer.IsEmpty)
        {
            return 0;
        }

        int n = _heap.ReadValue(value, _position, buffer[..(int)Math.Min(buffer.Length, length - _position)]);
        _position += Math.Max(0, n);
        return Math.Max(0, n);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override long Seek(long offset, SeekOrigin origin)
    {
        long p = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        return p < 0 ? throw new IOException("Seek before the start of the value.") : _position = p;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _heap?.Dispose();
            _heap = null;
        }

        base.Dispose(disposing);
    }
}
