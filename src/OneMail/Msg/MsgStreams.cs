using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Msg;

/// <summary>
/// Turns an object's properties back into the streams MS-OXMSG stores them in: the property stream and the value streams. It is the
/// writing half of the reader and is exact for a file that was read without size or layout defects, which is how round trips are checked.
/// </summary>
public static class MsgStreams
{
    /// <summary>
    /// Encodes the property stream and the value streams of one object, in the order a reader found them. Child storages (recipients,
    /// attachments, the embedded object, the named property map) are not part of the result.
    /// </summary>
    /// <param name="item">The object.</param>
    /// <param name="layout">The layout it was read with. The properties must still be the ones that were read: same count and order.</param>
    /// <returns>The property stream first, then each value stream.</returns>
    /// <exception cref="ArgumentException">The number of properties differs from the layout's.</exception>
    public static IReadOnlyList<MsgStreamData> Encode(MapiObject item, MsgObjectLayout layout)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(layout);
        MapiPropertyBag props = item.Properties;
        if (props.Count != layout.Entries.Count)
        {
            throw new ArgumentException("The layout describes a different number of properties than the object has.", nameof(layout));
        }

        var values = new List<MsgStreamData>();
        using var ps = new MemoryStream();
        ps.Write(layout.RawHeader);
        byte[] entry = new byte[16];
        byte[] eight = new byte[8];
        byte[] four = new byte[4];
        byte[] zero4 = new byte[4];
        for (int i = 0; i < props.Count; i++)
        {
            MapiProperty p = props[i];
            MsgEntryMeta meta = layout.Entries[i];
            string stream = MsgObjectReader.ValuePrefix + p.Tag.ToHex();
            ulong tail = meta.Tail;
            switch (meta.Storage)
            {
                case MsgValueStorage.Inline:
                {
                    // MS-OXMSG 2.4.2.1: the value, then reserved bytes that are ignored on reading and kept as they were.
                    BinaryPrimitives.WriteUInt64LittleEndian(eight, tail);
                    ReadOnlySpan<byte> raw = p.GetBytes().Span;
                    raw[..Math.Min(8, raw.Length)].CopyTo(eight);
                    tail = BinaryPrimitives.ReadUInt64LittleEndian(eight);
                    break;
                }

                case MsgValueStorage.Stream:
                {
                    byte[] v = p.GetBytes().ToArray();
                    values.Add(new MsgStreamData(stream, v));
                    ushort e = p.Tag.Type;
                    int terminator = e == (ushort)MapiType.UnicodeString ? 2 : e == (ushort)MapiType.String8 ? 1 : 0;
                    // A file whose size field equals the stream size (terminator stored in the stream) is written back that way.
                    uint size = terminator > 0 && (uint)tail == (uint)v.Length ? (uint)v.Length : (uint)(v.Length + terminator);
                    tail = (tail & 0xFFFFFFFF00000000UL) | size;
                    break;
                }

                case MsgValueStorage.ObjectStorage:
                case MsgValueStorage.Missing:
                    break;

                case MsgValueStorage.MultiFixedStream:
                {
                    using var joined = new MemoryStream();
                    for (int k = 0; k < p.ValueCount; k++)
                    {
                        joined.Write(p.GetBytes(k).Span);
                    }

                    values.Add(new MsgStreamData(stream, joined.ToArray()));
                    tail = (tail & 0xFFFFFFFF00000000UL) | (uint)joined.Length;
                    break;
                }

                case MsgValueStorage.MultiVariableStreams:
                {
                    bool binary = MapiTypes.ElementType(p.Tag.Type) == (ushort)MapiType.Binary;
                    var lengths = new MemoryStream();
                    var elements = new List<MsgStreamData>();
                    for (int k = 0; k < p.ValueCount; k++)
                    {
                        byte[] v = p.GetBytes(k).ToArray();
                        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)v.Length);
                        lengths.Write(four);
                        if (binary)
                        {
                            lengths.Write(zero4);
                        }

                        elements.Add(new MsgStreamData(stream + "-" + k.ToString("X8", CultureInfo.InvariantCulture), v));
                    }

                    values.Add(new MsgStreamData(stream, lengths.ToArray()));
                    values.AddRange(elements);
                    tail = (tail & 0xFFFFFFFF00000000UL) | (uint)lengths.Length;
                    break;
                }
            }

            if (meta.HasEntry)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry, p.Tag.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), meta.Flags);
                BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), tail);
                ps.Write(entry);
            }
        }

        var result = new List<MsgStreamData>(values.Count + 1) { new(MsgObjectReader.PropertyStreamName, ps.ToArray()) };
        result.AddRange(values);
        return result;
    }
}
