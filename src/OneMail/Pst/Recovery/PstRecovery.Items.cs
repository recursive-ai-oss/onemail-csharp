using System.Security.Cryptography;
using System.Buffers.Binary;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Recovery;

namespace RecursiveAI.OneMail.Pst.Recovery;

public sealed partial class PstRecovery
{
    private const uint NidRootFolder = 0x122;

    private readonly Dictionary<uint, FolderFact> _unreachedFolders = [];

    private long Marker() => _file.Defects.Count + _file.DroppedDefects + (_store?.Defects.Count ?? 0) + (_store?.DroppedDefects ?? 0);

    // ---- hierarchy ----

    // Which messages the live folders list, and whether each folder's table read cleanly. A message the node tree holds that no table lists is
    // soft-deleted only when the tables around it are trustworthy; a damaged table says nothing about its rows.
    private void WalkHierarchy(CancellationToken ct)
    {
        if (_store!.Root is null)
        {
            _hierarchyClean = false;
            return;
        }

        long start = Marker();
        long tables = 0;
        foreach (PstFolder f in _store.EnumerateFolders(ct))
        {
            if (f.IsSearchFolder)
            {
                _folders[f.Nid.Value] = new FolderFact(f.Path, f.ContainerClass, true);
                continue;
            }

            long t0 = Marker();
            foreach (Nid id in f.EnumerateMessageIds(PstMessageSet.All, ct))
            {
                _listed.Add(id.Value);
            }

            long delta = Marker() - t0;
            tables += delta;
            _folders[f.Nid.Value] = new FolderFact(f.Path, f.ContainerClass, delta == 0);
        }

        _hierarchyClean = Marker() - start - tables == 0;
    }

    // The folder path of a node the hierarchy did not reach, from the parent links in the node tree.
    private FolderFact FolderOf(uint nid, int depth = 0)
    {
        if (nid == NidRootFolder)
        {
            return new FolderFact(string.Empty, null, true);
        }

        if (_folders.TryGetValue(nid, out FolderFact? known))
        {
            return known;
        }

        if (_unreachedFolders.TryGetValue(nid, out FolderFact? cached))
        {
            return cached;
        }

        FolderFact result;
        if (depth > 32 || !_entries.TryGetValue(nid, out NodeEntry e) || new Nid(nid).Type != NidType.NormalFolder)
        {
            result = new FolderFact(string.Empty, null, false);
        }
        else
        {
            string? name = null;
            string? cls = null;
            if (_store!.TryGetFolder(new Nid(nid), out PstFolder? pf) && pf is not null)
            {
                name = pf.Name;
                cls = pf.ContainerClass;
            }

            string parent = FolderOf(e.Parent.Value, depth + 1).Path;
            string leaf = string.IsNullOrEmpty(name) ? "Folder " + nid.ToString("x", System.Globalization.CultureInfo.InvariantCulture) : name.Replace('/', '_');
            result = new FolderFact(parent.Length == 0 ? leaf : parent + "/" + leaf, cls, false);
        }

        _unreachedFolders[nid] = result;
        return result;
    }

    // ---- blocks no node names ----

    // Messages whose property context is in a block that neither the live nor any stale node refers to. The block of a node is accounted for by walking
    // the node's data and subnode trees; what is left over and starts like a property context is a candidate.
    private void FindNodelessMessages(NdbRecoveredIndex index, IEnumerable<NodeEntry> reached, IEnumerable<NodeEntry> best, IEnumerable<NodeEntry> old, CancellationToken ct)
    {
        var referenced = new HashSet<ulong>();
        void Account(NodeEntry e)
        {
            var w = new PstNodeWalker(_file, _options.MaxBlocksPerItem, readLeaves: false);
            w.Walk(e);
            foreach (ulong b in w.Visited)
            {
                referenced.Add(b);
            }
        }

        foreach (NodeEntry e in reached)
        {
            ct.ThrowIfCancellationRequested();
            Account(e);
        }

        foreach (NodeEntry e in best)
        {
            Account(e);
        }

        foreach (NodeEntry e in old)
        {
            Account(e);
        }

        byte[] buf = new byte[_file.MaxBlockDataSize];
        HashSet<UInt128>? live = null;
        var candidates = new List<BlockEntry>();
        foreach (BlockEntry b in index.Blocks.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (b.Bid.IsInternal || referenced.Contains(b.Bid.Normalized.Value))
            {
                continue;
            }

            if (!_file.TryReadBlock(b, buf, out int len, out NdbCheck check) || !(check.SignatureValid && check.CrcValid))
            {
                continue;
            }

            // MS-PST 2.3.1.1 HNHDR: ibHnpm (2), bSig 0xEC, bClientSig; 0xBC is a property context (2.3.1.2).
            if (len < 12 || buf[2] != 0xEC || buf[3] != 0xBC)
            {
                continue;
            }

            live ??= LiveMessageFingerprints(reached, buf);
            if (live.Contains(Fingerprint(buf.AsSpan(0, len))))
            {
                continue;
            }

            candidates.Add(b);
        }

        foreach (BlockEntry b in candidates)
        {
            uint alias = NewFake(NidType.NormalMessage);
            var entry = new NodeEntry(new Nid(alias), b.Bid, default, default);
            index.Nodes[alias] = entry;
            _entries[alias] = entry;
            _nodeless[alias] = !_nodesIntact || (_blocksIntact && (_liveBlocks?.Contains(b.Bid.Normalized.Value) ?? false));
        }
    }

