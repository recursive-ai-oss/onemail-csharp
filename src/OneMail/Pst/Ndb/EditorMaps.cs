using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// The allocation state of a Unicode PST (MS-PST 2.2.2.7.2): one bit per 64 bytes in AMap pages, one AMap page at the start of each 253,952-byte range,
/// loaded when first needed and written back at commit. Space freed during a transaction stays allocated until the commit that frees it, because the
/// committed tree still points at it.
/// </summary>
internal sealed class EditorMaps
{
    public const int PageSize = 512;
    public const int Slot = 64;
    public const int BitsPerMap = 496 * 8;                          // 3,968 slots
    public const long RangeLength = (long)BitsPerMap * Slot;        // 253,952
    public const long FirstAMap = 0x4400;
    public const long DensityListOffset = 0x4200;
    public const int DensityCapacity = (PageSize - 16 - 8 - 12) / 4;   // 119

    private readonly EditorIo _io;
    private readonly Dictionary<long, byte[]> _maps = [];          // range index -> the whole 512-byte AMap page
    private readonly HashSet<long> _dirty = [];
    private readonly List<(long Ib, int Length)> _pendingFree = [];
    private readonly Dictionary<long, int> _txnAllocated = [];      // ib -> length, space taken in this transaction
    private readonly SortedDictionary<long, int> _density = [];     // range index -> free slots (the density list's entries)
    private long _hint;

    public EditorMaps(EditorIo io, long rangeCount, bool zeroOnFree)
    {
        _io = io;
        ZeroOnFree = zeroOnFree;
        RangeCount = rangeCount;
        _hint = Math.Max(0, rangeCount - 1);
    }

    /// <summary>True to overwrite space with zeros as it is freed.</summary>
    public bool ZeroOnFree { get; }

    /// <summary>The number of AMap pages (ranges) in the file, new ones included.</summary>
    public long RangeCount { get; private set; }

    /// <summary>The sum of bytes freed minus bytes taken since the header was read: added to <c>cbAMapFree</c> at commit.</summary>
    public long FreeDelta { get; private set; }

    /// <summary>The end of the file implied by the ranges and allocations so far.</summary>
    public long RequiredEof { get; private set; }

    public bool HasChanges => _dirty.Count > 0 || _pendingFree.Count > 0;

    public int PendingFreeCount => _pendingFree.Count;

    public static long RangeStart(long index) => FirstAMap + (index * RangeLength);

    // MS-PST 2.2.2.7.2 / 2.2.2.7.3 / 2.2.2.7.5: which map pages sit at the start of range k.
    public static bool HasPMap(long k) => k % 8 == 0;

    public static bool HasFMap(long k) => k >= 128 && (k - 128) % 496 == 0;

    public static bool HasFpMap(long k) => k >= 8192 && (k - 8192) % 3968 == 0;

    public static int ReservedSlots(long k) => (1 + (HasPMap(k) ? 1 : 0) + (HasFMap(k) ? 1 : 0) + (HasFpMap(k) ? 1 : 0)) * (PageSize / Slot);

    public static int BlockTotal(int cb) => ((cb + NdbBlockBuilder.TrailerSize + Slot - 1) / Slot) * Slot;

    // ---- map pages ---------------------------------------------------------------------------------------------------------------------

