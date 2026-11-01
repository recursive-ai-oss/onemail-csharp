using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneMail.Tests.Stores;

/// <summary>One message to put in a generated message database.</summary>
public sealed class DbxTestMessage
{
    public byte[] Text { get; init; } = [];
    public uint Flags { get; init; } = 0x81;
    public DateTimeOffset Received { get; init; } = new(2004, 5, 6, 7, 8, 9, TimeSpan.Zero);
    public DateTimeOffset Sent { get; init; } = new(2004, 5, 6, 7, 0, 0, TimeSpan.Zero);
    public string? Subject { get; init; }
    public string? MessageId { get; init; }
    public string? SenderName { get; init; }
    public string? SenderAddress { get; init; }
    public int Priority { get; init; } = 3;
    /// <summary>A header-only record (IMAP and news headers never downloaded): no chain pointer.</summary>
    public bool NoBody { get; init; }
    /// <summary>When set, the length stored in the record instead of the true one.</summary>
    public int? DeclaredLength { get; init; }
}

/// <summary>One folder to put in a generated Folders.dbx.</summary>
public sealed class DbxTestFolder
{
    public required int Index { get; init; }
    public int Parent { get; init; } = -1;
    public required string Name { get; init; }
    public string? File { get; init; }
    public int Special { get; init; }
    public int Messages { get; init; }
}

/// <summary>Knobs for <see cref="DbxTestBuilder"/>.</summary>
public sealed class DbxBuild
{
    /// <summary>Most entries per tree node (the format allows 0x33). Small values make deep trees.</summary>
    public int TreeFanout { get; init; } = 0x33;
    /// <summary>When non-zero, message segments of all messages are placed in a shuffled order, so chains are not contiguous.</summary>
    public int ShuffleSeed { get; init; }
    /// <summary>Bytes of text per segment (the format uses 0x200).</summary>
    public int SegmentText { get; init; } = 0x200;
    /// <summary>
    /// Positions (in the message list) of messages to delete the way Outlook Express does: the tree entry is removed, the record and every
    /// segment of the chain get a deleted-object header (docs/recovery.md), and the segments go on the deleted-message list in chain order, each
    /// pushed to the front.
    /// </summary>
    public IReadOnlySet<int>? Deleted { get; init; }
    /// <summary>Positions of messages whose tree entry is removed and nothing else is changed: the orphaned-chain case.</summary>
    public IReadOnlySet<int>? Unlinked { get; init; }
}

/// <summary>
/// Writes Outlook Express 5/6 database files from the format notes, for tests: a header, a file info block, then conditions,
/// message segments, records and tree nodes laid out one after the other. The layout is ours; the structures are the notes'.
/// </summary>
public static class DbxTestBuilder
{
    private const int HeaderLength = 0x24BC;
    private const int NodeSize = 0x27C;
    private const int SegmentSize = 0x210;
    private const long FileTimeEpochTicks = 504911232000000000L;

