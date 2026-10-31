using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// The only thing the NDB layer needs from storage: read a range at an absolute offset. It maps one-to-one onto the
/// random-access source type of the sources layer.
/// </summary>
internal interface IRandomAccess : IDisposable
{
    /// <summary>The length in bytes.</summary>
    long Length { get; }

    /// <summary>Reads up to <paramref name="destination"/>.Length bytes at <paramref name="offset"/>; returns the count, short only at the end.</summary>
    int Read(long offset, Span<byte> destination);

    /// <summary>The asynchronous twin of <see cref="Read"/>.</summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}

internal sealed class MemoryAccess(ReadOnlyMemory<byte> data) : IRandomAccess
{
    public long Length => data.Length;

    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return 0;
        }

        int n = (int)Math.Min(destination.Length, data.Length - offset);
        data.Span.Slice((int)offset, n).CopyTo(destination);
        return n;
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        new(Read(offset, destination.Span));

    public void Dispose()
    {
    }
}

internal sealed class FileRandomAccess : IRandomAccess
{
    private readonly SafeFileHandle _handle;
    private readonly long _length;

    public FileRandomAccess(string path)
    {
        // Read-only, shared: other readers (and Outlook) may have the file open.
        _handle = File.OpenHandle(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        _length = RandomAccess.GetLength(_handle);
    }

    public long Length => _length;

    public int Read(long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = await RandomAccess.ReadAsync(_handle, destination[total..], offset + total, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public void Dispose() => _handle.Dispose();
}

internal sealed class StreamAccess(Stream stream, bool leaveOpen) : IRandomAccess
{
    public long Length => stream.Length;

    public int Read(long offset, Span<byte> destination)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < destination.Length)
        {
            int n = stream.Read(destination[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < destination.Length)
        {
            int n = await stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public void Dispose()
    {
        if (!leaveOpen)
        {
            stream.Dispose();
        }
    }
}
