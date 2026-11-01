using System.Buffers.Binary;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Pst.Recovery;

/// <summary>
/// Recovery of a damaged or partly deleted PST or OST (ANSI, Unicode, OST 2013+): finds what the file still holds without trusting its header,
/// B-trees or allocation maps, and hands every message out with a <see cref="RecoveryProvenance"/> that says where it came from and how far to
/// trust it. Never writes to the source; <see cref="RepairTo(string, string?, CancellationToken)"/> writes a new file.
/// </summary>
/// <remarks>
/// <para><b>How.</b> The node tree is walked from the header's root first. When that walk is clean (and recovery is not forensic) the file is read as it
/// stands. Otherwise a scan finds every page and block by its trailer (signature and checksum, <see cref="NdbFile.Scan"/>), the block index is
/// rebuilt from the blocks found, and the node index is rebuilt from the leaf pages of the node tree found, the newest page winning when a node ID
/// appears in several. A header that cannot be read is replaced by one inferred from the body (generation by which layout finds the most pages and
/// blocks, encoding by which one makes the heap headers of sampled blocks appear). The messaging layer then reads the rebuilt index as usual.</para>
/// <para><b>Tags</b> (<c>docs/recovery.md</c>): <see cref="RecoveryStatus.Live"/> only when the node tree's own walk reaches the node, its folder lists it
/// and every block it needs reads back whole. Items rebuilt from scanned pages are <see cref="RecoveryStatus.Orphaned"/> (with an
/// <see cref="PstRecoveryDefectKind.IndexEntryRebuilt"/> defect) because nothing intact reached them; a repaired copy files them back in their folder.</para>
/// <para>The index is held in memory: about 60 bytes per block and per node. Everything else streams, one item at a time. Not thread-safe.</para>
/// </remarks>
public sealed partial class PstRecovery : IDisposable
{
    private const string DefectFamily = "pst-recovery";
    private const uint FakeNidBase = 0x4000000;

    private readonly NdbFile _file;
    private readonly IRandomAccessSource _source;
    private readonly bool _leaveOpen;
    private readonly PstRecoveryOptions _options;
    private readonly List<PstRecoveryDefect> _defects = [];

    private PstStore? _store;
    private bool _headerUsable;
    private bool _nodesIntact;
    private bool _blocksIntact;
    private bool _hierarchyClean = true;
    private bool _scanned;
    private PstRecoveryStats _stats;

    // Node classification. Every node the store can open is in _entries; the sets say how it got there.
    private readonly Dictionary<uint, NodeEntry> _entries = [];
    private readonly HashSet<uint> _reached = [];
    private readonly HashSet<uint> _rebuilt = [];
    private readonly HashSet<uint> _stale = [];
    private readonly HashSet<uint> _superseded = [];
    private readonly Dictionary<uint, bool> _nodeless = [];   // alias NID -> true when the live block tree still lists the block
    private readonly Dictionary<ulong, ByteRange> _damagedBlocks = [];
    private HashSet<ulong>? _liveBlocks;
    private uint _nextFake = FakeNidBase;

    // Folder facts from the hierarchy walk.
    private readonly Dictionary<uint, FolderFact> _folders = [];
    private readonly HashSet<uint> _listed = [];

    private sealed record FolderFact(string Path, string? ContainerClass, bool TableClean);

    private PstRecovery(NdbFile file, IRandomAccessSource source, bool leaveOpen, PstRecoveryOptions options, bool headerUsable)
    {
        _file = file;
        _source = source;
        _leaveOpen = leaveOpen;
        _options = options;
        _headerUsable = headerUsable;
    }

    /// <summary>The node database underneath, with the rebuilt index in place when <see cref="IndexRebuilt"/> is true.</summary>
    public NdbFile Database => _file;

    /// <summary>The messaging layer over the (possibly rebuilt) index.</summary>
    public PstStore Store => _store!;

    /// <summary>True when the file's own header was read; false when generation and encoding were inferred.</summary>
    public bool HeaderUsable => _headerUsable;

    /// <summary>True when the block and node indexes come from a scan rather than from the file's B-trees.</summary>
    public bool IndexRebuilt => _scanned && (!_nodesIntact || !_blocksIntact);

