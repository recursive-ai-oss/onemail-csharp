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
            ("g-0f03", m => Bin(m, 0x0F03, 16)),
            ("g-3013", m => Bin(m, 0x3013, 16)),
            ("g-0017", m => Int(m, 0x0017, 1)),
            ("g-0036", m => Int(m, 0x0036, 0)),
            ("g-0057", m => { Bool(m, 0x0057, true); Bool(m, 0x0058, false); }),
            ("g-times", m => { Time(m, 0x0E06); Time(m, 0x0039); }),
            ("g-0e04", m => { Str(m, 0x0E04, "To Someone"); Str(m, 0x0E03, ""); }),
            ("g-topic", m => { Str(m, 0x0070, "x"); Bin(m, 0x0071, 22); Str(m, 0x0042, "Sender"); }),
            ("g-65e2", m => Bin(m, 0x65E2, 22)),
            ("g-all", m => { Bin(m, 0x0F03, 16); Bin(m, 0x3013, 16); Int(m, 0x0017, 1); Int(m, 0x0036, 0); Bool(m, 0x0057, true); Bool(m, 0x0058, false); Time(m, 0x0E06); Time(m, 0x0039); Str(m, 0x0E04, "To Someone"); Str(m, 0x0070, "x"); Bin(m, 0x0071, 22); Str(m, 0x0042, "Sender"); Bin(m, 0x65E2, 22); }),
            ("size318", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E08, MapiType.Integer32), BitConverter.GetBytes(318)))),
            ("size418", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E08, MapiType.Integer32), BitConverter.GetBytes(418)))),
            ("size474", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E08, MapiType.Integer32), BitConverter.GetBytes(474)))),
            ("size1000", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E08, MapiType.Integer32), BitConverter.GetBytes(1000)))),
            ("flags-read", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E07, MapiType.Integer32), BitConverter.GetBytes(1)))),
            ("submit", m => m.Properties.Add(MapiProperty.Create(new MapiTag(0x0039, MapiType.Time), BitConverter.GetBytes(133_500_000_000_000_000L)))),
            ("class-empty", m => Str(m, 0x001A, "")),
            ("no-class", m => { }),
            ("att-flags0", m => { Flags(m, 0); Attach(m); }),
            ("att-noflagprop", m => Attach(m)),
            ("att-flags10", m => { Flags(m, 0x10); Attach(m); }),
            ("flags10-noatt", m => Flags(m, 0x10)),
            ("rcpt-nameonly", m => Rcpt(m, false)),
            ("rcpt-type", m => Rcpt(m, true)),
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

        // A small realistic store: two folders, five messages with recipients and bodies, one with an attachment.
        {
            string path = Path.Combine(dir, "full.pst");
            File.Delete(path);
            using PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero) });
            foreach (string fname in new[] { "Inbox", "Sent" })
            {
                PstFolderWriter folder = b.IpmSubtree.CreateFolder(fname);
                for (int i = 0; i < 5; i++)
                {
                    var m = new MapiMessage();
                    Str(m, 0x001A, "IPM.Note");
                    Str(m, 0x0037, fname + " message " + i);
                    Str(m, 0x1000, "Body of message " + i);
                    Str(m, 0x0C1A, "Sender");
                    Rcpt(m, true);
                    if (i == 2)
                    {
                        Attach(m);
                    }

                    folder.AddMessage(m);
                }
            }

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

    private static void Bin(MapiMessage m, ushort id, int n) =>
        m.Properties.Add(MapiProperty.Create(new MapiTag(id, MapiType.Binary), System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(1, n), i => (byte)(i * 7)))));

    private static void Int(MapiMessage m, ushort id, int v) =>
        m.Properties.Add(MapiProperty.Create(new MapiTag(id, MapiType.Integer32), BitConverter.GetBytes(v)));

    private static void Bool(MapiMessage m, ushort id, bool v) =>
        m.Properties.Add(MapiProperty.Create(new MapiTag(id, MapiType.Boolean), [(byte)(v ? 1 : 0)]));

    private static void Time(MapiMessage m, ushort id) =>
        m.Properties.Add(MapiProperty.Create(new MapiTag(id, MapiType.Time), BitConverter.GetBytes(133_500_000_000_000_000L)));

    private static void Flags(MapiMessage m, int flags) =>
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E07, MapiType.Integer32), BitConverter.GetBytes(flags)));

    private static void Rcpt(MapiMessage m, bool withType)
    {
        var r = new MapiRecipient(m);
        r.Properties.Add(MapiProperty.Create(new MapiTag(0x3001, MapiType.UnicodeString), Encoding.Unicode.GetBytes("Someone")));
        if (withType)
        {
            r.Properties.Add(MapiProperty.Create(new MapiTag(0x0C15, MapiType.Integer32), BitConverter.GetBytes(1)));
        }

        m.AddRecipient(r);
    }

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
