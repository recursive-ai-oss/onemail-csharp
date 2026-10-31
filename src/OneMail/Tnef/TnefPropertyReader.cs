using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Tnef;

/// <summary>Reads a range of the stream as if it were a sequence of little-endian fields, through a small window so that big values are not buffered.</summary>
internal sealed class TnefCursor : IDisposable
{
    private readonly IRandomAccessSource _source;
    private readonly long _end;
    private byte[]? _window;
    private long _windowStart;
    private int _windowLength;

    public TnefCursor(IRandomAccessSource source, long start, long length)
    {
        _source = source;
        Position = start;
        _end = start + length;
        _window = ArrayPool<byte>.Shared.Rent(4096);
    }

    public long Position { get; private set; }

    public long Remaining => _end - Position;

    public bool TryRead(Span<byte> destination)
    {
        if (destination.Length > Remaining)
        {
            return false;
        }

        if (destination.Length >= 2048)
        {
            if (_source.ReadFully(Position, destination) != destination.Length)
            {
                return false;
            }
        }
        else if (destination.Length > 0)
        {
            byte[] w = _window!;
            if (Position < _windowStart || Position + destination.Length > _windowStart + _windowLength)
            {
                _windowStart = Position;
                _windowLength = _source.ReadFully(Position, w.AsSpan(0, (int)Math.Min(4096, Remaining)));
                if (_windowLength < destination.Length)
                {
                    return false;
                }
            }

            w.AsSpan((int)(Position - _windowStart), destination.Length).CopyTo(destination);
        }

        Position += destination.Length;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        Span<byte> b = stackalloc byte[4];
        if (!TryRead(b))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(b);
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        if (!TryRead(b))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(b);
        return true;
    }

    public bool Skip(long count)
    {
        if (count < 0 || count > Remaining)
        {
            return false;
        }

        Position += count;
        return true;
    }

    public void Dispose()
    {
        if (_window is { } w)
        {
            _window = null;
            ArrayPool<byte>.Shared.Return(w);
        }
    }
}

/// <summary>Supplies one value of an encapsulated property from the stream on demand.</summary>
internal sealed class TnefValueSource : MapiValueSource
{
    private readonly IRandomAccessSource _source;
    private readonly long _offset;
    private readonly long _length;

    public TnefValueSource(IRandomAccessSource source, long offset, long length)
    {
        _source = source;
        _offset = offset;
        _length = length;
    }

    public override int Count => 1;

    public override long GetLength(int index) => index == 0 ? _length : 0;

    public override byte[] Read(int index)
    {
        if (index != 0 || _length > Array.MaxLength)
        {
            return [];
        }

        byte[] data = new byte[_length];
        int n = _source.ReadFully(_offset, data);
        return n == data.Length ? data : data[..n];
    }

    public override Stream OpenRead(int index) => index == 0 ? _source.OpenRead(_offset, _length) : new MemoryStream([], writable: false);
}

/// <summary>A parsed property and where its first value sits in the stream (used to find an embedded message's bytes).</summary>
internal readonly record struct TnefParsedProperty(MapiProperty Property, long ValueOffset, long ValueLength);

/// <summary>Reads the property lists of attMsgProps, attRecipTable rows and attAttachment (MS-OXTNEF 2.1.3.4).</summary>
internal sealed class TnefPropertyReader
{
    private readonly TnefReaderOptions _options;
    private readonly Action<TnefDefectKind, long, string> _defect;
    private readonly IRandomAccessSource _source;

    public TnefPropertyReader(IRandomAccessSource source, TnefReaderOptions options, Action<TnefDefectKind, long, string> defect)
    {
        _source = source;
        _options = options;
        _defect = defect;
    }

    /// <summary>Reads one property list: a count, then that many properties. Stops at the first property that cannot be read and returns what it has.</summary>
    /// <returns>False when the list was cut short or damaged (a defect says how), so the position after it is unknown.</returns>
    public bool ReadList(TnefCursor c, List<TnefParsedProperty> into)
    {
        long listStart = c.Position;
        if (!c.TryReadUInt32(out uint count))
        {
            _defect(TnefDefectKind.PropertyListTruncated, listStart, "no room for the property count");
            return false;
        }

        // Every property takes at least its 4-byte tag, so a count above that is a lie; it also guards the list's growth.
        if (count > c.Remaining / 4)
        {
            _defect(TnefDefectKind.PropertyListTruncated, listStart, count + " properties promised, " + c.Remaining + " bytes left");
            count = (uint)(c.Remaining / 4);
        }

        if (count > _options.MaxProperties)
        {
            _defect(TnefDefectKind.LimitReached, listStart, count + " properties in one list, limit " + _options.MaxProperties);
            count = (uint)_options.MaxProperties;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!ReadProperty(c, into))
            {
                return false;
            }
        }

