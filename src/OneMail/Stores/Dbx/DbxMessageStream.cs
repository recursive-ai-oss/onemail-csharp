using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Dbx;

/// <summary>The text of one message as a forward-only stream over its chain of segments.</summary>
public sealed class DbxMessageStream : Stream
{
    private readonly DbxReader _reader;
    private DbxReader.ChainWalker _walker;
    private long _segmentOffset;
    private int _segmentLeft;
    private long _position;

    internal DbxMessageStream(DbxReader reader, long first)
    {
        _reader = reader;
        _walker = new DbxReader.ChainWalker(reader, first);
    }

    /// <summary>What was irregular in the part of the chain read so far.</summary>
    public StoreDefects Defects => _walker.Defects;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            if (_segmentLeft == 0 && !_walker.Advance(out _segmentOffset, out _segmentLeft))
            {
                break;
            }

            int n = Math.Min(_segmentLeft, buffer.Length - done);
            int got = _reader.Source.ReadFully(_segmentOffset, buffer.Slice(done, n));
            if (got == 0)
            {
                _segmentLeft = 0;
                continue;
            }

            _segmentOffset += got;
            _segmentLeft -= got;
            done += got;
        }

        _position += done;
        return done;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
