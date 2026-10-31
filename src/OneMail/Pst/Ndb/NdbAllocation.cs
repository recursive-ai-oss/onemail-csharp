using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>One entry of the density list (MS-PST 2.2.2.7.4.1 DLISTPAGEENT).</summary>
/// <param name="AMapIndex">dwPageNum: the zero-based index of an AMap page.</param>
/// <param name="FreeSlots">dwFreeSlots: free allocation slots (64 bytes; 512 in 4 KiB files) in that AMap, not necessarily contiguous.</param>
public readonly record struct DensityEntry(int AMapIndex, int FreeSlots);

/// <summary>The density list page (MS-PST 2.2.2.7.4): AMaps ordered by free space, plus the allocation cursor.</summary>
/// <param name="Flags">bFlags; bit 0 is DFL_BACKFILL_COMPLETE.</param>
/// <param name="CurrentPage">ulCurrentPage: the AMap index used for the next allocation, or being backfilled.</param>
/// <param name="Entries">The cEntDList entries, as stored.</param>
/// <param name="Check">The page checks. MS-PST says a density list with a bad CRC must not be used.</param>
public sealed record NdbDensityList(byte Flags, uint CurrentPage, IReadOnlyList<DensityEntry> Entries, NdbCheck Check);

/// <summary>The result of reading every AMap page and adding up the free space.</summary>
/// <param name="AMapCount">How many AMap pages the header's ibAMapLast implies.</param>
/// <param name="InvalidCount">How many of them failed a check (outside the file, bad type, signature, CRC or BID).</param>
/// <param name="FreeBytes">Free bytes summed over the AMaps that could be read (clear bits times the slot size, 64 bytes or 512 in 4 KiB files).</param>
/// <param name="HeaderFreeBytes">cbAMapFree from the header, for comparison.</param>
public readonly record struct NdbAllocationSummary(long AMapCount, long InvalidCount, long FreeBytes, long HeaderFreeBytes);

public sealed partial class NdbFile
{
    private byte[]? _amapCache;
    private long _amapCacheIndex = -1;
    private bool _amapCacheOk;

    /// <summary>
    /// The number of AMap pages implied by the header: from the first AMap to ibAMapLast at fixed intervals. A damaged
    /// ibAMapLast cannot make this larger than the number of AMap slots that fit in the file.
    /// </summary>
    public long AMapCount
    {
        get
        {
            long last = (long)Math.Min(Header.Root.AMapLast, long.MaxValue);
            if (last < Layout.FirstAMap)
            {
                return 0;
            }

            long implied = ((last - Layout.FirstAMap) / Layout.AMapInterval) + 1;
            long fits = Length < Layout.FirstAMap ? 0 : ((Length - Layout.FirstAMap) / Layout.AMapInterval) + 1;
            return Math.Min(implied, fits);
        }
    }

    /// <summary>The file offset of the <paramref name="index"/>th AMap page (MS-PST 2.2.2.7.2: the first is at 0x4400, then every 253,952 bytes; 4 KiB files differ).</summary>
    /// <param name="index">Zero-based AMap index.</param>
    /// <returns>The offset.</returns>
    public long AMapOffset(long index) => Layout.FirstAMap + (index * Layout.AMapInterval);

    /// <summary>Reads and checks one AMap page.</summary>
    /// <param name="index">Zero-based AMap index.</param>
    /// <param name="bits">Receives the allocation bits (496 bytes, or 4,072 for 4 KiB files): bit n set means the slot (64 bytes; 512 in 4 KiB files) starting <c>n</c> slots past the AMap is allocated. Must be at least <see cref="PageSize"/> bytes.</param>
    /// <param name="check">The page checks.</param>
    /// <returns>False when the page lies outside the file or is not an AMap page.</returns>
    public bool TryReadAMap(long index, Span<byte> bits, out NdbCheck check)
    {
        byte[] page = ArrayPool<byte>.Shared.Rent(Layout.PageSize);
        try
        {
            long ib = AMapOffset(index);
            if (!ReadPage(ib, page, NdbPageType.AllocationMap, (ulong)ib, out check))
            {
                return false;
            }

            page.AsSpan(Layout.AllocBitsOffset, Layout.AllocBitsLength).CopyTo(bits);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
        }
    }

