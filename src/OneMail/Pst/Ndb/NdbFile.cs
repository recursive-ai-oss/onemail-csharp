using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>The page types of MS-PST 2.2.2.7.1.</summary>
public enum NdbPageType : byte
{
    /// <summary>ptypeBBT: block B-tree page.</summary>
    BlockBTree = 0x80,
    /// <summary>ptypeNBT: node B-tree page.</summary>
    NodeBTree = 0x81,
    /// <summary>ptypeFMap: free map page.</summary>
    FreeMap = 0x82,
    /// <summary>ptypePMap: page map page.</summary>
    PageMap = 0x83,
    /// <summary>ptypeAMap: allocation map page.</summary>
    AllocationMap = 0x84,
    /// <summary>ptypeFPMap: free page map page.</summary>
    FreePageMap = 0x85,
    /// <summary>ptypeDL: density list page.</summary>
    DensityList = 0x86,
}

/// <summary>What the checks on one page or block found. All true means the structure is exactly as the format requires.</summary>
/// <param name="TypeValid">The page type byte matched what was expected, and ptypeRepeat equals it. Always true for blocks.</param>
/// <param name="SignatureValid">wSig matches the offset and BID.</param>
/// <param name="CrcValid">dwCRC matches the contents.</param>
/// <param name="BidValid">The trailer's BID is the one the reference expected.</param>
public readonly record struct NdbCheck(bool TypeValid, bool SignatureValid, bool CrcValid, bool BidValid)
{
    /// <summary>True when every check passed.</summary>
    public bool IsClean => TypeValid && SignatureValid && CrcValid && BidValid;
}

/// <summary>
/// An open PST, OST or PAB file at the node-database level (MS-PST 2.2): header, the node and block B-trees, blocks,
/// data trees and subnode trees, allocation maps and scan mode.
/// </summary>
/// <remarks>
/// <para>Reading never throws for malformed content. Everything found wrong is a <see cref="NdbDefect"/> in
/// <see cref="Defects"/> and the structure is skipped or read as far as it can be. I/O errors from the underlying
/// source (<see cref="IOException"/> and friends) propagate.</para>
/// <para>Instances are not thread-safe. Open one per thread, or serialise calls.</para>
/// </remarks>
public sealed partial class NdbFile : IDisposable
{
    private readonly IRandomAccessSource _source;
    private readonly bool _leaveOpen;
    private readonly NdbOptions _options;
    private readonly List<NdbDefect> _defects = [];
    private int _dropped;
    private readonly NodeBTree _nodes;
    private readonly BlockBTree _blocks;

    private NdbFile(IRandomAccessSource source, bool leaveOpen, NdbHeader header, NdbLayout layout, NdbOptions options)
    {
        _source = source;
        _leaveOpen = leaveOpen;
        Header = header;
        Layout = layout;
        _options = options;
        _nodes = new NodeBTree(this);
        _blocks = new BlockBTree(this);
    }

    internal NdbLayout Layout { get; private set; }

    /// <summary>The parsed header.</summary>
    public NdbHeader Header { get; }

    /// <summary>The file generation (ANSI, Unicode, or Unicode with 4 KiB pages).</summary>
    public NdbFormat Format => Layout.Format;

    /// <summary>The size of a page in bytes: 512, or 4096 for <see cref="NdbFormat.Unicode4K"/>.</summary>
    public int PageSize => Layout.PageSize;

    /// <summary>The largest data a single block can hold in this generation (8,176 for Unicode, 8,180 for ANSI, 65,536 for 4 KiB pages).</summary>
    public int MaxBlockDataSize => Layout.MaxBlockData;

    /// <summary>The real length of the file in bytes.</summary>
    public long Length => _source.Length;

    /// <summary>The node B-tree (NBT): every node, keyed by NID.</summary>
    public NodeBTree Nodes => _nodes;

    /// <summary>The block B-tree (BBT): every block, keyed by BID.</summary>
    public BlockBTree Blocks => _blocks;

    /// <summary>Everything found wrong so far, in the order it was found. Grows as more of the file is read. Capped by <see cref="NdbOptions.MaxDefects"/>.</summary>
    public IReadOnlyList<NdbDefect> Defects => _defects;

