using Microsoft.Win32.SafeHandles;

namespace RecursiveAI.OneMail.Sources;

/// <summary>A source over bytes already in memory. Reads never block and never copy more than the caller asks for.</summary>
public sealed class MemorySource : IRandomAccessSource
{
    private readonly ReadOnlyMemory<byte> _data;

    /// <summary>Wraps <paramref name="data"/>. The memory must not change while the source is in use.</summary>
    /// <param name="data">The bytes.</param>
    public MemorySource(ReadOnlyMemory<byte> data) => _data = data;

    /// <inheritdoc/>
    public long Length => _data.Length;

    /// <inheritdoc/>
    public bool SupportsSyncRead => true;

    /// <summary>The wrapped bytes, for callers that can use them without copying.</summary>
    public ReadOnlyMemory<byte> Memory => _data;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= _data.Length)
        {
            return 0;
        }

        int n = (int)Math.Min(buffer.Length, _data.Length - offset);
        _data.Span.Slice((int)offset, n).CopyTo(buffer);
        return n;
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(offset, buffer.Span));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}

/// <summary>
/// A source over a seekable <see cref="Stream"/>. Reads are serialised, because a stream has one position.
/// </summary>
public sealed class StreamSource : IRandomAccessSource
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _length;

    /// <summary>Wraps a stream. Its length is taken now.</summary>
    /// <param name="stream">A readable, seekable stream.</param>
    /// <param name="leaveOpen">Whether disposing the source leaves the stream open (the default).</param>
    /// <exception cref="ArgumentException">The stream cannot seek or read.</exception>
    public StreamSource(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _length = stream.Length;
    }

    /// <inheritdoc/>
    public long Length => _length;

    /// <inheritdoc/>
    public bool SupportsSyncRead => true;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        _gate.Wait();
        try
        {
            _stream.Position = offset;
            return _stream.Read(buffer);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream.Position = offset;
            return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        _gate.Dispose();
    }
}

/// <summary>A source over a file on disk, read with positioned reads so any number of readers can share it.</summary>
public sealed class FileSource : IRandomAccessSource
{
    private readonly SafeFileHandle _handle;
    private readonly long _length;

    private FileSource(SafeFileHandle handle)
    {
        _handle = handle;
        _length = RandomAccess.GetLength(handle);
    }

    /// <summary>Opens a file for reading, sharing it with other readers.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The source. Dispose it to close the file.</returns>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    public static FileSource Open(string path) => Open(path, FileShare.Read);

    /// <summary>Opens a file for reading with the given sharing, for files another program may be writing or deleting.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="share">What other openers may do while the source is open.</param>
    /// <returns>The source. Dispose it to close the file.</returns>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    public static FileSource Open(string path, FileShare share)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, share, FileOptions.Asynchronous | FileOptions.RandomAccess);
        return new FileSource(handle);
    }

    /// <inheritdoc/>
    public long Length => _length;

    /// <inheritdoc/>
    public bool SupportsSyncRead => true;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return offset >= _length ? 0 : RandomAccess.Read(_handle, buffer, offset);
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return offset >= _length ? new ValueTask<int>(0) : RandomAccess.ReadAsync(_handle, buffer, offset, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// A source built from ranged reads the caller supplies: HTTP range requests, blob storage, a browser <c>Blob</c> slice.
/// </summary>
public sealed class RangedSource : IRandomAccessSource
{
    private readonly Func<long, Memory<byte>, CancellationToken, ValueTask<int>> _readAsync;
    private readonly Func<long, Span<byte>, int>? _read;
    private readonly IDisposable? _owner;

    /// <summary>Creates a source from read callbacks.</summary>
    /// <param name="length">The total length in bytes.</param>
    /// <param name="readAsync">Reads at an offset into the memory and returns the count read (short reads are allowed).</param>
    /// <param name="read">An optional synchronous twin. Without it the source is asynchronous only.</param>
    /// <param name="owner">Disposed with the source, if given.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public RangedSource(long length, Func<long, Memory<byte>, CancellationToken, ValueTask<int>> readAsync, Func<long, Span<byte>, int>? read = null, IDisposable? owner = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(readAsync);
        Length = length;
        _readAsync = readAsync;
        _read = read;
        _owner = owner;
    }

    /// <inheritdoc/>
    public long Length { get; }

    /// <inheritdoc/>
    public bool SupportsSyncRead => _read is not null;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (_read is null)
        {
            throw new NotSupportedException("This source can only be read asynchronously.");
        }

        if (offset >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > Length - offset)
        {
            buffer = buffer[..(int)(Length - offset)];
        }

        return _read(offset, buffer);
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
        {
            return new ValueTask<int>(0);
        }

        if (buffer.Length > Length - offset)
        {
            buffer = buffer[..(int)(Length - offset)];
        }

        return _readAsync(offset, buffer, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose() => _owner?.Dispose();
}

/// <summary>Helpers over <see cref="IRandomAccessSource"/>.</summary>
public static class RandomAccessSourceExtensions
{
    /// <summary>Reads until <paramref name="buffer"/> is full or the source ends.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">Where to start.</param>
    /// <param name="buffer">Where the bytes go.</param>
    /// <returns>The number of bytes read, smaller than the buffer only at the end of the source.</returns>
    public static int ReadFully(this IRandomAccessSource source, long offset, Span<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(source);
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.Read(offset + total, buffer[total..]);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>Asynchronous twin of <see cref="ReadFully"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="offset">Where to start.</param>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read, smaller than the buffer only at the end of the source.</returns>
    public static async ValueTask<int> ReadFullyAsync(this IRandomAccessSource source, long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await source.ReadAsync(offset + total, buffer[total..], cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>Opens a read-only, seekable <see cref="Stream"/> over a range of the source. Reads are lazy.</summary>
    /// <param name="source">The source. The stream does not dispose it.</param>
    /// <param name="offset">First byte of the range.</param>
    /// <param name="length">Length of the range; it is clamped to the source.</param>
    /// <returns>The stream.</returns>
    public static Stream OpenRead(this IRandomAccessSource source, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SourceStream(source, offset, length);
    }
}

/// <summary>A lazy, read-only stream over a range of an <see cref="IRandomAccessSource"/>.</summary>
internal sealed class SourceStream : Stream
{
    private readonly IRandomAccessSource _source;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public SourceStream(IRandomAccessSource source, long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _source = source;
        _start = Math.Min(offset, source.Length);
        _length = Math.Min(length, source.Length - _start);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        int n = _source.Read(_start + _position, buffer);
        _position += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long remaining = _length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        int n = await _source.ReadAsync(_start + _position, buffer, cancellationToken).ConfigureAwait(false);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
