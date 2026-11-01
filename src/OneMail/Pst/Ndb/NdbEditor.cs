using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// Changes an existing Unicode PST in place: allocates from the free space the allocation maps record, edits the node and block B-trees, and
/// commits crash-consistently (see <c>docs/pst-writing.md</c>, "In place"). The editor owns the file exclusively. Reads go through <see cref="Committed"/>,
/// which shows the last commit; changes are visible there after <see cref="Commit"/>.
/// </summary>
/// <remarks>
/// This is the node database layer: blocks and nodes. <c>RecursiveAI.OneMail.Pst.Messaging.PstWritableStore</c> builds folders and items on it. The
/// commit path has not been through an independent review yet; treat it as preview quality.
/// </remarks>
public sealed class NdbEditor : IBidAllocator, INdbBlockSink, INdbNodeSink, IDisposable
{
    private const int HeaderSize = 564;

    private readonly EditorIo _io;
    private readonly NdbEditorOptions _options;
    private byte[] _header = new byte[HeaderSize];      // the committed header, as it is on disk
    private NdbFile _reader = null!;
    private EditorMaps _maps = null!;
    private EditorPageStore _pages = null!;
    private EditorBTree _nbt = null!;
    private EditorBTree _bbt = null!;
    private ulong _nextBlock;
    private uint[] _counters = new uint[32];
    private uint _unique;
    private long _committedRanges;
    private long _eof;
    private byte _densityFlags;
    private bool _changed;
    private bool _absoluteFree;
    private bool _rebuildPending;
    private bool _failed;
    private bool _disposed;

    private NdbEditor(EditorIo io, NdbEditorOptions options)
    {
        _io = io;
        _options = options;
        Blocks = null!;
    }

