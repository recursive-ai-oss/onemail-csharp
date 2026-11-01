using System.IO.Pipelines;
using System.Text;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Tests.Client;

/// <summary>A stream that serves scripted bytes to the reader and records what the writer sends.</summary>
internal sealed class ScriptedStream(byte[] script) : Stream
{
    private int _pos;

    public MemoryStream Sent { get; } = new();

    public string SentText => Encoding.UTF8.GetString(Sent.ToArray());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    // Serve at most 7 bytes at a time so every parser sees lines and literals split across reads.
    public override int Read(Span<byte> buffer)
    {
        int n = Math.Min(Math.Min(buffer.Length, 7), script.Length - _pos);
        script.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));
    public override void Write(byte[] buffer, int offset, int count) => Sent.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => Sent.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Sent.Write(buffer.Span); return ValueTask.CompletedTask; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal static class ClientTestSupport
{
    public static (ProtocolConnection Connection, ScriptedStream Stream) Scripted(string script, ProtocolConnectionOptions? options = null)
    {
        var s = new ScriptedStream(Encoding.UTF8.GetBytes(script));
        return (ProtocolConnection.FromStream(s, "localhost", options), s);
    }

    public static (ProtocolConnection Connection, ScriptedStream Stream) Scripted(byte[] script, ProtocolConnectionOptions? options = null)
    {
        var s = new ScriptedStream(script);
        return (ProtocolConnection.FromStream(s, "localhost", options), s);
    }

    /// <summary>Two connected streams: what one writes the other reads.</summary>
    public static (Stream Client, Stream Server) Pair()
    {
        var a = new Pipe();
        var b = new Pipe();
        return (new JoinedStream(b.Reader.AsStream(), a.Writer.AsStream()), new JoinedStream(a.Reader.AsStream(), b.Writer.AsStream()));
    }

    private sealed class JoinedStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
