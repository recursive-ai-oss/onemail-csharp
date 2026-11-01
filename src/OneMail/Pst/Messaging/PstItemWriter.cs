using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>What the folder needs to remember of a message it has just had written: its rows for the folder's tables and its flags.</summary>
internal sealed class PstWrittenMessage
{
    public byte[]?[] ContentsRow { get; init; } = [];
    public byte[]?[] AssocRow { get; init; } = [];
    public int Flags { get; init; }
}

/// <summary>
/// Turns MAPI items into nodes (MS-PST 2.4.5 and 2.4.6): the message property context, the recipient and attachment tables, attachment objects,
/// embedded messages and value subnodes; keeps the name-to-ID map (2.4.7); writes the template tables at the end.
/// </summary>
internal sealed class PstItemWriter
{
    private const int MaxNamedProperties = 0x7FFF;
    private const int MaxChildren = 0xFFFF;

    // MS-PST 2.4.4.4.1, 2.4.4.5.1, 2.4.4.6.1, 2.4.8.6.2.1, 2.4.5.3.1 and 2.4.6.1.1: the template tables' columns.
    internal static readonly (ushort Id, ushort Type)[] HierarchyColumns =
    [
        (0x0E30, 3), (0x0E33, 0x14), (0x0E34, 0x102), (0x0E38, 3), (0x3001, 0x1F), (0x3602, 3), (0x3603, 3), (0x360A, 0xB), (0x3613, 0x1F), (0x6635, 3), (0x6636, 3),
    ];

    internal static readonly (ushort Id, ushort Type)[] ContentsColumns =
    [
        (0x0017, 3), (0x001A, 0x1F), (0x0036, 3), (0x0037, 0x1F), (0x0039, 0x40), (0x0042, 0x1F), (0x0057, 0xB), (0x0058, 0xB), (0x0070, 0x1F), (0x0071, 0x102),
        (0x0E03, 0x1F), (0x0E04, 0x1F), (0x0E06, 0x40), (0x0E07, 3), (0x0E08, 3), (0x0E17, 3), (0x0E30, 3), (0x0E33, 0x14), (0x0E34, 0x102), (0x0E38, 3),
        (0x0E3C, 0x102), (0x0E3D, 0x102), (0x1097, 3), (0x3008, 0x40), (0x65C6, 3),
    ];

    internal static readonly (ushort Id, ushort Type)[] AssocColumns =
    [
        (0x001A, 0x1F), (0x0E07, 3), (0x0E17, 3), (0x3001, 0x1F), (0x6800, 0x1F), (0x6803, 0xB), (0x6805, 0x1003), (0x7003, 3), (0x7004, 0x102), (0x7005, 0x102),
        (0x7006, 0x1F), (0x7007, 3),
    ];

    internal static readonly (ushort Id, ushort Type)[] SearchContentsColumns =
    [
        (0x0017, 3), (0x001A, 0x1F), (0x0036, 3), (0x0037, 0x1F), (0x0042, 0x1F), (0x0057, 0xB), (0x0E03, 0x1F), (0x0E04, 0x1F), (0x0E05, 0x1F), (0x0E06, 0x40),
        (0x0E07, 3), (0x0E08, 3), (0x0E17, 3), (0x0E2A, 0xB), (0x3008, 0x40), (0x67F1, 3),
    ];

    private static readonly (ushort Id, ushort Type)[] RecipientColumns =
    [
        (0x0C15, 3), (0x0E0F, 0xB), (0x0FF9, 0x102), (0x0FFE, 3), (0x0FFF, 0x102), (0x3001, 0x1F), (0x3002, 0x1F), (0x3003, 0x1F), (0x300B, 0x102), (0x3900, 3),
        (0x39FF, 0x1F), (0x3A40, 0xB),
    ];

    private static readonly (ushort Id, ushort Type)[] AttachmentColumns = [(0x0E20, 3), (0x3704, 0x1F), (0x3705, 3), (0x370B, 3)];

    private readonly PstFileWriter _file;
    private readonly PstBuilderOptions _options;
    private readonly DateTimeOffset _timestamp;
    private readonly List<PstWriteDefect> _defects;
    private readonly Dictionary<MapiNamedProperty, ushort> _nameIds = [];
    private readonly List<MapiNamedProperty> _names = [];

