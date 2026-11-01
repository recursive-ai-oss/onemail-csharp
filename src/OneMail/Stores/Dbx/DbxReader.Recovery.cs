using System.Buffers.Binary;
using System.Security.Cryptography;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Dbx;

/// <summary>What makes a recovered DBX message less than a live one. Surfaced as <see cref="MailDefect"/>s (family <c>dbx-recovery</c>) on the message's <see cref="RecoveryProvenance"/>.</summary>
public enum DbxRecoveryDefect
{
    /// <summary>The first four bytes of every segment's text were overwritten by the free list's link, so they are missing from the content. Their ranges are in <see cref="RecoveryProvenance.Lost"/>.</summary>
    LeadingBytesOverwritten = 0,

    /// <summary>A freed segment no longer says how much of it was text, so the last segment's length is a guess: trailing zero bytes are dropped, anything else stays.</summary>
    LastSegmentLengthUnknown,

    /// <summary>The chain's next pointer leads into a segment that is in use now, or free but not part of this scan's set. The message ends there.</summary>
    ChainContinuesIntoOtherObject,

    /// <summary>The chain's next pointer leads outside the file or to something that is not a message segment. The message ends there.</summary>
    ChainEndsAtBadPointer,

    /// <summary>The chain meets a segment of its own or another recovered chain. The message ends there.</summary>
    ChainLoopOrShared,

    /// <summary>The first segment of the chain has no predecessor among the recovered segments; if it was not the message's first segment, the start of the message is missing.</summary>
    StartNotConfirmed,

    /// <summary>The free list that the file header names is broken (a pointer outside the file, a wrong marker or a loop). The rest of the list is not followed.</summary>
    FreeListBroken,

    /// <summary>The recovered bytes equal a live message. Only reported when <see cref="DbxRecoveryOptions.IncludeDuplicatesOfLive"/> is set; otherwise the copy is left out.</summary>
    DuplicateOfLive,
}

/// <summary>Switches of <see cref="DbxReader.RecoverDeleted"/>.</summary>
public sealed class DbxRecoveryOptions
{
    /// <summary>
    /// Report chains whose bytes are identical to a live message of the same file. Default false: such a copy is not a recovery, and leaving it out
    /// keeps a live message from being listed twice.
    /// </summary>
    public bool IncludeDuplicatesOfLive { get; init; }

    /// <summary>The most recovered messages to produce. Default 10 million.</summary>
    public int MaxMessages { get; init; } = 10_000_000;
}

/// <summary>A message found outside the live index of a DBX file.</summary>
/// <remarks>Holds its bytes; one is built at a time while <see cref="DbxReader.RecoverDeleted"/> enumerates.</remarks>
public sealed class DbxRecoveredMessage
{
    internal DbxRecoveredMessage(RecoveryProvenance provenance, byte[] content, long[] segments, ByteRange[] segmentRanges)
    {
        Provenance = provenance;
        Content = content;
        Segments = segments;
        SegmentRanges = segmentRanges;
    }

    /// <summary>The tag, source ranges and defects. Never <see cref="RecoveryStatus.Live"/>.</summary>
    public RecoveryProvenance Provenance { get; }

    /// <summary>The recovered message bytes as they are in the file (Outlook Express keeps CR LF line ends). See <see cref="DbxRecoveryDefect"/> for what can be missing.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>The file offsets of the chain's segments, in message order.</summary>
    public IReadOnlyList<long> Segments { get; }

    /// <summary>The file range each segment contributed, in message order: reading these from the file and joining them gives <see cref="Content"/>.</summary>
    public IReadOnlyList<ByteRange> SegmentRanges { get; }

    /// <summary>Parses the content as a MIME message and attaches the provenance.</summary>
    /// <returns>An item whose <see cref="IMailItem.Recovery"/> is <see cref="Provenance"/>.</returns>
    public IMailItem ToMailItem() =>
        new MimeMailItem(MimeEntity.Parse(Content), new MailProvenance(MailFormat.Dbx, null, null, Provenance.Ranges.Count > 0 ? Provenance.Ranges[0].Offset : -1, Content.Length, -1))
        {
            RecoveryInfo = Provenance,
        };
}

