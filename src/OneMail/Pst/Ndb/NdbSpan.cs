using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>What <see cref="NdbSpan.TryParsePage"/> found in a page.</summary>
/// <param name="Format">The layout the page was read with.</param>
/// <param name="PageType">The trailer's ptype byte (see <see cref="NdbPageType"/>; other values are possible in damaged data).</param>
/// <param name="Bid">The trailer's BID: for B-tree and density pages the page's block id, for allocation pages its own offset.</param>
/// <param name="Signature">The trailer's wSig.</param>
/// <param name="Crc">The trailer's dwCRC.</param>
/// <param name="Check">The checks. <see cref="NdbCheck.TypeValid"/> here means "ptype equals ptypeRepeat and is a known page type"; <see cref="NdbCheck.BidValid"/> is true unless the caller gave a BID to compare.</param>
public readonly record struct NdbPageInfo(NdbFormat Format, byte PageType, ulong Bid, ushort Signature, uint Crc, NdbCheck Check)
{
    /// <summary>True for FMap, PMap, AMap and FPMap pages.</summary>
    public bool IsAllocationPage => PageType is >= 0x82 and <= 0x85;
}

/// <summary>What <see cref="NdbSpan.TryParseBlock"/> found in a block.</summary>
/// <param name="Format">The layout the block was read with.</param>
/// <param name="Bid">The trailer's BID.</param>
/// <param name="DataLength">The trailer's cb: the number of data bytes at the start of the span (still encoded or compressed as the file stores them).</param>
/// <param name="TotalLength">The bytes the block occupies: data, padding and trailer, a multiple of the block alignment.</param>
/// <param name="UncompressedLength">4 KiB layouts only: the trailer's uncompressed size, else 0. A value other than 0 and <paramref name="DataLength"/> means the data is compressed.</param>
/// <param name="Check">The checks: size (the trailer's cb fits the span's size), signature, CRC. <see cref="NdbCheck.BidValid"/> is true unless the caller gave a BID.</param>
public readonly record struct NdbBlockInfo(NdbFormat Format, ulong Bid, int DataLength, int TotalLength, int UncompressedLength, NdbCheck Check)
{
    /// <summary>True when the BID has the internal bit set (MS-PST 2.2.2.2): XBLOCK, XXBLOCK, SLBLOCK, SIBLOCK, never encoded.</summary>
    public bool IsInternal => (Bid & 2) != 0;
}

/// <summary>
/// Span-level parsing of the NDB layer's pieces (MS-PST 2.2.2.7 pages, 2.2.2.8 blocks) for tools that find them somewhere other than where a
/// file's own B-trees say. Nothing throws and nothing is allocated: a span that is not the piece gives false.
/// </summary>
/// <remarks>
/// These do not open a file and know nothing about the B-trees, so they say what the bytes are, not whether a live tree reaches them. Who owns
/// a recovered piece is for the caller to decide (<c>docs/recovery.md</c>).
/// </remarks>
public static class NdbSpan
{
    /// <summary>Parses and checks one page: its trailer, then signature and CRC against the offset it was found at.</summary>
    /// <param name="page">At least one page (512 bytes, or 4096 for <see cref="NdbFormat.Unicode4K"/>), starting at the page's first byte. Longer spans are read for one page only.</param>
    /// <param name="format">The layout to read it with.</param>
    /// <param name="ib">The file offset the page was found at (the signature mixes it in).</param>
    /// <param name="info">The page's trailer fields and the check results.</param>
    /// <param name="defects">Receives a defect for each failed check, or null. The offsets are <paramref name="ib"/>.</param>
    /// <param name="expectedBid">When given, the BID the trailer must hold (ignored for allocation pages, whose BID is their own offset).</param>
    /// <returns>False when the span is shorter than a page or the trailer's type is not a page type at all (0x80 to 0x86); otherwise true even when checks failed, so the caller can read <paramref name="info"/>.Check.</returns>
    public static bool TryParsePage(ReadOnlySpan<byte> page, NdbFormat format, long ib, out NdbPageInfo info, ICollection<NdbDefect>? defects = null, ulong? expectedBid = null)
    {
        info = default;
        NdbLayout l = NdbLayout.For(format);
        if (page.Length < l.PageSize || ib < 0)
        {
            return false;
        }

        page = page[..l.PageSize];
        int t = l.PageSize - l.PageTrailerSize;
        byte type = page[t];
        if (type is < 0x80 or > 0x86)
        {
            return false;
        }

        byte repeat = page[t + 1];
        ushort sig = BinaryPrimitives.ReadUInt16LittleEndian(page[(t + 2)..]);
        uint crc;
        ulong bid;
        if (l.IsAnsi)
        {
            bid = BinaryPrimitives.ReadUInt32LittleEndian(page[(t + 4)..]);
            crc = BinaryPrimitives.ReadUInt32LittleEndian(page[(t + 8)..]);
        }
        else
        {
            crc = BinaryPrimitives.ReadUInt32LittleEndian(page[(t + 4)..]);
            bid = BinaryPrimitives.ReadUInt64LittleEndian(page[(t + 8)..]);
        }

        // MS-PST 2.2.2.7.1: ptype and ptypeRepeat are the same byte; 2.2.2.7.1 gives allocation pages signature 0 and their own offset as BID.
        bool typeOk = repeat == type;
        if (!typeOk)
        {
            defects?.Add(new NdbDefect(NdbDefectKind.PageTypeRepeatMismatch, ib, $"ptypeRepeat 0x{repeat:x2} differs from ptype 0x{type:x2}"));
        }

        bool allocation = type is >= 0x82 and <= 0x85;
        bool sigOk = allocation ? sig == 0 : sig == NdbChecksums.Signature((ulong)ib, bid);
        if (!sigOk)
        {
            defects?.Add(new NdbDefect(NdbDefectKind.PageSignatureMismatch, ib, $"wSig 0x{sig:x4}"));
        }

        bool crcOk = l.IsAnsi && allocation
            ? NdbChecksums.Crc32(page.Slice(l.AllocBitsOffset, l.AllocBitsLength)) == crc || NdbChecksums.Crc32(page[..t]) == crc
            : NdbChecksums.Crc32(page[..t]) == crc;
        if (!crcOk)
        {
            defects?.Add(new NdbDefect(NdbDefectKind.PageCrcMismatch, ib, "dwCRC does not match the page"));
        }

        bool bidOk = allocation ? bid == (ulong)ib : expectedBid is not { } want || bid == want;
        if (!bidOk)
        {
            defects?.Add(new NdbDefect(NdbDefectKind.PageBidMismatch, ib, $"trailer BID 0x{bid:x}"));
        }

        info = new NdbPageInfo(format, type, bid, sig, crc, new NdbCheck(typeOk, sigOk, crcOk, bidOk));
        return true;
    }

