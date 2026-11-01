using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Stores.Dbx;

/// <summary>Which shape a message segment has.</summary>
public enum DbxSegmentKind
{
    /// <summary>A segment of a message chain in use: marker, body length 0x200, text length, next pointer, then the text.</summary>
    Live = 0,

    /// <summary>A freed segment: marker, 0x1fc, 0x210, the old next pointer, the next deleted object, then the text without its first four bytes.</summary>
    Deleted = 1,
}

/// <summary>What <see cref="DbxSpan.TryParseSegment"/> found.</summary>
/// <param name="Kind">Live or freed.</param>
/// <param name="TextStart">Where the text starts inside the segment: 16 for a live one, 20 for a freed one.</param>
/// <param name="TextLength">The live segment's text length; for a freed segment the most text it can hold (0x1fc), because the real length was overwritten.</param>
/// <param name="TextLengthKnown">False for a freed segment.</param>
/// <param name="Next">The next segment of the chain (live) or the old next pointer a freed segment kept; 0 at the end.</param>
/// <param name="NextDeleted">A freed segment's link to the next deleted object; 0 for a live one.</param>
public readonly record struct DbxSegmentInfo(DbxSegmentKind Kind, int TextStart, int TextLength, bool TextLengthKnown, uint Next, uint NextDeleted);

/// <summary>
/// Span-level parsing of DBX objects (oedbx notes: OE_Dbx_Message, OE_Dbx_Deleted, OE_Dbx_IndexedInfo) for tools that find them outside a file's
/// own tree. Nothing throws; the checks are the same ones <see cref="DbxReader"/> makes.
/// </summary>
public static class DbxSpan
{
    /// <summary>Size of a message segment: 16 header bytes and 0x200 of text.</summary>
    public const int SegmentSize = 0x210;

    /// <summary>
    /// Recognises a message segment by its own offset (the marker), its fixed body length and, for a live one, a sane text length and next pointer.
    /// </summary>
    /// <param name="data">At least 20 bytes from the segment's first byte; the full 0x210 for the text to be usable.</param>
    /// <param name="offset">The file offset the data was found at: the marker must equal it.</param>
    /// <param name="info">The parsed header.</param>
    /// <returns>False when the marker is not the offset or the lengths are not those of a live or freed segment.</returns>
    public static bool TryParseSegment(ReadOnlySpan<byte> data, long offset, out DbxSegmentInfo info)
    {
        info = default;
        if (data.Length < 20 || offset < DbxReader.HeaderLength || offset > uint.MaxValue || BinaryPrimitives.ReadUInt32LittleEndian(data) != (uint)offset)
        {
            return false;
        }

        uint body = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint text = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint next = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (body == 0x200 && text is > 0 and <= 0x200 && (next == 0 || (next >= DbxReader.HeaderLength && next % 4 == 0)))
        {
            info = new DbxSegmentInfo(DbxSegmentKind.Live, 16, (int)text, true, next, 0);
            return true;
        }

        if (body == 0x1FC && text == SegmentSize && (next == 0 || (next >= DbxReader.HeaderLength && next % 4 == 0)))
        {
            info = new DbxSegmentInfo(DbxSegmentKind.Deleted, 20, 0x1FC, false, next, BinaryPrimitives.ReadUInt32LittleEndian(data[16..]));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses an indexed-info object (a message or folder record): marker, body length, field count, the index and its data.
    /// </summary>
    /// <param name="data">The object's bytes from its first byte. The object's own length decides how much is used; a record cut by the end of the span keeps what is there and says so.</param>
    /// <param name="offset">The file offset the data was found at: the marker must equal it.</param>
    /// <param name="record">The record; read fields with its typed members.</param>
    /// <param name="defects">What was irregular (<see cref="StoreDefects.DbxBadIndexedInfo"/> for a cut record).</param>
    /// <param name="maxBytes">The largest body accepted. Default 1 MiB.</param>
    /// <returns>False when the marker is wrong, the body is larger than <paramref name="maxBytes"/> or too small for its index.</returns>
    public static bool TryParseRecord(ReadOnlySpan<byte> data, long offset, out DbxRecord? record, out StoreDefects defects, int maxBytes = 1 << 20)
    {
        record = null;
        defects = StoreDefects.None;
        if (data.Length < DbxRecord.HeaderLength || offset < DbxReader.HeaderLength || offset > uint.MaxValue || BinaryPrimitives.ReadUInt32LittleEndian(data) != (uint)offset)
        {
            return false;
        }

        long body = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        int count = data[10];
        if (body > maxBytes || body < 4L * count)
        {
            return false;
        }

        long avail = data.Length - DbxRecord.HeaderLength;
        if (body > avail)
        {
            defects |= StoreDefects.DbxBadIndexedInfo;
            body = avail;
            if (body < 4L * count)
            {
                return false;
            }
        }

        record = new DbxRecord(offset, data[..(DbxRecord.HeaderLength + (int)body)].ToArray(), defects);
        return true;
    }
}