    /// <summary>What recovery found wrong or did while it opened the store.</summary>
    public IReadOnlyList<PstRecoveryDefect> Defects => _defects;

    /// <summary>Counts from opening.</summary>
    public PstRecoveryStats Stats => _stats;

    /// <summary>Opens a file by path for recovery, read-only and shared.</summary>
    /// <param name="path">The PST or OST.</param>
    /// <param name="recovery">The recovery, or null when no page or block of any generation was found.</param>
    /// <param name="options">Switches; null for defaults.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>True when something could be read.</returns>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    public static bool TryOpen(string path, out PstRecovery? recovery, PstRecoveryOptions? options = null, CancellationToken cancellationToken = default) =>
        TryOpen(path, FileSource.Open(path), out recovery, options, cancellationToken);

    private static bool TryOpen(string path, IRandomAccessSource source, out PstRecovery? recovery, PstRecoveryOptions? options, CancellationToken cancellationToken)
    {
        bool ok = TryOpen(source, false, out recovery, options, cancellationToken);
        if (recovery is not null)
        {
            recovery.SourcePath = path;
        }

        return ok;
    }

    /// <summary>Opens a source for recovery.</summary>
    /// <param name="source">The source; it must support synchronous reads.</param>
    /// <param name="leaveOpen">True to leave it undisposed with the recovery.</param>
    /// <param name="recovery">The recovery, or null when nothing could be read.</param>
    /// <param name="options">Switches; null for defaults.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>True when something could be read. Never throws for malformed content.</returns>
    /// <exception cref="ArgumentException">The source is asynchronous only.</exception>
    public static bool TryOpen(IRandomAccessSource source, bool leaveOpen, out PstRecovery? recovery, PstRecoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new PstRecoveryOptions();
        recovery = null;
        bool ok = NdbFile.TryOpen(source, true, out NdbFile? file, out _, options.Ndb);
        bool headerUsable = ok && file is not null;
        var early = new List<PstRecoveryDefect>();
        if (!headerUsable)
        {
            if (!Probe(source, options, early, out NdbFormat format, cancellationToken))
            {
                if (!leaveOpen)
                {
                    source.Dispose();
                }

                return false;
            }

            file = NdbFile.OpenSynthesized(source, true, format, NdbEncryption.None, options.Ndb);
            early.Add(new PstRecoveryDefect(PstRecoveryDefectKind.HeaderUnusable, 0, $"the header cannot be read; the file is taken to be {format}"));
        }

        var r = new PstRecovery(file!, source, leaveOpen, options, headerUsable);
        r._defects.AddRange(early);
        r.Build(cancellationToken);
        recovery = r;
        return true;
    }

    /// <summary>Releases the file. The source is disposed unless it was passed with <c>leaveOpen</c>.</summary>
    public void Dispose()
    {
        _store?.Dispose();
        if (_store is null)
        {
            _file.Dispose();
        }

        if (!_leaveOpen)
        {
            _source.Dispose();
        }
    }

    // ---- generation ----

    // With no header there is nothing to say which of the three layouts the file uses. Each is tried over the start of the file; the one whose
    // trailers check out most often wins (a wrong layout finds almost nothing, since a trailer must carry a matching signature and checksum).
    private static bool Probe(IRandomAccessSource source, PstRecoveryOptions options, List<PstRecoveryDefect> defects, out NdbFormat format, CancellationToken ct)
    {
        const long Window = 32L << 20;
        format = NdbFormat.Unicode;
        long best = 0;
        foreach (NdbFormat f in new[] { NdbFormat.Unicode, NdbFormat.Unicode4K, NdbFormat.Ansi })
        {
            using NdbFile probe = NdbFile.OpenSynthesized(source, true, f, NdbEncryption.None, options.Ndb);
            long score = 0;
            foreach (NdbScanItem it in probe.Scan(new NdbScanOptions(), ct))
            {
                if (it.Ib > Window)
                {
                    break;
                }

                if (it.Kind == NdbScanKind.Page && it.PageType is 0x80 or 0x81)
                {
                    score += 8;
                }
                else if (it.Kind == NdbScanKind.Block || it.PageType is >= 0x82 and <= 0x85)
                {
                    score++;
                }
            }

            if (score > best)
            {
                best = score;
                format = f;
            }
        }

        return best > 0;
    }

