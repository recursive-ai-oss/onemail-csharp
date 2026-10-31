using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Tnef;

namespace RecursiveAI.OneMail.Tests.Tnef;

/// <summary>
/// Builds TNEF streams byte by byte from MS-OXTNEF 2.1.3, independently of the library's property writer, so the reader is checked against an
/// encoder that was written from the specification and not from the reader. Attribute framing goes through the library's low-level TnefWriter.
/// </summary>
internal static class TnefTestBuilder
{
    public static readonly Guid PsMapi = new("00020328-0000-0000-c000-000000000046");
    public static readonly Guid PsPublic = new("00020329-0000-0000-c000-000000000046");
    public static readonly Guid PsCommon = new("00062008-0000-0000-c000-000000000046");

    private static byte[] Cat(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (byte[] p in parts)
        {
            ms.Write(p);
        }

        return ms.ToArray();
    }

    private static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);

    private static byte[] U32(uint v) => BitConverter.GetBytes(v);

    private static byte[] Pad(byte[] v) => v.Length % 4 == 0 ? v : Cat(v, new byte[4 - (v.Length % 4)]);

    // ---- one property: tag (type, id), optional named-property specification, then data
    public sealed record Prop(byte[] Bytes)
    {
        public Prop Named(Guid set, uint lid) => new(Cat(Bytes[..4], set.ToByteArray(), U32(0), U32(lid), Bytes[4..]));

        public Prop Named(Guid set, string name)
        {
            byte[] chars = Encoding.Unicode.GetBytes(name);
            return new(Cat(Bytes[..4], set.ToByteArray(), U32(1), U32((uint)chars.Length + 2), Pad(Cat(chars, new byte[2])), Bytes[4..]));
        }
    }

    private static Prop P(int type, int id, params byte[][] data) => new(Cat(U16(type), U16(id), Cat(data)));

    public static Prop Int16(int id, short v) => P(0x02, id, Pad(BitConverter.GetBytes(v)));

    public static Prop Int32(int id, int v) => P(0x03, id, BitConverter.GetBytes(v));

    public static Prop Float(int id, float v) => P(0x04, id, BitConverter.GetBytes(v));

    public static Prop Double(int id, double v) => P(0x05, id, BitConverter.GetBytes(v));

    public static Prop Currency(int id, long v) => P(0x06, id, BitConverter.GetBytes(v));

    public static Prop AppTime(int id, double v) => P(0x07, id, BitConverter.GetBytes(v));

    public static Prop Error(int id, int v) => P(0x0A, id, BitConverter.GetBytes(v));

    public static Prop Bool(int id, bool v) => P(0x0B, id, [(byte)(v ? 1 : 0), 0, 0, 0]);

    public static Prop Int64(int id, long v) => P(0x14, id, BitConverter.GetBytes(v));

    public static Prop Time(int id, DateTime utc) => P(0x40, id, BitConverter.GetBytes(utc.ToFileTimeUtc()));

    public static Prop Clsid(int id, Guid g) => P(0x48, id, g.ToByteArray());

    public static Prop Null(int id) => P(0x01, id);

    public static Prop String8(int id, string s, int codePage = 1252)
    {
        byte[] b = Cat(RecursiveAI.OneMail.Text.MailEncoding.ForCodePage(codePage)!.GetBytes(s), [0]);
        return P(0x1E, id, U32(1), U32((uint)b.Length), Pad(b));
    }

    public static Prop Unicode(int id, string s)
    {
        byte[] b = Cat(Encoding.Unicode.GetBytes(s), [0, 0]);
        return P(0x1F, id, U32(1), U32((uint)b.Length), Pad(b));
    }

    public static Prop Binary(int id, byte[] v) => P(0x102, id, U32(1), U32((uint)v.Length), Pad(v));

    public static Prop Object(int id, byte[] value) => P(0x0D, id, U32(1), U32((uint)value.Length), Pad(value));

    public static Prop MvInt16(int id, params short[] v) => P(0x1002, id, U32((uint)v.Length), Cat(v.Select(x => Pad(BitConverter.GetBytes(x))).ToArray()));

    public static Prop MvInt32(int id, params int[] v) => P(0x1003, id, U32((uint)v.Length), Cat(v.Select(BitConverter.GetBytes).ToArray()));

    public static Prop MvInt64(int id, params long[] v) => P(0x1014, id, U32((uint)v.Length), Cat(v.Select(BitConverter.GetBytes).ToArray()));

    public static Prop MvTime(int id, params DateTime[] v) => P(0x1040, id, U32((uint)v.Length), Cat(v.Select(x => BitConverter.GetBytes(x.ToFileTimeUtc())).ToArray()));

    public static Prop MvClsid(int id, params Guid[] v) => P(0x1048, id, U32((uint)v.Length), Cat(v.Select(x => x.ToByteArray()).ToArray()));

    public static Prop MvString8(int id, params string[] v) =>
        P(0x101E, id, U32((uint)v.Length), Cat(v.Select(s => { byte[] b = Cat(Encoding.Latin1.GetBytes(s), [0]); return Cat(U32((uint)b.Length), Pad(b)); }).ToArray()));

    public static Prop MvUnicode(int id, params string[] v) =>
        P(0x101F, id, U32((uint)v.Length), Cat(v.Select(s => { byte[] b = Cat(Encoding.Unicode.GetBytes(s), [0, 0]); return Cat(U32((uint)b.Length), Pad(b)); }).ToArray()));

    public static Prop MvBinary(int id, params byte[][] v) =>
        P(0x1102, id, U32((uint)v.Length), Cat(v.Select(b => Cat(U32((uint)b.Length), Pad(b))).ToArray()));

    public static byte[] List(params Prop[] props) => Cat(U32((uint)props.Length), Cat(props.Select(p => p.Bytes).ToArray()));

    public static byte[] RecipTable(params byte[][] rows) => Cat(U32((uint)rows.Length), Cat(rows));

    // ---- legacy attribute data
    public static byte[] Dtr(DateTime t) => Cat(U16(t.Year), U16(t.Month), U16(t.Day), U16(t.Hour), U16(t.Minute), U16(t.Second), U16((int)t.DayOfWeek));

    public static byte[] Str8(string s, int codePage = 1252) => Cat(RecursiveAI.OneMail.Text.MailEncoding.ForCodePage(codePage)!.GetBytes(s), [0]);

    public static byte[] RendData(int type, int position, int width, int height, uint flags) => Cat(U16(type), BitConverter.GetBytes(position), U16(width), U16(height), U32(flags));

    public static byte[] Trp(string name, string typeAndAddress, int codePage = 1252)
    {
        byte[] n = Str8(name, codePage);
        byte[] e = Str8(typeAndAddress);
        return Cat(U16(4), U16(8 + n.Length + e.Length + 8), U16(n.Length), U16(e.Length), n, e, new byte[8]);
    }

    public static byte[] Owner(string name, string typeAndAddress)
    {
        byte[] n = Str8(name);
        byte[] e = Str8(typeAndAddress);
        return Cat(U16(n.Length), n, U16(e.Length), e);
    }

    public sealed class StreamBuilder
    {
        private readonly List<byte[]> _parts = [];

        public StreamBuilder(ushort key = 0x0001)
        {
            _parts.Add(Cat(U32(TnefWriter.Signature), U16(key)));
        }

        private StreamBuilder Frame(byte level, uint id, byte[] data, ushort? checksum)
        {
            using var ms = new MemoryStream();
            new TnefWriter(ms).WriteAttribute(level, id, data, checksum);
            _parts.Add(ms.ToArray()[6..]);
            return this;
        }

        public StreamBuilder Msg(uint id, byte[] data, ushort? checksum = null)
        {
            return Frame(1, id, data, checksum);
        }

        public StreamBuilder Att(uint id, byte[] data, ushort? checksum = null)
        {
            return Frame(2, id, data, checksum);
        }

        public StreamBuilder Header(int codePage = 1252) =>
            Msg(TnefAttributeIds.TnefVersion, [0, 0, 1, 0]).Msg(TnefAttributeIds.OemCodepage, Cat(U32((uint)codePage), U32(0)));

        public StreamBuilder Raw(byte[] bytes)
        {
            _parts.Add(bytes);
            return this;
        }

        public byte[] ToArray() => Cat([.. _parts]);
    }

    public static readonly byte[] MessageIid = [0x07, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    /// <summary>The start of the specification's sample message (MS-OXTNEF 3.1), byte for byte, through attMessageID.</summary>
    public static byte[] SpecSampleStart() => Convert.FromHexString(
        "789f3e22" + "0100"
        + "01" + "06900800" + "04000000" + "00000100" + "0100"
        + "01" + "07900600" + "08000000" + "e404000000000000" + "e800"
        + "01" + "08800700" + "18000000" + "49504d2e4d6963726f736f6674204d61696c2e4e6f746500" + "3108"
        + "01" + "0d800400" + "02000000" + "0200" + "0200"
        + "01" + "04800100" + "0f000000" + "53696d706c65207375626a65637400" + "7a05"
        + "01" + "05800300" + "0e000000" + "d4070200110" + "00b00190023000200" + "3701"
        + "01" + "20800300" + "0e000000" + "d4070200110" + "00c001a0009000200" + "1f01"
        + "01" + "09800100" + "21000000" + "3735374242313943444539333641343038374439304242373834433538453342" + "00" + "5107");

    /// <summary>Message with every property type, named properties, recipients, three attachments (one legacy, one props-only, one embedded message).</summary>
    /// <param name="codePage">attOemCodepage and the code page of the 8-bit strings.</param>
    /// <param name="everyType">false leaves out the multi-valued, currency, application-time and error properties, which the other readers used for comparison cannot parse.</param>
    public static byte[] Rich(int codePage = 1252, bool everyType = true)
    {
        byte[] inner = new StreamBuilder(7).Header().Msg(TnefAttributeIds.MessageClass, Str8("IPM.Note")).Msg(TnefAttributeIds.Subject, Str8("inner subject"))
            .Msg(TnefAttributeIds.MsgProps, List(String8(0x1000, "inner body"))).ToArray();
        DateTime t = new(2004, 2, 17, 19, 25, 35, DateTimeKind.Utc);
        return new StreamBuilder(0x1234).Header(codePage)
            .Msg(TnefAttributeIds.MessageClass, Str8("IPM.Note"))
            .Msg(TnefAttributeIds.Subject, Str8("Caf\u00e9 menu"))
            .Msg(TnefAttributeIds.DateSent, Dtr(t))
            .Msg(TnefAttributeIds.RecipTable, RecipTable(
                List(Int32(0x0C15, 1), Unicode(0x3001, "Ann Example"), String8(0x3002, "SMTP"), String8(0x3003, "ann@example.test")),
                List(Int32(0x0C15, 2), Unicode(0x3001, "Bob Example"), Unicode(0x3003, "bob@example.test"))))
            .Msg(TnefAttributeIds.MsgProps, List([
                Unicode(0x0037, "Caf\u00e9 menu \u2713"),
                String8(0x1000, "plain body \u00e9"),
                Int32(0x0017, 1),
                Bool(0x0029, true),
                Int16(0x6601, -7),
                Float(0x6602, 1.5f),
                Double(0x6603, -2.25),
                .. (everyType ? new[] { Currency(0x6604, 123456), AppTime(0x6605, 36526.5), Error(0x6606, unchecked((int)0x8004010F)) } : []),
                Int64(0x6607, long.MinValue + 3),
                Time(0x0039, t),
                Clsid(0x6608, Guid.Parse("00020307-0000-0000-c000-000000000046")),
                // an uncompressed (MELA) compressed-RTF stream: compressed size = 12 + data, raw size = data, then the signature and CRC
                Binary(0x1009, Cat(U32(22), U32(10), "MELA"u8.ToArray(), U32(0), "{\\rtf1 hi}"u8.ToArray())),
                Binary(0x6610, [1, 2, 3, 4, 5]),
                .. (everyType ? new[] {
                    MvInt16(0x6611, 1, -2, 3),
                    MvInt32(0x6612, 10, 20),
                    MvInt64(0x6613, 5, 6, 7),
                    MvTime(0x6614, t, t.AddDays(1)),
                    MvClsid(0x6615, Guid.Empty, PsMapi),
                    MvString8(0x6616, "one", "two", "three"),
                    MvUnicode(0x6617, "alpha", "\u00df\u00e9"),
                    MvBinary(0x6618, [1], [2, 3], []),
                    MvUnicode(0x8001, "red", "blue").Named(PsPublic, "Keywords"),
                } : []),
                Int32(0x8000, 42).Named(PsCommon, 0x8501),
                Unicode(0x8002, "x").Named(PsPublic, "ab"),
                Unicode(0x8003, "y").Named(PsPublic, "abc")]))
            // attachment 1: legacy attributes plus a few properties
            .Att(TnefAttributeIds.AttachRendData, RendData(1, -1, 32, 32, 0))
            .Att(TnefAttributeIds.AttachData, [10, 20, 30, 40, 50, 60])
            .Att(TnefAttributeIds.AttachTitle, Str8("report.txt"))
            .Att(TnefAttributeIds.AttachCreateDate, Dtr(t))
            .Att(TnefAttributeIds.Attachment, List(Int32(0x3705, 1), String8(0x3707, "report \u00e9.txt"), String8(0x370E, "text/plain")))
            // attachment 2: properties only
            .Att(TnefAttributeIds.AttachRendData, RendData(1, 5, -1, -1, 0))
            .Att(TnefAttributeIds.Attachment, List(Int32(0x3705, 1), Binary(0x3701, [9, 9, 9]), Unicode(0x3707, "props.bin")))
            // attachment 3: an embedded message
            .Att(TnefAttributeIds.AttachRendData, RendData(1, -1, -1, -1, 0))
            .Att(TnefAttributeIds.Attachment, List(Int32(0x3705, 5), Object(0x3701, Cat(MessageIid, inner)), Unicode(0x3001, "inner.msg")))
            .ToArray();
    }

    /// <summary>Only legacy attributes, none of the encapsulated lists.</summary>
    public static byte[] LegacyOnly()
    {
        DateTime t = new(2020, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        return new StreamBuilder(9).Header(1251)
            .Msg(TnefAttributeIds.MessageClass, Str8("IPM.Microsoft Schedule.MtgReq"))
            .Msg(TnefAttributeIds.From, Trp("\u041e\u043b\u044f", "SMTP:olya@example.test", 1251))
            .Msg(TnefAttributeIds.Subject, Str8("\u041f\u0440\u0438\u0432\u0435\u0442", 1251))
            .Msg(TnefAttributeIds.DateSent, Dtr(t))
            .Msg(TnefAttributeIds.DateRecd, Dtr(t.AddMinutes(1)))
            .Msg(TnefAttributeIds.DateModified, Dtr(t.AddMinutes(2)))
            .Msg(TnefAttributeIds.DateStart, Dtr(t.AddDays(1)))
            .Msg(TnefAttributeIds.DateEnd, Dtr(t.AddDays(1).AddHours(1)))
            .Msg(TnefAttributeIds.MessageStatus, [0xA1])
            .Msg(TnefAttributeIds.MessageId, Str8("0A1B2C"))
            .Msg(TnefAttributeIds.Body, Str8("\u0442\u0435\u043b\u043e", 1251))
            .Msg(TnefAttributeIds.Priority, U16(1))
            .Msg(TnefAttributeIds.Owner, Owner("Owner Name", "SMTP:owner@example.test"))
            .Msg(TnefAttributeIds.SentFor, Owner("Sent For", "SMTP:boss@example.test"))
            .Msg(TnefAttributeIds.Delegate, [1, 2, 3, 4])
            .Msg(TnefAttributeIds.AidOwner, U32(77))
            .Msg(TnefAttributeIds.RequestRes, U16(1))
            .Msg(TnefAttributeIds.OriginalMessageClass, Str8("IPM.Microsoft Mail.Note"))
            .Att(TnefAttributeIds.AttachRendData, RendData(1, 12, 32, 32, 1))
            .Att(TnefAttributeIds.AttachData, [1, 2, 3])
            .Att(TnefAttributeIds.AttachTitle, Str8("data.bin"))
            .Att(TnefAttributeIds.AttachMetaFile, [7, 7])
            .Att(TnefAttributeIds.AttachCreateDate, Dtr(t))
            .Att(TnefAttributeIds.AttachModifyDate, Dtr(t.AddHours(1)))
            .Att(TnefAttributeIds.AttachTransportFilename, Str8("DATA~1.BIN"))
            .Att(TnefAttributeIds.AttachRendData, RendData(2, -1, -1, -1, 0))
            .Att(TnefAttributeIds.AttachTitle, Str8("ole.bin"))
            .ToArray();
    }
}
