using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Msg;

/// <summary>What is irregular about a compound file header found by <see cref="CfbSpan.TryParseHeader"/> (MS-CFB 2.2).</summary>
[Flags]
public enum CfbHeaderDefects
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The header CLSID is not all zero.</summary>
    ClassIdNotZero = 1 << 0,

    /// <summary>The byte order mark is not 0xFFFE.</summary>
    BadByteOrder = 1 << 1,

    /// <summary>The sector shift is not the one the major version requires (9 for version 3, 12 for version 4).</summary>
    SectorShiftVersionMismatch = 1 << 2,

    /// <summary>The mini sector shift is not 6.</summary>
    BadMiniSectorShift = 1 << 3,

    /// <summary>The mini stream cutoff is not 0x1000.</summary>
    BadMiniStreamCutoff = 1 << 4,

    /// <summary>Version 3 with a non-zero directory sector count, or a reserved field that is not zero.</summary>
    ReservedNotZero = 1 << 5,

    /// <summary>More FAT sectors are declared than a header and its DIFAT chain could hold in the file's span, or the DIFAT count is inconsistent.</summary>
    BadSectorCounts = 1 << 6,
}

/// <summary>A compound file header (MS-CFB 2.2), as found.</summary>
/// <param name="MajorVersion">3 (512-byte sectors) or 4 (4096-byte sectors).</param>
/// <param name="MinorVersion">Usually 0x3E.</param>
/// <param name="SectorSize">1 shifted left by the sector shift.</param>
/// <param name="MiniSectorSize">1 shifted left by the mini sector shift.</param>
/// <param name="DirectorySectorCount">Number of directory sectors (0 for version 3).</param>
/// <param name="FatSectorCount">Number of FAT sectors.</param>
/// <param name="FirstDirectorySector">Sector number of the first directory sector.</param>
/// <param name="MiniStreamCutoff">Streams smaller than this live in the mini stream; 0x1000 in valid files.</param>
/// <param name="FirstMiniFatSector">First mini FAT sector, or 0xFFFFFFFE for none.</param>
/// <param name="MiniFatSectorCount">Number of mini FAT sectors.</param>
/// <param name="FirstDifatSector">First DIFAT sector, or 0xFFFFFFFE for none.</param>
/// <param name="DifatSectorCount">Number of DIFAT sectors.</param>
/// <param name="Defects">What was irregular.</param>
public readonly record struct CfbHeaderInfo(
    int MajorVersion, int MinorVersion, int SectorSize, int MiniSectorSize, uint DirectorySectorCount, uint FatSectorCount, uint FirstDirectorySector,
    uint MiniStreamCutoff, uint FirstMiniFatSector, uint MiniFatSectorCount, uint FirstDifatSector, uint DifatSectorCount, CfbHeaderDefects Defects);

/// <summary>A compound file directory entry (MS-CFB 2.6), as found.</summary>
/// <param name="Name">The entry name decoded from UTF-16; empty for a free entry.</param>
/// <param name="ObjectType">0 unallocated, 1 storage, 2 stream, 5 root storage.</param>
/// <param name="ColorFlag">0 red, 1 black.</param>
/// <param name="LeftSibling">Directory id of the left sibling, or 0xFFFFFFFF.</param>
/// <param name="RightSibling">Directory id of the right sibling, or 0xFFFFFFFF.</param>
/// <param name="Child">Directory id of the child, or 0xFFFFFFFF.</param>
/// <param name="ClassId">The entry's CLSID.</param>
/// <param name="StartSector">First sector of the stream (or of the mini stream for the root).</param>
/// <param name="Size">Stream size in bytes.</param>
public readonly record struct CfbDirectoryEntryInfo(
    string Name, byte ObjectType, byte ColorFlag, uint LeftSibling, uint RightSibling, uint Child, Guid ClassId, uint StartSector, ulong Size);

