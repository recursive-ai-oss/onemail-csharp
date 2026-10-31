using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>Whether a scan hit is a page or a block.</summary>
public enum NdbScanKind
{
    /// <summary>A 512-byte (or 4096-byte) page with a plausible trailer.</summary>
    Page,
    /// <summary>A block with a plausible trailer.</summary>
    Block,
}

/// <summary>A page or block found by scan mode, independent of the B-trees.</summary>
/// <param name="Kind">Page or block.</param>
/// <param name="Ib">File offset where the page or block starts.</param>
/// <param name="Size">Bytes the page or block occupies on disk, trailer and padding included.</param>
/// <param name="PageType">For pages, the ptype byte (see <see cref="NdbPageType"/>); zero for blocks.</param>
/// <param name="Bid">The BID from the trailer. For AMap, PMap, FMap and FPMap pages this equals <paramref name="Ib"/>.</param>
/// <param name="DataSize">For blocks, cb from the trailer: the data bytes without padding or trailer. Zero for pages.</param>
/// <param name="Check">Which checks passed: signature and CRC are computed; <c>TypeValid</c> is true for blocks and for pages whose ptype repeats; <c>BidValid</c> means the BID is structurally possible.</param>
public readonly record struct NdbScanItem(NdbScanKind Kind, long Ib, int Size, byte PageType, ulong Bid, int DataSize, NdbCheck Check);

/// <summary>Switches for <see cref="NdbFile.Scan"/>.</summary>
public sealed class NdbScanOptions
{
    /// <summary>
    /// True to report pages and blocks that fail their CRC or signature too, as long as their trailer looks plausible.
    /// Default false: only structures whose signature and CRC both match are reported, which keeps noise from slack space and
    /// random data to nothing. Damaged-content recovery sets this and ranks the results.
    /// </summary>
    public bool IncludeDamaged { get; init; }

    /// <summary>Offset to start at; rounded down to a page boundary. Default: the density list position, the first byte after the reserved area.</summary>
    public long? StartOffset { get; init; }
}

public sealed partial class NdbFile
{
    private const int ScanChunk = 1 << 20;

