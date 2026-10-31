using System.Buffers;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Tnef;

/// <summary>The level an attribute applies at (MS-OXTNEF 2.1.3.2 attrLevelMessage, attrLevelAttachment).</summary>
public enum TnefLevel : byte
{
    /// <summary>The attribute applies to the message.</summary>
    Message = 1,

    /// <summary>The attribute applies to the attachment started by the last attAttachRendData.</summary>
    Attachment = 2,
}

/// <summary>
/// The attribute ids of MS-OXTNEF 2.1.3.2, as the 32-bit value the stream holds: the low 16 bits name the attribute, the high 16 bits are
/// the legacy data type. Reading by id never relies on the type half being right, but the constants include it so that writers produce it.
/// </summary>
public static class TnefAttributeIds
{
    /// <summary>attTnefVersion.</summary>
    public const uint TnefVersion = 0x00089006;

    /// <summary>attOemCodepage.</summary>
    public const uint OemCodepage = 0x00069007;

    /// <summary>attFrom.</summary>
    public const uint From = 0x00008000;

    /// <summary>attSubject.</summary>
    public const uint Subject = 0x00018004;

    /// <summary>attDateSent.</summary>
    public const uint DateSent = 0x00038005;

    /// <summary>attDateRecd.</summary>
    public const uint DateRecd = 0x00038006;

    /// <summary>attMessageStatus.</summary>
    public const uint MessageStatus = 0x00068007;

    /// <summary>attMessageClass.</summary>
    public const uint MessageClass = 0x00078008;

    /// <summary>attMessageID.</summary>
    public const uint MessageId = 0x00018009;

    /// <summary>attBody.</summary>
    public const uint Body = 0x0002800C;

    /// <summary>attPriority.</summary>
    public const uint Priority = 0x0004800D;

    /// <summary>attDateModified.</summary>
    public const uint DateModified = 0x00038020;

    /// <summary>attMsgProps.</summary>
    public const uint MsgProps = 0x00069003;

    /// <summary>attRecipTable.</summary>
    public const uint RecipTable = 0x00069004;

    /// <summary>attOriginalMessageClass.</summary>
    public const uint OriginalMessageClass = 0x00070600;

    /// <summary>attOwner.</summary>
    public const uint Owner = 0x00060000;

    /// <summary>attSentFor.</summary>
    public const uint SentFor = 0x00060001;

    /// <summary>attDelegate.</summary>
    public const uint Delegate = 0x00060002;

    /// <summary>attDateStart.</summary>
    public const uint DateStart = 0x00030006;

    /// <summary>attDateEnd.</summary>
    public const uint DateEnd = 0x00030007;

    /// <summary>attAidOwner.</summary>
    public const uint AidOwner = 0x00050008;

    /// <summary>attRequestRes.</summary>
    public const uint RequestRes = 0x00040009;

    /// <summary>attAttachData.</summary>
    public const uint AttachData = 0x0006800F;

    /// <summary>attAttachTitle.</summary>
    public const uint AttachTitle = 0x00018010;

    /// <summary>attAttachMetaFile.</summary>
    public const uint AttachMetaFile = 0x00068011;

    /// <summary>attAttachCreateDate.</summary>
    public const uint AttachCreateDate = 0x00038012;

    /// <summary>attAttachModifyDate.</summary>
    public const uint AttachModifyDate = 0x00038013;

    /// <summary>attAttachTransportFilename.</summary>
    public const uint AttachTransportFilename = 0x00069001;

    /// <summary>attAttachRendData.</summary>
    public const uint AttachRendData = 0x00069002;

    /// <summary>attAttachment.</summary>
    public const uint Attachment = 0x00069005;

    /// <summary>Compares two attribute ids by their low 16 bits only, which is what identifies the attribute.</summary>
    /// <param name="a">One id.</param>
    /// <param name="b">The other id.</param>
    /// <returns>True when the attribute numbers are equal.</returns>
    public static bool Same(uint a, uint b) => (a & 0xFFFF) == (b & 0xFFFF);
}

/// <summary>
/// One attribute of a TNEF stream: level, id, length, data and checksum (MS-OXTNEF 2.1.3.2). The data is not held in memory; it is read from the
/// stream's source on demand.
/// </summary>
public sealed class TnefAttributeRecord
{
    private readonly IRandomAccessSource _source;

