namespace RecursiveAI.OneMail.Sources;

/// <summary>
/// A read-only byte source that can be read at any offset: a file, a stream, memory, or ranged reads supplied by the
/// caller (blob storage, HTTP range requests, a browser <c>Blob</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the one abstraction under every file format in the library. The core type assumes no file API, so it works
/// where files do not exist (browser WebAssembly). Sources that wrap local resources are synchronous and asynchronous;
/// remote ones are asynchronous only and say so through <see cref="SupportsSyncRead"/>.
/// </para>
/// <para>
/// Implementations are safe to call from several threads at once unless their own documentation says otherwise.
/// A source's <see cref="Length"/> is fixed when it is created; bytes appended later are not seen.
/// </para>
/// </remarks>
public interface IRandomAccessSource : IDisposable
{
    /// <summary>The number of bytes in the source. Never negative.</summary>
    long Length { get; }

    /// <summary>
    /// Whether <see cref="Read"/> can be called. When false, <see cref="Read"/> throws
    /// <see cref="NotSupportedException"/> and only <see cref="ReadAsync"/> works.
    /// </summary>
    bool SupportsSyncRead { get; }

    /// <summary>Reads up to <c>buffer.Length</c> bytes starting at <paramref name="offset"/>.</summary>
    /// <param name="offset">Absolute offset of the first byte to read. Offsets at or past <see cref="Length"/> read nothing.</param>
    /// <param name="buffer">Where the bytes go.</param>
    /// <returns>
    /// The number of bytes read. It is zero only when <paramref name="offset"/> is at or past the end or the buffer is empty;
    /// a smaller count than requested before the end is a short read and the caller reads again.
    /// </returns>
    /// <exception cref="NotSupportedException">The source is asynchronous only.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The source was disposed.</exception>
    int Read(long offset, Span<byte> buffer);

    /// <summary>Asynchronous twin of <see cref="Read"/>, with the same result contract.</summary>
    /// <param name="offset">Absolute offset of the first byte to read.</param>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);
}