    /// <summary>
    /// Scan mode: finds every page and block in the file by looking for trailers at page and 64-byte granularity, without using
    /// either B-tree. A page is a 512-byte unit whose last bytes are a valid page trailer; a block is a 64-byte-aligned extent
    /// ending in a valid block trailer. Results come in file order (blocks by their end), one pass, constant memory.
    /// </summary>
    /// <remarks>
    /// This is the substrate for recovery: structures the B-trees no longer reference (deleted, orphaned, free space) show up
    /// here with their offsets and validity. By default only clean structures are reported; see <see cref="NdbScanOptions.IncludeDamaged"/>.
    /// </remarks>
    /// <param name="options">Switches; null for defaults.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The hits, lazily.</returns>
    public IEnumerable<NdbScanItem> Scan(NdbScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new NdbScanOptions();
        NdbLayout l = Layout;
        int pageSize = l.PageSize;
        long start = options.StartOffset ?? l.DensityList;
        start -= start % pageSize;
        if (start < 0)
        {
            start = 0;
        }

        int carry = l.MaxBlockData + l.BlockTrailerSize + 64;
        byte[] buf = ArrayPool<byte>.Shared.Rent(ScanChunk + carry);
        try
        {
            long bufStart = start;         // file offset of buf[0]
            int bufLen = 0;                // valid bytes in buf
            long unit = start;             // next page unit to examine
            long fileLength = Length;
            var results = new List<NdbScanItem>(16);
            while (unit + pageSize <= fileLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Make sure the buffer holds this unit and enough before it to find the start of a block that ends in it.
                if (unit + pageSize > bufStart + bufLen)
                {
                    long keepFrom = Math.Max(start, unit - carry);
                    keepFrom -= keepFrom % pageSize;
                    int keep = 0;
                    if (keepFrom >= bufStart && keepFrom < bufStart + bufLen)
                    {
                        keep = (int)(bufStart + bufLen - keepFrom);
                        Buffer.BlockCopy(buf, (int)(keepFrom - bufStart), buf, 0, keep);
                    }

                    bufStart = keepFrom;
                    int want = (int)Math.Min(ScanChunk, fileLength - (bufStart + keep));
                    int got = want > 0 ? ReadRaw(bufStart + keep, buf.AsSpan(keep, want)) : 0;
                    bufLen = keep + got;
                    if (unit + pageSize > bufStart + bufLen)
                    {
                        yield break;
                    }
                }

                results.Clear();
                ScanUnit(buf, bufStart, bufLen, unit, start, options.IncludeDamaged, results);
                foreach (NdbScanItem r in results)
                {
                    yield return r;
                }

                unit += pageSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // Spans cannot live across a yield, so the per-unit work is done here and handed back as a list.
    private void ScanUnit(byte[] buf, long bufStart, int bufLen, long unit, long regionStart, bool includeDamaged, List<NdbScanItem> results)
    {
        ReadOnlySpan<byte> all = buf.AsSpan(0, bufLen);
        int unitAt = (int)(unit - bufStart);
        if (TryPage(unit, all.Slice(unitAt, Layout.PageSize), includeDamaged, out NdbScanItem pageHit))
        {
            results.Add(pageHit);
            return;
        }

        for (int e = unitAt + 64; e <= unitAt + Layout.PageSize; e += 64)
        {
            if (TryBlock(bufStart, all, e, regionStart, includeDamaged, out NdbScanItem blockHit))
            {
                results.Add(blockHit);
            }
        }
    }

    private bool TryPage(long ib, ReadOnlySpan<byte> page, bool includeDamaged, out NdbScanItem item)
    {
        item = default;
        NdbLayout l = Layout;
        int t = l.PageSize - l.PageTrailerSize;
        byte type = page[t];
        if (type is < 0x80 or > 0x86 || page[t + 1] != type)
        {
            return false;
        }

        ulong bid = ReadTrailerBid(page);
        NdbCheck c = CheckPage(ib, page, (NdbPageType)type, bid, report: false);
        bool allocation = type is >= 0x82 and <= 0x85;
        bool bidOk = allocation ? bid == (ulong)ib : bid != 0;
        c = c with { BidValid = bidOk };
        if (!(c.SignatureValid && c.CrcValid && bidOk) && !(includeDamaged && (c.SignatureValid || c.CrcValid)))
        {
            return false;
        }

        item = new NdbScanItem(NdbScanKind.Page, ib, l.PageSize, type, bid, 0, c);
        return true;
    }

    private bool TryBlock(long bufStart, ReadOnlySpan<byte> all, int end, long regionStart, bool includeDamaged, out NdbScanItem item)
    {
        item = default;
        NdbLayout l = Layout;
        int tsz = l.BlockTrailerSize;
        if (end - tsz < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> tr = all.Slice(end - tsz, tsz);
        int cb = BinaryPrimitives.ReadUInt16LittleEndian(tr);
        if (cb == 0 || cb > l.MaxBlockData)
        {
            return false;
        }

        int total = l.BlockTotal(cb);
        int startIdx = end - total;
        long ib = bufStart + startIdx;
        if (startIdx < 0 || ib < regionStart)
        {
            return false;
        }

        ushort sig = BinaryPrimitives.ReadUInt16LittleEndian(tr[2..]);
        ulong bid;
        uint crc;
        if (l.IsAnsi)
        {
            bid = BinaryPrimitives.ReadUInt32LittleEndian(tr[4..]);
            crc = BinaryPrimitives.ReadUInt32LittleEndian(tr[8..]);
        }
        else
        {
            crc = BinaryPrimitives.ReadUInt32LittleEndian(tr[4..]);
            bid = BinaryPrimitives.ReadUInt64LittleEndian(tr[8..]);
        }

        // BIDs are never zero and bit 0 is reserved (MS-PST 2.2.2.2).
        if (bid == 0 || (bid & 1) != 0)
        {
            return false;
        }

        bool sigOk = sig == NdbChecksums.Signature((ulong)ib, bid);
        if (!sigOk && !includeDamaged)
        {
            return false;
        }

        bool crcOk = NdbChecksums.Crc32(all.Slice(startIdx, cb)) == crc;
        if (!(sigOk && crcOk) && !(includeDamaged && (sigOk || crcOk)))
        {
            return false;
        }

        item = new NdbScanItem(NdbScanKind.Block, ib, total, 0, bid, cb, new NdbCheck(true, sigOk, crcOk, true));
        return true;
    }
}
