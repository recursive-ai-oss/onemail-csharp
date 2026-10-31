using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Tests.Msg;

namespace ContactTool;

// Generated contact and distribution list MSG files that cover the MS-OXOCNTC properties the conversions touch: every telephone slot, the three
// addresses, three email and fax slots, instant messaging, user fields, dates, categories, a photo, empty strings, odd characters. The seed makes
// the files reproducible: `ContactTool gen DIR [seed] [count]`.
internal static class ContactGen
{
    private static uint T(ushort id, MapiType t) => ((uint)id << 16) | (ushort)t;

    private static MapiNamedProperty Addr(uint lid) => new(MapiPropertySets.Address, lid);

    private static readonly string[] Words = ["Anna", "Müller", "O'Brien", "李雷", "Zoë", "Smith, Jr.", "R;D", "a\\b", "Ünal", "Dan", "Fennell", "Contoso, Ltd.", "Åsa", "x:y", "\"quoted\""];

    private static string Text(Random r, bool multiline = false)
    {
        int n = r.Next(1, 4);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(multiline && r.Next(3) == 0 ? "\r\n" : " ");
            }

            sb.Append(Words[r.Next(Words.Length)]);
        }

        return sb.ToString();
    }

    private static readonly ushort[] TaggedText =
    [
        0x3001, 0x3A06, 0x3A11, 0x3A44, 0x3A45, 0x3A4F, 0x3A16, 0x3A18, 0x3A17, 0x3A46, 0x3A50, 0x3A51, 0x3A30, 0x3A4E, 0x3A48, 0x3A43,
        0x3A59, 0x3A5A, 0x3A5B, 0x3A5C, 0x3A5D, 0x3A5E, 0x3A5F, 0x3A60, 0x3A61, 0x3A62, 0x3A63, 0x3A64,
        0x3A08, 0x3A1B, 0x3A09, 0x3A2F, 0x3A1C, 0x3A1E, 0x3A21, 0x3A1D, 0x3A1A, 0x3A1F, 0x3A57, 0x3A2E, 0x3A02, 0x3A2D, 0x3A2C, 0x3A4B, 0x3A24, 0x3A25, 0x3A23,
    ];

    private static readonly int[] Sensitivities = [0, 2, 3, 1];

    private static readonly uint[] NamedText = [0x8045, 0x8046, 0x8047, 0x8048, 0x8049, 0x804A, 0x8062, 0x804F, 0x8050, 0x8051, 0x8052, 0x80D8];

    private static byte[] OneOff(string name, string type, string address)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[4]);
        ms.Write([0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02]);
        ms.Write([0, 0, 0x90, 0x01]);
        foreach (string s in new[] { name, type, address })
        {
            ms.Write(Encoding.Unicode.GetBytes(s));
            ms.Write([0, 0]);
        }

        return ms.ToArray();
    }

    private static byte[] Wrapped(byte type, byte[] embedded)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[4]);
        ms.Write([0xC0, 0x91, 0xAD, 0xD3, 0x51, 0x9D, 0xCF, 0x11, 0xA4, 0xA9, 0x00, 0xAA, 0x00, 0x47, 0xFA, 0xA4]);
        ms.WriteByte(type);
        ms.Write(embedded);
        return ms.ToArray();
    }

    private static ulong Ft(int y, int mo, int d, int h = 0) => MsgTestBuilder.FileTime(new DateTime(y, mo, d, h, 0, 0, DateTimeKind.Utc));

    private static BuildObject Contact(Random r, int index)
    {
        var m = new BuildObject();
        m.Props.Add(new BuildProp(T(MapiPid.MessageClass, MapiType.UnicodeString), "IPM.Contact"));
        foreach (ushort id in TaggedText)
        {
            int roll = r.Next(10);
            if (roll < 4 && id != 0x3001)
            {
                continue;
            }

            // One in ten is the empty string, which a vCard cannot tell from no value.
            m.Props.Add(new BuildProp(T(id, MapiType.UnicodeString), roll == 9 ? string.Empty : Text(r, id is >= 0x3A59 and <= 0x3A64)));
        }

        foreach (uint lid in NamedText)
        {
            if (r.Next(3) > 0)
            {
                m.Props.Add(new BuildProp(T(0x8000, MapiType.UnicodeString), r.Next(12) == 0 ? string.Empty : Text(r, lid == 0x8045), Addr(lid)));
            }
        }

        for (uint slot = 0; slot < 3; slot++)
        {
            if (r.Next(4) == 0)
            {
                continue;
            }

            uint b = 0x8080 + (slot * 0x10);
            bool ex = r.Next(5) == 0;
            string addr = ex ? "/o=Org/ou=Group/cn=Recipients/cn=u" + index : "user" + index + "." + slot + "@example.com";
            m.Props.Add(new BuildProp(T(0x8000, MapiType.UnicodeString), ex ? "EX" : "SMTP", Addr(b + 2)));
            m.Props.Add(new BuildProp(T(0x8000, MapiType.UnicodeString), addr, Addr(b + 3)));
            m.Props.Add(new BuildProp(T(0x8000, MapiType.UnicodeString), Text(r) + " (" + addr + ")", Addr(b)));
            if (r.Next(2) == 0)
            {
                m.Props.Add(new BuildProp(T(0x8000, MapiType.UnicodeString), Text(r), Addr(b + 4)));
            }
        }

        if (r.Next(2) == 0)
        {
            m.Props.Add(new BuildProp(T(0x8000, MapiType.Integer32), r.Next(0, 4), Addr(0x8022)));
        }

        if (r.Next(2) == 0)
        {
            m.Props.Add(new BuildProp(T(0x1000, MapiType.UnicodeString), Text(r, true) + "\r\n" + Text(r)));
        }

        if (r.Next(2) == 0)
        {
            int y = r.Next(1920, 2020), mo = r.Next(1, 13), d = r.Next(1, 28);
            m.Props.Add(new BuildProp(T(MapiPid.Birthday, MapiType.Time), Ft(y, mo, d, 11)));
            if (r.Next(2) == 0)
            {
                m.Props.Add(new BuildProp(T(0x8000, MapiType.Time), Ft(y, mo, d), Addr(0x80DE)));
            }
        }

        if (r.Next(3) == 0)
        {
            m.Props.Add(new BuildProp(T(0x3A41, MapiType.Time), Ft(r.Next(1950, 2020), r.Next(1, 13), r.Next(1, 28), 11)));
        }

        if (r.Next(3) == 0)
        {
            m.Props.Add(new BuildProp(T(0x3A4D, MapiType.Integer16), (short)r.Next(0, 3)));
        }

        if (r.Next(3) == 0)
        {
            m.Props.Add(new BuildProp(T(0x0036, MapiType.Integer32), Sensitivities[r.Next(4)]));
        }

        if (r.Next(2) == 0)
        {
            m.Props.Add(new BuildProp(T(0x3A58, MapiType.MultipleString), new[] { Text(r), Text(r) }));
        }

        if (r.Next(2) == 0)
        {
            m.Props.Add(new BuildProp(T(0x8000, MapiType.MultipleString), new[] { "red", "a,b", Text(r) }, new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords")));
        }

        if (r.Next(3) == 0)
        {
            m.Props.Add(new BuildProp(T(0x3008, MapiType.Time), Ft(2024, r.Next(1, 13), r.Next(1, 28), r.Next(0, 24))));
        }

        if (r.Next(3) == 0)
        {
            byte[] img = new byte[r.Next(8, 64)];
            r.NextBytes(img);
            img[0] = 0xFF;
            img[1] = 0xD8;
            img[2] = 0xFF;
            var photo = new BuildAttachment();
            photo.Props.Add(new BuildProp(T(0x3705, MapiType.Integer32), 1));
            photo.Props.Add(new BuildProp(T(0x3701, MapiType.Binary), img));
            photo.Props.Add(new BuildProp(T(0x3703, MapiType.UnicodeString), ".jpg"));
            photo.Props.Add(new BuildProp(T(0x7FFF, MapiType.Boolean), true));
            m.Attachments.Add(photo);
        }

        return m;
    }

    private static BuildObject DistributionList(Random r, int index)
    {
        var m = new BuildObject();
        m.Props.Add(new BuildProp(T(MapiPid.MessageClass, MapiType.UnicodeString), "IPM.DistList"));
        m.Props.Add(new BuildProp(T(0x3001, MapiType.UnicodeString), "List " + index + " " + Text(r)));
        int n = r.Next(0, 6);
        var members = new List<byte[]>();
        var wrapped = new List<byte[]>();
        for (int i = 0; i < n; i++)
        {
            byte[] one = OneOff(Text(r), "SMTP", "member" + i + "@example.com");
            members.Add(one);
            wrapped.Add(r.Next(3) == 0 ? Wrapped((byte)(0x03 | 0x80 | (i << 4)), new byte[30]) : Wrapped(0, one));
        }

        m.Props.Add(new BuildProp(T(0x8000, MapiType.MultipleBinary), wrapped.ToArray(), Addr(0x8055)));
        m.Props.Add(new BuildProp(T(0x8000, MapiType.MultipleBinary), members.ToArray(), Addr(0x8054)));
        return m;
    }

    public static int Run(string dir, int seed, int count)
    {
        Directory.CreateDirectory(dir);
        var r = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            bool list = i % 8 == 7;
            BuildObject m = list ? DistributionList(r, i) : Contact(r, i);
            File.WriteAllBytes(Path.Combine(dir, (list ? "list" : "contact") + i.ToString("D3", CultureInfo.InvariantCulture) + ".msg"), MsgTestBuilder.Build(m));
        }

        Console.Error.WriteLine($"wrote {count} generated contact and list MSG files (seed {seed})");
        return 0;
    }
}