public sealed partial class DbxReader
{
    // OE_Dbx_FileHeader (12): the root of the list of deleted message segments, at 0x48.
    private const int DeletedMessageListRoot = 0x48;

    // OE_Dbx_Message: a live segment is 0x10 bytes of header and 0x200 of text.
    private const int SegmentBody = 0x200;
    private const int SegmentSize = SegmentHeaderSize + SegmentBody;

    // OE_Dbx_Deleted: a deleted object's header is five int4: marker, length of field (6), that + 0x14, the old word 4 (not cleared), the next
    // deleted object. For a message segment the length is 0x1fc, so the old next-segment pointer survives at +12 and the first four text bytes
    // (+16) are replaced by the free list link. Observed in the Hacking Case Inbox.dbx: 20 such segments whose +12 words chain them.
    private const int DeletedHeaderSize = 20;
    private const int DeletedBody = SegmentSize - DeletedHeaderSize;

    /// <summary>
    /// Finds the messages that are no longer reachable from the file's index but whose bytes are still in it, and returns them tagged.
    /// Reads the file only; opt-in because a live-only reader must never list them.
    /// </summary>
    /// <remarks>
    /// <para>Two sources, both checked against the live set so that nothing the ordinary reader lists is returned:</para>
    /// <list type="bullet">
    /// <item><description><b>The deleted-message list</b> (file header 0x48): freed segments whose old next pointers are followed to rebuild chains. Tag <see cref="RecoveryStatus.FreeSpace"/>.</description></item>
    /// <item><description><b>A scan for segments</b> (marker equal to own offset, 0x200 body, text length 1 to 0x200) that no live chain, record or free list reaches: chains whose index entry is gone. Tag <see cref="RecoveryStatus.Orphaned"/>.</description></item>
    /// </list>
    /// <para>A chain that stops at an object that is not part of it is tagged <see cref="RecoveryStatus.Partial"/>. DBX has no soft-delete state, so
    /// <see cref="RecoveryStatus.SoftDeleted"/> is not produced. Metadata records of deleted messages are not read: the freed record loses its header
    /// and one index entry, and the message's own headers carry the same facts.</para>
    /// <para>Never throws on malformed content. Memory: the offsets of live segments, plus the recovered message being built.</para>
    /// </remarks>
    /// <param name="options">Switches, or null for the defaults.</param>
    /// <returns>The messages in file order of their first segment. Empty for files that are not message databases.</returns>
    public IEnumerable<DbxRecoveredMessage> RecoverDeleted(DbxRecoveryOptions? options = null)
    {
        options ??= new DbxRecoveryOptions();
        if (Kind != DbxFileKind.MessageDatabase)
        {
            yield break;
        }

        HashSet<long> live = CollectLiveObjects();

        // The deleted-message list, then the scan for intact orphans.
        var free = new Dictionary<long, uint>();   // segment offset -> old next pointer
        WalkDeletedList(free, live);
        var orphans = new Dictionary<long, Orphan>();
        ScanOrphans(orphans, live, free);

        var results = new List<(long First, Func<DbxRecoveredMessage?> Build)>();
        var claimed = new HashSet<long>();
        foreach ((long first, bool deleted, long[] segs, DbxRecoveryDefect[] defects, RecoveryStatus status) in BuildChains(free, orphans, live, claimed))
        {
            results.Add((first, () => Assemble(segs, deleted, status, defects)));
        }

        results.Sort(static (a, b) => a.First.CompareTo(b.First));

        HashSet<string>? liveHashes = null;
        int produced = 0;
        foreach ((long _, Func<DbxRecoveredMessage?> build) in results)
        {
            if (produced >= options.MaxMessages)
            {
                yield break;
            }

            DbxRecoveredMessage? m = build();
            if (m is null)
            {
                continue;
            }

            if (m.Provenance.Status != RecoveryStatus.FreeSpace)
            {
                // Intact copies: a chain that spells out a live message is not a recovery.
                liveHashes ??= HashLiveMessages();
                if (liveHashes.Contains(Convert.ToHexString(SHA256.HashData(m.Content.Span))))
                {
                    if (!options.IncludeDuplicatesOfLive)
                    {
                        continue;
                    }

                    m = WithDefect(m, DbxRecoveryDefect.DuplicateOfLive);
                }
            }

            produced++;
            yield return m;
        }

    }