    /// <summary>Reads every AMap and compares the free space they add up to with the header (MS-PST 2.6.1.3.7). Constant memory.</summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The summary. Problems are also recorded as defects.</returns>
    public NdbAllocationSummary ValidateAllocation(CancellationToken cancellationToken = default)
    {
        long count = AMapCount;
        long invalid = 0;
        if (Header.Root.AMapLast > (ulong)Length)
        {
            // Not a hang risk (the count is clamped to the file), but the header is wrong about where the last AMap is.
            Report(NdbDefectKind.AllocationPageInvalid, 0, $"ibAMapLast 0x{Header.Root.AMapLast:x} is beyond the end of the file");
            invalid++;
        }

        long free = 0;
        byte[] bits = ArrayPool<byte>.Shared.Rent(Layout.PageSize);
        try
        {
            for (long i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadAMap(i, bits, out NdbCheck c))
                {
                    invalid++;
                    Report(NdbDefectKind.AllocationPageInvalid, AMapOffset(i), $"AMap {i} cannot be read");
                    continue;
                }

                if (!c.IsClean)
                {
                    invalid++;
                    Report(NdbDefectKind.AllocationPageInvalid, AMapOffset(i), $"AMap {i} fails a check");
                }

                free += FreeBits(bits.AsSpan(0, Layout.AllocBitsLength)) * (long)Layout.BlockAlignment;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bits);
        }

        long header = (long)Math.Min(Header.Root.AMapFree, long.MaxValue);
        if (Header.Root.AMapValid is 1 or 2 && invalid == 0 && free != header)
        {
            Report(NdbDefectKind.AMapFreeSpaceMismatch, 0, $"the AMaps have {free} free bytes, the header says {header}");
        }

        return new NdbAllocationSummary(count, invalid, free, header);
    }

    private static long FreeBits(ReadOnlySpan<byte> bits)
    {
        long set = 0;
        foreach (byte b in bits)
        {
            set += System.Numerics.BitOperations.PopCount(b);
        }

        return ((long)bits.Length * 8) - set;
    }

    /// <summary>
    /// Whether the AMaps mark every allocation slot of the range as allocated. Ranges before the first AMap (the header area) count as allocated.
    /// Reads AMap pages on demand and keeps the last one.
    /// </summary>
    /// <param name="ib">Start of the range.</param>
    /// <param name="length">Length of the range in bytes.</param>
    /// <returns>True when all slots are allocated, or an AMap could not be read (that failure is its own defect).</returns>
    public bool IsAllocated(long ib, long length)
    {
        if (length <= 0 || ib < Layout.FirstAMap)
        {
            return true;
        }

        long end = ib + length;
        _amapCache ??= new byte[Layout.AllocBitsLength];
        for (long p = ib - (ib % Layout.BlockAlignment); p < end; p += Layout.BlockAlignment)
        {
            long index = (p - Layout.FirstAMap) / Layout.AMapInterval;
            if (index != _amapCacheIndex)
            {
                _amapCacheOk = TryReadAMap(index, _amapCache, out _);
                _amapCacheIndex = index;
            }

            if (!_amapCacheOk)
            {
                return true;
            }

            long bit = (p - AMapOffset(index)) / Layout.BlockAlignment;
            if ((_amapCache[bit >> 3] & (1 << (int)(bit & 7))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads the density list page (MS-PST 2.2.2.7.4), at 0x4200 in 512-byte-page files.</summary>
    /// <returns>The list, or null when there is no readable density list page (it is optional, and older writers omit it).</returns>
    public NdbDensityList? ReadDensityList()
    {
        byte[] page = ArrayPool<byte>.Shared.Rent(Layout.PageSize);
        try
        {
            long ib = Layout.DensityList;
            if (ib + Layout.PageSize > Length)
            {
                return null;
            }

            if (ReadRaw(ib, page.AsSpan(0, Layout.PageSize)) != Layout.PageSize)
            {
                return null;
            }

            ReadOnlySpan<byte> p = page.AsSpan(0, Layout.PageSize);
            if (p[Layout.PageSize - Layout.PageTrailerSize] != (byte)NdbPageType.DensityList)
            {
                return null;
            }

            // The page's BID comes from the bidNextP counter, so only the signature (which needs it) and CRC are checked here.
            NdbCheck check = CheckPage(ib, p, NdbPageType.DensityList, ReadTrailerBid(p), report: true);
            byte flags = p[0];
            int cEnt = p[1];
            uint current = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
            int capacity = (Layout.PageSize - Layout.PageTrailerSize - 8 - 12) / 4;
            if (cEnt > capacity)
            {
                Report(NdbDefectKind.DensityListInvalid, ib, $"cEntDList {cEnt} exceeds the {capacity} entries a page holds");
                cEnt = capacity;
            }

            var entries = new List<DensityEntry>(cEnt);
            for (int i = 0; i < cEnt; i++)
            {
                // dwPageNum is the low 20 bits and dwFreeSlots the high 12 of each little-endian DWORD.
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(p[(8 + (i * 4))..]);
                entries.Add(new DensityEntry((int)(v & 0xFFFFF), (int)(v >> 20)));
            }

            return new NdbDensityList(flags, current, entries, check);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
        }
    }

    private ulong ReadTrailerBid(ReadOnlySpan<byte> page)
    {
        int t = Layout.PageSize - Layout.PageTrailerSize;
        return Layout.IsAnsi ? BinaryPrimitives.ReadUInt32LittleEndian(page[(t + 4)..]) : BinaryPrimitives.ReadUInt64LittleEndian(page[(t + 8)..]);
    }
}
