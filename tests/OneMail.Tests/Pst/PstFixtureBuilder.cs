using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>A recipient of a generated message.</summary>
public sealed class FxRecipient
{
    public string Name { get; init; } = "";
    public string Smtp { get; init; } = "";
    public int Type { get; init; } = 1;
}

/// <summary>An attachment of a generated message: data, or an embedded message.</summary>
public sealed class FxAttachment
{
    public string Name { get; init; } = "file.bin";
    public byte[] Data { get; init; } = [];
    public string? ContentId { get; init; }
    public FxMessage? Embedded { get; init; }
}

/// <summary>A message to generate. Everything optional is left out when null/empty.</summary>
public sealed class FxMessage
{
    public string Subject { get; set; } = "";
    public string MessageClass { get; init; } = "IPM.Note";
    public string? Body { get; init; }
    public string? Html { get; init; }
    public string SenderName { get; init; } = "Sender";
    public string SenderSmtp { get; init; } = "sender@example.com";
    public List<FxRecipient> Recipients { get; init; } = [];
    public List<FxAttachment> Attachments { get; init; } = [];
    /// <summary>Named properties: (set, lid, value) as Integer32; they get IDs from the file's name map.</summary>
    public List<(Guid Set, uint Lid, int Value)> NamedNumbers { get; init; } = [];
    public List<(Guid Set, string Name, string Value)> NamedStrings { get; init; } = [];
    public long SubmitFileTime { get; init; } = 133_500_000_000_000_000L;
}

/// <summary>A folder to generate.</summary>
public sealed class FxFolder
{
    public string Name { get; init; } = "";
    public List<FxFolder> Children { get; init; } = [];
    public List<FxMessage> Messages { get; init; } = [];
    public List<FxMessage> Associated { get; init; } = [];
    /// <summary>NIDs listed in the hierarchy table as they are (a loop, a missing node).</summary>
    public List<uint> RawChildNids { get; init; } = [];
}

/// <summary>
/// Writes a complete small PST/OST (ANSI, Unicode or 4 KiB) from a folder tree, through the MS-PST 2.4 structures: the message store,
/// the name-to-ID map, folders with their hierarchy, contents and associated-contents tables, messages with recipient and attachment
/// tables, attachment objects, embedded messages and subnode-held values. Test-side only; strings are UTF-16 in Unicode files and
/// windows-1252 in ANSI files.
/// </summary>
public sealed class PstFixtureBuilder
{
    private readonly NdbFormat _format;
    private readonly List<TestNode> _nodes = [];
    private readonly Dictionary<(Guid, uint?, string?), ushort> _names = [];
    private readonly List<(Guid Set, uint Lid, string? Name)> _nameList = [];
    private uint _folderIdx = 0x400;
    private uint _messageIdx = 0x10000;
    private uint _attachIdx = 0x100000;

    public PstFixtureBuilder(NdbFormat format) => _format = format;

    private bool Ansi => _format == NdbFormat.Ansi;

    private ushort StrType => Ansi ? LtpPropertyTypes.PtypString8 : LtpPropertyTypes.PtypString;

    public byte[] Str(string s) => Ansi ? Encoding.Latin1.GetBytes(s) : Encoding.Unicode.GetBytes(s);

    private static byte[] I32(int v) => BitConverter.GetBytes(v);

    /// <summary>Builds the whole file for the tree under an unnamed root folder.</summary>
    public NdbTestBuilder.Result Build(FxFolder root, string storeName = "Test Store", bool compress = false, byte crypt = 0)
    {
        _nodes.Clear();
        AddFolder(root, 0x122, isRoot: true);
        _nodes.Add(PcBuilder.Build(0x21, Sorted([new PcSpec(0x3001, StrType, Str(storeName)), new PcSpec(0x67FF, LtpPropertyTypes.PtypInteger32, I32(0))])));
        _nodes.Add(BuildNameMap());
        return NdbTestBuilder.Build(_format, _nodes, new TestBuild { Compress = compress && _format == NdbFormat.Unicode4K, Crypt = crypt });
    }

    private static List<PcSpec> Sorted(IEnumerable<PcSpec> specs) => [.. specs.OrderBy(p => p.Id)];

    private ushort NameId(Guid set, uint? lid, string? name)
    {
        if (!_names.TryGetValue((set, lid, name), out ushort id))
        {
            id = (ushort)(0x8000 + _names.Count);
            _names[(set, lid, name)] = id;
            _nameList.Add((set, lid ?? 0, name));
        }

        return id;
    }

