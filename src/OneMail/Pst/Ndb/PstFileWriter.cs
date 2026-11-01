using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>Options for <see cref="PstFileWriter"/>.</summary>
public sealed class PstFileWriterOptions
{
    /// <summary>
    /// How data blocks are encoded (bCryptMethod). Default <see cref="NdbEncryption.Permute"/>, which is what Outlook writes; it is
    /// obfuscation that every reader undoes, not protection. <see cref="NdbEncryption.None"/> and <see cref="NdbEncryption.Cyclic"/> are the others.
    /// </summary>
    public NdbEncryption Encryption { get; init; } = NdbEncryption.Permute;

    /// <summary>
    /// Makes a temporary read-write stream for sorting the node B-tree's entries, which arrive in any order. Default: a temporary file that
    /// is deleted when closed, or memory where files are not available (the browser). Entries are sorted 65,536 at a time and merged at the end.
    /// </summary>
    public Func<Stream>? CreateSpool { get; init; }

    /// <summary>How many node entries are sorted in memory before a run goes to the spool. Default 65,536 (1.5 MiB).</summary>
    public int NodeRunLength { get; init; } = 65_536;
}

/// <summary>What <see cref="PstFileWriter.Complete"/> wrote.</summary>
/// <param name="FileLength">The file size in bytes: 0x4400 plus a whole number of 253,952-byte allocation ranges.</param>
/// <param name="Blocks">Blocks in the block B-tree.</param>
/// <param name="Nodes">Nodes in the node B-tree.</param>
/// <param name="AllocationMaps">AMap pages.</param>
/// <param name="FreeBytes">Free bytes over all AMaps (cbAMapFree): the unused tail of the last range plus any gaps left before aligned pages.</param>
public readonly record struct PstFileSummary(long FileLength, long Blocks, long Nodes, long AllocationMaps, long FreeBytes);

/// <summary>
/// Writes the node database of a new Unicode PST (MS-PST 2.2.2, 512-byte pages, wVer 23): the blocks that an <see cref="NdbBlockBuilder"/> makes,
/// the block and node B-trees, the allocation maps and the header. It only appends, front to back, and holds a bounded amount of memory
/// however many blocks and nodes there are.
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> The header (564 bytes) is written last; until then the first 0x4400 bytes are zero, so a file the writer was stopped on
/// is refused by every reader (no "!BDN" magic) rather than mistaken for a store. From 0x4400 the file is a series of 253,952-byte ranges
/// (MS-PST 2.2.2.7.2). Each starts with its AMap page, then, where the format wants them, a PMap page (every 8th range), an FMap page (range 128,
/// then every 496th) and an FPMap page (range 8,192, then every 3,968th). Blocks (64-byte aligned) and B-tree pages (512-byte aligned) fill the rest in the order they
/// are made; neither crosses into the next range. A range is held in memory and written when full; the last one is written whole, so the file
/// ends on a range boundary as Outlook's files do. The density list page is at 0x4200.</para>
/// <para><b>Block B-tree</b> pages are written as entries arrive (bottom-up, only one open page per level), so it costs no memory per block.
/// Blocks are placed in the order their BIDs were taken; a BID taken early for a block that is written later (<see cref="NdbBlockBuilder.DataTreeWriter.ReserveBlock"/>)
/// holds back only the entries after it until it is placed.</para>
/// <para><b>Node B-tree.</b> Nodes can be added in any order. They are sorted in runs of <see cref="PstFileWriterOptions.NodeRunLength"/>, runs beyond the first go to a
/// spool stream, and <see cref="Complete"/> merges them into the tree.</para>
/// <para><b>Reference counts.</b> Every block gets the count <see cref="NdbBlock.RefCount"/> suggests: the block B-tree entry plus the one node,
/// subnode or tree block that names it. Nothing is ever freed, so a new file has no freed blocks to zero.</para>
/// <para><b>Unverified against Outlook.</b> The FMap and FPMap page positions and contents (files over about 32 MB), and the PMap contents (all ones, as seen in a
/// file Outlook wrote), follow the specification's statements that they must exist at the right intervals; the pages are valid and our readers and libpff accept them.</para>
/// <para>Not thread-safe. The output stream must be seekable and writable.</para>
/// </remarks>
public sealed class PstFileWriter : IBidAllocator, INdbBlockSink, IDisposable
{
    private const int PageSize = 512;
    private const int AMapBits = 496 * 8;
    private const int Slot = 64;
    private const long RangeLength = (long)AMapBits * Slot;       // 253,952
    private const long FirstAMap = 0x4400;
    private const long DensityListOffset = 0x4200;
    private const int PageDataLength = 496;                       // a page less its 16-byte trailer
    private const int DensityCapacity = (PageSize - 16 - 8 - 12) / 4;