    public static byte[] MessageDatabase(IReadOnlyList<DbxTestMessage> messages, DbxBuild? build = null)
    {
        build ??= new DbxBuild();
        Layout l = new(HeaderLength + 0x618);
        byte[] conditions = Conditions("((MSGCOL_FLAGS & ARF_WATCH) != 0 || (MSGCOL_FLAGS & ARF_IGNORE) != 0)");
        int conditionsAt = l.Alloc(conditions.Length);
        l.Put(conditionsAt, conditions);

        // Message text chains: segments are allocated first so their addresses are known to the records.
        List<(int Message, int Piece)> pieces = [];
        int[] firstPiece = new int[messages.Count];
        int[] pieceCount = new int[messages.Count];
        for (int i = 0; i < messages.Count; i++)
        {
            DbxTestMessage m = messages[i];
            int n = m.NoBody ? 0 : Math.Max(1, (m.Text.Length + build.SegmentText - 1) / build.SegmentText);
            firstPiece[i] = pieces.Count;
            pieceCount[i] = n;
            for (int p = 0; p < n; p++)
            {
                pieces.Add((i, p));
            }
        }

        int[] order = Enumerable.Range(0, pieces.Count).ToArray();
        if (build.ShuffleSeed != 0)
        {
            Random rng = new(build.ShuffleSeed);
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
        }

        int[] pieceAt = new int[pieces.Count];
        foreach (int k in order)
        {
            pieceAt[k] = l.Alloc(SegmentSize);
        }

        for (int i = 0; i < messages.Count; i++)
        {
            for (int p = 0; p < pieceCount[i]; p++)
            {
                int self = pieceAt[firstPiece[i] + p];
                int next = p + 1 < pieceCount[i] ? pieceAt[firstPiece[i] + p + 1] : 0;
                byte[] text = messages[i].Text;
                int from = p * build.SegmentText;
                int len = Math.Min(build.SegmentText, text.Length - from);
                byte[] seg = new byte[SegmentSize];
                BinaryPrimitives.WriteInt32LittleEndian(seg, self);
                BinaryPrimitives.WriteInt32LittleEndian(seg.AsSpan(4), 0x200);
                BinaryPrimitives.WriteInt32LittleEndian(seg.AsSpan(8), Math.Max(len, 0));
                BinaryPrimitives.WriteInt32LittleEndian(seg.AsSpan(12), next);
                if (len > 0)
                {
                    text.AsSpan(from, len).CopyTo(seg.AsSpan(16));
                }

                l.Put(self, seg);
            }
        }

        // Records, in index order (the order of the tree).
        List<(int Index, int At)> records = [];
        List<int> recordSize = [];
        for (int i = 0; i < messages.Count; i++)
        {
            DbxTestMessage m = messages[i];
            int index = i + 1;
            int chain = m.NoBody ? 0 : pieceAt[firstPiece[i]];
            RecordBuilder r = new();
            r.Direct(0x00, index);
            r.Int(0x01, (int)m.Flags);
            r.Time(0x02, m.Sent);
            r.Int(0x04, chain);
            if (m.Subject is not null)
            {
                r.Text(0x05, m.Subject);
                r.Text(0x08, "Re: " + m.Subject);
            }

            r.Time(0x06, m.Received);
            if (m.MessageId is not null)
            {
                r.Text(0x07, m.MessageId);
            }

            if (m.SenderName is not null)
            {
                r.Text(0x0D, m.SenderName);
            }

            if (m.SenderAddress is not null)
            {
                r.Text(0x0E, m.SenderAddress);
                r.Text(0x09, (m.SenderName ?? string.Empty) + " <" + m.SenderAddress + ">");
            }

            r.Int(0x10, m.Priority);
            r.Int(0x11, m.DeclaredLength ?? m.Text.Length);
            r.Time(0x12, m.Received);
            r.Text(0x1A, "Test account");
            byte[] obj = r.Build(0);
            int at = l.Alloc(obj.Length);
            r.Build(at, obj);
            l.Put(at, obj);
            records.Add((index, at));
            recordSize.Add((obj.Length + 3) & ~3);
        }

        bool Gone(int i) => (build.Deleted?.Contains(i) ?? false) || (build.Unlinked?.Contains(i) ?? false);
        int[] linked = records.Where((_, i) => !Gone(i)).Select(x => x.At).ToArray();
        int root = TreeBuilder.Build(l, linked, build.TreeFanout);
        byte[] file = l.ToFile();
        Header(file, 0xC5, 0x618, l.Length);
        W(file, 0x88, conditionsAt);
        W(file, 0xC4, linked.Length);
        W(file, 0xE4, root);
        W(file, 0x5C, records.Count);
        W(file, 0x24, conditionsAt);
        W(file, 0x30, root);
        W(file, 0x3C, pieces.Count == 0 ? 0 : pieceAt.Min());
        W(file, 0x38, NodeSize);
        W(file, 0x44, SegmentSize);
        W(file, 0x80, 2);
        file[HeaderLength + 4] = 3; // the notes: source type 3 is a local folder
        if (build.Deleted is { Count: > 0 })
        {
            // OE_Dbx_Deleted: marker kept, word 2 becomes the length of the free body, word 3 that plus 0x14, word 4 stays as it was (a message
            // segment's next pointer), word 5 links to the next deleted object. A message segment's first four text bytes are word 5's place.
            int freeHead = 0;
            foreach (int i in build.Deleted.OrderBy(x => x))
            {
                for (int p = 0; p < pieceCount[i]; p++)
                {
                    int at = pieceAt[firstPiece[i] + p];
                    W(file, at + 4, SegmentSize - 0x14);
                    W(file, at + 8, SegmentSize);
                    W(file, at + 16, freeHead);
                    freeHead = at;
                }

                int rec = records[i].At;
                int size = recordSize[i];
                if (size < 0x2000)
                {
                    W(file, rec + 4, size - 0x14);
                    W(file, rec + 8, size);
                    W(file, rec + 16, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x29C + size)));
                    W(file, 0x29C + size, rec);
                }
            }