    private readonly record struct Orphan(uint Text, uint Next);

    // The offsets a live reader uses: every record and every segment of every chain it can follow. A broken live chain is followed as far as
    // markers hold, so that its tail is not mistaken for an orphan.
    private HashSet<long> CollectLiveObjects()
    {
        var live = new HashSet<long>();
        var plain = new DbxReader(_source, new DbxReaderOptions { MaxInfoBytes = _options.MaxInfoBytes, MaxTreeDepth = _options.MaxTreeDepth });
        Span<byte> h = stackalloc byte[SegmentHeaderSize];
        foreach (DbxMessageEntry e in plain.EnumerateMessages())
        {
            live.Add(e.Offset);
            long p = e.ChainOffset;
            while (p != 0 && p >= HeaderLength && p + SegmentHeaderSize <= _source.Length && !live.Contains(p)
                   && _source.ReadFully(p, h) == SegmentHeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(h) == p)
            {
                live.Add(p);
                p = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
            }
        }

        return live;
    }

    private HashSet<string> HashLiveMessages()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var plain = new DbxReader(_source, new DbxReaderOptions { MaxInfoBytes = _options.MaxInfoBytes, MaxTreeDepth = _options.MaxTreeDepth, MaxMessageBytes = _options.MaxMessageBytes });
        foreach (DbxMessageEntry e in plain.EnumerateMessages())
        {
            if (!e.HasMessage)
            {
                continue;
            }

            byte[] bytes = plain.ReadMessage(e, out _);
            set.Add(Convert.ToHexString(SHA256.HashData(bytes)));
        }

