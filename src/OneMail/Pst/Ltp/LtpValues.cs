using System;
using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// Reads the fixed-size scalar types out of value bytes, and splits multi-valued data into its elements (MS-PST 2.3.3.4).
/// Nothing here interprets strings, binary blobs or any structured type: those stay raw bytes for the layers above.
/// </summary>
public static class LtpValues
{
    /// <summary>Reads a PtypInteger16.</summary>
    /// <param name="bytes">The value bytes (two or more).</param>
    /// <param name="value">The value.</param>
    /// <returns>False when there are too few bytes.</returns>
    public static bool TryGetInt16(ReadOnlySpan<byte> bytes, out short value)
    {
        value = 0;
        if (bytes.Length < 2)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt16LittleEndian(bytes);
        return true;
    }

    /// <summary>Reads a PtypInteger32 or PtypErrorCode.</summary>
    /// <param name="bytes">The value bytes (four or more).</param>
    /// <param name="value">The value.</param>
    /// <returns>False when there are too few bytes.</returns>
    public static bool TryGetInt32(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        if (bytes.Length < 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    /// <summary>Reads a PtypInteger64 or PtypCurrency (a 64-bit integer scaled by 10,000).</summary>
    /// <param name="bytes">The value bytes (eight or more).</param>
    /// <param name="value">The value.</param>
    /// <returns>False when there are too few bytes.</returns>
    public static bool TryGetInt64(ReadOnlySpan<byte> bytes, out long value)
    {
        value = 0;
        if (bytes.Length < 8)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        return true;
    }

    /// <summary>Reads a PtypBoolean. MS-OXCDATA says any non-zero byte is true.</summary>
    /// <param name="bytes">The value bytes (one or more).</param>
    /// <param name="value">The value.</param>
    /// <returns>False when there are no bytes.</returns>
    public static bool TryGetBoolean(ReadOnlySpan<byte> bytes, out bool value)
    {
        value = false;
        if (bytes.Length < 1)
        {
            return false;
        }

        value = bytes[0] != 0;
        return true;
    }

    /// <summary>Reads a PtypFloating32.</summary>
    /// <param name="bytes">The value bytes (four or more).</param>
    /// <param name="value">The value.</param>
    /// <returns>False when there are too few bytes.</returns>
    public static bool TryGetSingle(ReadOnlySpan<byte> bytes, out float value)
    {
        value = 0;
        if (bytes.Length < 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        return true;
    }

    /// <summary>Reads a PtypFloating64 or PtypFloatingTime (days since 1899-12-30).</summary>
    /// <param name="bytes">The value bytes (eight or more).</param>
    /// <param name="value">The value.</param>
    /// <returns>False when there are too few bytes.</returns>
    public static bool TryGetDouble(ReadOnlySpan<byte> bytes, out double value)
    {
        value = 0;
        if (bytes.Length < 8)
        {
            return false;
        }

        value = BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        return true;
    }

    /// <summary>Reads a PtypTime as the raw FILETIME: 100-nanosecond ticks since 1601-01-01 UTC. No range is imposed.</summary>
    /// <param name="bytes">The value bytes (eight or more).</param>
    /// <param name="fileTime">The tick count.</param>
    /// <returns>False when there are too few bytes.</returns>
    public static bool TryGetFileTime(ReadOnlySpan<byte> bytes, out long fileTime) => TryGetInt64(bytes, out fileTime);

    /// <summary>Reads a PtypGuid (the first three fields little-endian, as in the Windows GUID layout).</summary>
    /// <param name="bytes">The value bytes (sixteen or more).</param>
    /// <param name="value">The value.</param>
    /// <returns>False when there are too few bytes.</returns>
    public static bool TryGetGuid(ReadOnlySpan<byte> bytes, out Guid value)
    {
        value = default;
        if (bytes.Length < 16)
        {
            return false;
        }

        value = new Guid(bytes[..16]);
        return true;
    }

    /// <summary>
    /// Counts the elements of a multi-valued value. Fixed-size element types are stored back to back; String8, String and
    /// Binary elements are a 4-byte count, one 4-byte offset per element (from the start of the value), then the data.
    /// </summary>
    /// <param name="type">The multi-valued property type (the 0x1000 flag set).</param>
    /// <param name="bytes">The whole value.</param>
    /// <param name="count">The number of elements.</param>
    /// <returns>False when the type is not multi-valued, or the count and offsets do not fit the bytes (a trailing partial fixed-size element counts as invalid).</returns>
    public static bool TryGetMultiValueCount(ushort type, ReadOnlySpan<byte> bytes, out int count)
    {
        count = 0;
        if (!LtpPropertyTypes.IsMultiValue(type))
        {
            return false;
        }

        int element = LtpPropertyTypes.FixedElementSize(type);
        if (element > 0)
        {
            if (bytes.Length % element != 0)
            {
                return false;
            }

            count = bytes.Length / element;
            return true;
        }

        // Only String8, String and Binary have the offset-table layout; any other multi-valued type is not understood here.
        if ((ushort)(type & ~LtpPropertyTypes.MultiValueFlag) is not (LtpPropertyTypes.PtypString8 or LtpPropertyTypes.PtypString or LtpPropertyTypes.PtypBinary) || bytes.Length < 4)
        {
            return false;
        }

        uint n = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (4L + (4L * n) > bytes.Length)
        {
            return false;
        }

        count = (int)n;
        return true;
    }

    /// <summary>Finds one element of a multi-valued value.</summary>
    /// <param name="type">The multi-valued property type.</param>
    /// <param name="bytes">The whole value.</param>
    /// <param name="index">Zero-based element index.</param>
    /// <param name="offset">Where the element starts in <paramref name="bytes"/>.</param>
    /// <param name="length">The element's length in bytes.</param>
    /// <returns>False when the index is out of range or the offsets are not in increasing order inside the value.</returns>
    public static bool TryGetMultiValueElement(ushort type, ReadOnlySpan<byte> bytes, int index, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (!TryGetMultiValueCount(type, bytes, out int count) || (uint)index >= (uint)count)
        {
            return false;
        }

        int element = LtpPropertyTypes.FixedElementSize(type);
        if (element > 0)
        {
            offset = index * element;
            length = element;
            return true;
        }

        // The offsets are ascending, and element i ends where element i+1 starts (the last one ends with the value).
        uint start = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 + (4 * index))..]);
        uint end = index + 1 < count ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 + (4 * (index + 1)))..]) : (uint)bytes.Length;
        if (start < 4 + (4L * count) || end < start || end > bytes.Length)
        {
            return false;
        }

        offset = (int)start;
        length = (int)(end - start);
        return true;
    }
}
