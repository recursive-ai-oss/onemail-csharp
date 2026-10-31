using System.IO.Compression;
using System.Text;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Olm;

namespace RecursiveAI.OneMail.Fuzz;

// OLM archives. Zip bytes mutate poorly (every entry has a CRC), so most inputs are XML in a small, valid archive:
//   byte 0, low 3 bits: which entry the XML goes into (0 message, 1 calendar, 2 contacts, 3 groups, 4 tasks, 5 notes, 6 categories);
//                       7 = the rest of the input is the archive itself;
//   byte 0, bit 3: compress the entry (deflate) instead of storing it;
//   the rest: the entry's text.
// A second entry, the attachment the message seeds point at, is always present. The reader must never throw, hang or grow without
// bound, and every view and conversion over what it read must run to the end.
internal static class OlmTarget
{
    private const string Messages = "Local/com.microsoft.__Messages/Inbox/";

    private static readonly string[] Paths =
    [
        Messages + "message_00000.xml", "Local/Calendar/Calendar.xml", "Local/Address Book/Contacts.xml", "Local/Address Book/Groups.xml",
        "Local/Tasks/Tasks.xml", "Local/Notes/Notes.xml", "Categories.xml",
    ];

    private static readonly OlmReaderOptions Small = new() { MaxXmlBytes = 1 << 20, MaxTextLength = 1 << 18, MaxElementsPerItem = 20_000, MaxXmlDepth = 32, MaxExpansionRatio = 200 };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] zip;
        if ((data[0] & 7) == 7)
        {
            zip = data[1..].ToArray();
        }
        else
        {
            zip = Build(Paths[data[0] & 7], data[1..], compress: (data[0] & 8) != 0);
        }

        if (!OlmArchive.IsOlm(new MemorySource(zip)) && (data[0] & 7) != 7)
        {
            throw new InvalidOperationException("the sniffer did not recognise a well-formed archive");
        }

        OlmArchive olm;
        try
        {
            olm = OlmArchive.Open(new MemorySource(zip), false, null, Small);
        }
        catch (InvalidDataException)
        {
            return;
        }

        using (olm)
        {
            _ = olm.GetFolders();
            int n = 0;
            foreach (OlmItem it in olm.Enumerate())
            {
                if (++n > 2000)
                {
                    break;
                }

                Touch(it);
            }

            _ = olm.Defects.Count;
        }
    }

    private static void Touch(OlmItem it)
    {
        _ = it.Subject;
        _ = it.From;
        _ = it.ToRecipients;
        _ = it.Cc;
        _ = it.Bcc;
        _ = it.Sender;
        _ = it.Sent;
        _ = it.TryGetDate(MailDateSource.Delivery, out _);
        _ = it.MessageId;
        _ = it.References;
        _ = it.ConversationIndex;
        _ = it.TextBody?.ReadText();
        _ = it.HtmlBody?.ReadText();
        foreach (IMailAttachment a in it.Attachments)
        {
            _ = a.FileName;
            using Stream s = a.Open();
            s.CopyTo(Stream.Null);
        }

        _ = it.MimeSource;
        _ = it.Categories;
        _ = it.UnmappedElements();
        _ = it.OlmDefects;
        _ = it.Defects.Count();
        Calendar.Component? converted = it.OlmKind switch
        {
            OlmItemKind.Appointment or OlmItemKind.Task or OlmItemKind.Note => new OlmToCalendarConverter().Convert(it).Value,
            OlmItemKind.Contact or OlmItemKind.Group => new OlmToVCardConverter().Convert(it).Value,
            _ => null,
        };
        if (converted is not null)
        {
            var doc = new Calendar.ContentDocument();
            doc.Root.Add(converted);
            _ = doc.ToArray();
        }
    }

    private static byte[] Build(string path, ReadOnlySpan<byte> xml, bool compress)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            CompressionLevel level = compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
            using (Stream s = z.CreateEntry(path, level).Open())
            {
                s.Write(xml);
            }

            using (Stream s = z.CreateEntry(Messages + "com.microsoft.__Attachments/a_0000", level).Open())
            {
                s.Write(Encoding.ASCII.GetBytes("attachment bytes"));
            }
        }

        return ms.ToArray();
    }
}
