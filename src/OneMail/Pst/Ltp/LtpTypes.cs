using System;
using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// A heap ID (MS-PST 2.3.1.1): the address of one allocation inside a heap-on-node. Bits 0-4 are the type and must be zero
/// (NID_TYPE_HID), bits 5-15 are the 1-based allocation index within a block, bits 16-31 are the block index.
/// </summary>
/// <param name="Value">The raw 32-bit value.</param>
public readonly record struct Hid(uint Value)
{
    /// <summary>The five type bits; zero for a well-formed HID.</summary>
    public int Type => (int)(Value & 0x1F);

    /// <summary>The 1-based index of the allocation in its block's page map (11 bits). Zero is invalid.</summary>
    public int Index => (int)((Value >> 5) & 0x7FF);

    /// <summary>The zero-based index of the heap block (the data block of the node, in stream order).</summary>
    public int BlockIndex => (int)(Value >> 16);

    /// <summary>True for the all-zero HID, which references nothing.</summary>
    public bool IsNull => Value == 0;

    /// <inheritdoc/>
    public override string ToString() => "0x" + Value.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A heap-or-node ID (MS-PST 2.3.1.1 HNID): a <see cref="Hid"/> when the low five bits are zero, otherwise the NID of a
/// subnode of the node that holds the heap.
/// </summary>
/// <param name="Value">The raw 32-bit value.</param>
public readonly record struct Hnid(uint Value)
{
    /// <summary>True when the value addresses a heap allocation.</summary>
    public bool IsHid => (Value & 0x1F) == 0;

    /// <summary>The value read as a heap ID.</summary>
    public Hid Hid => new(Value);

    /// <summary>The value read as a subnode NID.</summary>
    public Nid Nid => new(Value);

    /// <inheritdoc/>
    public override string ToString() => "0x" + Value.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Where the bytes of a property or cell value live.</summary>
public enum LtpValueKind : byte
{
    /// <summary>No value bytes (a zero HNID): an empty string or binary.</summary>
    Empty = 0,
    /// <summary>The value is stored in the reference itself (at most eight bytes).</summary>
    Inline = 1,
    /// <summary>The value is one allocation of the node's heap.</summary>
    Heap = 2,
    /// <summary>The value is the data of a subnode of the node.</summary>
    Subnode = 3,
}

/// <summary>
/// A lazy reference to the bytes of one property or table cell. Holding it reads nothing; hand it to
/// <see cref="HeapNode.ReadValue(in LtpValueRef, long, Span{byte})"/> (or the same method on <see cref="PropertyContext"/> and <see cref="TableContext"/>).
/// </summary>
/// <param name="Kind">Where the bytes are.</param>
/// <param name="Hnid">The HNID for <see cref="LtpValueKind.Heap"/> and <see cref="LtpValueKind.Subnode"/>; otherwise zero.</param>
/// <param name="InlineValue">The value bytes as a little-endian integer, for <see cref="LtpValueKind.Inline"/>.</param>
/// <param name="InlineSize">The number of bytes in <paramref name="InlineValue"/> that belong to the value (1 to 8).</param>
public readonly record struct LtpValueRef(LtpValueKind Kind, uint Hnid, ulong InlineValue, int InlineSize)
{
    /// <summary>The empty reference.</summary>
    public static LtpValueRef Empty => default;

    internal static LtpValueRef FromInline(ulong value, int size) => new(LtpValueKind.Inline, 0, value, size);

    internal static LtpValueRef FromHnid(uint hnid) =>
        hnid == 0 ? default : new LtpValueRef((hnid & 0x1F) == 0 ? LtpValueKind.Heap : LtpValueKind.Subnode, hnid, 0, 0);
}

/// <summary>
/// Property type codes (MS-OXCDATA 2.11.1) and the layout facts the LTP layer needs about them. The layer gives values no
/// meaning beyond this: whether they are stored inline, and how multi-valued data is laid out.
/// </summary>
public static class LtpPropertyTypes
{
    /// <summary>PtypInteger16.</summary>
    public const ushort PtypInteger16 = 0x0002;
    /// <summary>PtypInteger32.</summary>
    public const ushort PtypInteger32 = 0x0003;
    /// <summary>PtypFloating32.</summary>
    public const ushort PtypFloating32 = 0x0004;
    /// <summary>PtypFloating64.</summary>
    public const ushort PtypFloating64 = 0x0005;
    /// <summary>PtypCurrency.</summary>
    public const ushort PtypCurrency = 0x0006;
    /// <summary>PtypFloatingTime.</summary>
    public const ushort PtypFloatingTime = 0x0007;
    /// <summary>PtypErrorCode.</summary>
    public const ushort PtypErrorCode = 0x000A;
    /// <summary>PtypBoolean.</summary>
    public const ushort PtypBoolean = 0x000B;
    /// <summary>PtypObject.</summary>
    public const ushort PtypObject = 0x000D;
    /// <summary>PtypInteger64.</summary>
    public const ushort PtypInteger64 = 0x0014;
    /// <summary>PtypString8.</summary>
    public const ushort PtypString8 = 0x001E;
    /// <summary>PtypString (UTF-16LE).</summary>
    public const ushort PtypString = 0x001F;
    /// <summary>PtypTime (FILETIME).</summary>
    public const ushort PtypTime = 0x0040;
    /// <summary>PtypGuid.</summary>
    public const ushort PtypGuid = 0x0048;
    /// <summary>PtypBinary.</summary>
    public const ushort PtypBinary = 0x0102;
    /// <summary>The flag that makes a type multi-valued (0x1000).</summary>
    public const ushort MultiValueFlag = 0x1000;

    /// <summary>True when the multi-value flag is set.</summary>
    /// <param name="type">A property type code.</param>
    /// <returns>True for the multi-valued types.</returns>
    public static bool IsMultiValue(ushort type) => (type & MultiValueFlag) != 0;

    /// <summary>The stored size in bytes of a fixed-size single-valued type; 0 for variable-size and unknown types.</summary>
    /// <param name="type">A property type code.</param>
    /// <returns>1, 2, 4, 8 or 16, or 0.</returns>
    public static int FixedSize(ushort type) => type switch
    {
        PtypInteger16 => 2,
        PtypInteger32 or PtypFloating32 or PtypErrorCode => 4,
        PtypFloating64 or PtypCurrency or PtypFloatingTime or PtypInteger64 or PtypTime => 8,
        PtypBoolean => 1,
        PtypGuid => 16,
        _ => 0,
    };

    /// <summary>
    /// The element size of a multi-valued type whose elements are fixed-size (the elements are stored back to back); 0 when
    /// the elements are variable-size (PtypString8, PtypString, PtypBinary) or the type is not multi-valued.
    /// </summary>
    /// <param name="type">A multi-valued property type code.</param>
    /// <returns>The element size or 0.</returns>
    public static int FixedElementSize(ushort type) => IsMultiValue(type) ? FixedSize((ushort)(type & ~MultiValueFlag)) : 0;
}

/// <summary>What the LTP reader found wrong. Every repair or refusal is one of these.</summary>
public enum LtpDefectKind
{
    // Nodes
    /// <summary>The node is not in the node B-tree.</summary>
    NodeNotFound,
    /// <summary>The node's data cannot be opened, or has no data blocks.</summary>
    HeapUnreadable,

    // Heap-on-node (MS-PST 2.3.1)
    /// <summary>The first heap block is too short for an HNHDR.</summary>
    HeapBadHeader,
    /// <summary>bSig of the HNHDR is not 0xEC.</summary>
    HeapBadSignature,
    /// <summary>bClientSig is not the one the context type requires.</summary>
    HeapClientSignatureMismatch,
    /// <summary>A block's HNPAGEMAP does not fit in the block, or its allocation offsets are out of order or out of range.</summary>
    HeapPageMapInvalid,
    /// <summary>A HID names a block the heap does not have.</summary>
    HeapBlockMissing,
    /// <summary>A HID is malformed (type bits not zero, index zero or past the page map).</summary>
    HidInvalid,

    // Subnodes
    /// <summary>An HNID names a subnode that is not in the node's subnode tree.</summary>
    SubnodeMissing,
    /// <summary>The node's subnode tree cannot be read.</summary>
    SubnodesUnreadable,
    /// <summary>A value's data cannot be read (subnode data tree missing or damaged).</summary>
    ValueUnreadable,

    // BTH (MS-PST 2.3.2)
    /// <summary>The BTHHEADER is too short, has the wrong bType, or describes key or entry sizes the format does not allow.</summary>
    BthBadHeader,
    /// <summary>bIdxLevels is above the configured limit.</summary>
    BthTooDeep,
    /// <summary>A BTH node's size is not a whole number of records; the remainder is ignored.</summary>
    BthNodeSize,
    /// <summary>Keys are not strictly increasing (a node referenced twice, or records swapped); the record is skipped.</summary>
    BthKeyOrder,
    /// <summary>Walking the BTH visited more nodes than the heap can hold; it was cut off.</summary>
    BthTooManyNodes,

    // PC (MS-PST 2.3.3)
    /// <summary>The BTH under a property context does not have 2-byte keys and 6-byte entries.</summary>
    PcBadShape,

    // TC (MS-PST 2.3.4)
    /// <summary>The TCINFO is too short, has the wrong bType, or its column table does not fit.</summary>
    TcBadInfo,
    /// <summary>The group end offsets are not in order, the row is too small for its cell-existence bitmap, or a column lies outside the row.</summary>
    TcBadLayout,
    /// <summary>The row index BTH does not have 4-byte keys and 4-byte entries.</summary>
    TcRowIndexShape,
    /// <summary>The row matrix cannot be found, or a row lies outside it.</summary>
    TcRowOutOfRange,
    /// <summary>A cell's size does not fit its column's type.</summary>
    TcCellShape,
}

/// <summary>One thing the LTP reader found wrong, and where.</summary>
/// <param name="Kind">What was wrong.</param>
/// <param name="Offset">The file offset of the heap block it was found in, or 0 when there is none.</param>
/// <param name="Detail">A short plain-language description with the values involved.</param>
public readonly record struct LtpDefect(LtpDefectKind Kind, long Offset, string Detail)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Kind} at 0x{Offset:x}: {Detail}";
}

