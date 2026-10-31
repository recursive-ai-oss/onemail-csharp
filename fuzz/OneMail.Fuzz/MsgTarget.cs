using System.Buffers.Binary;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Tests.Msg;

namespace RecursiveAI.OneMail.Fuzz;

// The MSG reader on damaged property streams and named property maps. A fuzzer rarely produces a valid compound file from nothing,
// and the container is OneArchive's to validate, so most inputs are patch lists over the streams of a valid file made by the test
// builder; the damaged streams are packed into a new compound file and read:
//   byte 0: which base file (0 all types, 1 recipients and attachments, 2 ANSI Russian; 3 = the rest of the input is the file itself)
//   then 4-byte records: stream index (low 7 bits, modulo the stream count; the high bit means "cut the stream at the offset"),
//   a 16-bit offset (modulo the stream length) and a value byte to put there. The record 0xFF, 0xFF, 0xFF, 0xFF deletes the stream.
// The reader must never throw, hang or grow without bound, whatever the streams hold.
internal static class MsgTarget
{
    private static readonly (List<(string Path, byte[] Data)> Streams, List<string> Empty, bool Unicode)[] Bases =
    [
        Make(MsgFixtures.AllTypes(), true),
        Make(MsgFixtures.Conversation(), true),
        Make(MsgFixtures.AnsiRussian(), false),
    ];

    private static (List<(string Path, byte[] Data)>, List<string>, bool) Make(BuildObject o, bool unicode)
    {
        (List<(string Path, byte[] Data)> s, List<string> e) = MsgTestBuilder.BuildStreams(o, unicode);
        return (s, e, unicode);
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (BuildFile(data) is not { } file)
        {
            return;
        }

        if (!MsgFile.TryOpen(file, out MsgFile? f, out _, new MsgReaderOptions { MaxValueBytes = 1 << 20 }))
        {
            return;
        }

        using (f)
        {
            f.ReadEverything();
            Touch(f.Message);
        }
    }

    /// <summary>Makes the compound file a patch-list input stands for, or null when it stands for none.</summary>
    internal static byte[]? BuildFile(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        byte[] file;
        int which = data[0] & 3;
        if (which == 3)
        {
            file = data[1..].ToArray();
        }
        else
        {
            (List<(string Path, byte[] Data)> baseStreams, List<string> empty, _) = Bases[which % Bases.Length];
            var streams = new List<(string Path, byte[] Data)>(baseStreams);
            for (int i = 1; i + 3 < data.Length; i += 4)
            {
                int index = (data[i] & 0x7F) % streams.Count;
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i + 1, 2));
                byte value = data[i + 3];
                if (data[i] == 0xFF && offset == 0xFFFF && value == 0xFF)
                {
                    streams.RemoveAt(index);
                    if (streams.Count == 0)
                    {
                        return null;
                    }

                    continue;
                }

                byte[] bytes = streams[index].Data.ToArray();
                if ((data[i] & 0x80) != 0)
                {
                    bytes = bytes[..(bytes.Length == 0 ? 0 : offset % (bytes.Length + 1))];
                }
                else if (bytes.Length > 0)
                {
                    bytes[offset % bytes.Length] = value;
                }

                streams[index] = (streams[index].Path, bytes);
            }

            file = MsgTestBuilder.Pack(streams, empty);
        }

        return file;
    }

    private static void Touch(MapiMessage m)
    {
        TouchProps(m);
        foreach (MapiRecipient r in m.Recipients)
        {
            TouchProps(r);
        }

        foreach (MapiAttachment a in m.Attachments)
        {
            TouchProps(a);
            if (a.EmbeddedMessage is { } inner)
            {
                Touch(inner);
            }
            else if (a.ObjectStorage is { } st)
            {
                foreach (MapiRawStorageEntry e in st.Entries.Take(20))
                {
                    if (!e.IsStorage)
                    {
                        using Stream s = st.OpenStream(e.Path);
                        s.ReadExactly(new byte[Math.Min(e.Length, 4096)]);
                    }
                }
            }
        }
    }

    private static void TouchProps(MapiObject o)
    {
        foreach (MapiProperty p in o.Properties)
        {
            for (int i = 0; i < Math.Min(p.ValueCount, 64); i++)
            {
                _ = p.GetBytes(i);
            }

            _ = p.AsString(o.String8Encoding);
            _ = p.AsTime();
            _ = p.AsFloatingTime();
            _ = p.AsGuid();
            _ = p.AsInt64();
            _ = p.Name;
        }

        _ = o.String8Encoding;
    }
}