    private void Defect(PstRecoveryDefectKind kind, long offset, string detail) => _defects.Add(new PstRecoveryDefect(kind, offset, detail));

    // ---- build ----

    private void Build(CancellationToken ct)
    {
        var reached = new Dictionary<uint, NodeEntry>();
        long pages = 0, damagedPages = 0, blocks = 0, damagedBlocks = 0;

        if (_headerUsable)
        {
            long m0 = _file.Defects.Count + _file.DroppedDefects;
            foreach (NodeEntry e in _file.Nodes)
            {
                reached[e.Nid.Value] = e;
            }

            _nodesIntact = _file.Defects.Count + _file.DroppedDefects == m0 && reached.Count > 0;

            long m1 = _file.Defects.Count + _file.DroppedDefects;
            if (_options.Forensic)
            {
                _liveBlocks = [];
            }

            long liveCount = 0;
            foreach (BlockEntry b in _file.Blocks)
            {
                liveCount++;
                _liveBlocks?.Add(b.Bid.Normalized.Value);
            }

            _blocksIntact = _file.Defects.Count + _file.DroppedDefects == m1 && liveCount > 0;
        }

        bool needScan = !_nodesIntact || !_blocksIntact || _options.Forensic;
        if (!needScan)
        {
            foreach (KeyValuePair<uint, NodeEntry> kv in reached)
            {
                _entries[kv.Key] = kv.Value;
                _reached.Add(kv.Key);
            }
        }
        else
        {
            ScanAndIndex(reached, ref pages, ref damagedPages, ref blocks, ref damagedBlocks, ct);
        }

        _stats = _stats with
        {
            Pages = pages,
            Blocks = blocks,
            DamagedBlocks = damagedBlocks,
            DamagedPages = damagedPages,
            Nodes = _entries.Count,
            ReachedNodes = _reached.Count,
            StaleNodes = _stale.Count,
        };

        _store = PstStore.OpenPrepared(_file, _options.Reader);
        WalkHierarchy(ct);
    }

    private sealed class LeafPage
    {
        public long Ib;
        public ulong Bid;
        public bool Clean;
    }

