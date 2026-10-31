using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Mapi;

/// <summary>
/// One property of a MAPI object: its tag, the name when it is a named property, and its raw value bytes, with typed accessors that decode on demand.
/// </summary>
/// <remarks>
/// <para>
/// The raw bytes are always kept and always available through <see cref="GetBytes"/>; the typed accessors never change them. The raw layout of a
/// value, as the property bag holds it for every file format, is:
/// fixed-size types little-endian at their natural size (boolean: one byte); <see cref="MapiType.UnicodeString"/> UTF-16LE without a terminator;
/// <see cref="MapiType.String8"/> bytes in the item's code page without a terminator; <see cref="MapiType.Binary"/> the bytes;
/// <see cref="MapiType.GuidValue"/> 16 bytes in the Windows layout. A multi-valued property holds one such value per element.
/// </para>
/// <para>
/// A property is not thread-safe to build; once built, reading it from several threads is safe if its <see cref="MapiValueSource"/> is.
/// </para>
/// </remarks>
public sealed class MapiProperty
{
    private readonly byte[]? _single;
    private readonly byte[][]? _multi;
    private readonly MapiValueSource? _source;

    private MapiProperty(MapiTag tag, MapiNamedProperty? name, byte[]? single, byte[][]? multi, MapiValueSource? source)
    {
        Tag = tag;
        Name = name;
        _single = single;
        _multi = multi;
        _source = source;
    }

    /// <summary>Creates a single-valued property from raw value bytes.</summary>
    /// <param name="tag">The tag; its type decides how accessors read the bytes.</param>
    /// <param name="value">The raw value (see the class remarks for the layout). Copied.</param>
    /// <param name="name">The property name when the tag is a named property whose name is known.</param>
    /// <returns>The property.</returns>
    public static MapiProperty Create(MapiTag tag, ReadOnlySpan<byte> value, MapiNamedProperty? name = null) =>
        new(tag, name, value.ToArray(), null, null);

    /// <summary>Creates a property that owns the array it is given, without copying.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="value">The raw value; the caller must not change it afterwards.</param>
    /// <param name="name">The property name when known.</param>
    /// <returns>The property.</returns>
    public static MapiProperty CreateOwned(MapiTag tag, byte[] value, MapiNamedProperty? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(tag, name, value, null, null);
    }

    /// <summary>Creates a multi-valued property from the raw bytes of each element.</summary>
    /// <param name="tag">The tag, with a multi-valued type.</param>
    /// <param name="values">One raw value per element, in the layout of the element type. Copied.</param>
    /// <param name="name">The property name when known.</param>
    /// <returns>The property.</returns>
    public static MapiProperty CreateMulti(MapiTag tag, IReadOnlyList<byte[]> values, MapiNamedProperty? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new byte[values.Count][];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = (byte[])values[i].Clone();
        }