    /// <summary>Defects found after <see cref="NdbOptions.MaxDefects"/> was reached; counted but not kept.</summary>
    public int DroppedDefects => _dropped;

    internal NdbOptions Options => _options;

    internal void Report(NdbDefectKind kind, long offset, string detail)
    {
        if (_defects.Count >= _options.MaxDefects)
        {
            _dropped++;
            return;
        }

        // The last slot says that the list was cut, so a caller who only reads Defects still sees it.
        if (_defects.Count == _options.MaxDefects - 1)
        {
            _defects.Add(new NdbDefect(NdbDefectKind.DefectLimitReached, offset, "further defects are counted but not listed"));
            _dropped++;
            return;
        }

        _defects.Add(new NdbDefect(kind, offset, detail));
    }

    /// <summary>Opens a file by path, read-only and shared. Does not throw for malformed content.</summary>
    /// <param name="path">The file.</param>
    /// <param name="file">The open file, or null when the header is unusable.</param>
    /// <param name="defects">The defects that made the file unusable (empty on success; see <see cref="Defects"/> for later ones).</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the header was recognised and the file can be read.</returns>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    public static bool TryOpen(string path, out NdbFile? file, out IReadOnlyList<NdbDefect> defects, NdbOptions? options = null) =>
        TryOpen(FileSource.Open(path, FileShare.ReadWrite | FileShare.Delete), leaveOpen: false, out file, out defects, options);

    /// <summary>Opens a seekable stream. Does not throw for malformed content.</summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere; it is seeked freely.</param>
    /// <param name="leaveOpen">True to leave the stream open when the file is disposed.</param>
    /// <param name="file">The open file, or null when the header is unusable.</param>
    /// <param name="defects">The defects that made the file unusable.</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the header was recognised and the file can be read.</returns>
    public static bool TryOpen(Stream stream, bool leaveOpen, out NdbFile? file, out IReadOnlyList<NdbDefect> defects, NdbOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return TryOpen(new StreamSource(stream, leaveOpen), leaveOpen: false, out file, out defects, options);
    }

    /// <summary>Opens a file held in memory. Does not throw for malformed content.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="file">The open file, or null when the header is unusable.</param>
    /// <param name="defects">The defects that made the file unusable.</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the header was recognised and the file can be read.</returns>
    public static bool TryOpen(ReadOnlyMemory<byte> data, out NdbFile? file, out IReadOnlyList<NdbDefect> defects, NdbOptions? options = null) =>
        TryOpen(new MemorySource(data), leaveOpen: false, out file, out defects, options);

    /// <summary>Opens a file over any random-access source: a file, a stream, memory, or ranged reads the caller supplies.</summary>
    /// <param name="source">The source. It must support synchronous reads (<see cref="IRandomAccessSource.SupportsSyncRead"/>).</param>
    /// <param name="leaveOpen">True to leave the source undisposed when the file is disposed (or when the header is unusable).</param>
    /// <param name="file">The open file, or null when the header is unusable.</param>
    /// <param name="defects">The defects that made the file unusable.</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the header was recognised and the file can be read.</returns>
    /// <exception cref="ArgumentException">The source is asynchronous only.</exception>
    public static bool TryOpen(IRandomAccessSource source, bool leaveOpen, out NdbFile? file, out IReadOnlyList<NdbDefect> defects, NdbOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.SupportsSyncRead)
        {
            throw new ArgumentException("The node database is read with synchronous positioned reads; this source is asynchronous only.", nameof(source));
        }

        options ??= new NdbOptions();
        var early = new List<NdbDefect>();
        file = null;
        defects = early;
        byte[] buf = new byte[564];
        int n = source.ReadFully(0, buf);
        if (!NdbHeader.TryParse(buf.AsSpan(0, n), out NdbHeader? header, early) || header is null)
        {
            if (!leaveOpen)
            {
                source.Dispose();
            }

            return false;
        }

        var f = new NdbFile(source, leaveOpen, header, NdbLayout.For(header.Format), options);
        foreach (NdbDefect d in early)
        {
            f.Report(d.Kind, d.Offset, d.Detail);
        }

