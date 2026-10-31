using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>The on-disk generation of a file, which fixes the width of IDs and offsets and the size of pages.</summary>
public enum NdbFormat
{
    /// <summary>ANSI: 32-bit BIDs and offsets, 512-byte pages, files up to about 2 GiB (wVer 14 or 15).</summary>
    Ansi,
    /// <summary>Unicode: 64-bit BIDs and offsets, 512-byte pages (wVer 21 or 23).</summary>
    Unicode,
    /// <summary>
    /// Unicode with 4096-byte pages and optionally compressed blocks, as written for Outlook 2013 and later offline stores
    /// (wVer 36). Not covered by MS-PST; layout from observed files.
    /// </summary>
    Unicode4K,
}

/// <summary>How data blocks are encoded (bCryptMethod, MS-PST 2.2.2.6).</summary>
public enum NdbEncryption : byte
{
    /// <summary>NDB_CRYPT_NONE: not encoded.</summary>
    None = 0,
    /// <summary>NDB_CRYPT_PERMUTE: byte substitution (obfuscation, not protection). Decoded regardless.</summary>
    Permute = 1,
    /// <summary>NDB_CRYPT_CYCLIC: keyed by the block's BID (obfuscation, not protection). Decoded regardless.</summary>
    Cyclic = 2,
    /// <summary>NDB_CRYPT_EDPCRYPTED: Windows Information Protection. Contents cannot be decoded here.</summary>
    Edp = 0x10,
}

/// <summary>The fixed-size geometry of a file generation: sizes of pages, trailers and entries, and where the fixed pages sit.</summary>
internal sealed class NdbLayout
{
    public required NdbFormat Format { get; init; }
    public required int PageSize { get; init; }
    public required int PageTrailerSize { get; init; }
    public required int BlockTrailerSize { get; init; }
    public required int BlockAlignment { get; init; }
    public required int MaxBlockData { get; init; }
    public required int IdSize { get; init; }
    /// <summary>Offset in a B-tree page of cEnt/cEntMax/cbEnt/cLevel.</summary>
    public required int BTreeFooterOffset { get; init; }
    public required int NbtLeafMin { get; init; }
    public required int BbtLeafMin { get; init; }
    public required int BTreeIndexMin { get; init; }
    public required long FirstAMap { get; init; }
    public required long AMapInterval { get; init; }
    public required long DensityList { get; init; }
    public required int AllocBitsOffset { get; init; }
    public required int AllocBitsLength { get; init; }

    public bool IsAnsi => Format == NdbFormat.Ansi;

    public static NdbLayout For(NdbFormat format) => format switch
    {
        // MS-PST 2.2.2.7: pages are 512 bytes; the trailer is 12 bytes (ANSI) or 16 (Unicode); AMaps start at 0x4400 and repeat
        // every 496 * 8 * 64 = 253,952 bytes; the density list is at 0x4200.
        NdbFormat.Ansi => new NdbLayout
        {
            Format = format, PageSize = 512, PageTrailerSize = 12, BlockTrailerSize = 12, BlockAlignment = 64, MaxBlockData = 8192 - 12,
            IdSize = 4, BTreeFooterOffset = 496, NbtLeafMin = 16, BbtLeafMin = 12, BTreeIndexMin = 12,
            FirstAMap = 0x4400, AMapInterval = 253_952, DensityList = 0x4200, AllocBitsOffset = 4, AllocBitsLength = 496,
        },
        NdbFormat.Unicode => new NdbLayout
        {
            Format = format, PageSize = 512, PageTrailerSize = 16, BlockTrailerSize = 16, BlockAlignment = 64, MaxBlockData = 8192 - 16,
            IdSize = 8, BTreeFooterOffset = 488, NbtLeafMin = 28, BbtLeafMin = 20, BTreeIndexMin = 24,
            FirstAMap = 0x4400, AMapInterval = 253_952, DensityList = 0x4200, AllocBitsOffset = 0, AllocBitsLength = 496,
        },
        // 4 KiB pages: the page trailer grows to 24 bytes (an extra 8 reserved bytes), an AMap page holds 4072 bytes of bits,
        // so it maps 4072 * 8 * 64 bytes. First AMap at 0x22000 and density list at 0x21000, from observed files.
        _ => new NdbLayout
        {
            Format = format, PageSize = 4096, PageTrailerSize = 24, BlockTrailerSize = 24, BlockAlignment = 64, MaxBlockData = 65536,
            IdSize = 8, BTreeFooterOffset = 4056, NbtLeafMin = 28, BbtLeafMin = 20, BTreeIndexMin = 24,
            FirstAMap = 0x22000, AMapInterval = 4072L * 8 * 64, DensityList = 0x21000, AllocBitsOffset = 0, AllocBitsLength = 4072,
        },
    };

    /// <summary>Rounds a block's data size plus trailer up to the block alignment: the size the block occupies on disk.</summary>
    public int BlockTotal(int cb) => ((cb + BlockTrailerSize + BlockAlignment - 1) / BlockAlignment) * BlockAlignment;
}

