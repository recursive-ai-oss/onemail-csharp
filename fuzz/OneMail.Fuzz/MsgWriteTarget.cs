using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Tests.Msg;

namespace RecursiveAI.OneMail.Fuzz;

// The MSG writer: the input is a recipe for a random MAPI model (properties of every fixed and multi-valued type, named properties,
// recipients, attachments, embedded messages nested a few levels). The model is written with the exact options and read back, and the
// read-back must be property-exact. The writer may refuse a model with a typed MsgWriteException (limits are tightened so hostile
// recipes hit them); it must never throw anything else, and what it writes must always read.
internal static class MsgWriteTarget
{
    private static readonly MsgWriterOptions Options = new()
    {
        Strings = MsgStringMode.AsGiven,
        KeepEmptyStrings = true,
        MaxProperties = 64,
        MaxRecipients = 8,
        MaxAttachments = 8,
        MaxEmbeddingDepth = 4,
        MaxNamedProperties = 32,
        MaxValueBytes = 1 << 16,
        MaxTotalBytes = 1 << 20,
    };

    private static readonly Guid[] Sets = [MapiPropertySets.Common, MapiPropertySets.PublicStrings, MapiPropertySets.InternetHeaders, MapiPropertySets.Mapi];

    private static readonly MapiType[] Types =
    [
        MapiType.Integer16, MapiType.Integer32, MapiType.Floating32, MapiType.Floating64, MapiType.Integer64,
        MapiType.UnicodeString, MapiType.Binary, MapiType.Time, MapiType.GuidValue,
        MapiType.MultipleInteger32, MapiType.MultipleString, MapiType.MultipleBinary,
    ];

    public static void Run(ReadOnlySpan<byte> data)
    {
        var rng = new Recipe(data);
        MapiMessage model = Message(rng, null, 0);
        byte[] file;
        try
        {
            file = MsgWriter.ToArray(model, Options);
        }
        catch (MsgWriteException)
        {
            return;
        }

        if (!MsgFile.TryOpen(file, out MsgFile? f, out IReadOnlyList<MapiDefect> openDefects))
        {
            throw new InvalidOperationException("the written file does not open: " + string.Join("; ", openDefects));
        }

        using (f)
        {
            f.ReadEverything();
            if (f.Defects.Count != 0)
            {
                throw new InvalidOperationException("the written file reads with defects: " + f.Defects[0]);
            }

            List<string> diffs = MsgModelComparer.Differences(model, f.Message, ignore: k => k is "340D0003" or "3FFD0003" or "3FDE0003");
            if (diffs.Count != 0)
            {
                throw new InvalidOperationException("read-back differs: " + diffs[0]);
            }
        }
    }

    private static MapiMessage Message(Recipe r, MapiObject? parent, int depth)
    {
        var m = new MapiMessage(parent);
        m.Properties.Add(Text(0x001A, "IPM.Note"));
        Properties(r, m, 0x6000);
        int recipients = r.Next() % 4;
        for (int i = 0; i < recipients; i++)
        {
            var rc = new MapiRecipient(m);
            Properties(r, rc, 0x6000);
            m.AddRecipient(rc);
        }

        int attachments = depth >= 4 ? 0 : r.Next() % 4;
        for (int i = 0; i < attachments; i++)
        {
            var a = new MapiAttachment(m);
            Properties(r, a, 0x6000);
            if (r.Next() % 3 == 0)
            {
                a.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x3705, MapiType.Integer32), BitConverter.GetBytes(5)));
                a.SetEmbeddedMessage(Message(r, a, depth + 1));
            }

            m.AddAttachment(a);
        }

        return m;
    }

    private static void Properties(Recipe r, MapiObject o, ushort baseId)
    {
        int count = r.Next() % 12;
        var used = new HashSet<ushort>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            MapiType type = Types[r.Next() % Types.Length];
            bool named = r.Next() % 3 == 0;
            MapiNamedProperty? name = null;
            ushort id = (ushort)(baseId + 1 + r.Next() % 0x400);
            if (named)
            {
                Guid set = Sets[r.Next() % Sets.Length];
                bool stringName = set == MapiPropertySets.PublicStrings || set == MapiPropertySets.InternetHeaders || r.Next() % 4 == 0;
                name = stringName ? new MapiNamedProperty(set, "n" + (r.Next() % 6)) : new MapiNamedProperty(set, (uint)(0x8000 + r.Next() % 6));
                id = (ushort)(0x8000 + r.Next() % 0x40);
                // One property per name; the writer keeps the later of two and reports the other, which would not read back.
                if (!names.Add(name.Value.ToString()))
                {
                    continue;
                }
            }

            if (!used.Add(id) || (id >= 0x3000 && id < 0x6000))
            {
                continue;
            }

            o.Properties.Add(Make(r, new MapiTag(id, type), name));
        }
    }

    private static MapiProperty Make(Recipe r, MapiTag tag, MapiNamedProperty? name)
    {
        switch (tag.PropertyType)
        {
            case MapiType.MultipleInteger32:
                return MapiProperty.CreateMulti(tag, Enumerable.Range(0, r.Next() % 5).Select(_ => r.Bytes(4)).ToArray(), name);
            case MapiType.MultipleString:
                return MapiProperty.CreateMulti(tag, Enumerable.Range(0, r.Next() % 4).Select(_ => Encoding.Unicode.GetBytes(r.String())).ToArray(), name);
            case MapiType.MultipleBinary:
                return MapiProperty.CreateMulti(tag, Enumerable.Range(0, r.Next() % 4).Select(_ => r.Bytes(r.Next() % 40)).ToArray(), name);
            case MapiType.UnicodeString:
                return MapiProperty.CreateOwned(tag, Encoding.Unicode.GetBytes(r.String()), name);
            case MapiType.Binary:
                return MapiProperty.CreateOwned(tag, r.Bytes(r.Next() % 300), name);
            case MapiType.Integer16:
                return MapiProperty.CreateOwned(tag, r.Bytes(2), name);
            case MapiType.Integer32:
            case MapiType.Floating32:
                return MapiProperty.CreateOwned(tag, r.Bytes(4), name);
            case MapiType.GuidValue:
                return MapiProperty.CreateOwned(tag, r.Bytes(16), name);
            default:
                return MapiProperty.CreateOwned(tag, r.Bytes(8), name);
        }
    }

    private static MapiProperty Text(ushort id, string s) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(s));

    // Reads the recipe left to right, then yields zeros, so every input is a valid recipe.
    private sealed class Recipe(ReadOnlySpan<byte> data)
    {
        private readonly byte[] _data = data.ToArray();
        private int _at;

        public int Next() => _at < _data.Length ? _data[_at++] : 0;

        public byte[] Bytes(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++)
            {
                b[i] = (byte)Next();
            }

            return b;
        }

        // Well-formed UTF-16 only: the model's strings are bytes, and the reader would not keep a lone surrogate's bytes as text.
        public string String()
        {
            var sb = new StringBuilder();
            int n = Next() % 24;
            for (int i = 0; i < n; i++)
            {
                int c = Next() | (Next() << 8);
                if (c is >= 0xD800 and <= 0xDFFF)
                {
                    sb.Append("\U0001F600");
                }
                else
                {
                    sb.Append((char)(c == 0 ? 'a' : c));
                }
            }

            return sb.ToString();
        }
    }
}
