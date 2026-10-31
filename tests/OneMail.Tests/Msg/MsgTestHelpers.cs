using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Tests.Msg;

internal static class MsgTestHelpers
{
    public static MsgFile Open(byte[] file, MsgReaderOptions? options = null)
    {
        Assert.True(MsgFile.TryOpen(file, out MsgFile? f, out IReadOnlyList<MapiDefect> defects, options), string.Join("; ", defects));
        return f;
    }

    public static MsgFile? TryOpen(byte[] file, MsgReaderOptions? options = null) =>
        MsgFile.TryOpen(file, out MsgFile? f, out _, options) ? f : null;

    /// <summary>Visits a message, its recipients, attachments and embedded messages.</summary>
    public static IEnumerable<MapiObject> Walk(MapiMessage m)
    {
        yield return m;
        foreach (MapiRecipient r in m.Recipients)
        {
            yield return r;
        }

        foreach (MapiAttachment a in m.Attachments)
        {
            yield return a;
            if (a.EmbeddedMessage is { } inner)
            {
                foreach (MapiObject o in Walk(inner))
                {
                    yield return o;
                }
            }
        }
    }

    /// <summary>Re-encodes every object and compares its property and value streams with the file's. Returns the differences.</summary>
    public static List<string> RoundTripDifferences(byte[] file, MsgFile msg)
    {
        var diffs = new List<string>();
        using var reader = new CompoundFileReader(file);
        foreach (MapiObject o in Walk(msg.Message))
        {
            var item = (IMsgItem)o;
            CfbEntry storage = item.Layout.Path.Length == 0 ? reader.Root : reader.Find(item.Layout.Path.TrimStart('/'))!;
            var original = reader.GetChildren(storage)
                .Where(k => k.ObjectType == CfbObjectType.Stream && (k.RawName.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase) || k.RawName == "__properties_version1.0"))
                .ToDictionary(k => k.RawName.ToUpperInvariant());
            foreach (MsgStreamData s in MsgStreams.Encode(o, item.Layout))
            {
                if (!original.Remove(s.Name.ToUpperInvariant(), out CfbEntry? e))
                {
                    diffs.Add($"{item.Layout.Path}/{s.Name} written but not in the file");
                    continue;
                }

                var bytes = new byte[e.Size];
                using Stream st = reader.OpenEntryStream(e);
                st.ReadExactly(bytes);
                if (!bytes.AsSpan().SequenceEqual(s.Data))
                {
                    diffs.Add($"{item.Layout.Path}/{s.Name} differs");
                }
            }

            diffs.AddRange(original.Keys.Select(k => $"{item.Layout.Path}/{k} in the file but not written"));
        }

        return diffs;
    }
}

/// <summary>A source over memory that counts the bytes read, to show that opening does not read the value streams.</summary>
internal sealed class CountingSource(byte[] data) : IRandomAccessSource
{
    public long BytesRead { get; private set; }

    public long Length => data.Length;

    public bool SupportsSyncRead => true;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= data.Length)
        {
            return 0;
        }

        int n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, n).CopyTo(buffer);
        BytesRead += n;
        return n;
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(offset, buffer.Span));

    public void Dispose()
    {
    }
}