        return set;
    }

    // OE_Dbx_Deleted: a single linked list; each object is checked before it is believed, and the walk is bounded by what the file can hold.
    private void WalkDeletedList(Dictionary<long, uint> free, HashSet<long> live)
    {
        long length = _source.Length;
        long p = U32(_header!, DeletedMessageListRoot);
        long budget = length / SegmentSize + 2;
        Span<byte> h = stackalloc byte[DeletedHeaderSize];
        while (p != 0 && budget-- > 0)
        {
            if (p < HeaderLength || p + SegmentSize > length || _source.ReadFully(p, h) != DeletedHeaderSize)
            {
                Report(StoreDefects.DbxBadPointer, p);
                _freeListBroken = true;
                return;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != p || BinaryPrimitives.ReadUInt32LittleEndian(h[4..]) != DeletedBody
                || BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) != SegmentSize)
            {
                Report(StoreDefects.DbxBadObjectMarker, p);
                _freeListBroken = true;
                return;
            }

            if (live.Contains(p) || free.ContainsKey(p))
            {
                // A freed segment that a live chain also uses, or a loop in the list.
                Report(StoreDefects.DbxTreeLoop, p);
                _freeListBroken = true;
                return;
            }

            free[p] = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
            p = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        }

        if (p != 0)
        {
            Report(StoreDefects.DbxTreeLoop, p);
            _freeListBroken = true;
        }
    }

    private bool _freeListBroken;

    // A scan at 4-byte steps (every pointer in the format is 4-aligned; checked on the Hacking Case files) for segments shaped like live ones.
    // Bounds: each window read stays inside the file; a candidate needs 16 bytes at p.
    private void ScanOrphans(Dictionary<long, Orphan> orphans, HashSet<long> live, Dictionary<long, uint> free)
    {
        long length = _source.Length;
        const int Window = 1 << 20;
        byte[] buf = new byte[Window + SegmentSize];
        long start = (HeaderLength + 3L) & ~3L;
        for (long pos = start; pos + SegmentHeaderSize <= length; pos += Window)
        {
            int want = (int)Math.Min(buf.Length, length - pos);
            int got = _source.ReadFully(pos, buf.AsSpan(0, want));
            int limit = Math.Min(got - SegmentHeaderSize, Window - 1);
            for (int i = 0; i <= limit; i += 4)
            {
                long p = pos + i;
                ReadOnlySpan<byte> s = buf.AsSpan(i, SegmentHeaderSize);
                if (BinaryPrimitives.ReadUInt32LittleEndian(s) != p || BinaryPrimitives.ReadUInt32LittleEndian(s[4..]) != SegmentBody)
                {
                    continue;
                }

                uint text = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
                uint next = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
                if (text is 0 or > SegmentBody || p + SegmentSize > length || live.Contains(p) || free.ContainsKey(p))
                {
                    continue;
                }

                orphans[p] = new Orphan(text, next);
            }
        }
    }

    // Chains are the connected runs of segments by next pointer. Heads are processed in offset order; a segment belongs to the first chain
    // that reaches it, a second arrival ends that chain with a defect.
    private List<(long First, bool Deleted, long[] Segments, DbxRecoveryDefect[] Defects, RecoveryStatus Status)> BuildChains(
        Dictionary<long, uint> free, Dictionary<long, Orphan> orphans, HashSet<long> live, HashSet<long> claimed)
    {
        var result = new List<(long First, bool Deleted, long[] Segments, DbxRecoveryDefect[] Defects, RecoveryStatus Status)>();
        long length = _source.Length;
        AddChains(deleted: true);
        AddChains(deleted: false);
        return result;

        void AddChains(bool deleted)
        {
            IEnumerable<long> keys = deleted ? free.Keys : orphans.Keys;
            var hasPredecessor = new HashSet<long>();
            foreach (long k in keys)
            {
                long n = deleted ? free[k] : orphans[k].Next;
                if (n != 0 && n != k)
                {
                    hasPredecessor.Add(n);
                }
            }

            var heads = new List<long>();
            foreach (long k in keys)
            {
                if (!hasPredecessor.Contains(k))
                {
                    heads.Add(k);
                }
            }

            heads.Sort();

            // Segments left over after the heads are in cycles; each cycle is cut at its lowest offset.
            foreach (long head in heads)
            {
                Follow(head, confirmed: false);
            }

            var rest = new List<long>();
            foreach (long k in keys)
            {
                if (!claimed.Contains(k))
                {
                    rest.Add(k);
                }
            }

            rest.Sort();
            foreach (long k in rest)
            {
                if (!claimed.Contains(k))
                {
                    Follow(k, confirmed: true);
                }
            }

            void Follow(long head, bool confirmed)
            {
                var segs = new List<long>();
                var defects = new List<DbxRecoveryDefect>();
                RecoveryStatus status = deleted ? RecoveryStatus.FreeSpace : RecoveryStatus.Orphaned;
                long p = head;
                while (true)
                {
                    segs.Add(p);
                    claimed.Add(p);
                    long n = deleted ? free[p] : orphans[p].Next;
                    if (n == 0)
                    {
                        break;
                    }

                    bool same = deleted ? free.ContainsKey(n) : orphans.ContainsKey(n);
                    if (same && !claimed.Contains(n))
                    {
                        p = n;
                        continue;
                    }

                    status = RecoveryStatus.Partial;
                    if (same)
                    {
                        defects.Add(DbxRecoveryDefect.ChainLoopOrShared);
                    }
                    else if (live.Contains(n) || free.ContainsKey(n) || orphans.ContainsKey(n))
                    {
                        defects.Add(DbxRecoveryDefect.ChainContinuesIntoOtherObject);
                    }
                    else
                    {
                        defects.Add(n < HeaderLength || n + SegmentHeaderSize > length ? DbxRecoveryDefect.ChainEndsAtBadPointer : DbxRecoveryDefect.ChainContinuesIntoOtherObject);
                    }

                    break;
                }

                if (confirmed)
                {
                    defects.Add(DbxRecoveryDefect.StartNotConfirmed);
                    status = RecoveryStatus.Partial;
                }

                result.Add((head, deleted, segs.ToArray(), defects.ToArray(), status));
            }
        }
    }

    private DbxRecoveredMessage? Assemble(long[] segs, bool deleted, RecoveryStatus status, DbxRecoveryDefect[] chainDefects)
    {
        var defects = new List<DbxRecoveryDefect>(chainDefects);
        if (_freeListBroken && deleted)
        {
            defects.Add(DbxRecoveryDefect.FreeListBroken);
        }

        var ranges = new List<ByteRange>();
        var lost = new List<ByteRange>();
        var content = new MemoryStream();
        byte[] seg = new byte[SegmentSize];
        long length = _source.Length;
        for (int i = 0; i < segs.Length; i++)
        {
            long p = segs[i];
            int got = _source.ReadFully(p, seg.AsSpan(0, (int)Math.Min(SegmentSize, length - p)));
            if (got < SegmentHeaderSize)
            {
                continue;
            }

            bool last = i == segs.Length - 1 && !Array.Exists(chainDefects, static d => d is DbxRecoveryDefect.ChainContinuesIntoOtherObject or DbxRecoveryDefect.ChainEndsAtBadPointer or DbxRecoveryDefect.ChainLoopOrShared);
            if (deleted)
            {
                // The text (0x1fc bytes) starts after the five-word deleted header; the four bytes the link replaced are gone.
                int avail = Math.Max(0, got - DeletedHeaderSize);
                ReadOnlySpan<byte> text = seg.AsSpan(DeletedHeaderSize, avail);
                lost.Add(new ByteRange(p + SegmentHeaderSize, 4));
                if (last)
                {
                    int end = text.Length;
                    while (end > 0 && text[end - 1] == 0)
                    {
                        end--;
                    }

                    text = text[..end];
                    defects.Add(DbxRecoveryDefect.LastSegmentLengthUnknown);
                }

                content.Write(text);
                ranges.Add(new ByteRange(p + DeletedHeaderSize, text.Length));
            }
            else
            {
                int textLen = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(seg.AsSpan(8)), Math.Max(0, got - SegmentHeaderSize));
                content.Write(seg.AsSpan(SegmentHeaderSize, textLen));
                ranges.Add(new ByteRange(p + SegmentHeaderSize, textLen));
            }
        }

        if (deleted)
        {
            defects.Insert(0, DbxRecoveryDefect.LeadingBytesOverwritten);
        }

        // A chain whose first segment is already cut (start unconfirmed) is partial even when it ends cleanly.
        var mail = new List<MailDefect>(defects.Count);
        foreach (DbxRecoveryDefect d in defects)
        {
            mail.Add(new MailDefect("dbx-recovery", d.ToString(), segs[0], false));
        }

        string reason = deleted
            ? "segments on the file's deleted-message list, chained by their surviving old next pointers"
            : "intact segments that no index entry reaches";
        var prov = new RecoveryProvenance(status)
        {
            Format = MailFormat.Dbx,
            Ranges = RecoveryProvenance.Merge(ranges),
            Lost = lost,
            Defects = mail,
            Reason = reason,
        };
        return new DbxRecoveredMessage(prov, content.ToArray(), segs, ranges.ToArray());
    }

    private static DbxRecoveredMessage WithDefect(DbxRecoveredMessage m, DbxRecoveryDefect d)
    {
        var defects = new List<MailDefect>(m.Provenance.Defects) { new("dbx-recovery", d.ToString(), m.Segments[0], false) };
        var p = new RecoveryProvenance(m.Provenance.Status)
        {
            Format = m.Provenance.Format,
            Ranges = m.Provenance.Ranges,
            Lost = m.Provenance.Lost,
            Defects = defects,
            Reason = m.Provenance.Reason,
        };
        return new DbxRecoveredMessage(p, m.Content.ToArray(), [.. m.Segments], [.. m.SegmentRanges]);
    }
}