        return new(tag, name, null, copy, null);
    }

    /// <summary>Creates a property whose value is read from <paramref name="source"/> when first needed.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="source">Supplies the value or values.</param>
    /// <param name="name">The property name when known.</param>
    /// <returns>The property.</returns>
    public static MapiProperty CreateLazy(MapiTag tag, MapiValueSource source, MapiNamedProperty? name = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(tag, name, null, null, source);
    }

    /// <summary>The property tag as the file or store holds it (for a named property the ID is local to that file or store).</summary>
    public MapiTag Tag { get; }

    /// <summary>The property ID.</summary>
    public ushort Id => Tag.Id;

    /// <summary>The property type.</summary>
    public MapiType Type => Tag.PropertyType;

    /// <summary>The name of a named property, or null for a tagged property and for a named property whose mapping is missing.</summary>
    public MapiNamedProperty? Name { get; }

    /// <summary>Whether the type is multi-valued.</summary>
    public bool IsMultiValued => Tag.IsMultiValued;

    /// <summary>
    /// The number of values: 1 for a single-valued property, the number of elements for a multi-valued one.
    /// Asking a lazy multi-valued property may read its length data.
    /// </summary>
    public int ValueCount => _source is not null ? _source.Count : _multi is not null ? _multi.Length : 1;

    /// <summary>Whether the value has not been read from its file yet. Reading it does not change this.</summary>
    public bool IsLazy => _source is not null;

    /// <summary>The size in bytes of one raw value, without reading it when the source can tell.</summary>
    /// <param name="index">The zero-based value index.</param>
    /// <returns>The size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <see cref="ValueCount"/>.</exception>
    public long GetByteLength(int index = 0)
    {
        CheckIndex(index);
        return _source is not null ? _source.GetLength(index) : _multi is not null ? _multi[index].Length : _single!.Length;
    }

    /// <summary>The raw bytes of one value. A lazy property reads them from its file on each call.</summary>
    /// <param name="index">The zero-based value index.</param>
    /// <returns>The raw value; for an object-typed property with no bytes, empty.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <see cref="ValueCount"/>.</exception>
    public ReadOnlyMemory<byte> GetBytes(int index = 0)
    {
        CheckIndex(index);
        return Raw(index);
    }

    /// <summary>Opens one raw value as a stream; for a lazy value this reads it piece by piece when the source can.</summary>
    /// <param name="index">The zero-based value index.</param>
    /// <returns>A readable, seekable stream over the raw value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <see cref="ValueCount"/>.</exception>
    public Stream OpenRead(int index = 0)
    {
        CheckIndex(index);
        return _source is not null ? _source.OpenRead(index) : new MemoryStream(GetBytes(index).ToArray(), writable: false);
    }

    /// <summary>
    /// Whether this property has the same tag, name and raw values as another. Reads lazy values. Used to check round trips.
    /// </summary>
    /// <param name="other">The property to compare with.</param>
    /// <returns>True when tag, name and every value's bytes are equal.</returns>
    public bool ValueEquals(MapiProperty other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Tag != other.Tag || Name != other.Name || ValueCount != other.ValueCount)
        {
            return false;
        }

        for (int i = 0; i < ValueCount; i++)
        {
            if (!GetBytes(i).Span.SequenceEqual(other.GetBytes(i).Span))
            {
                return false;
            }
        }

        return true;
    }

    // ---- typed accessors. Each returns null when the value's type is not the asked-for type or the bytes are too short;
    // ---- none throws on damaged data. A multi-valued property is read by element type, one element per index.

    private bool Is(MapiType single, MapiType multiType) => Tag.PropertyType == single || Tag.PropertyType == multiType;

    /// <summary>The value as a signed 16-bit integer (PtypInteger16).</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The value, or null if the type differs or the bytes are shorter than 2.</returns>
    public short? AsInt16(int index = 0) =>
        Is(MapiType.Integer16, MapiType.MultipleInteger16) && Bytes(index, 2) is { } b ? BinaryPrimitives.ReadInt16LittleEndian(b) : null;

    /// <summary>The value as a signed 32-bit integer (PtypInteger32 or, as its bit pattern, PtypErrorCode).</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The value, or null if the type differs or the bytes are shorter than 4.</returns>
    public int? AsInt32(int index = 0) =>
        Tag.PropertyType is MapiType.Integer32 or MapiType.MultipleInteger32 or MapiType.ErrorCode && Bytes(index, 4) is { } b
            ? BinaryPrimitives.ReadInt32LittleEndian(b) : null;

    /// <summary>The value as a signed 64-bit integer (PtypInteger64).</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The value, or null if the type differs or the bytes are shorter than 8.</returns>
    public long? AsInt64(int index = 0) =>
        Is(MapiType.Integer64, MapiType.MultipleInteger64) && Bytes(index, 8) is { } b ? BinaryPrimitives.ReadInt64LittleEndian(b) : null;

    /// <summary>The value of a PtypCurrency property in ten-thousandths of the unit.</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The scaled integer, or null if the type differs or the bytes are shorter than 8.</returns>
    public long? AsCurrencyUnits(int index = 0) =>
        Is(MapiType.Currency, MapiType.MultipleCurrency) && Bytes(index, 8) is { } b ? BinaryPrimitives.ReadInt64LittleEndian(b) : null;

    /// <summary>The value as a single-precision float (PtypFloating32).</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The value, or null if the type differs or the bytes are shorter than 4.</returns>
    public float? AsFloat(int index = 0) =>
        Is(MapiType.Floating32, MapiType.MultipleFloating32) && Bytes(index, 4) is { } b ? BinaryPrimitives.ReadSingleLittleEndian(b) : null;

    /// <summary>The value as a double (PtypFloating64).</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The value, or null if the type differs or the bytes are shorter than 8.</returns>
    public double? AsDouble(int index = 0) =>
        Is(MapiType.Floating64, MapiType.MultipleFloating64) && Bytes(index, 8) is { } b ? BinaryPrimitives.ReadDoubleLittleEndian(b) : null;

    /// <summary>The value as a boolean (PtypBoolean): false when the first byte is zero.</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The value, or null if the type differs or there is no byte.</returns>
    public bool? AsBoolean(int index = 0) =>
        Tag.PropertyType == MapiType.Boolean && Bytes(index, 1) is { } b ? b[0] != 0 : null;

    /// <summary>The raw FILETIME of a PtypTime property: 100 ns ticks since 1601-01-01 UTC.</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The ticks, or null if the type differs or the bytes are shorter than 8.</returns>
    public ulong? AsFileTime(int index = 0) =>
        Is(MapiType.Time, MapiType.MultipleTime) && Bytes(index, 8) is { } b ? BinaryPrimitives.ReadUInt64LittleEndian(b) : null;

    /// <summary>
    /// The value of a PtypTime property as a UTC instant. A zero FILETIME (no date) and a value outside the range of
    /// <see cref="DateTimeOffset"/> (including the "no end" sentinel 0x7FFFFFFFFFFFFFFF) give null; <see cref="AsFileTime"/> still has them.
    /// </summary>
    /// <param name="index">The value index.</param>
    /// <returns>The instant at offset zero, or null.</returns>
    public DateTimeOffset? AsTime(int index = 0)
    {
        ulong? ft = AsFileTime(index);
        // 2650467743999999999 is the FILETIME of 9999-12-31T23:59:59.9999999Z, the last instant DateTime holds.
        if (ft is null or 0 or > 2650467743999999999UL)
        {
            return null;
        }

        return new DateTimeOffset(DateTime.FromFileTimeUtc((long)ft.Value), TimeSpan.Zero);
    }

    /// <summary>The value of a PtypFloatingTime property as an instant (days since 1899-12-30, treated as UTC).</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The instant, or null if the type differs, the bytes are short, or the number is not a date in range.</returns>
    public DateTimeOffset? AsFloatingTime(int index = 0)
    {
        if (!Is(MapiType.FloatingTime, MapiType.MultipleFloatingTime) || Bytes(index, 8) is not { } b)
        {
            return null;
        }

        double days = BinaryPrimitives.ReadDoubleLittleEndian(b);
        if (!(days is > -657434.0 and < 2958465.0))
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(days), DateTimeKind.Utc));
    }

    /// <summary>The value as a GUID (PtypGuid).</summary>
    /// <param name="index">The value index.</param>
    /// <returns>The value, or null if the type differs or the bytes are shorter than 16.</returns>
    public Guid? AsGuid(int index = 0) =>
        Is(MapiType.GuidValue, MapiType.MultipleGuid) && Bytes(index, 16) is { } b ? new Guid(b.AsSpan(0, 16)) : null;

    /// <summary>
    /// The value as text: PtypString is read as UTF-16LE, PtypString8 with <paramref name="string8Encoding"/> (windows-1252 when null).
    /// Trailing NUL characters are removed. An odd trailing byte of a PtypString is dropped and invalid bytes become U+FFFD; both
    /// are reported through <paramref name="issues"/> when it is given.
    /// </summary>
    /// <param name="string8Encoding">The code page for PtypString8, normally the owning item's <see cref="MapiObject.String8Encoding"/>.</param>
    /// <param name="issues">Receives what the decoder repaired, or null.</param>
    /// <param name="index">The value index.</param>
    /// <returns>The text, or null if the property is not a string type or has no value at <paramref name="index"/> (a multi-valued property can hold none).</returns>
    public string? AsString(MailEncoding? string8Encoding = null, List<TextIssue>? issues = null, int index = 0)
    {
        MapiType t = Tag.PropertyType;
        if (t is not (MapiType.UnicodeString or MapiType.MultipleString or MapiType.String8 or MapiType.MultipleString8))
        {
            return null;
        }

        if ((uint)index >= (uint)ValueCount)
        {
            return null;
        }

        ReadOnlyMemory<byte> raw = GetBytes(index);
        ReadOnlySpan<byte> s = raw.Span;
        if (t is MapiType.UnicodeString or MapiType.MultipleString)
        {
            if ((s.Length & 1) != 0)
            {
                issues?.Add(new TextIssue(TextIssueKind.InvalidBytes, s.Length - 1, 1));
                s = s[..^1];
            }

            while (s.Length >= 2 && s[^1] == 0 && s[^2] == 0)
            {
                s = s[..^2];
            }

            return Encoding.Unicode.GetString(s);
        }

        while (s.Length > 0 && s[^1] == 0)
        {
            s = s[..^1];
        }

        return (string8Encoding ?? MailEncoding.Windows1252).GetString(s, issues);
    }

    private byte[]? Bytes(int index, int min)
    {
        if ((uint)index >= (uint)ValueCount)
        {
            return null;
        }

        byte[] raw = Raw(index);
        return raw.Length >= min ? raw : null;
    }

    // The stored array itself for inline values (never handed out), a fresh read for lazy ones.
    private byte[] Raw(int index) => _source is not null ? _source.Read(index) : _multi is not null ? _multi[index] : _single!;

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)ValueCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Name is { } n ? $"{Tag} {n}" : Tag.ToString();
}