    private static UInt128 Fingerprint(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(data));

    private HashSet<UInt128> LiveMessageFingerprints(IEnumerable<NodeEntry> reached, byte[] buf)
    {
        var set = new HashSet<UInt128>();
        foreach (NodeEntry e in reached)
        {
            if (e.Nid.Type is not (NidType.NormalMessage or NidType.AssocMessage) || e.Data.IsNull || e.Data.IsInternal)
            {
                continue;
            }

            if (_file.Blocks.TryGet(e.Data, out BlockEntry be) && _file.TryReadBlock(be, buf, out int len, out _))
            {
                set.Add(Fingerprint(buf.AsSpan(0, len)));
            }
        }

        return set;
    }

    // ---- items ----

    /// <summary>
    /// Enumerates the recoverable messages in node ID order, one at a time. Without <see cref="PstRecoveryOptions.Forensic"/>: messages the live
    /// structure reaches and messages rebuilt from a damaged index. With it: also soft-deleted, stale, superseded and free-space messages.
    /// </summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The items; each message is valid until the recovery is disposed. Never throws for malformed content.</returns>
    public IEnumerable<PstRecoveredItem> EnumerateItems(CancellationToken cancellationToken = default)
    {
        var ids = new List<uint>(_entries.Count);
        foreach (uint nid in _entries.Keys)
        {
            if (new Nid(nid).Type is NidType.NormalMessage or NidType.AssocMessage)
            {
                ids.Add(nid);
            }
        }

        ids.Sort();
        int emitted = 0;
        foreach (uint nid in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (emitted >= _options.MaxItems)
            {
                yield break;
            }

            PstRecoveredItem? item = Classify(nid);
            if (item is not null)
            {
                emitted++;
                yield return item;
            }
        }
    }

