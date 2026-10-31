using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Msg;

/// <summary>A single value held in one stream; read when asked for.</summary>
internal sealed class MsgStreamValue(MsgContainer container, CfbEntry stream, string location) : MapiValueSource
{
    public override int Count => 1;

    public override long GetLength(int index) => stream.Size;

    public override byte[] Read(int index) => container.ReadAll(stream, location, container.Options.MaxValueBytes) ?? [];

    public override Stream OpenRead(int index) => container.OpenLocked(stream, location);
}

/// <summary>
/// A fixed-length multi-valued property: one stream with the elements back to back (MS-OXMSG 2.1.4.1). A trailing partial element is
/// kept as a last, short value so that nothing in the stream is dropped.
/// </summary>
internal sealed class MsgFixedMultiValue(MsgContainer container, CfbEntry stream, string location, int elementSize) : MapiValueSource
{
    private byte[]? _data;

    public override int Count => (int)Math.Min(int.MaxValue, (stream.Size + elementSize - 1) / elementSize);

    public override long GetLength(int index) => Math.Max(0, Math.Min(elementSize, stream.Size - ((long)index * elementSize)));

    public override byte[] Read(int index)
    {
        _data ??= container.ReadAll(stream, location, container.Options.MaxValueBytes) ?? [];
        long start = (long)index * elementSize;
        if (start >= _data.Length)
        {
            return [];
        }

        return _data.AsSpan((int)start, (int)Math.Min(elementSize, _data.Length - start)).ToArray();
    }
}

/// <summary>
/// A variable-length multi-valued property: a length stream was read when the object was opened, and each element has a stream of its own
/// (MS-OXMSG 2.1.4.2). An element with no stream reads as empty.
/// </summary>
internal sealed class MsgVariableMultiValue(MsgContainer container, CfbEntry?[] streams, string location) : MapiValueSource
{
    public override int Count => streams.Length;

    public override long GetLength(int index) => streams[index]?.Size ?? 0;

    public override byte[] Read(int index) =>
        streams[index] is { } s ? container.ReadAll(s, location + "-" + index.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), container.Options.MaxValueBytes) ?? [] : [];

    public override Stream OpenRead(int index) =>
        streams[index] is { } s ? container.OpenLocked(s, location) : new MemoryStream([], writable: false);
}