    // ---- opening -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Opens a PST file for writing, exclusively: no other program (or second editor) can open it while this one holds it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Options, or null for the defaults (nothing suspicious accepted, freed space zeroed).</param>
    /// <returns>The editor.</returns>
    /// <exception cref="IOException">The file cannot be opened exclusively.</exception>
    /// <exception cref="NdbEditRefusedException">The file is not a Unicode PST, or looks suspicious and the options do not allow it.</exception>
    public static NdbEditor Open(string path, NdbEditorOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        try
        {
            return Open(fs, leaveOpen: false, options);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Opens a PST held in a seekable, readable and writable stream (memory, or a file the caller has opened exclusively).</summary>
    /// <param name="stream">The stream. Writes go straight to it; the editor never buffers across a commit.</param>
    /// <param name="leaveOpen">True to leave the stream open when the editor is disposed.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The editor.</returns>
    /// <exception cref="ArgumentException">The stream cannot seek, read and write.</exception>
    /// <exception cref="NdbEditRefusedException">The file is not a Unicode PST, or looks suspicious and the options do not allow it.</exception>
    public static NdbEditor Open(Stream stream, bool leaveOpen, NdbEditorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The stream must be readable, writable and seekable.", nameof(stream));
        }

        return Open(new StreamEditorIo(stream, leaveOpen), options ?? new NdbEditorOptions());
    }

    internal static NdbEditor Open(EditorIo io, NdbEditorOptions options)
    {
        var e = new NdbEditor(io, options);
        try
        {
            e.Initialize();
            return e;
        }
        catch
        {
            e.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        var refusals = new List<NdbRefusal>();
        var defects = new List<NdbDefect>();
        int got = _io.Read(0, _header);
        if (got < HeaderSize || !NdbHeader.TryParse(_header.AsSpan(0, got), out NdbHeader? h, defects) || h is null)
        {
            throw new NdbEditRefusedException([new NdbRefusal(NdbRefusalReason.NotAPst, defects.Count > 0 ? defects[0].Detail : "the file is too short", false)]);
        }

        if (h.Format == NdbFormat.Ansi)
        {
            refusals.Add(new NdbRefusal(NdbRefusalReason.Ansi, "ANSI files (wVer 14, 15) are read-only", false));
        }
        else if (h.Format == NdbFormat.Unicode4K || h.Version > 23)
        {
            refusals.Add(new NdbRefusal(NdbRefusalReason.LargePages, $"wVer {h.Version}: files with 4 KiB pages are read-only", false));
        }
        else if (h.Version != 21 && h.Version != 23)
        {
            refusals.Add(new NdbRefusal(NdbRefusalReason.UnsupportedVersion, $"wVer {h.Version} is not a Unicode PST version", false));
        }

        if (h.ClientMagic == 0x4F53)
        {
            refusals.Add(new NdbRefusal(NdbRefusalReason.OfflineStore, "offline stores (OST) are read-only", false));
        }
        else if (h.ClientMagic != 0x4D53)
        {
            refusals.Add(new NdbRefusal(NdbRefusalReason.UnsupportedVersion, $"wMagicClient 0x{h.ClientMagic:x4} is not a personal store", false));
        }

        if (h.CryptMethod is not (0 or 1 or 2))
        {
            refusals.Add(new NdbRefusal(NdbRefusalReason.ProtectedContent, $"bCryptMethod 0x{h.CryptMethod:x2} cannot be written", false));
        }

        if (refusals.Count > 0)
        {
            throw new NdbEditRefusedException(refusals);
        }

        NdbRoot root = h.Root;
        if (!h.PartialCrcValid || !h.FullCrcValid || h.Sentinel != 0x80)
        {
            refusals.Add(new NdbRefusal(NdbRefusalReason.HeaderChecksum, "the header's checksums or sentinel do not match", true));
        }

        bool mapsInvalid = root.AMapValid is not (1 or 2);
        if (mapsInvalid)
        {
            refusals.Add(new NdbRefusal(NdbRefusalReason.AllocationMaps,
                "fAMapValid says the allocation maps are not trusted (a writer was stopped before it finished); they would be rebuilt from the B-trees", true));
        }

        if (!NdbFile.TryOpen(_io, leaveOpen: true, out NdbFile? file, out IReadOnlyList<NdbDefect> early, _options.Ndb) || file is null)
        {
            throw new NdbEditRefusedException([new NdbRefusal(NdbRefusalReason.NotAPst, early.Count > 0 ? early[0].Detail : "the header is unusable", false)]);
        }

        _reader = file;
        file.ValidateAllocation();
        NdbDensityList? density = file.ReadDensityList();
        int other = 0;
        bool badMaps = false;
        bool badDensity = false;
        foreach (NdbDefect d in file.Defects)
        {
            switch (d.Kind)
            {
                case NdbDefectKind.HeaderPartialCrcMismatch or NdbDefectKind.HeaderFullCrcMismatch or NdbDefectKind.BadSentinel or NdbDefectKind.UnknownClientMagic:
                case NdbDefectKind.AMapFlagInvalid:
                    break;
                case NdbDefectKind.AllocationPageInvalid or NdbDefectKind.AMapFreeSpaceMismatch:
                    if (!badMaps)
                    {
                        refusals.Add(new NdbRefusal(NdbRefusalReason.AllocationMaps, d.Detail, true));
                    }

                    badMaps = true;
                    break;
                case NdbDefectKind.DensityListInvalid:
                    if (!badDensity)
                    {
                        refusals.Add(new NdbRefusal(NdbRefusalReason.DensityList, d.Detail, true));
                    }

                    badDensity = true;
                    break;
                case NdbDefectKind.FileSizeMismatch when _io.Length > (long)root.FileEof:
                    break;   // bytes after the recorded end are what a writer stopped while growing the file leaves; they are cut off below
                default:
                    other++;
                    if (other == _options.DefectThreshold + 1)
                    {
                        refusals.Add(new NdbRefusal(NdbRefusalReason.Damage, $"{d.Kind} at 0x{d.Offset:x}: {d.Detail}", true));
                    }

                    break;
            }
        }

        NdbOpenRisks needed = NdbOpenRisks.None;
        foreach (NdbRefusal r in refusals)
        {
            needed |= r.Reason switch
            {
                NdbRefusalReason.HeaderChecksum => NdbOpenRisks.HeaderChecksum,
                NdbRefusalReason.AllocationMaps => NdbOpenRisks.AllocationMaps,
                NdbRefusalReason.DensityList => NdbOpenRisks.DensityList,
                _ => NdbOpenRisks.Damage,
            };
        }

        if ((needed & ~_options.Allow) != 0)
        {
            file.Dispose();
            throw new NdbEditRefusedException(refusals);
        }

        _densityFlags = density?.Flags ?? 1;
        _rebuildPending = mapsInvalid || badMaps;
        LoadState(rebuildMaps: _rebuildPending, rebuildDensity: badDensity || density is null, density);

        if (_io.Length > _eof)
        {
            // MS-PST: the header's ibFileEof is the end of the file; anything after it was written by a commit that never finished.
            _io.SetLength(_eof);
            _io.Barrier();
        }
    }

    // Reads the committed header into the editing state: counters, roots, trees, maps.
    private void LoadState(bool rebuildMaps, bool rebuildDensity, NdbDensityList? density)
    {
        NdbHeader h = _reader.Header;
        NdbRoot root = h.Root;
        _nextBlock = (h.NextBlockBid + 3) & ~3UL;
        _counters = h.NodeCounters.ToArray();
        _unique = h.Unique;
        _eof = (long)root.FileEof;
        long fromLast = root.AMapLast >= (ulong)EditorMaps.FirstAMap ? (((long)root.AMapLast - EditorMaps.FirstAMap) / EditorMaps.RangeLength) + 1 : 0;
        long fromEof = _eof > EditorMaps.FirstAMap ? ((_eof - EditorMaps.FirstAMap + EditorMaps.RangeLength - 1) / EditorMaps.RangeLength) : 0;
        _committedRanges = Math.Max(fromLast, fromEof);
        _maps = new EditorMaps(_io, _committedRanges, _options.ZeroFreedSpace);
        _pages = new EditorPageStore(_io, _maps, h.NextPageBid);
        _nbt = new EditorBTree(_pages, isNbt: true, root.NodeBTree);
        _bbt = new EditorBTree(_pages, isNbt: false, root.BlockBTree);
        Blocks ??= new NdbBlockBuilder(this, this, h.Encryption);
        _changed = false;
        _absoluteFree = false;

        if (rebuildMaps)
        {
            RebuildMaps();
        }
        else if (rebuildDensity)
        {
            RebuildDensity();
        }
        else if (density is not null)
        {
            _maps.SetDensity(density.Entries.Select(static e => ((long)e.AMapIndex, e.FreeSlots)));
        }
    }

    // MS-PST 2.6.1.3.7: mark every map free, then mark the map pages, every B-tree page and every block the trees reach.
    private void RebuildMaps()
    {
        _maps.ResetForRebuild(_committedRanges);
        _nbt.ForEachPage(r => _maps.MarkUsed((long)r.Ib, EditorMaps.PageSize));
        _bbt.ForEachPage(r => _maps.MarkUsed((long)r.Ib, EditorMaps.PageSize));
        _bbt.ForEach((_, entry) =>
        {
            long ib = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            int cb = BinaryPrimitives.ReadUInt16LittleEndian(entry[16..]);
            _maps.MarkUsed(ib, EditorMaps.BlockTotal(cb));
        });
        _absoluteFree = true;
        RebuildDensity();
        _changed = true;
    }

    private void RebuildDensity()
    {
        var all = new List<(long Index, int Free)>();
        for (long k = 0; k < _maps.RangeCount; k++)
        {
            all.Add((k, _maps.CountFreeSlots(k)));
        }

        all.Sort(static (a, b) => b.Free.CompareTo(a.Free));
        _maps.SetDensity(all.Take(EditorMaps.DensityCapacity));
        _changed = true;
    }

    // ---- state -------------------------------------------------------------------------------------------------------------------------

    /// <summary>The file as of the last commit (or as opened). It is replaced by each <see cref="Commit"/>; do not keep it across one.</summary>
    public NdbFile Committed => _reader;

    internal EditorIo Source => _io;

    /// <summary>True when something has been changed since the last commit.</summary>
    public bool HasChanges => _changed;

    /// <summary>Roughly how many bytes of changed B-tree pages the editor holds for the next commit; a caller commits when this passes a bound it picks.</summary>
    public long PendingBytes => (long)_pages.DirtyCount * EditorMaps.PageSize;

    /// <summary>The block builder: blocks written through it are placed in free space and entered in the block B-tree.</summary>
    public NdbBlockBuilder Blocks { get; private set; }

    // ---- BIDs and blocks ---------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public Bid NextBlockBid(bool isInternal)
    {
        ThrowIfUnusable();
        _changed = true;
        Bid bid = new(_nextBlock | (isInternal ? 2UL : 0));
        _nextBlock += 4;
        return bid;
    }

    /// <inheritdoc/>
    public void Place(NdbBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        ThrowIfUnusable();
        _changed = true;
        int total = block.TotalLength;
        long ib = _maps.Allocate(total, 1);
        byte[] buf = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            block.WriteTo(buf.AsSpan(0, total), (ulong)ib);
            _io.Write(ib, buf.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        // MS-PST 2.2.2.7.7.3: BBTENTRY is BREF, cb, cRef, padding. cRef 2 is the tree entry plus the one reference from the node or block that names it.
        Span<byte> e = stackalloc byte[24];
        e.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(e, block.Bid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(e[8..], (ulong)ib);
        BinaryPrimitives.WriteUInt16LittleEndian(e[16..], (ushort)block.DataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(e[18..], (ushort)block.RefCount);
        _bbt.Upsert(block.Bid.Normalized.Value, e);
    }

    /// <summary>The location and size of a block, from the block B-tree as it stands now (including blocks written since the last commit).</summary>
    public bool TryGetBlock(Bid bid, out BlockEntry entry)
    {
        ThrowIfUnusable();
        Span<byte> e = stackalloc byte[24];
        if (!_bbt.TryGet(bid.Normalized.Value, e))
        {
            entry = default;
            return false;
        }

        entry = new BlockEntry(new Bref(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e)), BinaryPrimitives.ReadUInt64LittleEndian(e[8..])),
            BinaryPrimitives.ReadUInt16LittleEndian(e[16..]), BinaryPrimitives.ReadUInt16LittleEndian(e[18..]));
        return true;
    }

    /// <summary>
    /// Drops one reference to a block (MS-PST 2.6.1.1.7). At a count of one the block is freed and its entry removed; the blocks an XBLOCK, XXBLOCK, SLBLOCK or
    /// SIBLOCK names lose a reference too.
    /// </summary>
    /// <param name="bid">The block; a null BID does nothing.</param>
    public void Release(Bid bid)
    {
        ThrowIfUnusable();
        if (bid.IsNull)
        {
            return;
        }

        _changed = true;
        Span<byte> e = stackalloc byte[24];
        ulong key = bid.Normalized.Value;
        if (!_bbt.TryGet(key, e))
        {
            return;
        }

        int cRef = BinaryPrimitives.ReadUInt16LittleEndian(e[18..]);
        if (cRef > 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(e[18..], (ushort)(cRef - 1));
            _bbt.Upsert(key, e);
            return;
        }

        long ib = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
        int cb = BinaryPrimitives.ReadUInt16LittleEndian(e[16..]);
        _bbt.Remove(key);
        if (bid.IsInternal)
        {
            ReleaseChildren(ib, cb);
        }

        _maps.Free(ib, EditorMaps.BlockTotal(cb));
    }

    // MS-PST 2.2.2.8.3: XBLOCK/XXBLOCK (btype 1) list data or XBLOCK BIDs; 2.2.2.8.3.3: SLBLOCK (btype 2, level 0) lists SLENTRYs and SIBLOCK (level 1) SIENTRYs.
    private void ReleaseChildren(long ib, int cb)
    {
        if (cb < 8)
        {
            return;
        }

        byte[] data = new byte[cb];
        if (_io.Read(ib, data) != cb)
        {
            return;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        if (data[0] == 1)
        {
            for (int i = 0; i < count && 8 + (i * 8) + 8 <= cb; i++)
            {
                Release(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8 + (i * 8)))));
            }
        }
        else if (data[0] == 2 && data[1] == 0)
        {
            for (int i = 0; i < count && 8 + (i * 24) + 24 <= cb; i++)
            {
                Release(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8 + (i * 24) + 8))));
                Release(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8 + (i * 24) + 16))));
            }
        }
        else if (data[0] == 2)
        {
            for (int i = 0; i < count && 8 + (i * 16) + 16 <= cb; i++)
            {
                Release(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8 + (i * 16) + 8))));
            }
        }
    }

    // ---- nodes -------------------------------------------------------------------------------------------------------------------------

    /// <summary>Finds a node as it stands now, including changes since the last commit.</summary>
    public bool TryGetNode(Nid nid, out NodeEntry entry)
    {
        ThrowIfUnusable();
        Span<byte> e = stackalloc byte[32];
        if (!_nbt.TryGet(nid.Value, e))
        {
            entry = default;
            return false;
        }

        entry = new NodeEntry(nid, new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[8..])), new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[16..])),
            new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e[24..])));
        return true;
    }

    private void PutNode(in NodeEntry node)
    {
        Span<byte> e = stackalloc byte[32];
        e.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(e, node.Nid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(e[8..], node.Data.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(e[16..], node.Sub.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], node.Parent.Value);
        _nbt.Upsert(node.Nid.Value, e);
    }

    /// <summary>
    /// Adds a node, or replaces the node with that NID. The new entry's blocks must already be placed (they carry the reference the node holds). A replaced
    /// node's data and subnode blocks lose the reference it held.
    /// </summary>
    public void UpsertNode(NodeEntry node)
    {
        ThrowIfUnusable();
        _changed = true;
        bool had = TryGetNode(node.Nid, out NodeEntry old);
        PutNode(node);
        if (had)
        {
            if (old.Data != node.Data)
            {
                Release(old.Data);
            }

            if (old.Sub != node.Sub)
            {
                Release(old.Sub);
            }
        }
    }

    /// <inheritdoc/>
    void INdbNodeSink.AddNode(NodeEntry entry) => UpsertNode(entry);

    /// <summary>Changes a node's parent NID without touching its blocks.</summary>
    /// <returns>False when there is no such node.</returns>
    public bool SetNodeParent(Nid nid, Nid parent)
    {
        ThrowIfUnusable();
        if (!TryGetNode(nid, out NodeEntry n))
        {
            return false;
        }

        _changed = true;
        PutNode(n with { Parent = parent });
        return true;
    }

    /// <summary>Removes a node and drops the references it held.</summary>
    /// <returns>False when there is no such node.</returns>
    public bool RemoveNode(Nid nid)
    {
        ThrowIfUnusable();
        if (!TryGetNode(nid, out NodeEntry n))
        {
            return false;
        }

        _changed = true;
        _nbt.Remove(nid.Value);
        Release(n.Data);
        Release(n.Sub);
        return true;
    }

    /// <summary>Takes the next unused NID of a type from the header's counters (MS-PST 2.2.2.6, rgnid).</summary>
    public Nid AllocateNid(NidType type)
    {
        ThrowIfUnusable();
        int t = (int)type;
        uint index = Math.Max(_counters[t], 0x400u);
        Nid nid;
        do
        {
            nid = new Nid((index++ << 5) | (uint)type);
        }
        while (TryGetNode(nid, out _));
        _counters[t] = index;
        _changed = true;
        return nid;
    }

    /// <summary>Calls <paramref name="visit"/> for every node, in NID order, as the tree stands now.</summary>
    public void ForEachNode(Action<NodeEntry> visit)
    {
        ThrowIfUnusable();
        _nbt.ForEach((key, e) => visit(new NodeEntry(new Nid((uint)key), new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[8..])),
            new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[16..])), new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e[24..])))));
    }

    // ---- commit ------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes every change since the last commit durable, all or nothing. A stop at any point leaves the file as it was before this call or as it is
    /// after it (or, between the two header writes, as it was with its allocation maps marked invalid: the next open with
    /// <see cref="NdbOpenRisks.AllocationMaps"/> rebuilds them).
    /// </summary>
    /// <exception cref="IOException">A write failed. The editor cannot be used afterwards; dispose it and reopen the file.</exception>
    public void Commit()
    {
        ThrowIfUnusable();
        if (!_changed)
        {
            return;
        }

        try
        {
            CommitCore();
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private void CommitCore()
    {
        NdbRoot root = _reader.Header.Root;
        long oldRanges = _committedRanges;

        // 1. The new B-tree pages go to free space no committed state refers to, and the blocks went there as they were placed.
        _pages.Flush();
        _io.Barrier();

        // 2. From here the maps change: say so first (MS-PST 2.6.1.3.7), with the old roots still in place.
        WriteHeader(root.NodeBTree, root.BlockBTree, (long)root.FileEof, root.AMapLast, (long)root.AMapFree, valid: 0, nextPage: _reader.Header.NextPageBid, bidNextB: _reader.Header.NextBlockBid);
        _io.Barrier();

        // 3. The maps record the new allocations and the frees; freed space is not wiped yet because the old tree still reaches it.
        List<(long Ib, int Length)> freed = _maps.ApplyFrees();
        long eof = Math.Max((long)root.FileEof, _maps.RequiredEof);
        if (_io.Length < eof)
        {
            _io.SetLength(eof);
        }

        _maps.WriteMaps(oldRanges);
        ulong densityBid = _pages.TakePageBid();
        uint current = (uint)Math.Max(0, _maps.RangeCount - 1);
        _io.Write(EditorMaps.DensityListOffset, _maps.BuildDensityPage(_densityFlags, current, densityBid));
        _io.Barrier();

        // 4. One header write switches to the new state: the new roots, sizes and counters, with the maps valid again.
        long amapLast = EditorMaps.RangeStart(_maps.RangeCount - 1);
        long free;
        if (_absoluteFree)
        {
            free = 0;
            for (long k = 0; k < _maps.RangeCount; k++)
            {
                free += (long)_maps.CountFreeSlots(k) * EditorMaps.Slot;
            }
        }
        else
        {
            free = Math.Max(0, (long)root.AMapFree + _maps.FreeDelta);
        }

        WriteHeader(_nbt.Root, _bbt.Root, eof, (ulong)amapLast, free, valid: 2, nextPage: _pages.NextPageBid, bidNextB: _nextBlock);
        _io.Barrier();

        // 5. The old state is gone, so what it held can be wiped.
        if (_options.ZeroFreedSpace)
        {
            foreach ((long ib, int len) in freed)
            {
                _maps.ZeroExtent(ib, len);
            }

            _io.Barrier();
        }

        _maps.Committed();
        _pages.Clear();
        _rebuildPending = false;
        Reopen();
    }

    // MS-PST 2.2.2.6 HEADER (Unicode): dwCRCPartial covers bytes 8..478, dwCRCFull bytes 8..523. The header is 564 bytes and is written in one call.
    private void WriteHeader(Bref nbt, Bref bbt, long eof, ulong amapLast, long free, byte valid, ulong nextPage, ulong bidNextB)
    {
        byte[] h = (byte[])_header.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), nextPage);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), valid == 2 ? _unique + 1 : _unique);
        for (int i = 0; i < 32; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(44 + (i * 4)), valid == 2 ? _counters[i] : _reader.Header.NodeCounters[i]);
        }

        Span<byte> r = h.AsSpan(180);
        BinaryPrimitives.WriteUInt64LittleEndian(r[4..], (ulong)eof);
        BinaryPrimitives.WriteUInt64LittleEndian(r[12..], amapLast);
        BinaryPrimitives.WriteUInt64LittleEndian(r[20..], (ulong)free);
        BinaryPrimitives.WriteUInt64LittleEndian(r[36..], nbt.Bid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(r[44..], nbt.Ib);
        BinaryPrimitives.WriteUInt64LittleEndian(r[52..], bbt.Bid.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(r[60..], bbt.Ib);
        r[68] = valid;
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(516), bidNextB);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), NdbChecksums.Crc32(h.AsSpan(8, 471)));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(524), NdbChecksums.Crc32(h.AsSpan(8, 516)));
        _io.Write(0, h);
        _header = h;
    }

    private void Reopen()
    {
        _reader.Dispose();
        if (!NdbFile.TryOpen(_io, leaveOpen: true, out NdbFile? file, out _, _options.Ndb) || file is null)
        {
            throw new IOException("the file cannot be read back after the commit");
        }

        _reader = file;
        NdbDensityList? density = file.ReadDensityList();
        _densityFlags = density?.Flags ?? 1;
        LoadState(rebuildMaps: false, rebuildDensity: false, density);
    }

    /// <summary>Discards everything since the last commit. Blocks written to free space stay there unreferenced; the maps on disk never recorded them.</summary>
    public void Rollback()
    {
        ThrowIfUnusable();
        if (!_changed)
        {
            return;
        }

        _pages.Clear();
        _maps.Rollback(_committedRanges);
        _reader.Dispose();
        if (!NdbFile.TryOpen(_io, leaveOpen: true, out NdbFile? file, out _, _options.Ndb) || file is null)
        {
            _failed = true;
            throw new IOException("the file cannot be read back after the rollback");
        }

        _reader = file;
        NdbDensityList? density = file.ReadDensityList();
        LoadState(rebuildMaps: _rebuildPending, rebuildDensity: false, density);
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed)
        {
            throw new InvalidOperationException("a write failed; the editor can only be disposed");
        }
    }

    /// <summary>Releases the file. Changes that were not committed are dropped.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader?.Dispose();
        _io.Dispose();
    }
}