    private PstRecoveredItem? Classify(uint nid)
    {
        NodeEntry entry = _entries[nid];
        bool forensic = _options.Forensic;
        RecoveryStatus status;
        bool inFolder;
        string reason;
        var kinds = new List<PstRecoveryDefectKind>();
        uint folderNid = entry.Parent.Value;
        FolderFact folder;

        if (_reached.Contains(nid))
        {
            folder = FolderOf(folderNid);
            bool listed = _listed.Contains(nid);
            _folders.TryGetValue(folderNid, out FolderFact? hf);
            if (listed)
            {
                status = RecoveryStatus.Live;
                reason = "reached by the node tree and listed by its folder";
                inFolder = true;
            }
            else if (hf is not null && hf.TableClean && _hierarchyClean)
            {
                if (!forensic)
                {
                    return null;
                }

                status = RecoveryStatus.SoftDeleted;
                reason = "the node tree holds the message but its folder's contents table does not list it";
                kinds.Add(PstRecoveryDefectKind.NotListedByFolder);
                inFolder = false;
            }
            else if (hf is null && _hierarchyClean && _nodesIntact)
            {
                if (!forensic)
                {
                    return null;
                }

                status = RecoveryStatus.SoftDeleted;
                reason = "the node tree holds the message but its folder is no longer in the folder hierarchy";
                kinds.Add(PstRecoveryDefectKind.FolderNotInHierarchy);
                inFolder = false;
            }
            else
            {
                status = RecoveryStatus.Live;
                reason = "reached by the node tree; whether its folder still lists it could not be read";
                kinds.Add(PstRecoveryDefectKind.ListingUnknown);
                inFolder = true;
            }
        }
        else if (_rebuilt.Contains(nid))
        {
            folder = FolderOf(folderNid);
            status = RecoveryStatus.Orphaned;
            reason = "node index entry rebuilt from a scan of the file; the node tree does not reach it";
            kinds.Add(PstRecoveryDefectKind.IndexEntryRebuilt);
            inFolder = true;
        }
        else if (_stale.Contains(nid))
        {
            folder = FolderOf(folderNid);
            status = RecoveryStatus.Orphaned;
            reason = "named only by an older page of the node tree; the live tree no longer holds the node";
            inFolder = false;
        }
        else if (_superseded.Contains(nid))
        {
            folder = FolderOf(folderNid);
            status = RecoveryStatus.Orphaned;
            reason = "an earlier version of a node the live tree holds under another data block";
            kinds.Add(PstRecoveryDefectKind.SupersededVersion);
            inFolder = false;
        }
        else if (_nodeless.TryGetValue(nid, out bool listedByBlockTree))
        {
            folder = new FolderFact(string.Empty, null, false);
            status = listedByBlockTree ? RecoveryStatus.Orphaned : RecoveryStatus.FreeSpace;
            reason = listedByBlockTree
                ? "a message block the block tree still lists that no node refers to"
                : "a message block no node and no block tree entry refers to";
            kinds.Add(PstRecoveryDefectKind.SubnodesNotFound);
            inFolder = false;
        }
        else
        {
            return null;
        }

        if (!_store!.TryOpenMessage(new Nid(nid), out PstMessage? message) || message is null || message.MessageClass is null)
        {
            // A block that only looks like a property context is often a folder's or the store's, not a message: not worth a defect.
            if (!_nodeless.ContainsKey(nid))
            {
                _defects.Add(new PstRecoveryDefect(PstRecoveryDefectKind.NodeUnreadable, entry.Data.IsNull ? 0 : BlockOffset(entry.Data), $"node {new Nid(nid)} (data block {entry.Data}) does not read as a message"));
            }

            return null;
        }

        // Recipients and attachments load on demand; touching them here makes the message's own defects complete.
        _ = message.Recipients;
        _ = message.Attachments;
        bool damaged = HasDamageDefects(message);

        var walker = new PstNodeWalker(_file, _options.MaxBlocksPerItem);
        walker.Walk(entry);
        var lost = new List<ByteRange>(walker.Lost);
        foreach (Bid b in walker.Missing)
        {
            if (_damagedBlocks.TryGetValue(b.Normalized.Value, out ByteRange r))
            {
                lost.Add(r);
            }
        }

        if (walker.Missing.Count + walker.Damaged.Count > 0)
        {
            kinds.Add(walker.Damaged.Count > 0 ? PstRecoveryDefectKind.BlockDamaged : PstRecoveryDefectKind.BlockMissing);
            if (status != RecoveryStatus.FreeSpace)
            {
                status = RecoveryStatus.Partial;
                reason += "; " + (walker.Missing.Count + walker.Damaged.Count) + " block(s) it needs are missing or damaged";
            }
        }
        else if (damaged)
        {
            kinds.Add(PstRecoveryDefectKind.MessageDamaged);
            if (status is RecoveryStatus.Live)
            {
                status = RecoveryStatus.Partial;
                reason += "; the messaging layer repaired or skipped part of it";
            }
        }

        IReadOnlyList<ByteRange> ranges = RecoveryProvenance.Merge(walker.Ranges);
        long at = ranges.Count > 0 ? ranges[0].Offset : 0;
        var mail = new List<MailDefect>(kinds.Count);
        foreach (PstRecoveryDefectKind k in kinds)
        {
            mail.Add(new MailDefect(DefectFamily, k.ToString(), at, false));
        }

        bool real = _reached.Contains(nid) || _rebuilt.Contains(nid) || _stale.Contains(nid);
        var provenance = new RecoveryProvenance(status)
        {
            Format = MailFormat.Pst,
            Ranges = ranges,
            Lost = RecoveryProvenance.Merge(lost),
            Defects = mail,
            Nid = real ? nid : null,
            Bid = entry.Data.Value,
            Reason = reason,
        };
        return new PstRecoveredItem(message, provenance, folder.Path, inFolder, new Nid(nid).Type == NidType.AssocMessage);
    }

    private long BlockOffset(Bid bid) => _file.Blocks.TryGet(bid, out BlockEntry e) ? (long)e.Block.Ib : 0;

    private static bool HasDamageDefects(PstMessage m)
    {
        if (Damaging(m.Defects))
        {
            return true;
        }

        foreach (MapiRecipient r in m.Recipients)
        {
            if (Damaging(r.Defects))
            {
                return true;
            }
        }

        foreach (MapiAttachment a in m.Attachments)
        {
            if (Damaging(a.Defects))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Damaging(IReadOnlyList<MapiDefect> defects)
    {
        foreach (MapiDefect d in defects)
        {
            if (d.Kind is MapiDefectKind.NodeMissing or MapiDefectKind.ContextUnreadable or MapiDefectKind.ValueUnreadable or MapiDefectKind.LowerLayerDefect)
            {
                return true;
            }
        }

        return false;
    }
}