            W(file, 0x48, freeHead);
        }

        return file;
    }

    public static byte[] FolderDatabase(IReadOnlyList<DbxTestFolder> folders, DbxBuild? build = null)
    {
        build ??= new DbxBuild();
        Layout l = new(HeaderLength + 0x108);
        byte[] conditions = Conditions("(FLDCOL_FLAGS & FOLDER_SUBSCRIBED)");
        int conditionsAt = l.Alloc(conditions.Length);
        l.Put(conditionsAt, conditions);
        List<(int Index, int At, DbxTestFolder F)> records = [];
        foreach (DbxTestFolder f in folders.OrderBy(x => x.Index))
        {
            RecordBuilder r = new();
            r.Direct(0x00, f.Index);
            r.Int(0x01, f.Parent);
            r.Text(0x02, f.Name);
            if (f.File is not null)
            {
                r.Text(0x03, f.File);
            }

            r.Int(0x06, 1);
            r.Int(0x07, f.Messages);
            r.Int(0x08, 0);
            r.Int(0x09, f.Special);
            r.Int(0x0A, 3);
            byte[] obj = r.Build(0);
            int at = l.Alloc(obj.Length);
            r.Build(at, obj);
            l.Put(at, obj);
            records.Add((f.Index, at, f));
        }

        int root = TreeBuilder.Build(l, records.Select(x => x.At).ToArray(), build.TreeFanout);
        byte[] file = l.ToFile();
        Header(file, 0xC6, 0x108, l.Length);
        W(file, 0x8C, conditionsAt);
        W(file, 0xC4, records.Count);
        W(file, 0xC8, records.Count);
        W(file, 0xE4, root);
        W(file, 0xE8, root);
        W(file, 0x5C, records.Count == 0 ? 0 : records.Max(x => x.Index));
        W(file, 0x24, conditionsAt);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(HeaderLength), (new DateTime(2004, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks - FileTimeEpochTicks));
        return file;
    }

    // oedbx DBX format notes, OE_Dbx_FileHeader: the signature, class id and constant words, then the sizes the reader checks.
    private static void Header(byte[] file, byte clsid, int fileInfoLength, int length)
    {
        file[0] = 0xCF;
        file[1] = 0xAD;
        file[2] = 0x12;
        file[3] = 0xFE;
        file[4] = clsid;
        file[5] = 0xFD;
        file[6] = 0x74;
        file[7] = 0x6F;
        byte[] k = [0x66, 0xE3, 0xD1, 0x11, 0x9A, 0x4E, 0x00, 0xC0, 0x4F, 0xA3, 0x09, 0xD4, 0x05, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00];
        k.CopyTo(file, 8);
        W(file, 0x1C, fileInfoLength);
        W(file, 0x28, 0xC000);
        W(file, 0x34, 0x3E1C);
        W(file, 0x40, 0xF780);
        W(file, 0x54, length - HeaderLength - fileInfoLength);
        W(file, 0x7C, length);
    }

    private static void W(byte[] b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);

    private static byte[] Conditions(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text + "\0");
        byte[] b = new byte[((t.Length + 8) + 3) & ~3];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), t.Length);
        t.CopyTo(b, 8);
        return b;
    }

    private sealed class Layout(int start)
    {
        private readonly List<(int At, byte[] Data)> _objects = [];
        private int _next = start;

        public int Length => _next;

        public int Alloc(int size)
        {
            int at = _next;
            _next += (size + 3) & ~3;
            return at;
        }

        public void Put(int at, byte[] data) => _objects.Add((at, data));

        public byte[] ToFile()
        {
            byte[] f = new byte[_next];
            foreach ((int at, byte[] data) in _objects)
            {
                data.CopyTo(f, at);
                // Every object starts with its own address.
                BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(at), at);
            }

            return f;
        }
    }

    // oedbx DBX format notes, OE_Dbx_IndexedInfo: header, index entries (field byte with the "direct" bit, 3 value bytes), data.
    private sealed class RecordBuilder
    {
        private readonly SortedDictionary<int, (bool Direct, int Value, byte[]? Data)> _fields = [];

        public void Direct(int field, int value) => _fields[field] = (true, value, null);

        public void Int(int field, int value)
        {
            if (value is >= 0 and <= 0xFFFFFF)
            {
                _fields[field] = (true, value, null);
            }
            else
            {
                _fields[field] = (false, 0, BitConverter.GetBytes(value));
            }
        }

        public void Text(int field, string s) => _fields[field] = (false, 0, Encoding.Latin1.GetBytes(s + "\0"));

        public void Time(int field, DateTimeOffset t) =>
            _fields[field] = (false, 0, BitConverter.GetBytes(t.UtcTicks - FileTimeEpochTicks));

        public byte[] Build(int at, byte[]? into = null)
        {
            int count = _fields.Count;
            int dataLength = 0;
            foreach ((bool direct, _, byte[]? d) in _fields.Values)
            {
                if (!direct)
                {
                    dataLength += d!.Length;
                }
            }

            int body = (4 * count) + dataLength;
            body = (body + 3) & ~3;
            byte[] o = into ?? new byte[12 + body];
            BinaryPrimitives.WriteInt32LittleEndian(o, at);
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(4), body);
            o[10] = (byte)count;
            o[11] = 1;
            int e = 12;
            int data = 12 + (4 * count);
            int rel = 0;
            foreach ((int field, (bool direct, int value, byte[]? d)) in _fields)
            {
                int v = direct ? value : rel;
                o[e] = (byte)(field | (direct ? 0x80 : 0));
                o[e + 1] = (byte)v;
                o[e + 2] = (byte)(v >> 8);
                o[e + 3] = (byte)(v >> 16);
                e += 4;
                if (!direct)
                {
                    d!.CopyTo(o, data + rel);
                    rel += d.Length;
                }
            }

            return o;
        }
    }

    // oedbx DBX format notes, OE_Dbx_Tree: nodes of up to 0x33 (value, child, subtree count) entries after a child pointer.
    private sealed class TreeBuilder
    {
        private sealed class Node
        {
            public int At;
            public int Parent;
            public int Id;
            public int Total;
            public Node? Left;
            public List<(int Value, Node? Child)> Entries = [];
        }

        public static int Build(Layout l, int[] values, int fanout)
        {
            if (values.Length == 0)
            {
                return 0;
            }

            Node root = Make(l, values, 0, values.Length, Math.Clamp(fanout, 1, 0x33), 0, 0);
            Write(l, root);
            return root.At;
        }

        private static Node Make(Layout l, int[] v, int lo, int hi, int n, int parent, int id)
        {
            Node node = new() { At = l.Alloc(NodeSize), Parent = parent, Id = id, Total = hi - lo };
            int total = hi - lo;
            if (total <= n)
            {
                for (int i = lo; i < hi; i++)
                {
                    node.Entries.Add((v[i], null));
                }

                return node;
            }

            // n separators and n + 1 groups sharing the rest.
            int rest = total - n;
            int groups = n + 1;
            int at = lo;
            for (int g = 0; g < groups; g++)
            {
                int size = (rest / groups) + (g < rest % groups ? 1 : 0);
                Node? child = size > 0 ? Make(l, v, at, at + size, n, node.At, g == 0 ? 0 : g - 1) : null;
                at += size;
                if (g == 0)
                {
                    node.Left = child;
                }
                else
                {
                    node.Entries[g - 1] = (node.Entries[g - 1].Value, child);
                }

                if (g < n)
                {
                    node.Entries.Add((v[at], null));
                    at++;
                }
            }

            return node;
        }

        private static void Write(Layout l, Node node)
        {
            byte[] b = new byte[NodeSize];
            BinaryPrimitives.WriteInt32LittleEndian(b, node.At);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), node.Left?.At ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), node.Parent);
            b[16] = (byte)node.Id;
            b[17] = (byte)node.Entries.Count;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), node.Left?.Total ?? 0);
            for (int i = 0; i < node.Entries.Count; i++)
            {
                int at = 24 + (12 * i);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), node.Entries[i].Value);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 4), node.Entries[i].Child?.At ?? 0);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 8), node.Entries[i].Child?.Total ?? 0);
            }

            l.Put(node.At, b);
            if (node.Left is not null)
            {
                Write(l, node.Left);
            }

            foreach ((_, Node? c) in node.Entries)
            {
                if (c is not null)
                {
                    Write(l, c);
                }
            }
        }
    }
}