/// <summary>
/// Span-level recognition of compound file (CFB) structures, for tools that look for MSG files and other CFB containers inside larger data. Only
/// what a carver needs to say "this is a header" or "this is a directory sector": reading a file is OneArchive's reader, used by the MSG layer.
/// </summary>
/// <remarks>MS-CFB 2.2 (header), 2.6 (directory entries). Nothing throws and the header parse allocates nothing.</remarks>
public static class CfbSpan
{
    /// <summary>The compound file signature, MS-CFB 2.2.</summary>
    public static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Size of the header: 512 bytes in both versions (version 4's header sector is 4096 bytes with the rest zero).</summary>
    public const int HeaderSize = 512;

    /// <summary>Size of one directory entry.</summary>
    public const int DirectoryEntrySize = 128;

    /// <summary>Recognises a compound file header: the signature, a major version of 3 or 4 and a sector shift in the range the format allows.</summary>
    /// <param name="data">At least 512 bytes from the header's first byte.</param>
    /// <param name="info">The header fields and what was irregular.</param>
    /// <returns>False when the signature is wrong, the span is short, or the version or sector shift is outside the format. Other irregularities are in <c>info.Defects</c> and the result is still true.</returns>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out CfbHeaderInfo info)
    {
        info = default;
        if (data.Length < HeaderSize || !data.StartsWith(Signature))
        {
            return false;
        }

        int minor = BinaryPrimitives.ReadUInt16LittleEndian(data[0x18..]);
        int major = BinaryPrimitives.ReadUInt16LittleEndian(data[0x1A..]);
        int byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(data[0x1C..]);
        int sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(data[0x1E..]);
        int miniShift = BinaryPrimitives.ReadUInt16LittleEndian(data[0x20..]);
        if (major is not (3 or 4) || sectorShift is < 7 or > 16)
        {
            return false;
        }

        CfbHeaderDefects d = CfbHeaderDefects.None;
        if (data.Slice(8, 16).ContainsAnyExcept((byte)0))
        {
            d |= CfbHeaderDefects.ClassIdNotZero;
        }

        if (byteOrder != 0xFFFE)
        {
            d |= CfbHeaderDefects.BadByteOrder;
        }

        if (sectorShift != (major == 3 ? 9 : 12))
        {
            d |= CfbHeaderDefects.SectorShiftVersionMismatch;
        }

        if (miniShift != 6)
        {
            d |= CfbHeaderDefects.BadMiniSectorShift;
        }

        uint dirSectors = BinaryPrimitives.ReadUInt32LittleEndian(data[0x28..]);
        uint fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(data[0x2C..]);
        uint cutoff = BinaryPrimitives.ReadUInt32LittleEndian(data[0x38..]);
        uint difatSectors = BinaryPrimitives.ReadUInt32LittleEndian(data[0x48..]);
        if (cutoff != 0x1000)
        {
            d |= CfbHeaderDefects.BadMiniStreamCutoff;
        }

        if ((major == 3 && dirSectors != 0) || data.Slice(0x22, 6).ContainsAnyExcept((byte)0))
        {
            d |= CfbHeaderDefects.ReservedNotZero;
        }

        // A header holds 109 FAT sector numbers; more FAT sectors need DIFAT sectors to list them.
        if (fatSectors > 109 && difatSectors == 0)
        {
            d |= CfbHeaderDefects.BadSectorCounts;
        }

        info = new CfbHeaderInfo(
            major, minor, 1 << sectorShift, 1 << Math.Min(miniShift, 30), dirSectors, fatSectors,
            BinaryPrimitives.ReadUInt32LittleEndian(data[0x30..]), cutoff, BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[0x40..]), BinaryPrimitives.ReadUInt32LittleEndian(data[0x44..]), difatSectors, d);
        return true;
    }

    /// <summary>Recognises one 128-byte directory entry: a name length that fits and is even, a known object type, a name that is a terminated UTF-16 string, and a root or stream size that fits.</summary>
    /// <param name="data">At least 128 bytes from the entry's first byte.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>False for anything that is not an entry. A free entry (type 0 with an empty name) is true and has an empty name.</returns>
    public static bool TryParseDirectoryEntry(ReadOnlySpan<byte> data, out CfbDirectoryEntryInfo entry)
    {
        entry = default;
        if (data.Length < DirectoryEntrySize)
        {
            return false;
        }

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[0x40..]);
        byte type = data[0x42];
        byte color = data[0x43];
        if (nameLength > 64 || (nameLength & 1) != 0 || type is not (0 or 1 or 2 or 5) || color > 1)
        {
            return false;
        }

        // MS-CFB 2.6.1: the name length counts the terminating null; a free entry has length 0.
        string name = string.Empty;
        if (nameLength >= 2)
        {
            ReadOnlySpan<byte> raw = data[..(nameLength - 2)];
            if (data[nameLength - 2] != 0 || data[nameLength - 1] != 0)
            {
                return false;
            }

            name = System.Text.Encoding.Unicode.GetString(raw);
        }
        else if (type != 0)
        {
            return false;
        }

        entry = new CfbDirectoryEntryInfo(
            name, type, color, BinaryPrimitives.ReadUInt32LittleEndian(data[0x44..]), BinaryPrimitives.ReadUInt32LittleEndian(data[0x48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[0x4C..]), new Guid(data.Slice(0x50, 16)), BinaryPrimitives.ReadUInt32LittleEndian(data[0x74..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[0x78..]));
        return true;
    }
}
