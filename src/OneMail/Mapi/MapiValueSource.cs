namespace RecursiveAI.OneMail.Mapi;

/// <summary>
/// Supplies the values of a property on demand, so that opening an item does not read its bodies and attachments.
/// A file or store layer derives from this and gives it to <see cref="MapiProperty"/>.
/// </summary>
/// <remarks>
/// Implementations may be called from any thread that uses the property, so they synchronise their own access to the file.
/// They never throw for damaged data: a value that cannot be read is reported as empty by <see cref="GetLength"/> and
/// <see cref="Read"/>, and the layer records the damage as a defect when it finds it.
/// </remarks>
public abstract class MapiValueSource
{
    /// <summary>The number of values: 1 for a single-valued property, the element count for a multi-valued one.</summary>
    public abstract int Count { get; }

    /// <summary>The size in bytes of one value, without reading it when the source can tell.</summary>
    /// <param name="index">The zero-based value index, below <see cref="Count"/>.</param>
    /// <returns>The size of the raw value; zero when the value cannot be read.</returns>
    public abstract long GetLength(int index);

    /// <summary>Reads one value completely.</summary>
    /// <param name="index">The zero-based value index, below <see cref="Count"/>.</param>
    /// <returns>The raw value bytes (see <see cref="MapiProperty.GetBytes"/> for the layout of each type).</returns>
    public abstract byte[] Read(int index);

    /// <summary>Opens one value as a forward stream. The default reads the value into memory.</summary>
    /// <param name="index">The zero-based value index, below <see cref="Count"/>.</param>
    /// <returns>A readable, seekable stream over the raw value.</returns>
    public virtual Stream OpenRead(int index) => new MemoryStream(Read(index), writable: false);
}