/// <summary>The file header (MS-PST 2.2.2.6), parsed. Fields keep their on-disk values; nothing is repaired.</summary>
public sealed class NdbHeader
{
    private const int UnicodeHeaderSize = 564;
    private const int AnsiHeaderSize = 512;

    /// <summary>The generation, from wVer (and, for 4 KiB files, confirmed by <see cref="NdbFile"/> from the page trailers).</summary>
    public NdbFormat Format { get; internal set; }

    /// <summary>wVer: 14 or 15 for ANSI, 21 or 23 for Unicode, 36 for 4 KiB pages, 37 for Windows Information Protection.</summary>
    public ushort Version { get; private set; }

    /// <summary>wVerClient.</summary>
    public ushort ClientVersion { get; private set; }

    /// <summary>wMagicClient as a little-endian word: 0x4D53 "SM" for PST, 0x4F53 "SO" for OST, 0x4241 "AB" for PAB.</summary>
    public ushort ClientMagic { get; private set; }

    /// <summary>True when the client magic marks an offline store (OST).</summary>
    public bool IsOffline => ClientMagic == 0x4F53;

    /// <summary>bPlatformCreate (should be 1).</summary>
    public byte PlatformCreate { get; private set; }

    /// <summary>bPlatformAccess (should be 1).</summary>
    public byte PlatformAccess { get; private set; }

    /// <summary>bidNextB: the counter for the next block BID.</summary>
    public ulong NextBlockBid { get; private set; }

    /// <summary>bidNextP: the counter for the next page BID.</summary>
    public ulong NextPageBid { get; private set; }

    /// <summary>dwUnique: changes with every header update.</summary>
    public uint Unique { get; private set; }

    /// <summary>rgnid: the last nidIndex allocated for each of the 32 NID types.</summary>
    public IReadOnlyList<uint> NodeCounters { get; private set; } = [];

    /// <summary>The ROOT record.</summary>
    public NdbRoot Root { get; private set; }

    /// <summary>bSentinel (should be 0x80).</summary>
    public byte Sentinel { get; private set; }

    /// <summary>bCryptMethod as stored.</summary>
    public byte CryptMethod { get; private set; }

    /// <summary>bCryptMethod as an enumeration. Values the format does not define are cast through unchanged.</summary>
    public NdbEncryption Encryption => (NdbEncryption)CryptMethod;

    /// <summary>True when dwCRCPartial matches the 471 bytes it covers.</summary>
    public bool PartialCrcValid { get; private set; }

    /// <summary>True when dwCRCFull matches the 516 bytes it covers; always false for ANSI files, which have none.</summary>
    public bool FullCrcValid { get; private set; }

    internal int Size => Format == NdbFormat.Ansi ? AnsiHeaderSize : UnicodeHeaderSize;

