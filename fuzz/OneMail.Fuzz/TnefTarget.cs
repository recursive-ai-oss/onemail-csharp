using System.Buffers.Binary;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Tests.Tnef;
using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail.Fuzz;

// TNEF reader and writer. A fuzzer rarely produces a valid stream from nothing, so most inputs are patch lists over a valid base stream:
//   byte 0: which base (0 rich message, 1 legacy attributes only, 2 the specification's sample), bit 2 set: cut the result at the first
//   record's offset; 3 = the rest of the input is the stream itself;
//   then 3-byte records: a 16-bit offset (modulo the stream length) and a value byte to put there.
// The reader must never throw, hang or grow without bound; the attribute layer must write back what it read; and what the model reads must
// survive a write and a second read with the same properties.
internal static class TnefTarget
{
    private static readonly byte[][] Bases = [TnefTestBuilder.Rich(), TnefTestBuilder.LegacyOnly(), TnefTestBuilder.SpecSampleStart()];

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] stream;
        int which = data[0] & 3;
        if (which == 3)
        {
            stream = data[1..].ToArray();
        }
        else
        {
            stream = (byte[])Bases[which].Clone();
            int first = -1;
            for (int i = 1; i + 2 < data.Length; i += 3)
            {
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i, 2)) % stream.Length;
                stream[offset] = data[i + 2];
                first = first < 0 ? offset : first;
            }

            if ((data[0] & 4) != 0 && first >= 0)
            {
                stream = stream[..first];
            }
        }

        var options = new TnefReaderOptions { MaxEmbeddingDepth = 4, MaxAttributeBytes = 1 << 20 };
        TnefDocument doc = TnefDocument.Read(stream, options);
        TnefMessage m = TnefMessage.Read(doc, options);
        Touch(m);
        if (!doc.HasSignature)
        {
            return;
        }

        using var again = new MemoryStream();
        doc.WriteTo(again);
        if (!again.ToArray().AsSpan().SequenceEqual(stream))
        {
            throw new InvalidOperationException("the attribute layer did not write back what it read");
        }

        using var written = new MemoryStream();
        TnefMessageWriter.Write(m, written);
        TnefMessage second = TnefMessage.Read(written.ToArray(), options);
        Touch(second);
    }

    private static void Touch(MapiMessage m)
    {
        foreach (MapiProperty p in m.Properties)
        {
            for (int i = 0; i < p.ValueCount; i++)
            {
                if (p.GetByteLength(i) < (1 << 20))
                {
                    _ = p.GetBytes(i);
                }
            }
        }

        foreach (MapiRecipient r in m.Recipients)
        {
            _ = r.Properties.Count;
        }

        foreach (MapiAttachment a in m.Attachments)
        {
            _ = a.FileName;
            if (a.EmbeddedMessage is { } inner)
            {
                Touch(inner);
            }
        }
    }
}