    private TestNode BuildNameMap()
    {
        var guids = new List<Guid>();
        var strings = new List<byte>();
        var entries = new List<byte>();
        for (int i = 0; i < _nameList.Count; i++)
        {
            (Guid set, uint lid, string? name) = _nameList[i];
            int g = set == MapiPropertySets.Mapi ? 1 : set == MapiPropertySets.PublicStrings ? 2 : 3 + IndexOf(guids, set);
            uint dw = lid;
            if (name is not null)
            {
                dw = (uint)strings.Count;
                byte[] chars = Encoding.Unicode.GetBytes(name);
                strings.AddRange(I32(chars.Length));
                strings.AddRange(chars);
                while (strings.Count % 4 != 0) { strings.Add(0); }
            }

            entries.AddRange(BitConverter.GetBytes(dw));
            entries.AddRange(BitConverter.GetBytes((ushort)((g << 1) | (name is null ? 0 : 1))));
            entries.AddRange(BitConverter.GetBytes((ushort)i));
        }

        return PcBuilder.Build(0x61, Sorted([
            new PcSpec(0x0001, LtpPropertyTypes.PtypInteger32, I32(251)),
            new PcSpec(0x0002, LtpPropertyTypes.PtypBinary, [.. guids.SelectMany(g => g.ToByteArray())]),
            new PcSpec(0x0003, LtpPropertyTypes.PtypBinary, [.. entries]),
            new PcSpec(0x0004, LtpPropertyTypes.PtypBinary, [.. strings]),
        ]));

        static int IndexOf(List<Guid> list, Guid g)
        {
            int i = list.IndexOf(g);
            if (i < 0) { list.Add(g); i = list.Count - 1; }
            return i;
        }
    }

    private uint AddFolder(FxFolder f, uint nid, bool isRoot = false)
    {
        var childNids = new List<(uint Nid, FxFolder Folder)>();
        foreach (FxFolder c in f.Children)
        {
            uint cn = (_folderIdx++ << 5) | 2;
            childNids.Add((cn, c));
        }

        _nodes.Add(PcBuilder.Build(nid, Sorted([
            new PcSpec(0x3001, StrType, Str(f.Name)),
            new PcSpec(0x3602, LtpPropertyTypes.PtypInteger32, I32(f.Messages.Count)),
            new PcSpec(0x360A, LtpPropertyTypes.PtypBoolean, [(byte)(f.Children.Count > 0 ? 1 : 0)]),
            new PcSpec(0x3613, StrType, Str("IPF.Note")),
        ])));
        uint baseNid = nid & ~0x1Fu;
        List<(uint RowId, byte[]?[] Cells)> hier =
        [
            .. childNids.Select(c => (c.Nid, new byte[]?[] { Str(c.Folder.Name), I32(c.Folder.Messages.Count), [(byte)(c.Folder.Children.Count > 0 ? 1 : 0)] })),
            .. f.RawChildNids.Select(n => (n, new byte[]?[] { Str("raw"), I32(0), [0] })),
        ];
        _nodes.Add(TcBuilder.Build(baseNid | 0x0D, new TcSpec
        {
            Columns = [(0x3001, StrType), (0x3602, 3), (0x360A, 0x0B)],
            Rows = [.. hier.OrderBy(r => r.RowId)],
        }));

        var rows = new List<(uint RowId, byte[]?[] Cells)>();
        foreach (FxMessage m in f.Messages)
        {
            uint mn = (_messageIdx++ << 5) | 4;
            AddMessage(m, mn);
            rows.Add((mn, [Str(m.Subject), BitConverter.GetBytes(m.SubmitFileTime)]));
        }

        _nodes.Add(TcBuilder.Build(baseNid | 0x0E, new TcSpec { Columns = [(0x0037, StrType), (0x0039, 0x40)], Rows = rows }));
        var assoc = new List<(uint RowId, byte[]?[] Cells)>();
        foreach (FxMessage m in f.Associated)
        {
            uint mn = (_messageIdx++ << 5) | 8;
            AddMessage(m, mn);
            assoc.Add((mn, [Str(m.Subject), BitConverter.GetBytes(m.SubmitFileTime)]));
        }

        _nodes.Add(TcBuilder.Build(baseNid | 0x0F, new TcSpec { Columns = [(0x0037, StrType), (0x0039, 0x40)], Rows = assoc }));
        foreach ((uint cn, FxFolder child) in childNids)
        {
            AddFolder(child, cn);
        }

        _ = isRoot;
        return nid;
    }

    private void AddMessage(FxMessage m, uint nid) => _nodes.Add(MessageNode(m, nid));