    private readonly Stream _out;
    private readonly bool _leaveOpen;
    private readonly PstFileWriterOptions _options;
    private readonly SequentialBidAllocator _bids = new(1);
    private readonly SortedSet<ulong> _outstanding = [];          // BIDs taken and not yet placed
    private readonly SortedDictionary<ulong, BlockRecord> _pendingBlocks = [];
    private readonly PageTree _blockTree;
    private readonly PageTree _nodeTree;
    private readonly List<NodeEntry> _run = [];
    private readonly List<(long Offset, long Count)> _runs = [];
    private Stream? _spool;

    private readonly byte[] _range = new byte[RangeLength];
    private readonly byte[] _bits = new byte[496];
    private long _rangeIndex;
    private int _position;                                         // next free byte in the range
    private ulong _nextPageBid = 0x40;
    private long _blockCount;
    private long _nodeCount;
    private long _freeBytes;
    private readonly List<(int Index, int Free)> _density = [];
    private bool _completed;
    private bool _disposed;

    private readonly record struct BlockRecord(ulong Bid, long Ib, int Size, int RefCount);

    /// <summary>Starts a file.</summary>
    /// <param name="output">A seekable, writable stream positioned at its start. The writer writes the whole file to it.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="output"/> open on dispose.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException">The stream is not seekable and writable, or the encoding cannot be written.</exception>
    public PstFileWriter(Stream output, PstFileWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek || !output.CanWrite)
        {
            throw new ArgumentException("the output must be seekable and writable", nameof(output));
        }

