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

    /// <summary>One file per root property of <paramref name="source"/>: the plain base message plus that property (named properties are skipped),
    /// so that Outlook's verdict on each file points at the property it dislikes.</summary>
    public static int Props(string source, string dir)
    {
        Directory.CreateDirectory(dir);
        if (!MsgFile.TryOpen(source, out MsgFile? mf, out _) || mf is null)
        {
            return 1;
        }

        using (mf)
        {
            MapiMessage src = mf.Message;
            foreach (MapiProperty p in src.Properties)
            {
                if (p.Tag.Id >= 0x8000 || p.Tag.Id is 0x001A or 0x0037 or 0x1000)
                {
                    continue;
                }

                MapiMessage m = Base("p" + p.Tag.ToHex());
                m.Properties.Add(p);
                MsgWriter.Write(m, Path.Combine(dir, "p-" + p.Tag.ToHex() + ".msg"));
            }
        }

        return 0;
    }

    // A copy of a message with parts left out: recipients, attachments, or properties other than the class, subject, body and (for recipients and
    // attachments) a short list of the ones every object has.
    private static MapiMessage Clone(MapiMessage src, bool recipients, bool attachments, bool rootProps, bool minimalObjects, bool plainEmbedded)
    {
        var m = new MapiMessage();
        foreach (MapiProperty p in src.Properties)
        {
            if (rootProps ? p.Tag.Id < 0x8000 : p.Tag.Id is 0x001A or 0x0037 or 0x1000)
            {
                m.Properties.Add(p);
            }
        }

        if (recipients)
        {
            foreach (MapiRecipient r in src.Recipients)
            {
                var c = new MapiRecipient(m);
                foreach (MapiProperty p in r.Properties)
                {
                    if (!minimalObjects || p.Tag.Id is 0x0C15 or 0x3001 or 0x3002 or 0x3003)
                    {
                        c.Properties.Add(p);
                    }
                }

                m.AddRecipient(c);
            }
        }

        if (attachments)
        {
            foreach (MapiAttachment a in src.Attachments)
            {
                var c = new MapiAttachment(m);
                foreach (MapiProperty p in a.Properties)
                {
                    if (!minimalObjects || p.Tag.Id is 0x3705 or 0x3707 or 0x3701 or 0x3712 or 0x3704 or 0x3703)
                    {
                        c.Properties.Add(p);
                    }
                }

                if (a.EmbeddedMessage is { } e)
                {
                    c.SetEmbeddedMessage(plainEmbedded ? Base("inner") : Clone(e, recipients, attachments, rootProps, minimalObjects, plainEmbedded));
                }

                m.AddAttachment(c);
            }
        }

        return m;
    }

    /// <summary>Embedded-message attachments with growing property sets, to find what Outlook needs to save a copy.</summary>
    public static int Embedded(string dir)
    {
        Directory.CreateDirectory(dir);
        (string, Action<MapiMessage>)[] sets =
        [
            ("n0-base", i => { }),
            ("n1-mask", i => i.Properties.Add(Int(0x340D, 0x00040E79))),
            ("n2-codepage", i => i.Properties.Add(Int(0x3FDE, 65001))),
            ("n3-locale", i => i.Properties.Add(Int(0x3FF1, 0x0409))),
            ("n4-mask-unicode", i => i.Properties.Add(Int(0x340D, 0x00040000))),
            ("n5-all", i => { i.Properties.Add(Int(0x340D, 0x00040E79)); i.Properties.Add(Int(0x3FDE, 1200)); i.Properties.Add(Int(0x3FF1, 0x0409)); }),
        ];
        foreach ((string name, Action<MapiMessage> fill) in sets)
        {
            MapiMessage m = Base(name);
            var a = new MapiAttachment(m);
            a.Properties.Add(Int(0x3705, 5));
            MapiMessage inner = Base("inner");
            fill(inner);
            a.SetEmbeddedMessage(inner);
            m.AddAttachment(a);
            Write2(dir, name, m);
        }

        return 0;
    }

    /// <summary>Each message file in a directory written in the 8-bit string mode and as given (the default mode is what the other checks use).</summary>
    public static int Modes(string source, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.EnumerateFiles(source, "*.msg"))
        {
            if (!MsgFile.TryOpen(f, out MsgFile? mf, out _) || mf is null)
            {
                continue;
            }

            using (mf)
            {
                string n = Path.GetFileNameWithoutExtension(f);
                MsgWriter.Write(mf.Message, Path.Combine(dir, n + ".ansi.msg"), new MsgWriterOptions { Strings = MsgStringMode.Ansi });
                MsgWriter.Write(mf.Message, Path.Combine(dir, n + ".asgiven.msg"), new MsgWriterOptions { Strings = MsgStringMode.AsGiven });
            }
        }

        return 0;
    }

    /// <summary>Messages with and without a class and a store support mask, written exactly as given.</summary>
    public static int Required(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach ((string name, int[] ids) in new[] { ("y1-class-only", Array.Empty<int>()), ("y2-class-subject", new[] { 0x0037 }), ("y3-class-body", new[] { 0x1000 }), ("y4-class-mask", new[] { 0x340D }) })
        {
            var m = new MapiMessage();
            m.Properties.Add(Str(0x001A, "IPM.Note"));
            foreach (int id in ids)
            {
                m.Properties.Add(id == 0x340D ? Int(0x340D, 0x00040000) : Str((ushort)id, "text"));
            }

            MsgWriter.Write(m, Path.Combine(dir, name + ".msg"), new MsgWriterOptions { AddRequiredProperties = false });
        }

        (string, bool, int?)[] sets = [("x1-nomask-class", true, null), ("x2-mask-noclass", false, 0x00040000), ("x3-mask0-class", true, 0), ("x4-mask-class", true, 0x00040000), ("x5-neither", false, null), ("x6-ansimask-class", true, 0x00000E79)];
        foreach ((string name, bool cls, int? mask) in sets)
        {
            var m = new MapiMessage();
            if (cls)
            {
                m.Properties.Add(Str(0x001A, "IPM.Note"));
            }

            m.Properties.Add(Str(0x0037, name));
            m.Properties.Add(Str(0x1000, "body"));
            if (mask is { } v)
            {
                m.Properties.Add(Int(0x340D, v));
            }

            MsgWriter.Write(m, Path.Combine(dir, name + ".msg"), new MsgWriterOptions { AddRequiredProperties = false });
        }

        return 0;
    }

    private static void Write2(string dir, string name, MapiMessage m) => MsgWriter.Write(m, Path.Combine(dir, name + ".msg"));

    /// <summary>Variants of a message with parts left out, to find which part Outlook dislikes.</summary>
    public static int Prune(string source, string dir)
    {
        Directory.CreateDirectory(dir);
        if (!MsgFile.TryOpen(source, out MsgFile? mf, out _) || mf is null)
        {
            return 1;
        }

        using (mf)
        {
            MapiMessage s = mf.Message;
            void Write(string name, MapiMessage m) => MsgWriter.Write(m, Path.Combine(dir, name + ".msg"));
            Write("v0-full", Clone(s, true, true, true, false, false));
            Write("v1-norecips", Clone(s, false, true, true, false, false));
            Write("v2-noattach", Clone(s, true, false, true, false, false));
            Write("v3-rootmin", Clone(s, true, true, false, false, false));
            Write("v4-objmin", Clone(s, true, true, true, true, false));
            Write("v5-plainembedded", Clone(s, true, true, true, false, true));
            Write("v6-allmin", Clone(s, true, true, false, true, true));
            Write("v7-onlyroot", Clone(s, false, false, false, false, false));
        }

        return 0;
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
