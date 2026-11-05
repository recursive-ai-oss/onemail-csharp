using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;

namespace MsgTool;

/// <summary>Writes small message files that differ in one feature each (recipient and attachment property sets), for finding what Outlook
/// needs in a message file: <c>MsgTool variants &lt;out dir&gt;</c>, then tools/outlook/Check-Msgs.ps1 over the directory.</summary>
internal static class OutlookVariants
{
    private static MapiProperty Str(ushort id, string v) => MapiProperty.Create(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));
    private static MapiProperty Int(ushort id, int v) => MapiProperty.Create(new MapiTag(id, MapiType.Integer32), BitConverter.GetBytes(v));
    private static MapiProperty Bin(ushort id, byte[] v) => MapiProperty.Create(new MapiTag(id, MapiType.Binary), v);

    // MS-OXCDATA 2.2.5.1: a one-off entry ID is flags, the provider UID, version and flags, then display name, address type and address as
    // null-terminated Unicode strings.
    private static byte[] OneOff(string name, string type, string address)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[4]);
        ms.Write([0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02]);
        ms.Write([0, 0, 0, 0x80]);
        foreach (string t in new[] { name, type, address })
        {
            ms.Write(Encoding.Unicode.GetBytes(t + "\0"));
        }

        return ms.ToArray();
    }

    private static MapiMessage Base(string subject)
    {
        var m = new MapiMessage();
        m.Properties.Add(Str(0x001A, "IPM.Note"));
        m.Properties.Add(Str(0x0037, subject));
        m.Properties.Add(Str(0x1000, "body"));
        return m;
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        void Write(string name, MapiMessage m) => MsgWriter.Write(m, Path.Combine(dir, name + ".msg"));
        Write("m-plain", Base("plain"));

        (string, Action<MapiRecipient>)[] recipients =
        [
            ("r0-name", r => r.Properties.Add(Str(0x3001, "Ann"))),
            ("r1-type", r => { r.Properties.Add(Str(0x3001, "Ann")); r.Properties.Add(Int(0x0C15, 1)); }),
            ("r2-smtp", r => { r.Properties.Add(Str(0x3001, "Ann")); r.Properties.Add(Int(0x0C15, 1)); r.Properties.Add(Str(0x3002, "SMTP")); r.Properties.Add(Str(0x3003, "ann@example.org")); }),
            ("r3-more", r =>
            {
                r.Properties.Add(Str(0x3001, "Ann")); r.Properties.Add(Int(0x0C15, 1)); r.Properties.Add(Str(0x3002, "SMTP")); r.Properties.Add(Str(0x3003, "ann@example.org"));
                r.Properties.Add(Str(0x39FE, "ann@example.org")); r.Properties.Add(Int(0x0FFE, 6)); r.Properties.Add(Int(0x3900, 0)); r.Properties.Add(Int(0x5FDF, 0));
                r.Properties.Add(Bin(0x0FF9, [1, 2, 3, 4])); r.Properties.Add(Int(0x5FFD, 0));
            }),
            ("r4-oneoff", r =>
            {
                r.Properties.Add(Str(0x3001, "Ann")); r.Properties.Add(Int(0x0C15, 1)); r.Properties.Add(Str(0x3002, "SMTP")); r.Properties.Add(Str(0x3003, "ann@example.org"));
                byte[] id = OneOff("Ann", "SMTP", "ann@example.org");
                r.Properties.Add(Bin(0x0FFF, id)); r.Properties.Add(Bin(0x5FF7, id)); r.Properties.Add(Str(0x5FF6, "Ann")); r.Properties.Add(Bin(0x0FF9, [1, 2, 3, 4]));
            }),
            ("r5-rowid", r => { r.Properties.Add(Int(0x3000, 0)); r.Properties.Add(Str(0x3001, "Ann")); r.Properties.Add(Int(0x0C15, 1)); r.Properties.Add(Str(0x3002, "SMTP")); r.Properties.Add(Str(0x3003, "ann@example.org")); }),
        ];
        foreach ((string name, Action<MapiRecipient> fill) in recipients)
        {
            MapiMessage m = Base(name);
            var r = new MapiRecipient(m);
            fill(r);
            m.AddRecipient(r);
            Write(name, m);
        }

        (string, Action<MapiAttachment>)[] attachments =
        [
            ("a0-min", a => { a.Properties.Add(Int(0x3705, 1)); a.Properties.Add(Str(0x3707, "f.bin")); a.Properties.Add(Bin(0x3701, new byte[500])); }),
            ("a1-number", a => { a.Properties.Add(Int(0x0E21, 0)); a.Properties.Add(Int(0x3705, 1)); a.Properties.Add(Str(0x3707, "f.bin")); a.Properties.Add(Bin(0x3701, new byte[500])); }),
            ("a2-objtype", a => { a.Properties.Add(Int(0x0FFE, 7)); a.Properties.Add(Int(0x3705, 1)); a.Properties.Add(Str(0x3707, "f.bin")); a.Properties.Add(Bin(0x3701, new byte[500])); }),
            ("a3-render", a => { a.Properties.Add(Int(0x3705, 1)); a.Properties.Add(Int(0x370B, -1)); a.Properties.Add(Str(0x3707, "f.bin")); a.Properties.Add(Bin(0x3701, new byte[500])); }),
            ("a4-names", a => { a.Properties.Add(Int(0x3705, 1)); a.Properties.Add(Str(0x3001, "f.bin")); a.Properties.Add(Str(0x3704, "f.bin")); a.Properties.Add(Str(0x3707, "f.bin")); a.Properties.Add(Str(0x3703, ".bin")); a.Properties.Add(Bin(0x3701, new byte[500])); }),
            ("a5-all", a =>
            {
                a.Properties.Add(Int(0x0E21, 0)); a.Properties.Add(Int(0x0FFE, 7)); a.Properties.Add(Int(0x3705, 1)); a.Properties.Add(Int(0x370B, -1));
                a.Properties.Add(Str(0x3001, "f.bin")); a.Properties.Add(Str(0x3704, "f.bin")); a.Properties.Add(Str(0x3707, "f.bin")); a.Properties.Add(Str(0x3703, ".bin"));
                a.Properties.Add(Bin(0x3701, new byte[500]));
            }),
        ];
        foreach ((string name, Action<MapiAttachment> fill) in attachments)
        {
            MapiMessage m = Base(name);
            var a = new MapiAttachment(m);
            fill(a);
            m.AddAttachment(a);
            Write(name, m);
        }

        // An embedded message with the attachment number.
        {
            MapiMessage m = Base("e1-embedded-number");
            var a = new MapiAttachment(m);
            a.Properties.Add(Int(0x0E21, 0));
            a.Properties.Add(Int(0x3705, 5));
            a.SetEmbeddedMessage(Base("inner"));
            m.AddAttachment(a);
            Write("e1-embedded-number", m);
        }

        // An embedded message, plain.
        {
            MapiMessage m = Base("e0-embedded");
            var a = new MapiAttachment(m);
            a.Properties.Add(Int(0x3705, 5));
            a.SetEmbeddedMessage(Base("inner"));
            m.AddAttachment(a);
            Write("e0-embedded", m);
        }

        return 0;
    }
}