    private void ScanAndIndex(Dictionary<uint, NodeEntry> reached, ref long pages, ref long damagedPages, ref long blocks, ref long damagedBlocks, CancellationToken ct)
    {
        var index = new NdbRecoveredIndex();
        var leaves = new List<LeafPage>();
        foreach (NdbScanItem it in _file.Scan(new NdbScanOptions { IncludeDamaged = true }, ct))
        {
            if (it.Kind == NdbScanKind.Block)
            {
                if (it.Check.SignatureValid && it.Check.CrcValid)
                {
                    blocks++;
                    ulong key = it.Bid & ~1UL;
                    var entry = new BlockEntry(new Bref(new Bid(it.Bid), (ulong)it.Ib), it.DataSize, 2);
                    // A BID is never reused, so two clean copies are the same block written twice (a copy made by compaction, say). The later wins.
                    index.Blocks[key] = entry;
                }
                else
                {
                    damagedBlocks++;
                    _damagedBlocks[it.Bid & ~1UL] = new ByteRange(it.Ib, it.Size);
                    Defect(PstRecoveryDefectKind.BlockDamaged, it.Ib, $"block {new Bid(it.Bid)} at 0x{it.Ib:x} fails its {(it.Check.SignatureValid ? "checksum" : "signature")} and is left out");
                }
            }
            else if (it.PageType == (byte)NdbPageType.NodeBTree)
            {
                bool clean = it.Check.SignatureValid && it.Check.CrcValid;
                pages += clean ? 1 : 0;
                damagedPages += clean ? 0 : 1;
                leaves.Add(new LeafPage { Ib = it.Ib, Bid = it.Bid, Clean = clean });
            }
            else if (it.PageType == (byte)NdbPageType.BlockBTree)
            {
                bool clean = it.Check.SignatureValid && it.Check.CrcValid;
                pages += clean ? 1 : 0;
                damagedPages += clean ? 0 : 1;
            }
            else if (it.Check.SignatureValid && it.Check.CrcValid)
            {
                pages++;
            }
        }

        if (!_headerUsable)
        {
            NdbEncryption enc = InferEncryption(index.Blocks);
            _file.Header.CryptMethod = (byte)enc;
            if (enc != NdbEncryption.None)
            {
                Defect(PstRecoveryDefectKind.HeaderUnusable, 0, $"blocks are {enc}-encoded, judged from the heap headers of sampled blocks");
            }
        }

        // Node entries from the leaf pages, newest page first so that the first copy of a NID seen is the current one.
        leaves.Sort(static (a, b) => b.Bid.CompareTo(a.Bid));
        var best = new Dictionary<uint, NodeEntry>();
        var old = new List<NodeEntry>();
        byte[] page = new byte[_file.PageSize];
        foreach (LeafPage lp in leaves)
        {
            ct.ThrowIfCancellationRequested();
            if (_file.ReadRaw(lp.Ib, page) != page.Length)
            {
                continue;
            }

            int kept = 0;
            foreach (NodeEntry e in ParseNbtLeaf(page, lp.Clean, index.Blocks))
            {
                kept++;
                uint key = e.Nid.Value;
                if (!best.TryGetValue(key, out NodeEntry have))
                {
                    best[key] = e;
                }
                else if (have.Data != e.Data || have.Sub != e.Sub)
                {
                    old.Add(e);
                }
            }

            if (!lp.Clean)
            {
                Defect(PstRecoveryDefectKind.DamagedPageEntriesKept, lp.Ib, $"leaf page at 0x{lp.Ib:x} fails its checks; {kept} entr{(kept == 1 ? "y agrees" : "ies agree")} with blocks found in the file and {(kept == 0 ? "none is" : "those are")} kept");
            }
        }

        // Which nodes the index holds. Reached nodes always; the rest are rebuilt (the tree is damaged, so they may be live) or stale (the tree
        // is intact and does not name them; a deleted node whose page survives).
        foreach (KeyValuePair<uint, NodeEntry> kv in reached)
        {
            _entries[kv.Key] = kv.Value;
            _reached.Add(kv.Key);
        }

        bool damagedTree = !_nodesIntact;
        foreach (KeyValuePair<uint, NodeEntry> kv in best)
        {
            if (_entries.ContainsKey(kv.Key))
            {
                continue;
            }

            if (damagedTree)
            {
                _entries[kv.Key] = kv.Value;
                _rebuilt.Add(kv.Key);
            }
            else if (_options.Forensic)
            {
                _entries[kv.Key] = kv.Value;
                _stale.Add(kv.Key);
            }
        }

        if (damagedTree)
        {
            Defect(PstRecoveryDefectKind.NodeIndexRebuilt, 0, $"the node tree cannot be walked cleanly; {_rebuilt.Count} node(s) were rebuilt from {leaves.Count} leaf page(s), {_reached.Count} are reached from the root");
        }

        _stats = _stats with { SupersededVersions = 0 };
        long superseded = 0;
        var seenOld = new HashSet<(uint, ulong)>();
        foreach (NodeEntry e in old)
        {
            if (!seenOld.Add((e.Nid.Value, e.Data.Value)))
            {
                continue;
            }

            superseded++;
            if (_options.Forensic && _options.IncludeSuperseded && e.Nid.Type is NidType.NormalMessage or NidType.AssocMessage)
            {
                uint alias = NewFake(e.Nid.Type);
                _entries[alias] = e with { Nid = new Nid(alias) };
                _superseded.Add(alias);
            }
        }

        // Entries of reached nodes that differ from the stale pages' copies are superseded versions too.
        foreach (KeyValuePair<uint, NodeEntry> kv in best)
        {
            if (_reached.Contains(kv.Key) && (reached[kv.Key].Data != kv.Value.Data))
            {
                superseded++;
                if (_options.Forensic && _options.IncludeSuperseded && kv.Key is var k && new Nid(k).Type is NidType.NormalMessage or NidType.AssocMessage)
                {
                    uint alias = NewFake(new Nid(k).Type);
                    _entries[alias] = kv.Value with { Nid = new Nid(alias) };
                    _superseded.Add(alias);
                }
            }
        }

        _stats = _stats with { SupersededVersions = superseded };

        foreach (KeyValuePair<uint, NodeEntry> kv in _entries)
        {
            index.Nodes[kv.Key] = kv.Value;
        }

        _file.Recovered = index;
        _scanned = true;

        // With the node tree gone the messages' blocks may be all that is left, so they are looked for without forensic mode too.
        if (_options.Forensic || damagedTree)
        {
            FindNodelessMessages(index, reached.Values, best.Values, old, ct);
        }
    }

