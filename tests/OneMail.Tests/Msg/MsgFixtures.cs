using System.Text;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Tests.Msg;

/// <summary>
/// The MSG files the tests, the fuzz target and the comparison with other readers start from. Each is built by <see cref="MsgTestBuilder"/>
/// from a description, so there is nothing to check in but the generator; the files in testdata/msg are its output.
/// </summary>
internal static class MsgFixtures
{
    public static readonly Guid CustomSet = new("11111111-2222-3333-4444-555555555555");

    public static IReadOnlyDictionary<string, byte[]> All() => new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["all-types.msg"] = Build(AllTypes()),
        ["recipients-attachments.msg"] = Build(Conversation()),
        ["recipients-embedded.msg"] = Build(Conversation(withOle: false)),
        ["ansi-cp1251.msg"] = Build(AnsiRussian(), unicode: false),
        ["ansi-terminator-in-stream.msg"] = Build(Simple("Terminator in the stream", ansi: true), unicode: false, terminatorInStream: true),
        ["template.msg"] = Build(Template()),
        ["empty.msg"] = Build(new BuildObject()),
    };

    private static byte[] Build(BuildObject o, bool unicode = true, bool terminatorInStream = false) => MsgTestBuilder.Build(o, unicode, terminatorInStream);

    /// <summary>An array from constants, built fresh each call so fixtures never share one.</summary>
    public static T[] Arr<T>(params ReadOnlySpan<T> values) => values.ToArray();

    public static uint T(ushort id, MapiType t) => ((uint)id << 16) | (ushort)t;

    public static BuildObject Simple(string subject, bool ansi = false)
    {
        var m = new BuildObject();
        m.Props.Add(new BuildProp(T(MapiPid.MessageClass, ansi ? MapiType.String8 : MapiType.UnicodeString), "IPM.Note"));
        m.Props.Add(new BuildProp(T(MapiPid.Subject, ansi ? MapiType.String8 : MapiType.UnicodeString), subject));
        m.Props.Add(new BuildProp(T(MapiPid.StoreSupportMask, MapiType.Integer32), ansi ? 0x0 : 0x00040000));
        if (ansi)
        {
            m.Props.Add(new BuildProp(T(MapiPid.MessageCodepage, MapiType.Integer32), 1252));
        }

        return m;
    }

    public static BuildObject AllTypes()
    {
        BuildObject m = Simple("Every property type");
        m.Props.Add(new BuildProp(T(0x6001, MapiType.Integer16), (short)-2));
        m.Props.Add(new BuildProp(T(0x6002, MapiType.Integer32), -70000));
        m.Props.Add(new BuildProp(T(0x6003, MapiType.Floating32), 1.5f));
        m.Props.Add(new BuildProp(T(0x6004, MapiType.Floating64), -2.25d));
        m.Props.Add(new BuildProp(T(0x6005, MapiType.Currency), 123456789L));
        m.Props.Add(new BuildProp(T(0x6006, MapiType.FloatingTime), 40000.5d));
        m.Props.Add(new BuildProp(T(0x6007, MapiType.ErrorCode), unchecked((int)0x8004010F)));
        m.Props.Add(new BuildProp(T(0x6008, MapiType.Boolean), true));
        m.Props.Add(new BuildProp(T(0x6009, MapiType.Integer64), 1L << 40));
        m.Props.Add(new BuildProp(T(0x600A, MapiType.Time), MsgTestBuilder.FileTime(new DateTime(2024, 2, 29, 12, 30, 45, DateTimeKind.Utc))));
        m.Props.Add(new BuildProp(T(0x600B, MapiType.GuidValue), new Guid("00062008-0000-0000-c000-000000000046")));
        m.Props.Add(new BuildProp(T(0x600C, MapiType.UnicodeString), "caf\u00e9 \u65e5\u672c"));
        m.Props.Add(new BuildProp(T(0x600D, MapiType.String8), "caf\u00e9"));
        m.Props.Add(new BuildProp(T(0x600E, MapiType.Binary), Arr<byte>(1, 2, 3, 0, 255)));
        m.Props.Add(new BuildProp(T(0x6101, MapiType.MultipleInteger16), Arr<short>(1, -2, 3)));
        m.Props.Add(new BuildProp(T(0x6102, MapiType.MultipleInteger32), Arr(10, 20, 30, 40)));
        m.Props.Add(new BuildProp(T(0x6103, MapiType.MultipleInteger64), Arr(5L, 6L)));
        m.Props.Add(new BuildProp(T(0x6104, MapiType.MultipleTime), Arr(MsgTestBuilder.FileTime(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)), MsgTestBuilder.FileTime(new DateTime(2002, 2, 2, 0, 0, 0, DateTimeKind.Utc)))));
        m.Props.Add(new BuildProp(T(0x6105, MapiType.MultipleString), Arr("one", "two", "th\u00f6ree")));
        m.Props.Add(new BuildProp(T(0x6106, MapiType.MultipleString8), Arr("a", "bb")));
        m.Props.Add(new BuildProp(T(0x6107, MapiType.MultipleBinary), Arr(Arr<byte>(9), Array.Empty<byte>(), Arr<byte>(7, 7, 7))));
        m.Props.Add(new BuildProp(T(0x8000, MapiType.Integer32), 0x2A, new MapiNamedProperty(MapiPropertySets.Common, 0x8503u)));
        m.Props.Add(new BuildProp(T(0x8000, MapiType.UnicodeString), "urgent", new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords")));
        m.Props.Add(new BuildProp(T(0x8000, MapiType.Boolean), true, new MapiNamedProperty(CustomSet, "X-Custom \u00fcnicode")));
        m.Props.Add(new BuildProp(T(0x8000, MapiType.Integer32), 7, new MapiNamedProperty(MapiPropertySets.Mapi, 0x1234u)));
        return m;
    }

    public static BuildObject Conversation(bool withOle = true)
    {
        BuildObject m = Simple("Recipients, attachments and an embedded message");
        m.Props.Add(new BuildProp(T(MapiPid.Body, MapiType.UnicodeString), "Hello\r\nworld"));
        m.Props.Add(new BuildProp(T(MapiPid.RtfCompressed, MapiType.Binary), Arr<byte>(0x10, 0, 0, 0, 0x4C, 0x5A, 0x46, 0x75)));
        foreach ((string name, string addr, int kind) in new[] { ("Ann", "ann@example.org", 1), ("Bob", "bob@example.org", 2), ("Cy", "cy@example.org", 3) })
        {
            var r = new BuildObject();
            r.Props.Add(new BuildProp(T(MapiPid.RecipientType, MapiType.Integer32), kind));
            r.Props.Add(new BuildProp(T(MapiPid.DisplayName, MapiType.UnicodeString), name));
            r.Props.Add(new BuildProp(T(MapiPid.AddressType, MapiType.UnicodeString), "SMTP"));
            r.Props.Add(new BuildProp(T(MapiPid.EmailAddress, MapiType.UnicodeString), addr));
            m.Recipients.Add(r);
        }

        var data = new byte[5000];
        new Random(3).NextBytes(data);
        var a1 = new BuildAttachment();
        a1.Props.Add(new BuildProp(T(MapiPid.AttachMethod, MapiType.Integer32), 1));
        a1.Props.Add(new BuildProp(T(MapiPid.AttachLongFilename, MapiType.UnicodeString), "data.bin"));
        a1.Props.Add(new BuildProp(T(MapiPid.AttachDataBinary, MapiType.Binary), data));
        a1.Props.Add(new BuildProp(T(MapiPid.AttachContentId, MapiType.UnicodeString), "cid-1"));
        m.Attachments.Add(a1);

        BuildObject inner = Simple("The embedded message");
        var ir = new BuildObject();
        ir.Props.Add(new BuildProp(T(MapiPid.DisplayName, MapiType.UnicodeString), "Inner recipient"));
        inner.Recipients.Add(ir);
        var deepest = new BuildAttachment { Embedded = Simple("Embedded twice") };
        deepest.Props.Add(new BuildProp(T(MapiPid.AttachMethod, MapiType.Integer32), 5));
        inner.Attachments.Add(deepest);
        var a2 = new BuildAttachment { Embedded = inner };
        a2.Props.Add(new BuildProp(T(MapiPid.AttachMethod, MapiType.Integer32), 5));
        a2.Props.Add(new BuildProp(T(MapiPid.DisplayName, MapiType.UnicodeString), "Forwarded"));
        m.Attachments.Add(a2);

        if (!withOle)
        {
            return m;
        }

        var ole = new BuildStorage { ClassId = Guid.Empty };
        ole.Streams.Add(("\u0001Ole", Arr<byte>(1, 2, 3, 4)));
        ole.Streams.Add(("Contents", Encoding.ASCII.GetBytes("ole contents")));
        var a3 = new BuildAttachment { Ole = ole };
        a3.Props.Add(new BuildProp(T(MapiPid.AttachMethod, MapiType.Integer32), 6));
        a3.Props.Add(new BuildProp(T(MapiPid.DisplayName, MapiType.UnicodeString), "Object"));
        m.Attachments.Add(a3);
        return m;
    }

    public static BuildObject AnsiRussian()
    {
        // 8-bit strings in code page 1251: "Привет" and "тема".
        BuildObject m = Simple("x", ansi: true);
        m.Props.RemoveAll(p => (ushort)(p.Tag >> 16) is MapiPid.Subject or MapiPid.MessageCodepage);
        m.Props.Add(new BuildProp(T(MapiPid.Subject, MapiType.String8), Arr<byte>(0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2, 0)));
        m.Props.Add(new BuildProp(T(MapiPid.MessageCodepage, MapiType.Integer32), 1251));
        var r = new BuildObject();
        r.Props.Add(new BuildProp(T(MapiPid.DisplayName, MapiType.String8), Arr<byte>(0xF2, 0xE5, 0xEC, 0xE0, 0)));
        m.Recipients.Add(r);
        return m;
    }

    public static BuildObject Template()
    {
        BuildObject m = Simple("Template subject");
        m.Props.Add(new BuildProp(T(MapiPid.MessageFlags, MapiType.Integer32), 0x8));
        return m;
    }
}