    internal TnefAttributeRecord(IRandomAccessSource source, long offset, byte level, uint id, long length, long available, ushort stored, ushort? computed, bool truncated, long rawLength)
    {
        _source = source;
        Offset = offset;
        LevelValue = level;
        Id = id;
        Length = length;
        AvailableLength = available;
        StoredChecksum = stored;
        ComputedChecksum = computed;
        IsTruncated = truncated;
        RawLength = rawLength;
    }

    /// <summary>The level byte exactly as stored.</summary>
    public byte LevelValue { get; }

    /// <summary>The level, when the byte is a defined one, else null.</summary>
    public TnefLevel? Level => LevelValue is 1 or 2 ? (TnefLevel)LevelValue : null;

    /// <summary>The 32-bit attribute id as stored (see <see cref="TnefAttributeIds"/>).</summary>
    public uint Id { get; }

    /// <summary>Offset of the attribute's level byte in the stream.</summary>
    public long Offset { get; }

    /// <summary>Offset of the first data byte.</summary>
    public long DataOffset => Offset + 9;

    /// <summary>The data length the header declares.</summary>
    public long Length { get; }

    /// <summary>The data bytes actually present; less than <see cref="Length"/> only when the stream ends inside the attribute.</summary>
    public long AvailableLength { get; }

    /// <summary>Whether the stream ends inside this attribute's data or checksum.</summary>
    public bool IsTruncated { get; }

    /// <summary>The checksum stored after the data; zero for a truncated attribute with no checksum.</summary>
    public ushort StoredChecksum { get; }

    /// <summary>The sum of the data bytes modulo 65536, or null when the reader was asked not to compute it or the data is truncated.</summary>
    public ushort? ComputedChecksum { get; }

    /// <summary>Whether the stored and computed checksums agree; true when the checksum was not computed.</summary>
    public bool ChecksumValid => ComputedChecksum is not { } c || c == StoredChecksum;

    /// <summary>Total bytes this attribute occupies in the stream (header, data and checksum, or what is left of them).</summary>
    public long RawLength { get; }

    /// <summary>The stream the attribute was read from.</summary>
    internal IRandomAccessSource Source => _source;

    /// <summary>Reads the data into a new array.</summary>
    /// <returns>The data bytes present.</returns>
    /// <exception cref="InvalidOperationException">The data is larger than an array can hold.</exception>
    public byte[] ReadData()
    {
        if (AvailableLength > Array.MaxLength)
        {
            throw new InvalidOperationException("The attribute data is too large to read into one array; use OpenData.");
        }

        byte[] data = new byte[AvailableLength];
        int n = _source.ReadFully(DataOffset, data);
        return n == data.Length ? data : data[..n];
    }

    /// <summary>Reads part of the data.</summary>
    /// <param name="offset">Offset within the data.</param>
    /// <param name="buffer">Where the bytes go.</param>
    /// <returns>The number of bytes read, fewer than requested only at the end of the data.</returns>
    public int ReadData(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= AvailableLength)
        {
            return 0;
        }

        if (buffer.Length > AvailableLength - offset)
        {
            buffer = buffer[..(int)(AvailableLength - offset)];
        }

        return _source.ReadFully(DataOffset + offset, buffer);
    }

    /// <summary>Opens the data as a forward, seekable stream that reads from the source piece by piece.</summary>
    /// <returns>The stream.</returns>
    public Stream OpenData() => _source.OpenRead(DataOffset, AvailableLength);

    /// <inheritdoc/>
    public override string ToString() => $"level {LevelValue} id 0x{Id:X8} length {Length} at {Offset}";

    internal static ushort Sum(IRandomAccessSource source, long offset, long length)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            uint sum = 0;
            long done = 0;
            while (done < length)
            {
                int want = (int)Math.Min(buffer.Length, length - done);
                int n = source.ReadFully(offset + done, buffer.AsSpan(0, want));
                if (n <= 0)
                {
                    break;
                }

                for (int i = 0; i < n; i++)
                {
                    sum += buffer[i];
                }

                done += n;
            }

            return (ushort)sum;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