    private uint NewFake(NidType type) => (_nextFake++ << 5) | (uint)type;

    // MS-PST 2.2.2.7.7.4 NBTENTRY. Entries of a damaged page are kept only when what they point to is in the file as a whole block.
    private IEnumerable<NodeEntry> ParseNbtLeaf(byte[] page, bool clean, Dictionary<ulong, BlockEntry> blocks)
    {
        NdbLayout l = _file.Layout;
        int f = l.BTreeFooterOffset;
        int cEnt, cbEnt, cLevel;
        if (l.Format == NdbFormat.Unicode4K)
        {
            cEnt = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(f));
            cbEnt = page[f + 4];
            cLevel = page[f + 5];
        }
        else
        {
            cEnt = page[f];
            cbEnt = page[f + 2];
            cLevel = page[f + 3];
        }

        if (cLevel != 0 || cbEnt < l.NbtLeafMin || (long)cEnt * cbEnt > f)
        {
            yield break;
        }

        for (int i = 0; i < cEnt; i++)
        {
            ReadOnlySpan<byte> e = page.AsSpan(i * cbEnt, cbEnt);
            NodeEntry n;
            if (l.IdSize == 8)
            {
                n = new NodeEntry(new Nid((uint)BinaryPrimitives.ReadUInt64LittleEndian(e)), new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[8..])),
                    new Bid(BinaryPrimitives.ReadUInt64LittleEndian(e[16..])), new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e[24..])));
            }
            else
            {
                n = new NodeEntry(new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e)), new Bid(BinaryPrimitives.ReadUInt32LittleEndian(e[4..])),
                    new Bid(BinaryPrimitives.ReadUInt32LittleEndian(e[8..])), new Nid(BinaryPrimitives.ReadUInt32LittleEndian(e[12..])));
            }

            if (n.Nid.Value == 0)
            {
                continue;
            }

            if (!clean)
            {
                bool dataOk = !n.Data.IsNull && blocks.ContainsKey(n.Data.Normalized.Value);
                bool subOk = n.Sub.IsNull || blocks.ContainsKey(n.Sub.Normalized.Value);
                if (!dataOk || !subOk)
                {
                    continue;
                }
            }

            yield return n;
        }
    }

    // ---- encoding ----

    // bCryptMethod is in the header. Without it, the three encodings are tried on sampled data blocks: a property context, table context or B-tree
    // on heap starts with ibHnpm, bSig 0xEC and a client signature (MS-PST 2.3.1.2), which no other encoding makes appear.
    private NdbEncryption InferEncryption(Dictionary<ulong, BlockEntry> blocks)
    {
        var score = new int[3];
        byte[] raw = new byte[_file.MaxBlockDataSize];
        byte[] work = new byte[_file.MaxBlockDataSize];
        int sampled = 0;
        foreach (BlockEntry e in blocks.Values)
        {
            if (e.Bid.IsInternal || e.Size < 12 || e.Size > raw.Length)
            {
                continue;
            }

            if (_file.ReadRaw((long)e.Block.Ib, raw.AsSpan(0, e.Size)) != e.Size)
            {
                continue;
            }

            for (int m = 0; m < 3; m++)
            {
                raw.AsSpan(0, e.Size).CopyTo(work);
                Span<byte> w = work.AsSpan(0, e.Size);
                if (m == 1)
                {
                    NdbChecksums.Permute(w, encode: false);
                }
                else if (m == 2)
                {
                    NdbChecksums.Cyclic(w, (uint)e.Bid.Value);
                }

                if (w[2] == 0xEC && w[3] is 0x6C or 0x7C or 0x8C or 0x9C or 0xA5 or 0xAC or 0xB5 or 0xBC or 0xCC)
                {
                    score[m]++;
                }
            }

            if (++sampled >= 600)
            {
                break;
            }
        }

        int best = 0;
        for (int m = 1; m < 3; m++)
        {
            if (score[m] > score[best])
            {
                best = m;
            }
        }

        return (NdbEncryption)best;
    }
}
