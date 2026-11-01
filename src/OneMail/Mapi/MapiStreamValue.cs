namespace RecursiveAI.OneMail.Mapi;

/// <summary>
/// A single property value that is read from a stream the caller supplies, so that a large value (an attachment's data) is
/// never held in memory by the model. Writers that accept a <see cref="MapiProperty"/> copy it from a fresh stream, piece by piece.
/// </summary>
/// <remarks>
/// <see cref="MapiProperty.CreateLazy"/> with this source makes the property. The stream factory is called once for each read, so it must
/// be able to give the same bytes again (open the file again, or return a seekable stream positioned at its start). Streams it returns are
/// disposed by the reader of the property.
/// </remarks>
public sealed class MapiStreamValue : MapiValueSource
{
    private readonly Func<Stream> _open;
    private readonly long _length;

    /// <summary>Creates a value over a stream factory.</summary>
    /// <param name="open">Opens the value's bytes from the start. Called on every read.</param>
    /// <param name="length">The exact number of bytes the stream gives; the writers trust it for the sizes they record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public MapiStreamValue(Func<Stream> open, long length)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _open = open;
        _length = length;
    }

    /// <inheritdoc/>
    public override int Count => 1;

    /// <inheritdoc/>
    public override long GetLength(int index) => _length;

    /// <inheritdoc/>
    public override byte[] Read(int index)
    {
        if (_length > Array.MaxLength)
        {
            throw new InvalidOperationException("The value is too large to read into one array; open it as a stream.");
        }

        byte[] buffer = new byte[_length];
        using Stream s = _open();
        s.ReadExactly(buffer);
        return buffer;
    }

    /// <inheritdoc/>
    public override Stream OpenRead(int index) => _open();
}
