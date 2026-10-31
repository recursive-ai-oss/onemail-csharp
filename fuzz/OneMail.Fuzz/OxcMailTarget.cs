using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Msg;

namespace RecursiveAI.OneMail.Fuzz;

// The MS-OXCMAIL conversions on hostile input. Byte 0 picks the input:
//   0: the rest is a MIME message: converted to a MAPI message, back to MIME, and parsed again;
//   1: as 0, then 4-byte records (property index, 16-bit offset, value) damage the converted message's property values before it goes back;
//   2: the rest is a patch list over the streams of a valid MSG file (as for the msg target): the damaged message is converted to MIME and back.
// Neither direction may throw, hang or grow without bound, whatever the input holds.
internal static class OxcMailTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
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
