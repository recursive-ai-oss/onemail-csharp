using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Tests.Tnef;

namespace RecursiveAI.OneMail.Fuzz;

// The MS-OXCMAIL conversions on hostile input. Byte 0 picks the input:
//   0: the rest is a MIME message: converted to a MAPI message, back to MIME, and parsed again;
//   1: as 0, then 4-byte records (property index, 16-bit offset, value) damage the converted message's property values before it goes back;
//   2: the rest is a patch list over the streams of a valid MSG file (as for the msg target): the damaged message is converted to MIME and back;
//   bit 2 set (4..7): the rest is a patch list over a valid TNEF stream carried in a MIME message (see TnefInMime): merged into a MAPI message,
//   read through the shared view, exported as a TNEF message and imported again.
// Neither direction may throw, hang or grow without bound, whatever the input holds.
internal static class OxcMailTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if ((data[0] & 4) != 0)
        {
            TnefInMime(data[1..]);
            return;
        }

        switch (data[0] & 3)
        {
            case 0:
                RoundTrip(Import(data[1..].ToArray()));
                break;
            case 1:
                int split = Math.Min(data.Length, 1 + Math.Min(data.Length - 1, 1 + (data.Length - 1) * 3 / 4));
                MapiMessage m = Import(data[1..split].ToArray());
                Damage(m, data[split..]);
                RoundTrip(m);
                break;
            default:
                if (MsgTarget.BuildFile(data[1..]) is { } file && MsgFile.TryOpen(file, out MsgFile? f, out _, new MsgReaderOptions { MaxValueBytes = 1 << 20 }))
                {
                    using (f)
                    {
                        f.ReadEverything();
                        RoundTrip(f.Message);
                    }
                }

                break;
        }
    }

    private static readonly byte[] RichTnef = TnefTestBuilder.Rich();

    // Byte 0: bit 0 envelope fields (they win over the TNEF), bit 1 a text part before the TNEF part, bit 2 a correlator field that cannot match,
    // bit 3 the TNEF as the whole message; then 3-byte records (16-bit offset, value) over the stream, the first of which also cuts it when bit 4 is set.
    private static void TnefInMime(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte flags = data[0];
        byte[] stream = (byte[])RichTnef.Clone();
        int first = -1;
        for (int i = 1; i + 2 < data.Length; i += 3)
        {
            int offset = (data[i] | (data[i + 1] << 8)) % stream.Length;
            stream[offset] = data[i + 2];
            first = first < 0 ? offset : first;
        }

        if ((flags & 16) != 0 && first >= 0)
        {
            stream = stream[..first];
        }

        string part = "Content-Type: application/ms-tnef; name=\"winmail.dat\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(stream, Base64FormattingOptions.InsertLineBreaks) + "\r\n";
        string head = ((flags & 1) != 0 ? "From: a@example.com\r\nTo: b@example.com\r\nSubject: s\r\nDate: Mon, 02 Jan 2023 10:00:00 +0000\r\n" : string.Empty)
            + ((flags & 4) != 0 ? "X-MS-TNEF-Correlator: <none>\r\n" : string.Empty) + "MIME-Version: 1.0\r\n";
        string text = (flags & 8) != 0
            ? head + part
            : head + "Content-Type: multipart/mixed; boundary=\"b\"\r\n\r\n--b\r\n" + ((flags & 2) != 0 ? "Content-Type: text/plain\r\n\r\nbody\r\n--b\r\n" : string.Empty) + part + "--b--\r\n";
        byte[] eml = System.Text.Encoding.ASCII.GetBytes(text);

        MapiMessage m = Import(eml);
        RoundTrip(m);
        MimeMailItem view = MimeMailItem.Parse(eml);
        foreach (IMailAttachment a in view.Attachments)
        {
            _ = a.Size;
            _ = a.Embedded?.Subject;
        }

        _ = view.TextBody?.ReadText();
        _ = view.HtmlBody?.ReadText();
        _ = view.RtfBody?.ReadText();
        byte[] asTnef = new MapiToMimeConverter(new OxcMailExportOptions { Tnef = OxcMailTnefExport.TnefMessage, IncludeBcc = true }).Convert(m).Value.ToArray();
        _ = new MimeToMapiConverter().Convert(MimeEntity.Parse(asTnef));
    }

    private static MapiMessage Import(byte[] mime) =>
        new MimeToMapiConverter().Convert(MimeEntity.Parse(mime, new Mime.Reading.MimeReaderOptions())).Value;

    private static void RoundTrip(MapiMessage m)
    {
        ConversionResult<MimeEntity> r = new MapiToMimeConverter(new OxcMailExportOptions { IncludeBcc = true }).Convert(m);
        byte[] eml = r.Value.ToArray();
        MimeEntity again = MimeEntity.Parse(eml);
        _ = new MimeToMapiConverter().Convert(again);
        _ = new MapiMailItem(m, default).From;
        _ = new MapiMailItem(m, default).ReplyTo;
        _ = new MapiMailItem(m, default).TextBody?.ReadText();
        _ = new MapiMailItem(m, default).HtmlBody?.ReadText();
        _ = new MapiMailItem(m, default).RtfBody?.ReadText();
    }

    // Replaces property values with damaged copies: same tag, other bytes.
    private static void Damage(MapiMessage m, ReadOnlySpan<byte> records)
    {
        for (int i = 0; i + 3 < records.Length; i += 4)
        {
            if (m.Properties.Count == 0)
            {
                return;
            }

            MapiProperty p = m.Properties[records[i] % m.Properties.Count];
            if (p.IsMultiValued || p.IsLazy)
            {
                continue;
            }

            byte[] bytes = p.GetBytes().ToArray();
            int offset = records[i + 1] | (records[i + 2] << 8);
            if (bytes.Length > 0)
            {
                bytes[offset % bytes.Length] = records[i + 3];
            }

            m.Properties.Set(MapiProperty.CreateOwned(p.Tag, bytes, p.Name));
        }
    }
}
