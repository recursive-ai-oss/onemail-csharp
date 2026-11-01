using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Pst.Messaging;

/// <summary>
/// Holds the table rows of folders that are still open, so that a folder of any size costs a buffer of at most 32 KiB in memory. Rows are
/// appended per folder; a full buffer goes to one shared spool stream as a chunk, and the folder's chunks are read back in order when the
/// folder is closed and its tables are written.
/// </summary>
internal sealed class PstRowSpool : IDisposable
{
    private const int ChunkSize = 32 * 1024;
    private readonly Func<Stream>? _factory;
    private Stream? _stream;

    public PstRowSpool(Func<Stream>? factory) => _factory = factory;

    /// <summary>The rows of one folder's table.</summary>
    public sealed class Channel
    {
        internal readonly List<(long Offset, int Length)> Chunks = [];
        internal byte[] Data = new byte[256];
        internal int Length;
        public long Count { get; internal set; }
    }

    public static Channel Open() => new();

    /// <summary>Appends one row: a 32-bit row ID, then for each column a 32-bit length (-1 when the cell is absent) and the bytes.</summary>
    public void Append(Channel channel, uint rowId, ReadOnlySpan<byte[]?> cells)
    {
        int need = 4;
        foreach (byte[]? c in cells)
        {
            need += 4 + (c?.Length ?? 0);
        }

        if (channel.Length + need > channel.Data.Length)
        {
            Array.Resize(ref channel.Data, Math.Max(channel.Data.Length * 2, channel.Length + need));
        }

        Span<byte> w = channel.Data.AsSpan(channel.Length, need);
        BinaryPrimitives.WriteUInt32LittleEndian(w, rowId);
        int at = 4;
        foreach (byte[]? c in cells)
        {
            BinaryPrimitives.WriteInt32LittleEndian(w[at..], c is null ? -1 : c.Length);
            at += 4;
            if (c is not null)
            {
                c.CopyTo(w[at..]);
                at += c.Length;
            }
        }

        channel.Length += need;
        channel.Count++;
        if (channel.Length >= ChunkSize)
        {
            Flush(channel);
        }
    }

    private void Flush(Channel channel)
    {
        if (channel.Length == 0)
        {
            return;
        }

        _stream ??= (_factory ?? DefaultStream)();
        _stream.Seek(0, SeekOrigin.End);
        long at = _stream.Position;
        _stream.Write(channel.Data, 0, channel.Length);
        channel.Chunks.Add((at, channel.Length));
        channel.Length = 0;
    }

    private static Stream DefaultStream()
    {
        try
        {
            return new FileStream(Path.Combine(Path.GetTempPath(), "onemail-rows-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is PlatformNotSupportedException or UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return new MemoryStream();
        }
    }

    /// <summary>Reads the rows back in the order they were appended. The channel is empty afterwards.</summary>
    public IEnumerable<(uint RowId, byte[]?[] Cells)> ReadAll(Channel channel, int columns)
    {
        foreach ((long offset, int length) in channel.Chunks)
        {
            byte[] chunk = new byte[length];
            _stream!.Seek(offset, SeekOrigin.Begin);
            _stream.ReadExactly(chunk);
            foreach (var row in Parse(chunk, columns))
            {
                yield return row;
            }
        }

        channel.Chunks.Clear();
        byte[] tail = channel.Data.AsSpan(0, channel.Length).ToArray();
        channel.Length = 0;
        foreach (var row in Parse(tail, columns))
        {
            yield return row;
        }
    }

    private static IEnumerable<(uint RowId, byte[]?[] Cells)> Parse(byte[] data, int columns)
    {
        int at = 0;
        while (at < data.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            at += 4;
            var cells = new byte[columns][];
            for (int c = 0; c < columns; c++)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
                at += 4;
                if (len >= 0)
                {
                    cells[c] = data.AsSpan(at, len).ToArray();
                    at += len;
                }
            }

            yield return (id, cells);
        }
    }

    public void Dispose() => _stream?.Dispose();
}
