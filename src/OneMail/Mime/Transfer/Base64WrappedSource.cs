using System.Buffers;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Mime.Transfer;

/// <summary>
/// The base64 form of some bytes (RFC 2045 §6.8: 76-character lines, CRLF between lines, no line end after the last), produced
/// only when it is read. A part built from a large attachment therefore never holds its encoded text: the writer pulls it a
/// chunk at a time and the encoded bytes go straight to the output.
/// </summary>
internal sealed class Base64WrappedSource : IRandomAccessSource
{
    private const int DataPerLine = 57;     // 57 bytes encode to exactly 76 characters
    private const int LineWithBreak = 78;   // 76 characters and CRLF

    private readonly byte[] _data;
    private readonly long _lineCount;

    /// <summary>Takes ownership of <paramref name="data"/>; the caller must not change it afterwards.</summary>
    public Base64WrappedSource(byte[] data)
    {
        _data = data;
        _lineCount = (data.Length + DataPerLine - 1) / DataPerLine;
        Length = data.Length == 0 ? 0 : (((long)data.Length + 2) / 3 * 4) + ((_lineCount - 1) * 2);
    }

    public long Length { get; }

    public bool SupportsSyncRead => true;

    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        int n = (int)Math.Min(buffer.Length, Length - offset);
        long firstLine = offset / LineWithBreak;
        if (offset % LineWithBreak == 0 && firstLine + (n / LineWithBreak) < _lineCount)
        {
            // Starts on a line: encode whole lines straight into the caller's buffer (a short read; the last line goes the general way).
            int lines = n / LineWithBreak;
            if (lines > 0)
            {
                EncodeLines(firstLine, lines, buffer.Slice(0, lines * LineWithBreak));
                return lines * LineWithBreak;
            }
        }

        // General case: encode every line the range touches into a scratch buffer and cut the range out of it.
        long lastLine = (offset + n - 1) / LineWithBreak;
        int count = (int)(lastLine - firstLine + 1);
        int size = count * LineWithBreak;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int produced = EncodeLines(firstLine, count, scratch.AsSpan(0, size));
            int skip = (int)(offset - (firstLine * LineWithBreak));
            int take = Math.Min(n, produced - skip);
            scratch.AsSpan(skip, take).CopyTo(buffer);
            return take;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(offset, buffer.Span));
    }

    public void Dispose()
    {
    }

    /// <summary>Writes <paramref name="count"/> lines starting at line <paramref name="first"/>, each followed by CRLF except the last line of the whole text. Returns the bytes written.</summary>
    private int EncodeLines(long first, int count, Span<byte> destination)
    {
        long dataStart = first * DataPerLine;
        int dataLength = (int)Math.Min((long)count * DataPerLine, _data.Length - dataStart);
        int encoded = (dataLength + 2) / 3 * 4;
        bool endsText = first + count == _lineCount;
        int total = encoded + ((endsText ? count - 1 : count) * 2);

        // Encode once, unbroken, into the end of the destination, then spread the lines forward with their line ends. A line's
        // destination never starts after its source and ends before the next line's source, so nothing is overwritten unread.
        int tail = total - encoded;
        _ = System.Buffers.Text.Base64.EncodeToUtf8(_data.AsSpan((int)dataStart, dataLength), destination.Slice(tail), out _, out _);
        int o = 0;
        for (int k = 0; k < count; k++)
        {
            int length = Math.Min(76, encoded - (k * 76));
            destination.Slice(tail + (k * 76), length).CopyTo(destination.Slice(o));
            o += length;
            if (k + 1 < count || !endsText)
            {
                destination[o++] = (byte)'\r';
                destination[o++] = (byte)'\n';
            }
        }

        return total;
    }
}