        _out = output;
        _leaveOpen = leaveOpen;
        _options = options ?? new PstFileWriterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.NodeRunLength, 16);
        Blocks = new NdbBlockBuilder(this, this, _options.Encryption);
        _blockTree = new PageTree(this, ptype: 0x80, leafEntrySize: 24);
        _nodeTree = new PageTree(this, ptype: 0x81, leafEntrySize: 32);
        for (int i = 0; i < NodeCounters.Length; i++)
        {
            NodeCounters[i] = 0x400;
        }

        // The header, the reserved area and the density list are filled in at the end; zeros until then.
        _out.Write(new byte[FirstAMap]);
        StartRange(0);
    }

    /// <summary>Makes the blocks of the file: data blocks, data trees and subnode trees. Every block it makes is placed in the file as it is made.</summary>
    public NdbBlockBuilder Blocks { get; }

    /// <summary>rgnid: the next nidIndex to allocate for each of the 32 NID types, written to the header. All start at 0x400; the layer that hands out NIDs sets them.</summary>
    public uint[] NodeCounters { get; } = new uint[32];

    /// <summary>The blocks placed so far.</summary>
    public long BlockCount => _blockCount;

    /// <summary>The file offset the next allocation could start at, for progress reporting.</summary>
    public long Position => FirstAMap + (_rangeIndex * RangeLength) + _position;

    // ---- BID allocation and block placement -----------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public Bid NextBlockBid(bool isInternal)
    {
        Bid bid = _bids.NextBlockBid(isInternal);
        _outstanding.Add(bid.Value);
        return bid;
    }

    /// <inheritdoc/>
    public void Place(NdbBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        ThrowIfDone();
        long ib = Allocate(block.TotalLength, Slot, out int at);
        block.WriteTo(_range.AsSpan(at, block.TotalLength), (ulong)ib);
        _outstanding.Remove(block.Bid.Value);
        _blockCount++;
        var record = new BlockRecord(block.Bid.Value, ib, block.DataLength, block.RefCount);
        if (_pendingBlocks.Count == 0 && (_outstanding.Count == 0 || _outstanding.Min > record.Bid))
        {
            AddBlockEntry(record);
            return;
        }

        _pendingBlocks.Add(record.Bid, record);
        DrainBlocks();
    }

    private void DrainBlocks()
    {
        while (_pendingBlocks.Count > 0)
        {
            ulong first = 0;
            foreach (ulong key in _pendingBlocks.Keys)
            {
                first = key;
                break;
            }

            if (_outstanding.Count > 0 && _outstanding.Min < first)
            {
                return;
            }

            BlockRecord r = _pendingBlocks[first];
            _pendingBlocks.Remove(first);
            AddBlockEntry(r);
        }
    }

    // MS-PST 2.2.2.7.7.3.1 BBTENTRY: BREF (bid, ib), cb, cRef, dwPadding.
    private void AddBlockEntry(in BlockRecord r)
    {
        Span<byte> e = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(e, r.Bid);
        BinaryPrimitives.WriteUInt64LittleEndian(e[8..], (ulong)r.Ib);
        BinaryPrimitives.WriteUInt16LittleEndian(e[16..], (ushort)r.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(e[18..], (ushort)r.RefCount);
        e[20..].Clear();
        _blockTree.Add(r.Bid, e);
    }

    // ---- nodes -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Adds a node to the node B-tree. Data and subnode BIDs must name blocks already placed.</summary>
    /// <param name="entry">The node. Its NID must be unique in the file.</param>
    /// <exception cref="InvalidOperationException">The file is complete.</exception>
    public void AddNode(NodeEntry entry)
    {
        ThrowIfDone();
        _run.Add(entry);
        _nodeCount++;
        if (_run.Count >= _options.NodeRunLength)
        {
            SpillRun();
        }
    }

    // A run is 24 bytes per node: nid, bidData, bidSub, nidParent.
    private void SpillRun()
    {
        _run.Sort(static (a, b) => a.Nid.Value.CompareTo(b.Nid.Value));
        _spool ??= (_options.CreateSpool ?? DefaultSpool)();
        _spool.Seek(0, SeekOrigin.End);
        long start = _spool.Position;
        byte[] buffer = new byte[24 * 4096];
        int used = 0;
        foreach (NodeEntry n in _run)
        {
            Span<byte> s = buffer.AsSpan(used, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(s, n.Nid.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], n.Parent.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(s[8..], n.Data.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(s[16..], n.Sub.Value);
            used += 24;
            if (used == buffer.Length)
            {
                _spool.Write(buffer, 0, used);
                used = 0;
            }
        }

        _spool.Write(buffer, 0, used);
        _runs.Add((start, _run.Count));
        _run.Clear();
    }

    private static Stream DefaultSpool()
    {
        try
        {
            return new FileStream(Path.Combine(Path.GetTempPath(), "onemail-pst-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is PlatformNotSupportedException or UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return new MemoryStream();
        }
    }

    private sealed class RunReader
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[24 * 512];
        private long _next;      // file position of the next unread record
        private long _left;
        private int _have;
        private int _at;

        public RunReader(Stream s, long start, long count)
        {
            _s = s;
            _next = start;
            _left = count;
        }

        public bool TryRead(out NodeEntry entry)
        {
            if (_at >= _have)
            {
                if (_left == 0)
                {
                    entry = default;
                    return false;
                }

                int n = (int)Math.Min(_left, 512);
                _s.Seek(_next, SeekOrigin.Begin);
                _s.ReadExactly(_buf, 0, n * 24);
                _next += n * 24L;
                _left -= n;
                _have = n * 24;
                _at = 0;
            }

            ReadOnlySpan<byte> s = _buf.AsSpan(_at, 24);
            entry = new NodeEntry(new Nid(BinaryPrimitives.ReadUInt32LittleEndian(s)), new Bid(BinaryPrimitives.ReadUInt64LittleEndian(s[8..])),
                new Bid(BinaryPrimitives.ReadUInt64LittleEndian(s[16..])), new Nid(BinaryPrimitives.ReadUInt32LittleEndian(s[4..])));
            _at += 24;
            return true;
        }
    }

    private void WriteNodeTree()
    {
        if (_runs.Count == 0)
        {
            _run.Sort(static (a, b) => a.Nid.Value.CompareTo(b.Nid.Value));
            foreach (NodeEntry n in _run)
            {
                AddNodeEntry(n);
            }

            _run.Clear();
            return;
        }

        if (_run.Count > 0)
        {
            SpillRun();
        }

        var readers = new RunReader[_runs.Count];
        var heap = new PriorityQueue<(NodeEntry Entry, int Run), uint>();
        for (int i = 0; i < readers.Length; i++)
        {
            readers[i] = new RunReader(_spool!, _runs[i].Offset, _runs[i].Count);
            if (readers[i].TryRead(out NodeEntry first))
            {
                heap.Enqueue((first, i), first.Nid.Value);
            }
        }

        while (heap.TryDequeue(out (NodeEntry Entry, int Run) top, out _))
        {
            AddNodeEntry(top.Entry);
            if (readers[top.Run].TryRead(out NodeEntry next))
            {
                heap.Enqueue((next, top.Run), next.Nid.Value);
            }
        }
    }

    private ulong _lastNid = ulong.MaxValue;

    // MS-PST 2.2.2.7.7.4.1 NBTENTRY: nid (8, the 32-bit NID widened), bidData, bidSub, nidParent, dwPadding.
    private void AddNodeEntry(NodeEntry n)
    {
        if (_lastNid == n.Nid.Value)
        {
            throw new InvalidOperationException($"node {n.Nid} was added twice");
        }

        _lastNid = n.Nid.Value;
        Span<byte> e = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(e, n.Nid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(e[8..], n.Data.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(e[16..], n.Sub.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], n.Parent.Value);
        e[28..].Clear();
        _nodeTree.Add(n.Nid.Value, e);
    }

    // ---- allocation ranges -------------------------------------------------------------------------------------------------------------

    private static bool HasPMap(long k) => k % 8 == 0;

    private static bool HasFMap(long k) => k >= 128 && (k - 128) % 496 == 0;

    private static bool HasFpMap(long k) => k >= 8192 && (k - 8192) % 3968 == 0;

    private long RangeStart => FirstAMap + (_rangeIndex * RangeLength);

    private void StartRange(long index)
    {
        _rangeIndex = index;
        Array.Clear(_range);
        Array.Clear(_bits);
        int reserved = 1 + (HasPMap(index) ? 1 : 0) + (HasFMap(index) ? 1 : 0) + (HasFpMap(index) ? 1 : 0);
        _position = reserved * PageSize;
        MarkAllocated(0, _position);
    }

    private void MarkAllocated(int at, int length)
    {
        for (int slot = at / Slot, end = (at + length) / Slot; slot < end; slot++)
        {
            _bits[slot >> 3] |= (byte)(1 << (slot & 7));
        }
    }

    /// <summary>Takes <paramref name="length"/> bytes aligned to <paramref name="align"/> in the current range, starting the next range when they do not fit.</summary>
    private long Allocate(int length, int align, out int at)
    {
        at = (_position + align - 1) / align * align;
        if (at + length > RangeLength)
        {
            FlushRange();
            at = (_position + align - 1) / align * align;
        }

        MarkAllocated(at, length);
        _position = at + length;
        return RangeStart + at;
    }

    private void FlushRange()
    {
        long k = _rangeIndex;
        int page = 0;
        WriteMapPage(page++, 0x84, _bits);
        if (HasPMap(k))
        {
            // MS-PST 2.2.2.7.3: a PMap tracks 512-byte pages; the one in a file Outlook wrote has every bit set.
            Span<byte> ones = stackalloc byte[496];
            ones.Fill(0xFF);
            WriteMapPage(page++, 0x83, ones);
        }

        if (HasFMap(k))
        {
            // MS-PST 2.2.2.7.5: each byte is the longest free run in an AMap, up to 255 slots; the ranges it covers are still to come, so all are free.
            Span<byte> free = stackalloc byte[496];
            free.Fill(0xFF);
            WriteMapPage(page++, 0x82, free);
        }

        if (HasFpMap(k))
        {
            Span<byte> ones = stackalloc byte[496];
            ones.Fill(0xFF);
            WriteMapPage(page++, 0x85, ones);
        }

        _out.Write(_range);
        int used = 0;
        foreach (byte b in _bits)
        {
            used += System.Numerics.BitOperations.PopCount(b);
        }

        int free2 = AMapBits - used;
        _freeBytes += (long)free2 * Slot;
        if (free2 > 0)
        {
            _density.Add(((int)k, free2));
            if (_density.Count > DensityCapacity)
            {
                int smallest = 0;
                for (int i = 1; i < _density.Count; i++)
                {
                    if (_density[i].Free < _density[smallest].Free)
                    {
                        smallest = i;
                    }
                }

                _density.RemoveAt(smallest);
            }
        }

        StartRange(k + 1);
    }

    // AMap, PMap, FMap and FPMap pages: 496 bytes of map, then a trailer whose signature is zero and whose BID is the page's own offset (MS-PST 2.2.2.7.1).
    private void WriteMapPage(int pageIndex, byte ptype, ReadOnlySpan<byte> map)
    {
        Span<byte> p = _range.AsSpan(pageIndex * PageSize, PageSize);
        map.CopyTo(p);
        FinishPage(p, ptype, 0, (ulong)(RangeStart + (pageIndex * PageSize)), signature: false);
    }

    private static void FinishPage(Span<byte> page, byte ptype, ulong bid, ulong ib, bool signature = true)
    {
        Span<byte> t = page[PageDataLength..];
        t[0] = ptype;
        t[1] = ptype;
        if (signature)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(t[2..], NdbChecksums.Signature(ib, bid));
        }
        else
        {
            bid = ib;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(t[4..], NdbChecksums.Crc32(page[..PageDataLength]));
        BinaryPrimitives.WriteUInt64LittleEndian(t[8..], bid);
    }

    // ---- B-tree pages ----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A B-tree written bottom-up while its entries arrive in key order: one open page per level; a full page is placed and one entry for it goes to the level above.
    /// </summary>
    private sealed class PageTree
    {
        private const int Capacity = 488;   // rgentries in a Unicode BTPAGE
        private const int IndexEntrySize = 24;
        private readonly PstFileWriter _w;
        private readonly byte _ptype;
        private readonly int _leafSize;
        private readonly List<Level> _levels = [];

        private sealed class Level
        {
            public readonly byte[] Buffer = new byte[Capacity];
            public int Count;
            public long Emitted;
            public ulong FirstKey;
        }

        public PageTree(PstFileWriter w, byte ptype, int leafEntrySize)
        {
            _w = w;
            _ptype = ptype;
            _leafSize = leafEntrySize;
            _levels.Add(new Level());
        }

        public void Add(ulong key, ReadOnlySpan<byte> entry) => AddAt(0, key, entry);

        private void AddAt(int level, ulong key, ReadOnlySpan<byte> entry)
        {
            if (level == _levels.Count)
            {
                _levels.Add(new Level());
            }

            Level l = _levels[level];
            int size = level == 0 ? _leafSize : IndexEntrySize;
            int max = Capacity / size;
            if (l.Count == max)
            {
                Emit(level, final: false);
            }

            if (l.Count == 0)
            {
                l.FirstKey = key;
            }

            entry.CopyTo(l.Buffer.AsSpan(l.Count * size));
            l.Count++;
        }

        // Places the level's open page and adds an index entry for it (the smallest key under it, its BID and offset) to the level above.
        private (Bid Bid, ulong Ib) Emit(int level, bool final)
        {
            Level l = _levels[level];
            int size = level == 0 ? _leafSize : IndexEntrySize;
            Span<byte> page = stackalloc byte[PageSize];
            page.Clear();
            l.Buffer.AsSpan(0, l.Count * size).CopyTo(page);
            // BTPAGE footer: cEnt, cEntMax, cbEnt, cLevel, then four bytes of padding (MS-PST 2.2.2.7.7.1).
            page[488] = (byte)l.Count;
            page[489] = (byte)(Capacity / size);
            page[490] = (byte)size;
            page[491] = (byte)level;
            ulong bid = _w._nextPageBid++;
            long ib = _w.Allocate(PageSize, PageSize, out int at);
            page.CopyTo(_w._range.AsSpan(at, PageSize));
            FinishPage(_w._range.AsSpan(at, PageSize), _ptype, bid, (ulong)ib);

            ulong first = l.FirstKey;
            l.Count = 0;
            l.Emitted++;
            if (!final)
            {
                Span<byte> up = stackalloc byte[IndexEntrySize];
                BinaryPrimitives.WriteUInt64LittleEndian(up, first);
                BinaryPrimitives.WriteUInt64LittleEndian(up[8..], bid);
                BinaryPrimitives.WriteUInt64LittleEndian(up[16..], (ulong)ib);
                AddAt(level + 1, first, up);
            }

            return (new Bid(bid), (ulong)ib);
        }

        /// <summary>Places every open page and returns the root.</summary>
        public Bref Finish()
        {
            for (int level = 0; ; level++)
            {
                Level l = _levels[level];
                bool onlyPage = l.Emitted == 0;
                if (onlyPage)
                {
                    // Nothing was placed at this level before, so nothing is above it: this page is the root (an empty tree is one empty leaf).
                    (Bid bid, ulong ib) = Emit(level, final: true);
                    return new Bref(bid, ib);
                }

                if (l.Count > 0)
                {
                    Emit(level, final: false);
                }
            }
        }
    }

    // ---- finishing ---------------------------------------------------------------------------------------------------------------------------

    private void ThrowIfDone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("the file is complete");
        }
    }

    /// <summary>
    /// Writes the B-trees, the last allocation range, the density list and the header, and flushes the stream. After this the file is a complete store.
    /// </summary>
    /// <returns>What was written.</returns>
    /// <exception cref="InvalidOperationException">A BID taken for a block was never placed, or two nodes share a NID.</exception>
    public PstFileSummary Complete()
    {
        ThrowIfDone();
        if (_outstanding.Count > 0 || _pendingBlocks.Count > 0)
        {
            throw new InvalidOperationException("a block whose BID was taken was never written");
        }

        WriteNodeTree();
        _completed = true;
        Bref nbt = _nodeTree.Finish();
        Bref bbt = _blockTree.Finish();
        long lastAMap = FirstAMap + (_rangeIndex * RangeLength);
        long ranges = _rangeIndex + 1;
        FlushRange();
        long eof = FirstAMap + (ranges * RangeLength);
        WriteDensityList();
        WriteHeader(eof, lastAMap, nbt, bbt);
        _out.Flush();
        _spool?.Dispose();
        _spool = null;
        return new PstFileSummary(eof, _blockCount, _nodeCount, ranges, _freeBytes);
    }

    // MS-PST 2.2.2.7.4: bFlags, cEntDList, wPadding, ulCurrentPage, then DLISTPAGEENTs (page number in the low 20 bits, free slots in the high 12), most free first.
    private void WriteDensityList()
    {
        byte[] page = new byte[PageSize];
        _density.Sort(static (a, b) => b.Free != a.Free ? b.Free.CompareTo(a.Free) : a.Index.CompareTo(b.Index));
        page[0] = 0x01;   // DFL_BACKFILL_COMPLETE
        page[1] = (byte)_density.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), (uint)(_rangeIndex - 1));
        for (int i = 0; i < _density.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8 + (i * 4)), (uint)_density[i].Index | ((uint)_density[i].Free << 20));
        }

        FinishPage(page, 0x86, _nextPageBid++, (ulong)DensityListOffset);
        _out.Seek(DensityListOffset, SeekOrigin.Begin);
        _out.Write(page);
    }

    // MS-PST 2.2.2.6 HEADER (Unicode). dwCRCPartial covers bytes 8..478, dwCRCFull bytes 8..523.
    private void WriteHeader(long eof, long lastAMap, Bref nbt, Bref bbt)
    {
        byte[] h = new byte[564];
        h[0] = 0x21;
        h[1] = 0x42;
        h[2] = 0x44;
        h[3] = 0x4E;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), 0x4D53);   // wMagicClient "SM": a personal store
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), 23);      // wVer: Unicode
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), 19);      // wVerClient
        h[14] = 1;
        h[15] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), _nextPageBid);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), 1);       // dwUnique
        for (int i = 0; i < 32; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(44 + (i * 4)), NodeCounters[i]);
        }

        Span<byte> r = h.AsSpan(180);
        BinaryPrimitives.WriteUInt64LittleEndian(r[4..], (ulong)eof);
        BinaryPrimitives.WriteUInt64LittleEndian(r[12..], (ulong)lastAMap);
        BinaryPrimitives.WriteUInt64LittleEndian(r[20..], (ulong)_freeBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(r[28..], 0);            // cbPMapFree: deprecated
        BinaryPrimitives.WriteUInt64LittleEndian(r[36..], nbt.Bid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(r[44..], nbt.Ib);
        BinaryPrimitives.WriteUInt64LittleEndian(r[52..], bbt.Bid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(r[60..], bbt.Ib);
        r[68] = 2;                                                       // fAMapValid: VALID_AMAP2
        h.AsSpan(256, 128).Fill(0xFF);                                   // rgbFM: deprecated, filled with 0xFF
        h.AsSpan(384, 128).Fill(0xFF);                                   // rgbFP
        h[512] = 0x80;                                                   // bSentinel
        h[513] = (byte)_options.Encryption;
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(516), _bids.NextIndex << 2);   // bidNextB
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), NdbChecksums.Crc32(h.AsSpan(8, 471)));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(524), NdbChecksums.Crc32(h.AsSpan(8, 516)));
        _out.Seek(0, SeekOrigin.Begin);
        _out.Write(h);
        _out.Seek(eof, SeekOrigin.Begin);
    }

    /// <summary>
    /// Releases the stream. A file that was not completed is left as written so far: its header is still zero, so readers refuse it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _spool?.Dispose();
        if (!_leaveOpen)
        {
            _out.Dispose();
        }
    }
}