    /// <summary>
    /// Parses and checks one block. The block's size is not known in advance (the trailer holds it and sits at the end), so every size the format
    /// allows is tried in turn and the first whose trailer agrees with its own size, signature and CRC wins; if none is clean the first
    /// whose trailer agrees with its own size is returned with the failed checks.
    /// </summary>
    /// <param name="data">The bytes from the block's first byte; at least the block's total size (data, padding and trailer).</param>
    /// <param name="format">The layout to read it with.</param>
    /// <param name="ib">The file offset the block was found at (the signature mixes it in).</param>
    /// <param name="info">The block's trailer fields and the check results.</param>
    /// <param name="defects">Receives a defect for each failed check, or null.</param>
    /// <param name="expectedBid">When given, the BID the trailer must hold.</param>
    /// <returns>False when no candidate size gives a trailer whose cb fits it.</returns>
    public static bool TryParseBlock(ReadOnlySpan<byte> data, NdbFormat format, long ib, out NdbBlockInfo info, ICollection<NdbDefect>? defects = null, ulong? expectedBid = null)
    {
        info = default;
        NdbLayout l = NdbLayout.For(format);
        if (ib < 0)
        {
            return false;
        }

        // Bounds: total is a multiple of the alignment, at most data.Length, so every read of total - BlockTrailerSize.. stays inside data.
        int smallest = l.BlockTotal(0);
        int largest = Math.Min(data.Length, l.BlockTotal(l.MaxBlockData)) / l.BlockAlignment * l.BlockAlignment;
        NdbBlockInfo? fallback = null;
        for (int total = smallest; total <= largest; total += l.BlockAlignment)
        {
            ReadOnlySpan<byte> tr = data.Slice(total - l.BlockTrailerSize, l.BlockTrailerSize);
            int cb = BinaryPrimitives.ReadUInt16LittleEndian(tr);
            if (cb > l.MaxBlockData || l.BlockTotal(cb) != total)
            {
                continue;
            }

            ushort sig = BinaryPrimitives.ReadUInt16LittleEndian(tr[2..]);
            uint crc;
            ulong bid;
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

            bool sigOk = sig == NdbChecksums.Signature((ulong)ib, bid);
            bool crcOk = NdbChecksums.Crc32(data[..cb]) == crc;
            bool bidOk = expectedBid is not { } want || bid == want;
            int uncompressed = format == NdbFormat.Unicode4K ? BinaryPrimitives.ReadUInt16LittleEndian(tr[18..]) : 0;
            var candidate = new NdbBlockInfo(format, bid, cb, total, uncompressed, new NdbCheck(true, sigOk, crcOk, bidOk));
            if (sigOk && crcOk)
            {
                info = candidate;
                if (!bidOk)
                {
                    defects?.Add(new NdbDefect(NdbDefectKind.BlockBidMismatch, ib, $"trailer BID 0x{bid:x}"));
                }

                return true;
            }

            fallback ??= candidate;
        }

        if (fallback is not { } f)
        {
            return false;
        }

        info = f;
        if (!f.Check.SignatureValid)
        {
            defects?.Add(new NdbDefect(NdbDefectKind.BlockSignatureMismatch, ib, "wSig does not match the offset and BID"));
        }

        if (!f.Check.CrcValid)
        {
            defects?.Add(new NdbDefect(NdbDefectKind.BlockCrcMismatch, ib, "dwCRC does not match the block data"));
        }

        if (!f.Check.BidValid)
        {
            defects?.Add(new NdbDefect(NdbDefectKind.BlockBidMismatch, ib, $"trailer BID 0x{f.Bid:x}"));
        }

        return true;
    }
}