        f.ResolvePageSize();
        f.CheckRoot();
        file = f;
        defects = [];
        return true;
    }

    // wVer 36 is 4 KiB pages. wVer 37 only says the file may be protected; a protected file can still be either size, and
    // so can any later version. When the version doesn't settle it, the root B-tree page does: its trailer sits at a
    // different offset in each size and repeats ptype, which is rare to get right by accident.
    private void ResolvePageSize()
    {
        ushort ver = Header.Version;
        if (Header.Format == NdbFormat.Ansi || ver is 21 or 23 or 36)
        {
            return;
        }

        NdbFormat guess = NdbFormat.Unicode;
        long ib = (long)Header.Root.NodeBTree.Ib;
        if (ib > 0 && ib + 4096 <= _source.Length)
        {
            byte[] page = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                _source.ReadFully(ib, page.AsSpan(0, 4096));
                bool small = page[496] == 0x81 && page[497] == 0x81;
                bool big = page[4072] == 0x81 && page[4073] == 0x81;
                if (big && !small)
                {
                    guess = NdbFormat.Unicode4K;
                }
                else if (small == big)
                {
                    Report(NdbDefectKind.AmbiguousPageSize, ib, $"wVer {ver} does not say whether pages are 512 or 4096 bytes and the root page does not settle it; assuming 512");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(page);
            }
        }

        if (guess != Layout.Format)
        {
            Layout = NdbLayout.For(guess);
            Header.Format = guess;
        }
    }

    private void CheckRoot()
    {
        NdbRoot r = Header.Root;
        if (r.FileEof != (ulong)_source.Length)
        {
            Report(NdbDefectKind.FileSizeMismatch, 0, $"the header records {r.FileEof} bytes but the file is {_source.Length}");
        }

        foreach ((string name, Bref b) in new[] { ("NBT", r.NodeBTree), ("BBT", r.BlockBTree) })
        {
            if (b.Ib == 0 || b.Ib + (ulong)Layout.PageSize > (ulong)_source.Length)
            {
                Report(NdbDefectKind.RootOutOfRange, 0, $"the {name} root page at 0x{b.Ib:x} is outside the file");
            }
        }
    }

    // ---- pages ----

    /// <summary>
    /// Reads and checks one page. Records a defect for each failed check and returns false only when the page cannot be used
    /// at all (outside the file, or not the expected type).
    /// </summary>
    internal bool ReadPage(long ib, Span<byte> page, NdbPageType expected, ulong expectedBid, out NdbCheck check)
    {
        check = default;
        NdbLayout l = Layout;
        if (ib < 0 || ib + l.PageSize > _source.Length)
        {
            Report(ib + l.PageSize > _source.Length && ib < _source.Length ? NdbDefectKind.PageTruncated : NdbDefectKind.PageOutOfRange, ib, $"page at 0x{ib:x} extends past the end of the {_source.Length}-byte file");
            return false;
        }

        if (_source.ReadFully(ib, page[..l.PageSize]) != l.PageSize)
        {
            Report(NdbDefectKind.PageTruncated, ib, "short read");
            return false;
        }

        check = CheckPage(ib, page[..l.PageSize], expected, expectedBid, report: true);
        return check.TypeValid;
    }

    internal NdbCheck CheckPage(long ib, ReadOnlySpan<byte> page, NdbPageType expected, ulong expectedBid, bool report)
    {
        NdbLayout l = Layout;
        int t = l.PageSize - l.PageTrailerSize;
        byte type = page[t];
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

        bool typeOk = type == (byte)expected;
        if (!typeOk && report)
        {
            Report(NdbDefectKind.PageTypeMismatch, ib, $"page type 0x{type:x2}, expected 0x{(byte)expected:x2}");
        }

        bool repeatOk = repeat == type;
        if (!repeatOk && report)
        {
            Report(NdbDefectKind.PageTypeRepeatMismatch, ib, $"ptypeRepeat 0x{repeat:x2} differs from ptype 0x{type:x2}");
        }

        // MS-PST 2.2.2.7.1: AMap, PMap, FMap and FPMap pages carry signature 0 and use their own offset as BID.
        bool allocation = type is >= 0x82 and <= 0x85;
        bool sigOk = allocation ? sig == 0 : sig == NdbChecksums.Signature((ulong)ib, bid);
        if (!sigOk && report)
        {
            Report(NdbDefectKind.PageSignatureMismatch, ib, $"wSig 0x{sig:x4}");
        }

        // The CRC covers everything before the trailer. ANSI allocation pages begin with four bytes of padding that the
        // writers leave out of the checksum; both forms are accepted for them.
        bool crcOk;
        if (l.IsAnsi && allocation)
        {
            crcOk = NdbChecksums.Crc32(page.Slice(l.AllocBitsOffset, l.AllocBitsLength)) == crc || NdbChecksums.Crc32(page[..t]) == crc;
        }
        else
        {
            crcOk = NdbChecksums.Crc32(page[..t]) == crc;
        }

        if (!crcOk && report)
        {
            Report(NdbDefectKind.PageCrcMismatch, ib, "dwCRC does not match the page");
        }

        ulong want = allocation ? (ulong)ib : expectedBid;
        bool bidOk = bid == want;
        if (!bidOk && report)
        {
            Report(NdbDefectKind.PageBidMismatch, ib, $"trailer BID 0x{bid:x}, expected 0x{want:x}");
        }

        return new NdbCheck(typeOk && repeatOk, sigOk, crcOk, bidOk);
    }

    // ---- blocks ----

    /// <summary>
    /// Reads one block's data, checks its trailer, and decodes it if the file is encoded (internal blocks are never encoded).
    /// </summary>
    /// <param name="entry">The block's BBT entry.</param>
    /// <param name="destination">Receives the data. Must hold at least <see cref="MaxBlockDataSize"/> bytes.</param>
    /// <param name="length">The number of data bytes written.</param>
    /// <param name="check">Which trailer checks passed. Failed checks are also recorded as defects; the data is still returned.</param>
    /// <returns>False when the block cannot be read at all (outside the file, impossible size, unreadable compressed data).</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is smaller than <see cref="MaxBlockDataSize"/>.</exception>
    public bool TryReadBlock(in BlockEntry entry, Span<byte> destination, out int length, out NdbCheck check)
    {
        if (destination.Length < Layout.MaxBlockData)
        {
            throw new ArgumentException($"destination must hold at least {Layout.MaxBlockData} bytes", nameof(destination));
        }

        length = 0;
        check = default;
        NdbLayout l = Layout;
        long ib = (long)entry.Block.Ib;
        if (entry.Size < 0 || entry.Size > l.MaxBlockData)
        {
            Report(NdbDefectKind.BlockTooLarge, ib, $"block {entry.Bid} claims {entry.Size} data bytes; the most a block holds is {l.MaxBlockData}");
            return false;
        }

        int total = l.BlockTotal(entry.Size);
        if (ib < 0 || ib + total > _source.Length)
        {
            Report(ib < _source.Length ? NdbDefectKind.BlockTruncated : NdbDefectKind.BlockOutOfRange, ib, $"block {entry.Bid} at 0x{ib:x} ({total} bytes) extends past the end of the file");
            return false;
        }

        byte[] raw = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            if (_source.ReadFully(ib, raw.AsSpan(0, total)) != total)
            {
                Report(NdbDefectKind.BlockTruncated, ib, "short read");
                return false;
            }

            ReadOnlySpan<byte> block = raw.AsSpan(0, total);
            ReadOnlySpan<byte> tr = block[(total - l.BlockTrailerSize)..];
            int cb = BinaryPrimitives.ReadUInt16LittleEndian(tr);
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

            bool sizeOk = cb == entry.Size;
            if (!sizeOk)
            {
                Report(NdbDefectKind.BlockSizeMismatch, ib, $"block trailer says {cb} bytes, BBT says {entry.Size}");
            }

            bool sigOk = sig == NdbChecksums.Signature((ulong)ib, bid);
            if (!sigOk)
            {
                Report(NdbDefectKind.BlockSignatureMismatch, ib, $"wSig 0x{sig:x4}");
            }

            // MS-PST 2.2.2.8.2: the CRC covers cb data bytes, not the padding before the trailer.
            ReadOnlySpan<byte> data = block[..entry.Size];
            bool crcOk = NdbChecksums.Crc32(data) == crc;
            if (!crcOk)
            {
                Report(NdbDefectKind.BlockCrcMismatch, ib, "dwCRC does not match the block data");
            }

            bool bidOk = bid == entry.Bid.Value;
            if (!bidOk)
            {
                Report(NdbDefectKind.BlockBidMismatch, ib, $"trailer BID 0x{bid:x}, BBT BID {entry.Bid}");
            }

            check = new NdbCheck(sizeOk, sigOk, crcOk, bidOk);

            int uncompressed = 0;
            if (l.Format == NdbFormat.Unicode4K)
            {
                uncompressed = BinaryPrimitives.ReadUInt16LittleEndian(tr[18..]);
            }

            bool compressed = IsCompressed(entry, uncompressed);
            if (compressed)
            {
                if (!TryInflate(data, destination, uncompressed, ib, out length))
                {
                    return false;
                }
            }
            else
            {
                data.CopyTo(destination);
                length = entry.Size;
            }

            if (!entry.Bid.IsInternal)
            {
                Decode(destination[..length], entry.Bid, ib, compressed);
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    // 4 KiB files store the uncompressed size in the block trailer; a block is compressed when that size is present and
    // differs from the stored size. Internal blocks (XBLOCK, SLBLOCK...) are never compressed.
    private static bool IsCompressed(in BlockEntry entry, int uncompressed) =>
        !entry.Bid.IsInternal && uncompressed != 0 && uncompressed != entry.Size;

    private bool TryInflate(ReadOnlySpan<byte> data, Span<byte> destination, int expected, long ib, out int length)
    {
        length = 0;
        if (expected <= 0 || expected > Layout.MaxBlockData)
        {
            Report(NdbDefectKind.BlockDecompressionFailed, ib, $"uncompressed size {expected} is out of range");
            return false;
        }

        try
        {
            // The deflate stream may or may not carry a zlib header; a zlib header starts with 0x78.
            bool zlib = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using Stream inflater = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
            int total = 0;
            while (total < expected)
            {
                int n = inflater.Read(destination.Slice(total, expected - total));
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            // Output beyond the stated size would be a decompression bomb; the size is the cap, so anything more is ignored.
            if (total != expected)
            {
                Report(NdbDefectKind.BlockDecompressionFailed, ib, $"inflated to {total} bytes, expected {expected}");
                return false;
            }

            length = total;
            return true;
        }
        catch (InvalidDataException ex)
        {
            Report(NdbDefectKind.BlockDecompressionFailed, ib, ex.Message);
            return false;
        }
    }

    private void Decode(Span<byte> data, Bid bid, long ib, bool wasCompressed)
    {
        switch (Header.Encryption)
        {
            case NdbEncryption.Permute:
                if (wasCompressed)
                {
                    Report(NdbDefectKind.AmbiguousCompressedEncodedBlock, ib, "compressed block in an encoded file");
                }

                NdbChecksums.Permute(data, encode: false);
                break;
            case NdbEncryption.Cyclic:
                if (wasCompressed)
                {
                    Report(NdbDefectKind.AmbiguousCompressedEncodedBlock, ib, "compressed block in an encoded file");
                }

                // MS-PST 5.2: the key is the low 32 bits of the block's BID.
                NdbChecksums.Cyclic(data, (uint)bid.Value);
                break;
        }
    }

    /// <summary>Looks a BID up in the block B-tree and reads the block.</summary>
    /// <param name="bid">The block's BID (the reserved bit is ignored).</param>
    /// <param name="destination">Receives the data; at least <see cref="MaxBlockDataSize"/> bytes.</param>
    /// <param name="length">The number of data bytes written.</param>
    /// <returns>False when the BID is not in the BBT (<see cref="NdbDefectKind.BlockNotFound"/>) or the block cannot be read.</returns>
    public bool TryReadBlock(Bid bid, Span<byte> destination, out int length)
    {
        length = 0;
        if (!_blocks.TryGet(bid, out BlockEntry entry))
        {
            Report(NdbDefectKind.BlockNotFound, 0, $"block {bid} is not in the block B-tree");
            return false;
        }

        return TryReadBlock(entry, destination, out length, out _);
    }

    /// <summary>Releases the underlying source.</summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _source.Dispose();
        }
    }

    internal int ReadRaw(long offset, Span<byte> destination) => _source.ReadFully(offset, destination);
}