    /// <summary>Parses a header from the first bytes of a file. Does not throw on malformed content.</summary>
    /// <param name="data">At least the first 564 bytes of the file (512 suffice for ANSI files).</param>
    /// <param name="header">The header, when the magic and version are recognised.</param>
    /// <param name="defects">Receives a defect for everything wrong with the header.</param>
    /// <returns>True when the header is usable for reading (format known). Checksum and flag defects do not make it unusable.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out NdbHeader? header, ICollection<NdbDefect> defects)
    {
        header = null;
        if (data.Length < AnsiHeaderSize)
        {
            defects.Add(new NdbDefect(NdbDefectKind.FileTooShort, 0, $"{data.Length} bytes is shorter than the {AnsiHeaderSize}-byte minimum header"));
            return false;
        }

        if (data[0] != 0x21 || data[1] != 0x42 || data[2] != 0x44 || data[3] != 0x4E)
        {
            defects.Add(new NdbDefect(NdbDefectKind.BadMagic, 0, "the file does not start with \"!BDN\""));
            return false;
        }

        ushort ver = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        NdbFormat format;
        if (ver is 14 or 15)
        {
            format = NdbFormat.Ansi;
        }
        else if (ver >= 21)
        {
            format = ver == 36 ? NdbFormat.Unicode4K : NdbFormat.Unicode;
        }
        else
        {
            defects.Add(new NdbDefect(NdbDefectKind.UnsupportedVersion, 10, $"wVer {ver} is not a known PST/OST version"));
            return false;
        }

        if (format != NdbFormat.Ansi && data.Length < UnicodeHeaderSize)
        {
            defects.Add(new NdbDefect(NdbDefectKind.FileTooShort, 0, $"{data.Length} bytes is shorter than a {UnicodeHeaderSize}-byte Unicode header"));
            return false;
        }

        var h = new NdbHeader
        {
            Format = format,
            Version = ver,
            ClientMagic = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            ClientVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            PlatformCreate = data[14],
            PlatformAccess = data[15],
        };

        if (h.ClientMagic is not (0x4D53 or 0x4F53 or 0x4241))
        {
            defects.Add(new NdbDefect(NdbDefectKind.UnknownClientMagic, 8, $"wMagicClient 0x{h.ClientMagic:x4} is not SM, SO or AB"));
        }

        // dwCRCPartial covers the 471 bytes from wMagicClient (offset 8) in every generation.
        uint storedPartial = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        h.PartialCrcValid = NdbChecksums.Crc32(data.Slice(8, 471)) == storedPartial;
        if (!h.PartialCrcValid)
        {
            defects.Add(new NdbDefect(NdbDefectKind.HeaderPartialCrcMismatch, 4, "dwCRCPartial does not match bytes 8..478"));
        }

        var counters = new uint[32];
        if (format == NdbFormat.Ansi)
        {
            // ANSI layout (MS-PST 2.2.2.6): bidNextB, bidNextP, dwUnique, rgnid, ROOT (40 bytes), rgbFM, rgbFP, bSentinel, bCryptMethod.
            h.NextBlockBid = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
            h.NextPageBid = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
            h.Unique = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
            for (int i = 0; i < 32; i++)
            {
                counters[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(36 + (i * 4))..]);
            }

            ReadOnlySpan<byte> r = data[164..];
            h.Root = new NdbRoot(
                FileEof: BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
                AMapLast: BinaryPrimitives.ReadUInt32LittleEndian(r[8..]),
                AMapFree: BinaryPrimitives.ReadUInt32LittleEndian(r[12..]),
                PMapFree: BinaryPrimitives.ReadUInt32LittleEndian(r[16..]),
                NodeBTree: new Bref(new Bid(BinaryPrimitives.ReadUInt32LittleEndian(r[20..])), BinaryPrimitives.ReadUInt32LittleEndian(r[24..])),
                BlockBTree: new Bref(new Bid(BinaryPrimitives.ReadUInt32LittleEndian(r[28..])), BinaryPrimitives.ReadUInt32LittleEndian(r[32..])),
                AMapValid: r[36]);
            h.Sentinel = data[460];
            h.CryptMethod = data[461];
        }
        else
        {
            // Unicode layout: bidUnused(8), bidNextP(8), dwUnique, rgnid, qwUnused, ROOT (72 bytes), dwAlign, rgbFM, rgbFP,
            // bSentinel, bCryptMethod, rgbReserved, bidNextB, dwCRCFull.
            h.NextPageBid = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
            h.Unique = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);
            for (int i = 0; i < 32; i++)
            {
                counters[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(44 + (i * 4))..]);
            }

            ReadOnlySpan<byte> r = data[180..];
            h.Root = new NdbRoot(
                FileEof: BinaryPrimitives.ReadUInt64LittleEndian(r[4..]),
                AMapLast: BinaryPrimitives.ReadUInt64LittleEndian(r[12..]),
                AMapFree: BinaryPrimitives.ReadUInt64LittleEndian(r[20..]),
                PMapFree: BinaryPrimitives.ReadUInt64LittleEndian(r[28..]),
                NodeBTree: new Bref(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(r[36..])), BinaryPrimitives.ReadUInt64LittleEndian(r[44..])),
                BlockBTree: new Bref(new Bid(BinaryPrimitives.ReadUInt64LittleEndian(r[52..])), BinaryPrimitives.ReadUInt64LittleEndian(r[60..])),
                AMapValid: r[68]);
            h.Sentinel = data[512];
            h.CryptMethod = data[513];
            h.NextBlockBid = BinaryPrimitives.ReadUInt64LittleEndian(data[516..]);

            // dwCRCFull covers the 516 bytes from wMagicClient to the end of bidNextB.
            uint storedFull = BinaryPrimitives.ReadUInt32LittleEndian(data[524..]);
            h.FullCrcValid = NdbChecksums.Crc32(data.Slice(8, 516)) == storedFull;
            if (!h.FullCrcValid)
            {
                defects.Add(new NdbDefect(NdbDefectKind.HeaderFullCrcMismatch, 524, "dwCRCFull does not match bytes 8..523"));
            }
        }

        h.NodeCounters = counters;

        if (h.Sentinel != 0x80)
        {
            defects.Add(new NdbDefect(NdbDefectKind.BadSentinel, format == NdbFormat.Ansi ? 460 : 512, $"bSentinel is 0x{h.Sentinel:x2}, not 0x80"));
        }

        if (h.CryptMethod is not (0 or 1 or 2 or 0x10))
        {
            defects.Add(new NdbDefect(NdbDefectKind.UnknownEncryption, format == NdbFormat.Ansi ? 461 : 513, $"bCryptMethod 0x{h.CryptMethod:x2} is not defined; blocks are read as if not encoded"));
        }
        else if (h.CryptMethod == 0x10)
        {
            defects.Add(new NdbDefect(NdbDefectKind.ProtectedContent, format == NdbFormat.Ansi ? 461 : 513, "blocks are protected with Windows Information Protection and cannot be decoded"));
        }

        if (h.Root.AMapValid is not (1 or 2))
        {
            defects.Add(new NdbDefect(NdbDefectKind.AMapFlagInvalid, format == NdbFormat.Ansi ? 200 : 248, $"fAMapValid is {h.Root.AMapValid}: the allocation maps are not trusted"));
        }

        header = h;
        return true;
    }
}
