using System;
using System.IO;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;

namespace RecursiveAI.OneMail.PstOracle;

// One-item PSTs that differ in one property, to find which one Outlook cannot take. Test-side only.
//   PstOracle mkvariants <out dir>
internal static class Variants
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var cases = new (string Name, Action<MapiMessage> Fill)[]
        {
            ("bare", m => { }),
            ("subject", m => Str(m, 0x0037, "x")),
            ("subject-empty", m => Str(m, 0x0037, "")),
            ("body", m => Str(m, 0x1000, "x")),
            ("sender", m => Str(m, 0x0C1A, "Sender")),
            ("size", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E08, MapiType.Integer32), BitConverter.GetBytes(100)))),
            ("flags-read", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E07, MapiType.Integer32), BitConverter.GetBytes(1)))),
            ("submit", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0039, MapiType.Time), BitConverter.GetBytes(133_500_000_000_000_000L)))),
            ("class-empty", m => Str(m, 0x001A, "")),
            ("no-class", m => { }),
            ("att-flags0", m => { Flags(m, 0); Attach(m); }),
            ("att-noflagprop", m => Attach(m)),
            ("att-flags10", m => { Flags(m, 0x10); Attach(m); }),
            ("flags10-noatt", m => Flags(m, 0x10)),
        };
        foreach ((string name, Action<MapiMessage> fill) in cases)
        {
            string path = Path.Combine(dir, name + ".pst");
            File.Delete(path);
            using PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero) });
            var m = new MapiMessage();
            if (name != "no-class" && name != "class-empty")
            {
                Str(m, 0x001A, "IPM.Note");
            }

            fill(m);
            b.IpmSubtree.CreateFolder("Folder").AddMessage(m);
            b.Complete();
        }

        // Folders of many items, to find what Outlook cannot read in a large contents table: rows (the row matrix grows) against cell text (the heap grows).
        foreach ((string name, int count, int subjectChars) in new[] { ("rows300-short", 300, 1), ("rows100-long", 100, 200), ("rows60-long", 60, 200), ("rows600-short", 600, 1) })
        {
            string path = Path.Combine(dir, name + ".pst");
            File.Delete(path);
            using PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero) });
            PstFolderWriter folder = b.IpmSubtree.CreateFolder("Folder");
            for (int i = 0; i < count; i++)
            {
                var m = new MapiMessage();
                Str(m, 0x001A, "IPM.Note");
                Str(m, 0x0037, i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('s', Math.Max(0, subjectChars - 4)));
                folder.AddMessage(m);
            }

            b.Complete();
        }

        return 0;
    }

    private static void Flags(MapiMessage m, int flags) =>
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E07, MapiType.Integer32), BitConverter.GetBytes(flags)));

    private static void Attach(MapiMessage m)
    {
        var a = new MapiAttachment(m);
        a.Properties.Add(MapiProperty.Create(new MapiTag(0x3705, MapiType.Integer32), BitConverter.GetBytes(1)));
        a.Properties.Add(MapiProperty.Create(new MapiTag(0x3707, MapiType.UnicodeString), Encoding.Unicode.GetBytes("file.bin")));
        a.Properties.Add(MapiProperty.Create(new MapiTag(0x3701, MapiType.Binary), new byte[500]));
        m.AddAttachment(a);
    }

    private static void Str(MapiMessage m, ushort id, string v) =>
        m.Properties.Add(MapiProperty.Create(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v)));
}
