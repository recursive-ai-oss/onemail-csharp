using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using OneMime = RecursiveAI.OneMail.Mime;

namespace OneMail.Tools.SmimeInterop;

/// <summary>Writes the S/MIME messages of the vector sets in the form Outlook keeps them (MS-OXOSMIME 2.1.3): <c>SmimeInterop outlook-items &lt;vector dir&gt; &lt;out dir&gt;</c>
/// turns each <c>.eml</c> into a <c>.msg</c> that Outlook can open, so Outlook's reading of our messages can be checked.</summary>
internal static class OutlookItems
{
    private static MapiProperty Str(ushort id, string v) => MapiProperty.Create(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    public static int Run(string source, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.EnumerateFiles(source, "*.eml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            byte[] raw = File.ReadAllBytes(f);
            OneMime.MimeEntity e = OneMime.MimeEntity.Parse(raw);
            string contentType = e.Headers.GetFirst("Content-Type")?.Value ?? "text/plain";
            var m = new MapiMessage();
            var a = new MapiAttachment(m);
            a.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachMethod, MapiType.Integer32), BitConverter.GetBytes(1)));
            a.Properties.Add(Str(MapiPid.AttachLongFilename, "smime.p7m"));
            if (e is OneMime.MimeMultipart)
            {
                // MS-OXOSMIME 2.1.3.1: a clear-signed message is one attachment holding the multipart/signed entity with only its Content-Type header.
                byte[] whole = e.ToArray();
                int body = IndexOfBlankLine(whole);
                byte[] data = [.. Encoding.ASCII.GetBytes("Content-Type: " + contentType + "\r\n\r\n"), .. whole.AsSpan(body)];
                m.Properties.Add(Str(MapiPid.MessageClass, "IPM.Note.SMIME.MultipartSigned"));
                a.Properties.Add(Str(MapiPid.AttachMimeTag, "multipart/signed"));
                a.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachDataBinary, MapiType.Binary), data));
            }
            else
            {
                // MS-OXOSMIME 2.1.3.2: the attachment holds the CMS with the transfer encoding removed; the entity's Content-Type goes in a named property.
                byte[] cms = ((OneMime.MimePart)e).GetDecodedContent();
                m.Properties.Add(Str(MapiPid.MessageClass, "IPM.Note.SMIME"));
                m.Properties.Add(MapiProperty.Create(new MapiTag(0x8000, MapiType.UnicodeString), Encoding.Unicode.GetBytes(contentType),
                    new MapiNamedProperty(MapiPropertySets.InternetHeaders, "Content-Type")));
                a.Properties.Add(Str(MapiPid.AttachMimeTag, "application/pkcs7-mime"));
                a.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachDataBinary, MapiType.Binary), cms));
            }

            // The sender, so that Outlook can compare the signer's certificate address with it (PidTagSender* and PidTagSentRepresenting*, SMTP address type).
            string? from = e.Headers.GetFirst("From")?.Value?.Trim();
            if (!string.IsNullOrEmpty(from))
            {
                foreach (ushort id in new ushort[] { 0x0C1A, 0x0042 }) { m.Properties.Add(Str(id, from)); }
                foreach (ushort id in new ushort[] { 0x0C1F, 0x0065 }) { m.Properties.Add(Str(id, from)); }
                foreach (ushort id in new ushort[] { 0x0C1E, 0x0064 }) { m.Properties.Add(Str(id, "SMTP")); }
            }

            m.Properties.Add(Str(MapiPid.Subject, e.Headers.GetFirst("Subject")?.Value ?? "S/MIME test"));
            m.AddAttachment(a);
            string name = Path.GetRelativePath(source, f).Replace(Path.DirectorySeparatorChar, '_');
            MsgWriter.Write(m, Path.Combine(dir, Path.ChangeExtension(name, ".msg")));
        }

        return 0;
    }

    private static int IndexOfBlankLine(byte[] b)
    {
        for (int i = 0; i + 3 < b.Length; i++)
        {
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n')
            {
                return i + 4;
            }
        }

        return 0;
    }
}