        return true;
    }

    private static int FixedWireSize(ushort elementType) => elementType switch
    {
        0x01 => 0,
        0x02 or 0x0B => 2,
        0x03 or 0x04 or 0x0A => 4,
        0x05 or 0x06 or 0x07 or 0x14 or 0x40 => 8,
        0x48 => 16,
        _ => -1,
    };

    private static bool IsVariable(ushort elementType) => elementType is 0x1E or 0x1F or 0x102 or 0x0D;

    private static long Padded(long size) => (size + 3) & ~3L;

    private bool ReadProperty(TnefCursor c, List<TnefParsedProperty> into)
    {
        long at = c.Position;
        if (!c.TryReadUInt16(out ushort type) || !c.TryReadUInt16(out ushort id))
        {
            _defect(TnefDefectKind.PropertyListTruncated, at, "property tag cut off");
            return false;
        }

        MapiNamedProperty? name = null;
        if (id >= 0x8000)
        {
            if (!ReadName(c, at, out name))
            {
                return false;
            }
        }

        bool multi = MapiTypes.IsMultiValued(type);
        ushort element = MapiTypes.ElementType(type);
        if (type >= 0x2000 || (multi && element is 0x01 or 0x0A or 0x0B or 0x0D) || (FixedWireSize(element) < 0 && !IsVariable(element)))
        {
            _defect(TnefDefectKind.UnknownPropertyType, at, "type 0x" + type.ToString("X4") + " of property 0x" + id.ToString("X4") + " has no defined size");
            return false;
        }

        var tag = new MapiTag(id, type);
        int fixedSize = FixedWireSize(element);
        long valueOffset = c.Position;
        long valueLength = 0;
        MapiProperty? property;
        if (fixedSize >= 0 && !multi)
        {
            byte[] raw = new byte[fixedSize];
            if (!c.TryRead(raw))
            {
                _defect(TnefDefectKind.PropertyListTruncated, at, "value of property 0x" + id.ToString("X4") + " cut off");
                return false;
            }

            valueLength = fixedSize;
            SkipPad(c, fixedSize);
            property = MapiProperty.CreateOwned(tag, FromWire(element, raw), name);
        }
        else if (fixedSize >= 0)
        {
            if (!ReadCount(c, at, 4, out uint count))
            {
                return false;
            }

            var values = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                values[i] = new byte[fixedSize];
                if (!c.TryRead(values[i]))
                {
                    _defect(TnefDefectKind.PropertyListTruncated, at, "multi-valued property 0x" + id.ToString("X4") + " cut off at element " + i);
                    return false;
                }

                SkipPad(c, fixedSize);
            }

            property = MapiProperty.CreateMulti(tag, values, name);
        }
        else if (!multi)
        {
            if (!c.TryReadUInt32(out uint count))
            {
                _defect(TnefDefectKind.PropertyListTruncated, at, "value count of property 0x" + id.ToString("X4") + " cut off");
                return false;
            }

            if (count != 1)
            {
                _defect(TnefDefectKind.ValueCountMismatch, at, "property 0x" + id.ToString("X4") + " has " + count + " values, a single-valued type has one");
            }

            if (count == 0)
            {
                property = MapiProperty.CreateOwned(tag, [], name);
            }
            else
            {
                property = ReadVariableValue(c, at, tag, name, element, out valueOffset, out valueLength);
                if (property is null)
                {
                    return false;
                }

                // Values past the first of a mismatched count are read to keep the list in step and then dropped.
                for (uint k = 1; k < count && k < _options.MaxMultiValueCount; k++)
                {
                    if (!SkipVariable(c, at))
                    {
                        return false;
                    }
                }
            }
        }
        else
        {
            if (!ReadCount(c, at, 4, out uint count))
            {
                return false;
            }

            var values = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                MapiProperty? one = ReadVariableValue(c, at, new MapiTag(id, element), null, element, out _, out _, forceEager: true);
                if (one is null)
                {
                    return false;
                }

                values[i] = one.GetBytes().ToArray();
            }

            property = MapiProperty.CreateMulti(tag, values, name);
        }

        into.Add(new TnefParsedProperty(property, valueOffset, valueLength));
        return true;
    }

    private static void SkipPad(TnefCursor c, long size)
    {
        // MS-OXTNEF 2.1.3.4: values are padded with zero bytes to a multiple of 4; readers must accept any pad byte. A missing pad at the very end is tolerated.
        long pad = Padded(size) - size;
        c.Skip(Math.Min(pad, c.Remaining));
    }

    private bool ReadCount(TnefCursor c, long at, int minimumElementBytes, out uint count)
    {
        if (!c.TryReadUInt32(out count))
        {
            _defect(TnefDefectKind.PropertyListTruncated, at, "value count cut off");
            return false;
        }

        if (count > _options.MaxMultiValueCount)
        {
            _defect(TnefDefectKind.LimitReached, at, count + " values in one property, limit " + _options.MaxMultiValueCount);
            return false;
        }

        if (count > c.Remaining / minimumElementBytes)
        {
            _defect(TnefDefectKind.PropertyListTruncated, at, count + " values promised, " + c.Remaining + " bytes left");
            return false;
        }

        return true;
    }

    private bool SkipVariable(TnefCursor c, long at)
    {
        if (!c.TryReadUInt32(out uint size) || size > c.Remaining || !c.Skip(size))
        {
            _defect(TnefDefectKind.PropertyListTruncated, at, "extra value cut off");
            return false;
        }

        SkipPad(c, size);
        return true;
    }

    private MapiProperty? ReadVariableValue(TnefCursor c, long at, MapiTag tag, MapiNamedProperty? name, ushort element, out long valueOffset, out long valueLength, bool forceEager = false)
    {
        valueOffset = 0;
        valueLength = 0;
        if (!c.TryReadUInt32(out uint size))
        {
            _defect(TnefDefectKind.PropertyListTruncated, at, "value size cut off");
            return null;
        }

        if (size > c.Remaining)
        {
            _defect(TnefDefectKind.PropertyListTruncated, at, "value of " + size + " bytes, " + c.Remaining + " left");
            return null;
        }

        valueOffset = c.Position;
        valueLength = size;
        bool unicode = element == 0x1F;
        bool text = unicode || element == 0x1E;
        int terminator = unicode ? 2 : 1;
        MapiProperty result;
        if (!forceEager && size > _options.EagerValueBytes)
        {
            long keep = size;
            if (text)
            {
                // Only the last bytes tell whether the terminator is there.
                Span<byte> tail = stackalloc byte[2];
                if (size >= terminator && _source.ReadFully(c.Position + size - terminator, tail[..terminator]) == terminator && IsZero(tail[..terminator]))
                {
                    keep = size - terminator;
                }
                else
                {
                    _defect(TnefDefectKind.StringNotTerminated, c.Position, "string value of " + size + " bytes has no terminator");
                }
            }

            result = MapiProperty.CreateLazy(tag, new TnefValueSource(_source, c.Position, keep), name);
            c.Skip(size);
        }
        else
        {
            byte[] raw = new byte[size];
            if (!c.TryRead(raw))
            {
                _defect(TnefDefectKind.PropertyListTruncated, at, "value cut off");
                return null;
            }

            if (text)
            {
                if (raw.Length >= terminator && IsZero(raw.AsSpan(raw.Length - terminator)))
                {
                    raw = raw[..^terminator];
                }
                else
                {
                    _defect(TnefDefectKind.StringNotTerminated, valueOffset, "string value of " + size + " bytes has no terminator");
                }
            }

            result = MapiProperty.CreateOwned(tag, raw, name);
        }

        SkipPad(c, size);
        return result;
    }

    private static bool IsZero(ReadOnlySpan<byte> b)
    {
        foreach (byte x in b)
        {
            if (x != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Converts a fixed-size value from its wire form to the property bag's raw form (they differ only for PtypBoolean, which is 16 bits on the wire and one byte in the bag).</summary>
    private static byte[] FromWire(ushort element, byte[] raw) => element == 0x0B ? [(byte)(raw[0] != 0 || raw[1] != 0 ? 1 : 0)] : raw;

    private bool ReadName(TnefCursor c, long at, out MapiNamedProperty? name)
    {
        name = null;
        Span<byte> g = stackalloc byte[16];
        if (!c.TryRead(g) || !c.TryReadUInt32(out uint kind))
        {
            _defect(TnefDefectKind.PropertyListTruncated, at, "named property specification cut off");
            return false;
        }

        var guid = new Guid(g);
        if (kind == 0)
        {
            if (!c.TryReadUInt32(out uint lid))
            {
                _defect(TnefDefectKind.PropertyListTruncated, at, "named property number cut off");
                return false;
            }

            name = new MapiNamedProperty(guid, lid);
            return true;
        }

        if (kind == 1)
        {
            // MS-OXTNEF 2.1.3.4: PropMapString is a byte length, the UTF-16LE characters with their 2-byte terminator, then padding to 4 bytes.
            if (!c.TryReadUInt32(out uint len) || len > c.Remaining || len > (1 << 20))
            {
                _defect(TnefDefectKind.InvalidNamedProperty, at, "name length out of range");
                return false;
            }

            byte[] chars = new byte[len];
            if (!c.TryRead(chars))
            {
                _defect(TnefDefectKind.InvalidNamedProperty, at, "name cut off");
                return false;
            }

            SkipPad(c, len);
            int n = chars.Length / 2 * 2;
            if (n >= 2 && chars[n - 1] == 0 && chars[n - 2] == 0)
            {
                n -= 2;
            }

            name = new MapiNamedProperty(guid, Encoding.Unicode.GetString(chars, 0, n));
            return true;
        }

        _defect(TnefDefectKind.InvalidNamedProperty, at, "name kind " + kind + " is neither number (0) nor string (1)");
        return false;
    }
}