    private sealed class Prop
    {
        public ushort Id;
        public ushort Type;
        public MapiProperty? Source;
        public byte[]? Bytes;
    }

    // Subnode NIDs that are unique within one top-level message: value subnodes, attachment objects and embedded messages.
    private sealed class Ctx
    {
        public uint Ltp;
        public uint Attach = 0x400;
        public Nid NextLtp() => new((++Ltp << 5) | (uint)NidType.Ltp);
        public Nid NextAttachment() => new((Attach++ << 5) | (uint)NidType.Attachment);
    }

    public PstItemWriter(PstFileWriter file, PstBuilderOptions options, DateTimeOffset timestamp, List<PstWriteDefect> defects)
    {
        _file = file;
        _options = options;
        _timestamp = timestamp;
        _defects = defects;
    }

    private void Report(PstWriteDefectKind kind, string location, string detail)
    {
        if (_defects.Count < 10_000)
        {
            _defects.Add(new PstWriteDefect(kind, location, detail));
        }
    }

    private static byte[] I32(int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] FileTime(DateTimeOffset t)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, t.UtcDateTime.ToFileTimeUtc());
        return b;
    }

    // ---- named properties (MS-PST 2.4.7) --------------------------------------------------------------------------------------------------

    private ushort NameId(MapiNamedProperty name)
    {
        if (_nameIds.TryGetValue(name, out ushort id))
        {
            return id;
        }

        if (_names.Count >= MaxNamedProperties)
        {
            return 0;
        }

        id = (ushort)(0x8000 + _names.Count);
        _nameIds[name] = id;
        _names.Add(name);
        return id;
    }

    private LtpNodeResult BuildNameMap()
    {
        var guids = new List<Guid>();
        var strings = new List<byte>();
        const int Buckets = 251;
        var entries = new byte[_names.Count * 8];
        var bucketRecords = new List<byte[]>[Buckets];
        for (int i = 0; i < _names.Count; i++)
        {
            MapiNamedProperty n = _names[i];
            int g;
            if (n.PropertySet == MapiPropertySets.Mapi)
            {
                g = 1;
            }
            else if (n.PropertySet == MapiPropertySets.PublicStrings)
            {
                g = 2;
            }
            else
            {
                int at = guids.IndexOf(n.PropertySet);
                if (at < 0)
                {
                    guids.Add(n.PropertySet);
                    at = guids.Count - 1;
                }

                g = 3 + at;
            }

            ushort word = (ushort)((g << 1) | (n.IsString ? 1 : 0));
            uint dw = n.Lid;
            uint hashed = n.Lid;
            if (n.IsString)
            {
                byte[] chars = Encoding.Unicode.GetBytes(n.Name!);
                dw = (uint)strings.Count;
                hashed = NdbChecksums.Crc32(chars);
                strings.AddRange(I32(chars.Length));
                strings.AddRange(chars);
                while (strings.Count % 4 != 0)
                {
                    strings.Add(0);
                }
            }

            BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(i * 8), dw);
            BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan((i * 8) + 4), word);
            BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan((i * 8) + 6), (ushort)i);

            // MS-PST 2.4.7.5: the bucket is (dword 0 XOR the low word of dword 1) modulo the bucket count; a string's record holds the CRC of its name.
            byte[] record = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(record, hashed);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), word);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)i);
            (bucketRecords[(hashed ^ word) % Buckets] ??= []).Add(record);
        }

        var pc = new PropertyContextBuilder(_file.Blocks);
        pc.Set(0x0001, LtpPropertyTypes.PtypInteger32, I32(Buckets));
        var guidBytes = new byte[guids.Count * 16];
        for (int i = 0; i < guids.Count; i++)
        {
            guids[i].TryWriteBytes(guidBytes.AsSpan(i * 16));
        }

        pc.Set(0x0002, LtpPropertyTypes.PtypBinary, guidBytes);
        pc.Set(0x0003, LtpPropertyTypes.PtypBinary, entries);
        pc.Set(0x0004, LtpPropertyTypes.PtypBinary, strings.ToArray());
        for (int b = 0; b < Buckets; b++)
        {
            if (bucketRecords[b] is { } list)
            {
                pc.Set((ushort)(0x1000 + b), LtpPropertyTypes.PtypBinary, list.SelectMany(static r => r).ToArray());
            }
        }

        return pc.Build();
    }

    /// <summary>Writes the nodes that exist once per store: the name-to-ID map, the template tables and the store's own nodes.</summary>
    public void Finish(PstBuilder builder)
    {
        LtpNodeResult map = BuildNameMap();
        _file.AddNode(new NodeEntry(new Nid(PstBuilder.NidNameToIdMap), map.Data, map.WriteSubnodes(_file.Blocks), default));
        WriteTable(_file, new Nid(PstBuilder.NidHierarchyTemplate), HierarchyColumns, []);
        WriteTable(_file, new Nid(PstBuilder.NidContentsTemplate), ContentsColumns, []);
        WriteTable(_file, new Nid(PstBuilder.NidAssocContentsTemplate), AssocColumns, []);
        WriteTable(_file, new Nid(PstBuilder.NidSearchContentsTemplate), SearchContentsColumns, []);
        WriteTable(_file, new Nid(PstBuilder.NidRecipientTable), RecipientColumns, []);
        WriteTable(_file, new Nid(PstBuilder.NidAttachmentTable), AttachmentColumns, []);
        builder.WriteStoreNodes();
    }

    // ---- tables --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Writes a top-level table node: its columns and rows (cells in column order; null for an absent cell).</summary>
    public static void WriteTable(PstFileWriter file, Nid nid, (ushort Id, ushort Type)[] columns, IEnumerable<(uint RowId, byte[]?[] Cells)> rows, bool streamHeap = false)
    {
        var tc = new TableContextBuilder(file.Blocks, columns, streamHeap: streamHeap, rowIdsAscending: streamHeap);
        int[] map = ColumnMap(tc, columns);
        foreach ((uint rowId, byte[]?[] cells) in rows)
        {
            tc.BeginRow(rowId);
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] is { } v)
                {
                    tc.SetCell(map[i], v);
                }
            }

            tc.EndRow();
        }

        LtpNodeResult r = tc.Build();
        file.AddNode(new NodeEntry(nid, r.Data, r.WriteSubnodes(file.Blocks), default));
    }

    private static int[] ColumnMap(TableContextBuilder tc, (ushort Id, ushort Type)[] columns)
    {
        int[] map = new int[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            map[i] = tc.FindColumn(columns[i].Id, columns[i].Type);
        }

        return map;
    }

    // ---- properties ---------------------------------------------------------------------------------------------------------------------------

    private static bool IsSupportedType(ushort type)
    {
        ushort baseType = (ushort)(type & ~LtpPropertyTypes.MultiValueFlag);
        bool multi = baseType != type;
        return baseType switch
        {
            0x0002 or 0x0003 or 0x0004 or 0x0005 or 0x0006 or 0x0007 or 0x0014 or 0x001E or 0x001F or 0x0040 or 0x0048 or 0x0102 => true,
            0x000A or 0x000B => !multi,
            _ => false,
        };
    }

    private List<Prop> Collect(MapiObject obj, string location, bool skipObject)
    {
        var list = new List<Prop>(obj.Properties.Count + 8);
        var seen = new HashSet<ushort>();
        foreach (MapiProperty p in obj.Properties)
        {
            ushort id = p.Id;
            ushort type = p.Tag.Type;
            if ((skipObject && type == 0x000D) || id is 0x67F2 or 0x67F3)
            {
                continue;   // the row ID and row version belong to a table, not to the object
            }

            if (p.Tag.IsNamed)
            {
                if (p.Name is not { } name)
                {
                    Report(PstWriteDefectKind.NamedPropertyUnmapped, location, $"named property {p.Tag} has no name, so it cannot be mapped in this file");
                    continue;
                }

                id = NameId(name);
                if (id == 0)
                {
                    Report(PstWriteDefectKind.NamedPropertyLimit, location, $"{name} does not fit: a store holds {MaxNamedProperties} named properties at most");
                    continue;
                }
            }

            if (!IsSupportedType(type))
            {
                Report(PstWriteDefectKind.UnsupportedType, location, $"property {p.Tag} has a type a property context cannot hold");
                continue;
            }

            int fixedSize = LtpPropertyTypes.FixedSize(type);
            if (fixedSize > 0 && p.GetByteLength(0) != fixedSize)
            {
                Report(PstWriteDefectKind.UnsupportedType, location, $"property {p.Tag} has {p.GetByteLength(0)} bytes, but its type needs {fixedSize}");
                continue;
            }

            if (!seen.Add(id))
            {
                Report(PstWriteDefectKind.DuplicateProperty, location, $"property 0x{id:X4} is listed twice; the first is kept");
                continue;
            }

            list.Add(new Prop { Id = id, Type = type, Source = p });
        }

        return list;
    }

    private static Prop? Find(List<Prop> props, ushort id)
    {
        foreach (Prop p in props)
        {
            if (p.Id == id)
            {
                return p;
            }
        }

        return null;
    }

    private void Ensure(List<Prop> props, ushort id, ushort type, byte[] value, string location)
    {
        if (Find(props, id) is not null)
        {
            return;
        }

        props.Add(new Prop { Id = id, Type = type, Bytes = value });
        Report(PstWriteDefectKind.PropertyAdded, location, $"property 0x{id:X4} is required in a PST item and was missing; added");
    }

    // MS-PST 2.3.3.4: a multi-valued property of strings or binaries is its count, the offset of each element, then the elements; the other
    // multi-valued types are their elements back to back.
    private static byte[] Pack(MapiProperty p)
    {
        int n = p.ValueCount;
        int element = LtpPropertyTypes.FixedElementSize(p.Tag.Type);
        using var ms = new MemoryStream();
        if (element > 0)
        {
            for (int i = 0; i < n; i++)
            {
                ms.Write(p.GetBytes(i).Span);
            }

            return ms.ToArray();
        }

        byte[] head = new byte[4 + (4 * n)];
        BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)n);
        long at = head.Length;
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4 + (4 * i)), (uint)at);
            at += p.GetByteLength(i);
        }

        ms.Write(head);
        for (int i = 0; i < n; i++)
        {
            ms.Write(p.GetBytes(i).Span);
        }

        return ms.ToArray();
    }

    private static long ValueLength(Prop p)
    {
        if (p.Bytes is not null)
        {
            return p.Bytes.Length;
        }

        MapiProperty s = p.Source!;
        if (!s.IsMultiValued)
        {
            return s.GetByteLength(0);
        }

        long total = 0;
        for (int i = 0; i < s.ValueCount; i++)
        {
            total += s.GetByteLength(i);
        }

        return total;
    }

    private static void SetProperty(PropertyContextBuilder pc, Prop p)
    {
        if (p.Bytes is not null)
        {
            pc.Set(p.Id, p.Type, p.Bytes);
        }
        else if (p.Source!.IsMultiValued)
        {
            pc.Set(p.Id, p.Type, Pack(p.Source));
        }
        else if (p.Source.GetByteLength(0) > HeapNodeBuilder.MaxAllocation)
        {
            using Stream s = p.Source.OpenRead(0);
            pc.Set(p.Id, p.Type, s, p.Source.GetByteLength(0));
        }
        else
        {
            pc.Set(p.Id, p.Type, p.Source.GetBytes(0).Span);
        }
    }

    private static void SetCell(TableContextBuilder tc, int column, Prop p)
    {
        if (p.Bytes is not null)
        {
            tc.SetCell(column, p.Bytes);
        }
        else if (p.Source!.IsMultiValued)
        {
            tc.SetCell(column, Pack(p.Source));
        }
        else if (p.Source.GetByteLength(0) > HeapNodeBuilder.MaxAllocation)
        {
            using Stream s = p.Source.OpenRead(0);
            tc.SetCell(column, s, p.Source.GetByteLength(0));
        }
        else
        {
            tc.SetCell(column, p.Source.GetBytes(0).Span);
        }
    }

    // ---- messages ------------------------------------------------------------------------------------------------------------------------------

    private sealed record WrittenObject(Bid Data, Bid Sub, long Size, int Flags, int EffectiveStatus, byte[] LastModified);

    /// <summary>Writes a top-level message node and returns the rows its folder keeps.</summary>
    public PstWrittenMessage WriteMessage(MapiMessage message, Nid nid, Nid folder, bool associated, string location)
    {
        var ctx = new Ctx();
        WrittenObject o = WriteMessageObject(message, nid.Value, ctx, 0, location);
        _file.AddNode(new NodeEntry(nid, o.Data, o.Sub, folder));

        var eff = new Dictionary<ushort, byte[]>
        {
            [0x0E07] = I32(o.Flags),
            [0x0E08] = I32((int)Math.Min(int.MaxValue, o.Size)),
            [0x0E17] = I32(o.EffectiveStatus),
            [0x3008] = o.LastModified,
        };
        return new PstWrittenMessage
        {
            Flags = o.Flags,
            ContentsRow = associated ? [] : MakeRow(message, ContentsColumns, eff),
            AssocRow = associated ? MakeRow(message, AssocColumns, eff) : [],
        };
    }

    private static byte[]?[] MakeRow(MapiMessage m, (ushort Id, ushort Type)[] columns, Dictionary<ushort, byte[]> effective)
    {
        var cells = new byte[]?[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            (ushort id, ushort type) = columns[i];
            if (effective.TryGetValue(id, out byte[]? e))
            {
                cells[i] = e;
                continue;
            }

            MapiProperty? p = m.Properties.Get(id);
            if (p is null || p.IsMultiValued)
            {
                continue;
            }

            if (type == 0x1F)
            {
                cells[i] = p.Type switch
                {
                    MapiType.UnicodeString => p.GetBytes(0).ToArray(),
                    MapiType.String8 => Encoding.Unicode.GetBytes(p.AsString(m.String8Encoding) ?? string.Empty),
                    _ => null,
                };
                if (cells[i] is { Length: > HeapNodeBuilder.MaxAllocation })
                {
                    cells[i] = null;
                }
            }
            else if (p.Tag.Type == type)
            {
                int fixedSize = LtpPropertyTypes.FixedSize(type);
                long len = p.GetByteLength(0);
                if ((fixedSize > 0 && len == fixedSize) || (fixedSize == 0 && type == 0x102 && len <= HeapNodeBuilder.MaxAllocation))
                {
                    cells[i] = p.GetBytes(0).ToArray();
                }
            }
        }

        return cells;
    }

    private WrittenObject WriteMessageObject(MapiMessage m, uint key, Ctx ctx, int depth, string location)
    {
        NdbBlockBuilder blocks = _file.Blocks;
        List<Prop> props = Collect(m, location, skipObject: false);
        long size = 0;
        var extra = new List<SubnodeEntry>();

        // Recipient table: required for every message, even with no recipients (MS-PST 2.4.5.3).
        IReadOnlyList<MapiRecipient> recipients = m.Recipients;
        if (recipients.Count > MaxChildren)
        {
            Report(PstWriteDefectKind.TooManyChildren, location, $"{recipients.Count} recipients; the first {MaxChildren} were written");
        }

        extra.Add(WriteRecipients(recipients, ctx, location, ref size));

        IReadOnlyList<MapiAttachment> attachments = m.Attachments;
        if (attachments.Count > MaxChildren)
        {
            Report(PstWriteDefectKind.TooManyChildren, location, $"{attachments.Count} attachments; the first {MaxChildren} were written");
        }

        if (attachments.Count > 0)
        {
            extra.AddRange(WriteAttachments(attachments, ctx, depth, location, ref size));
        }

        int flags = 0;
        if (Find(props, 0x0E07) is { } fp && fp.Type == 3 && ValueLength(fp) == 4)
        {
            flags = BinaryPrimitives.ReadInt32LittleEndian(fp.Bytes ?? fp.Source!.GetBytes(0).Span);
        }
        else
        {
            flags = attachments.Count > 0 ? 0x10 : 0;   // MSGFLAG_HASATTACH
        }

        foreach (Prop p in props)
        {
            size += ValueLength(p);
        }

        byte[] lastModified = FileTime(_timestamp);
        int status = 0;
        if (_options.AddRequiredProperties)
        {
            // MS-PST 2.4.5.1.1: the properties every message property context must have.
            Ensure(props, 0x001A, 0x1F, Encoding.Unicode.GetBytes("IPM.Note"), location);
            Ensure(props, 0x0E07, 3, I32(flags), location);
            Ensure(props, 0x0E08, 3, I32((int)Math.Min(int.MaxValue, size)), location);
            Ensure(props, 0x0E17, 3, I32(0), location);
            byte[] created = Find(props, 0x3008) is { } lm && lm.Type == 0x40 && ValueLength(lm) == 8 ? (lm.Bytes ?? lm.Source!.GetBytes(0).ToArray())
                : Find(props, 0x0039) is { } cs && cs.Type == 0x40 && ValueLength(cs) == 8 ? (cs.Bytes ?? cs.Source!.GetBytes(0).ToArray()) : lastModified;
            Ensure(props, 0x3007, 0x40, created, location);
            Ensure(props, 0x3008, 0x40, created, location);
            Ensure(props, 0x300B, 0x102, SearchKey(key, props), location);
        }

        if (Find(props, 0x3008) is { } lmp && lmp.Type == 0x40 && ValueLength(lmp) == 8)
        {
            lastModified = lmp.Bytes ?? lmp.Source!.GetBytes(0).ToArray();
        }

        if (Find(props, 0x0E17) is { } sp && sp.Type == 3 && ValueLength(sp) == 4)
        {
            status = BinaryPrimitives.ReadInt32LittleEndian(sp.Bytes ?? sp.Source!.GetBytes(0).Span);
        }

        var pc = new PropertyContextBuilder(blocks, ctx.NextLtp);
        foreach (Prop p in props)
        {
            SetProperty(pc, p);
        }

        LtpNodeResult r = pc.Build();
        Bid sub = r.WriteSubnodes(blocks, extra);
        return new WrittenObject(r.Data, sub, size, flags, status, lastModified);
    }

    private static byte[] SearchKey(uint key, List<Prop> props)
    {
        // A stable 16 bytes for the message: a hash of its NID, class and subject.
        using var ms = new MemoryStream();
        ms.Write(I32((int)key));
        foreach (ushort id in new ushort[] { 0x001A, 0x0037 })
        {
            if (Find(props, id) is { Source: { } s } && s.GetByteLength(0) < 4096)
            {
                ms.Write(s.GetBytes(0).Span);
            }
        }

        return SHA256.HashData(ms.ToArray()).AsSpan(0, 16).ToArray();
    }

    private SubnodeEntry WriteRecipients(IReadOnlyList<MapiRecipient> recipients, Ctx ctx, string location, ref long size)
    {
        NdbBlockBuilder blocks = _file.Blocks;
        int count = Math.Min(recipients.Count, MaxChildren);
        var rows = new List<List<Prop>>(count);
        var columns = new List<(ushort Id, ushort Type)>(RecipientColumns);
        var known = new HashSet<uint>(columns.Select(static c => ((uint)c.Id << 16) | c.Type));
        for (int i = 0; i < count; i++)
        {
            List<Prop> props = Collect(recipients[i], location + " recipient " + i, skipObject: true);
            rows.Add(props);
            foreach (Prop p in props)
            {
                if (known.Add(((uint)p.Id << 16) | p.Type))
                {
                    if (columns.Count >= 253)
                    {
                        Report(PstWriteDefectKind.UnsupportedType, location + " recipient " + i, $"property 0x{p.Id:X4} would be a 254th column of the recipient table; left out");
                        known.Remove(((uint)p.Id << 16) | p.Type);
                        p.Id = 0;
                        continue;
                    }

                    columns.Add((p.Id, p.Type));
                }
            }
        }

        var tc = new TableContextBuilder(blocks, columns, ctx.NextLtp);
        for (int i = 0; i < count; i++)
        {
            tc.BeginRow((uint)(i + 1));
            foreach (Prop p in rows[i])
            {
                if (p.Id == 0)
                {
                    continue;
                }

                SetCell(tc, tc.FindColumn(p.Id, p.Type), p);
                size += ValueLength(p);
            }

            tc.EndRow();
        }

        LtpNodeResult t = tc.Build();
        return new SubnodeEntry(new Nid(PstBuilder.NidRecipientTable), t.Data, t.WriteSubnodes(blocks));
    }

    private List<SubnodeEntry> WriteAttachments(IReadOnlyList<MapiAttachment> attachments, Ctx ctx, int depth, string location, ref long size)
    {
        NdbBlockBuilder blocks = _file.Blocks;
        var entries = new List<SubnodeEntry>();
        int count = Math.Min(attachments.Count, MaxChildren);
        var tc = new TableContextBuilder(blocks, AttachmentColumns, ctx.NextLtp);
        int[] map = ColumnMap(tc, AttachmentColumns);
        for (int i = 0; i < count; i++)
        {
            MapiAttachment a = attachments[i];
            string loc = location + " attachment " + i;
            Nid nid = ctx.NextAttachment();
            List<Prop> props = Collect(a, loc, skipObject: true);
            var extra = new List<SubnodeEntry>();
            MapiMessage? embedded = a.EmbeddedMessage;
            long embeddedSize = 0;
            if (embedded is not null)
            {
                if (depth + 1 > _options.MaxEmbeddingDepth)
                {
                    Report(PstWriteDefectKind.EmbeddingTooDeep, loc, $"embedded messages are nested deeper than {_options.MaxEmbeddingDepth}; the embedded message was left out");
                    embedded = null;
                }
                else
                {
                    Nid embNid = ctx.NextLtp();
                    WrittenObject eo = WriteMessageObject(embedded, embNid.Value, ctx, depth + 1, loc + " embedded");
                    extra.Add(new SubnodeEntry(embNid, eo.Data, eo.Sub));
                    embeddedSize = eo.Size;
                    // MS-PST 2.3.3.5 and 2.4.6.2.2: the object property holds the embedded message's subnode NID and its size.
                    byte[] obj = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(obj, embNid.Value);
                    BinaryPrimitives.WriteUInt32LittleEndian(obj.AsSpan(4), (uint)Math.Min(uint.MaxValue, eo.Size));
                    props.RemoveAll(static p => p.Id == 0x3701);
                    props.Add(new Prop { Id = 0x3701, Type = 0x000D, Bytes = obj });
                }
            }

            if (a.ObjectStorage is not null && embedded is null)
            {
                Report(PstWriteDefectKind.ObjectStorageDropped, loc, "the attachment is an OLE object or a raw storage; its storage was left out");
            }

            long dataSize = Find(props, 0x3701) is { Type: 0x102 } d ? ValueLength(d) : embeddedSize;
            int method = Find(props, 0x3705) is { Type: 3 } mp && ValueLength(mp) == 4 ? BinaryPrimitives.ReadInt32LittleEndian(mp.Bytes ?? mp.Source!.GetBytes(0).Span) : (embedded is not null ? 5 : 1);
            if (_options.AddRequiredProperties)
            {
                // MS-PST 2.4.6.2.1: the properties every attachment object must have.
                Ensure(props, 0x0E20, 3, I32((int)Math.Min(int.MaxValue, dataSize)), loc);
                Ensure(props, 0x3705, 3, I32(method), loc);
                Ensure(props, 0x370B, 3, I32(-1), loc);   // PidTagRenderingPosition: not inline
            }

            foreach (Prop p in props)
            {
                size += ValueLength(p);
            }

            var pc = new PropertyContextBuilder(blocks, ctx.NextLtp);
            foreach (Prop p in props)
            {
                SetProperty(pc, p);
            }

            LtpNodeResult r = pc.Build();
            entries.Add(new SubnodeEntry(nid, r.Data, r.WriteSubnodes(blocks, extra)));

            // The attachment table row (MS-PST 2.4.6.1.1): size, file name, method, rendering position.
            tc.BeginRow(nid.Value);
            for (int c = 0; c < AttachmentColumns.Length; c++)
            {
                (ushort id, ushort type) = AttachmentColumns[c];
                Prop? p = Find(props, id);
                if (p is null || p.Type != type)
                {
                    if (id == 0x3704 && Find(props, 0x3704) is { Type: 0x1E, Source: { } s8 })
                    {
                        tc.SetCell(map[c], Encoding.Unicode.GetBytes(s8.AsString(a.String8Encoding) ?? string.Empty));
                    }

                    continue;
                }

                if (ValueLength(p) <= HeapNodeBuilder.MaxAllocation)
                {
                    SetCell(tc, map[c], p);
                }
            }

            tc.EndRow();
        }

        LtpNodeResult t = tc.Build();
        entries.Add(new SubnodeEntry(new Nid(PstBuilder.NidAttachmentTable), t.Data, t.WriteSubnodes(blocks)));
        return entries;
    }
}