    private byte[] Load(long index)
    {
        if (_maps.TryGetValue(index, out byte[]? page))
        {
            return page;
        }

        if (index < 0 || index >= RangeCount)
        {
            throw new InvalidOperationException($"allocation map {index} does not exist");
        }

        page = new byte[PageSize];
        long ib = RangeStart(index);
        if (_io.Read(ib, page) != PageSize || page[496] != 0x84 || page[497] != 0x84
            || BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(500)) != NdbChecksums.Crc32(page.AsSpan(0, 496))
            || BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(504)) != (ulong)ib)
        {
            throw new InvalidDataException($"the allocation map at 0x{ib:x} is damaged");
        }

        _maps[index] = page;
        return page;
    }

    /// <summary>Replaces every map with a fresh one for <paramref name="count"/> ranges, reserved pages marked and nothing else; used before a rebuild.</summary>
    public void ResetForRebuild(long count)
    {
        _maps.Clear();
        _dirty.Clear();
        _pendingFree.Clear();
        _txnAllocated.Clear();
        _density.Clear();
        RangeCount = count;
        for (long k = 0; k < count; k++)
        {
            _maps[k] = NewPage(k);
            _dirty.Add(k);
        }
    }

    private static byte[] NewPage(long k)
    {
        var page = new byte[PageSize];
        MarkBits(page, 0, ReservedSlots(k), true);
        return page;
    }

    private static void MarkBits(byte[] page, int firstSlot, int count, bool used)
    {
        for (int s = firstSlot; s < firstSlot + count; s++)
        {
            if (used)
            {
                page[s >> 3] |= (byte)(1 << (s & 7));
            }
            else
            {
                page[s >> 3] &= (byte)~(1 << (s & 7));
            }
        }
    }

    /// <summary>Marks an extent used (during a rebuild, from what the two trees reference). Extents outside the ranges are ignored.</summary>
    public void MarkUsed(long ib, int length)
    {
        long k = (ib - FirstAMap) / RangeLength;
        if (ib < FirstAMap || k >= RangeCount)
        {
            return;
        }

        int first = (int)((ib - RangeStart(k)) / Slot);
        int count = Math.Min((length + Slot - 1) / Slot, BitsPerMap - first);
        MarkBits(Load(k), first, count, true);
        _dirty.Add(k);
        RequiredEof = Math.Max(RequiredEof, ib + length);
    }

    public int CountFreeSlots(long index)
    {
        byte[] page = Load(index);
        int used = 0;
        for (int i = 0; i < 496; i++)
        {
            used += BitOperations.PopCount(page[i]);
        }

        return BitsPerMap - used;
    }

    // ---- density list (MS-PST 2.2.2.7.4) --------------------------------------------------------------------------------------------------

    public void SetDensity(IEnumerable<(long Index, int Free)> entries)
    {
        _density.Clear();
        foreach ((long index, int free) in entries)
        {
            if (index < RangeCount && free > 0)
            {
                _density[index] = free;
            }
        }
    }

    public IReadOnlyDictionary<long, int> Density => _density;

    private void NoteFree(long index)
    {
        int free = CountFreeSlots(index);
        if (free <= 0)
        {
            _density.Remove(index);
            return;
        }

        _density[index] = free;
        if (_density.Count > DensityCapacity)
        {
            long smallest = -1;
            int least = int.MaxValue;
            foreach (KeyValuePair<long, int> e in _density)
            {
                if (e.Value < least && e.Key != index)
                {
                    least = e.Value;
                    smallest = e.Key;
                }
            }

            _density.Remove(smallest);
        }
    }

    // ---- allocation --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes <paramref name="length"/> bytes at a multiple of <paramref name="alignSlots"/> 64-byte slots: first from the range last used, then from
    /// the ranges the density list says have room, then from the last range, and otherwise from a new range at the end of the file.
    /// </summary>
    public long Allocate(int length, int alignSlots)
    {
        int need = (length + Slot - 1) / Slot;
        if (need > BitsPerMap - ReservedSlots(0))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var tried = new HashSet<long>();
        long ib;
        if (TryRange(_hint, need, alignSlots, tried, out ib))
        {
            return Took(ib, need, length);
        }

        foreach (long k in CandidateRanges(need))
        {
            if (TryRange(k, need, alignSlots, tried, out ib))
            {
                _hint = k;
                return Took(ib, need, length);
            }
        }

        if (TryRange(RangeCount - 1, need, alignSlots, tried, out ib))
        {
            _hint = RangeCount - 1;
            return Took(ib, need, length);
        }

        long fresh = RangeCount;
        RangeCount++;
        _maps[fresh] = NewPage(fresh);
        _dirty.Add(fresh);
        FreeDelta += (BitsPerMap - ReservedSlots(fresh)) * (long)Slot;
        RequiredEof = Math.Max(RequiredEof, RangeStart(fresh) + RangeLength);
        _hint = fresh;
        if (!TryRange(fresh, need, alignSlots, tried, out ib))
        {
            throw new InvalidOperationException("a new range cannot hold the allocation");
        }

        return Took(ib, need, length);
    }

    private List<long> CandidateRanges(int need)
    {
        var list = new List<(long Index, int Free)>();
        foreach (KeyValuePair<long, int> e in _density)
        {
            if (e.Value >= need)
            {
                list.Add((e.Key, e.Value));
            }
        }

        // The emptiest first, as the density list is ordered (MS-PST 2.2.2.7.4), so backfilling fills the most useful gaps.
        list.Sort(static (a, b) => b.Free != a.Free ? b.Free.CompareTo(a.Free) : a.Index.CompareTo(b.Index));
        var result = new List<long>(list.Count);
        foreach ((long index, _) in list)
        {
            result.Add(index);
        }

        return result;
    }

    private bool TryRange(long k, int need, int align, HashSet<long> tried, out long ib)
    {
        ib = 0;
        if (k < 0 || k >= RangeCount || !tried.Add(k))
        {
            return false;
        }

        byte[] page = Load(k);
        int slot = FindRun(page, need, align);
        if (slot < 0)
        {
            return false;
        }

        MarkBits(page, slot, need, true);
        _dirty.Add(k);
        NoteFree(k);
        ib = RangeStart(k) + ((long)slot * Slot);
        return true;
    }

    private static int FindRun(byte[] page, int need, int align)
    {
        int run = 0;
        for (int s = 0; s < BitsPerMap; s++)
        {
            if ((page[s >> 3] & (1 << (s & 7))) != 0)
            {
                run = 0;
                continue;
            }

            if (run == 0 && s % align != 0)
            {
                continue;
            }

            run++;
            if (run == need)
            {
                return s - need + 1;
            }
        }

        return -1;
    }

    private long Took(long ib, int need, int length)
    {
        FreeDelta -= (long)need * Slot;
        _txnAllocated[ib] = need * Slot;
        RequiredEof = Math.Max(RequiredEof, ib + (need * Slot));
        return ib;
    }

    /// <summary>
    /// Frees an extent. One taken in this transaction is free at once (no committed state points at it); one from the committed file is only listed,
    /// and its bits are cleared at commit, after the new tree is written.
    /// </summary>
    /// <returns>True when the extent was taken in this transaction and is free now.</returns>
    public bool Free(long ib, int length)
    {
        int len = ((length + Slot - 1) / Slot) * Slot;
        if (_txnAllocated.Remove(ib))
        {
            Clear(ib, len);
            if (ZeroOnFree)
            {
                // Nothing committed points here, so the space can be wiped now; the next allocation of it writes over the zeros.
                ZeroExtent(ib, len);
            }

            return true;
        }

        _pendingFree.Add((ib, len));
        return false;
    }

    public bool IsTransactionSpace(long ib) => _txnAllocated.ContainsKey(ib);

    private void Clear(long ib, int len)
    {
        long k = (ib - FirstAMap) / RangeLength;
        int first = (int)((ib - RangeStart(k)) / Slot);
        MarkBits(Load(k), first, len / Slot, false);
        _dirty.Add(k);
        FreeDelta += len;
        NoteFree(k);
    }

    /// <summary>Clears the bits of everything freed in this transaction; returns the extents to zero once the new state is committed.</summary>
    public List<(long Ib, int Length)> ApplyFrees()
    {
        var zero = new List<(long, int)>(_pendingFree.Count);
        foreach ((long ib, int len) in _pendingFree)
        {
            Clear(ib, len);
            zero.Add((ib, len));
        }

        _pendingFree.Clear();
        return zero;
    }

    public void ZeroExtent(long ib, int len)
    {
        var zeros = new byte[Math.Min(len, 8192)];
        for (int done = 0; done < len; done += zeros.Length)
        {
            _io.Write(ib + done, zeros.AsSpan(0, Math.Min(zeros.Length, len - done)));
        }
    }

    // ---- writing back ------------------------------------------------------------------------------------------------------------------

    /// <summary>Writes every changed map page, and the PMap, FMap and FPMap pages of ranges that are new.</summary>
    public void WriteMaps(long oldRangeCount)
    {
        foreach (long k in _dirty)
        {
            byte[] page = Load(k);
            long ib = RangeStart(k);
            Span<byte> t = page.AsSpan(496);
            t[0] = 0x84;
            t[1] = 0x84;
            BinaryPrimitives.WriteUInt16LittleEndian(t[2..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(t[4..], NdbChecksums.Crc32(page.AsSpan(0, 496)));
            BinaryPrimitives.WriteUInt64LittleEndian(t[8..], (ulong)ib);
            _io.Write(ib, page);
            if (k >= oldRangeCount)
            {
                int at = PageSize;
                if (HasPMap(k))
                {
                    WriteFull(ib + at, 0x83);   // MS-PST 2.2.2.7.3: the PMap content in files Outlook wrote is all ones
                    at += PageSize;
                }

                if (HasFMap(k))
                {
                    WriteFull(ib + at, 0x82);
                    at += PageSize;
                }

                if (HasFpMap(k))
                {
                    WriteFull(ib + at, 0x85);
                }
            }
        }

        _dirty.Clear();
    }

    private void WriteFull(long ib, byte ptype)
    {
        var page = new byte[PageSize];
        page.AsSpan(0, 496).Fill(0xFF);
        page[496] = ptype;
        page[497] = ptype;
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(500), NdbChecksums.Crc32(page.AsSpan(0, 496)));
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(504), (ulong)ib);
        _io.Write(ib, page);
    }

    /// <summary>The density list page (without its trailer) for the current state: flags, entries, the most free first.</summary>
    public byte[] BuildDensityPage(byte flags, uint currentPage, ulong bid)
    {
        var entries = new List<(long Index, int Free)>();
        foreach (KeyValuePair<long, int> e in _density)
        {
            entries.Add((e.Key, e.Value));
        }

        entries.Sort(static (a, b) => b.Free != a.Free ? b.Free.CompareTo(a.Free) : a.Index.CompareTo(b.Index));
        var page = new byte[PageSize];
        page[0] = flags;
        page[1] = (byte)Math.Min(entries.Count, DensityCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), currentPage);
        for (int i = 0; i < page[1]; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8 + (i * 4)), (uint)entries[i].Index | ((uint)entries[i].Free << 20));
        }

        page[496] = 0x86;
        page[497] = 0x86;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(498), NdbChecksums.Signature((ulong)DensityListOffset, bid));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(500), NdbChecksums.Crc32(page.AsSpan(0, 496)));
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(504), bid);
        return page;
    }

    /// <summary>Forgets the transaction: its allocations were never recorded on disk, so dropping the cached maps returns every bit to the file's own.</summary>
    public void Rollback(long committedRangeCount)
    {
        _maps.Clear();
        _dirty.Clear();
        _pendingFree.Clear();
        _txnAllocated.Clear();
        RangeCount = committedRangeCount;
        FreeDelta = 0;
        RequiredEof = 0;
        _hint = Math.Max(0, committedRangeCount - 1);
    }

    /// <summary>After a commit: nothing is pending and the deltas are folded into the header.</summary>
    public void Committed()
    {
        _txnAllocated.Clear();
        FreeDelta = 0;
        // Only the ranges allocated from are worth keeping; the cache would otherwise grow with the file.
        if (_maps.Count > 64)
        {
            _maps.Clear();
        }
    }
}