    // The message's property context plus, as subnodes, the recipient table, the attachment table, the attachment objects and any value
    // too big for the heap. An embedded message is built the same way and hangs under its attachment (MS-PST 2.4.6.2.2).
    private TestNode MessageNode(FxMessage m, uint nid)
    {
        var props = new List<PcSpec>
        {
            new(0x001A, StrType, Str(m.MessageClass)),
            new(0x0037, StrType, Str(m.Subject)),
            new(0x0039, 0x40, BitConverter.GetBytes(m.SubmitFileTime)),
            new(0x0C1A, StrType, Str(m.SenderName)),
            new(0x0C1E, StrType, Str("SMTP")),
            new(0x0C1F, StrType, Str(m.SenderSmtp)),
            new(0x0E07, LtpPropertyTypes.PtypInteger32, I32(1)),
            new(0x3FFD, LtpPropertyTypes.PtypInteger32, I32(1252)),
        };
        if (m.Body is not null)
        {
            props.Add(new PcSpec(0x1000, StrType, Str(m.Body), InSubnode: Str(m.Body).Length > 1500));
        }

        if (m.Html is not null)
        {
            byte[] h = Encoding.UTF8.GetBytes(m.Html);
            props.Add(new PcSpec(0x1013, LtpPropertyTypes.PtypBinary, h, InSubnode: h.Length > 1500));
            props.Add(new PcSpec(0x3FDE, LtpPropertyTypes.PtypInteger32, I32(65001)));
        }

        foreach ((Guid set, uint lid, int value) in m.NamedNumbers)
        {
            props.Add(new PcSpec(NameId(set, lid, null), LtpPropertyTypes.PtypInteger32, I32(value)));
        }

        foreach ((Guid set, string name, string value) in m.NamedStrings)
        {
            props.Add(new PcSpec(NameId(set, null, name), StrType, Str(value)));
        }

        TestNode pc = PcBuilder.Build(nid, Sorted(props));
        var subs = new List<TestNode>(pc.Subnodes);
        if (m.Recipients.Count > 0)
        {
            subs.Add(TcBuilder.Build(0x692, new TcSpec
            {
                Columns = [(0x0C15, 3), (0x3000, 3), (0x3001, StrType), (0x3002, StrType), (0x3003, StrType), (0x39FE, StrType)],
                Rows = [.. m.Recipients.Select((r, i) => ((uint)(i + 1), new byte[]?[] { I32(r.Type), I32(i), Str(r.Name), Str("SMTP"), Str(r.Smtp), Str(r.Smtp) }))],
            }));
        }

        if (m.Attachments.Count > 0)
        {
            var rows = new List<(uint RowId, byte[]?[] Cells)>();
            foreach (FxAttachment a in m.Attachments)
            {
                uint an = (_attachIdx++ << 5) | 5;
                subs.Add(AttachmentNode(a, an));
                rows.Add((an, [I32(a.Data.Length), I32(a.Embedded is null ? 1 : 5), Str(a.Name)]));
            }

            subs.Add(TcBuilder.Build(0x671, new TcSpec { Columns = [(0x0E20, 3), (0x3705, 3), (0x3707, StrType)], Rows = rows }));
        }

        return new TestNode { Nid = nid, Blocks = pc.Blocks, Subnodes = subs };
    }

    private TestNode AttachmentNode(FxAttachment a, uint nid)
    {
        var props = new List<PcSpec>
        {
            new(0x0E20, LtpPropertyTypes.PtypInteger32, I32(a.Data.Length)),
            new(0x3705, LtpPropertyTypes.PtypInteger32, I32(a.Embedded is null ? 1 : 5)),
            new(0x3707, StrType, Str(a.Name)),
            new(0x3703, StrType, Str(Path.GetExtension(a.Name))),
        };
        if (a.ContentId is not null)
        {
            props.Add(new PcSpec(0x3712, StrType, Str(a.ContentId)));
        }

        TestNode? embedded = null;
        if (a.Embedded is { } e)
        {
            uint hnid = (uint)(0x7000 << 5) | 0x1F;
            embedded = MessageNode(e, hnid);
            props.Add(new PcSpec(0x3701, 0x000D, [], ForcedHnid: hnid));
        }
        else
        {
            props.Add(new PcSpec(0x3701, LtpPropertyTypes.PtypBinary, a.Data, InSubnode: a.Data.Length > 1500));
        }

        TestNode pc = PcBuilder.Build(nid, Sorted(props));
        return new TestNode { Nid = nid, Blocks = pc.Blocks, Subnodes = embedded is null ? pc.Subnodes : [.. pc.Subnodes, embedded] };
    }
}