/// <summary>Limits for reading heaps, trees and tables. Defaults are safe for hostile input.</summary>
public sealed class LtpOptions
{
    /// <summary>Most defects an open node keeps in its <c>Defects</c> list; later ones are counted. Default 1,000.</summary>
    public int MaxDefects { get; init; } = 1000;

    /// <summary>Most index levels a BTH may have. MS-PST does not state a limit; real trees have at most 3. Default 8.</summary>
    public int MaxBthLevels { get; init; } = 8;

    /// <summary>Largest value the convenience methods that return a whole value as an array will read. Default 256 MiB.</summary>
    public long MaxValueBytes { get; init; } = 256L << 20;
}

internal static class LtpBytes
{
    // MS-PST 2.3.2: keys are compared as little-endian unsigned integers, so the last byte is the most significant.
    public static int CompareKeys(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = a.Length - 1; i >= 0; i--)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    public static UInt128 KeyValue(ReadOnlySpan<byte> key)
    {
        UInt128 v = 0;
        for (int i = key.Length - 1; i >= 0; i--)
        {
            v = (v << 8) | key[i];
        }

        return v;
    }

    public static ulong ReadUInt64Partial(ReadOnlySpan<byte> bytes)
    {
        ulong v = 0;
        for (int i = Math.Min(bytes.Length, 8) - 1; i >= 0; i--)
        {
            v = (v << 8) | bytes[i];
        }

        return v;
    }

    public static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);

    public static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
}
