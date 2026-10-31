using System.Buffers.Binary;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Mbox;

namespace RecursiveAI.OneMail;

/// <summary>The mail formats the content sniffer can name. Naming a format does not mean it can be read yet: see <see cref="MailContainer.Open(string, Mime.Reading.MimeReaderOptions?, MboxReaderOptions?)"/>.</summary>
public enum MailFormat
{
    /// <summary>Nothing recognised.</summary>
    Unknown = 0,

    /// <summary>One RFC 5322 / MIME message: the file starts with a header field.</summary>
    Eml,

    /// <summary>A single mbox file, any variant.</summary>
    Mbox,

    /// <summary>One Apple Mail <c>.emlx</c> file.</summary>
    Emlx,

    /// <summary>A Maildir folder (<c>cur</c> and <c>new</c>).</summary>
    MaildirFolder,

    /// <summary>A Maildir++ root with dot-prefixed subfolders.</summary>
    MaildirRoot,

    /// <summary>An Apple Mail mailbox bundle.</summary>
    AppleMailBundle,

    /// <summary>A Personal Folders file (<c>!BDN</c> header, client magic "SM" or "AB"). Recognised; not readable through the shared view yet.</summary>
    Pst,

    /// <summary>An offline folders file (<c>!BDN</c> header, client magic "SO"). Recognised; not readable through the shared view yet.</summary>
    Ost,

    /// <summary>An Outlook message or template: a compound file with the MAPI property stream. Recognised; not readable yet.</summary>
    Msg,

    /// <summary>Any other compound file (the sniffer found no MAPI property stream, or could not read the directory).</summary>
    CompoundFile,

    /// <summary>A TNEF stream (<c>winmail.dat</c>). Recognised; not readable yet.</summary>
    Tnef,

    /// <summary>An iCalendar object (<c>BEGIN:VCALENDAR</c>). Recognised; not readable yet.</summary>
    Ics,

    /// <summary>A vCard (<c>BEGIN:VCARD</c>). Recognised; not readable yet.</summary>
    VCard,
}

/// <summary>
/// Content-based format detection. The file name and extension are never consulted. A file is recognised by its first bytes
/// (and, for compound files, by its directory); a message with no header at the top is not recognised, because the only evidence
/// of an EML is a header field on the first line.
/// </summary>
public static class MailFormatSniffer
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] CfbMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Names the format of a file's bytes.</summary>
    /// <param name="source">The bytes. At most the first 4 KiB is read, plus, for an Apple Mail <c>.emlx</c>, the bytes after the message and, for a compound file, its directory.</param>
    /// <returns>The format, or <see cref="MailFormat.Unknown"/>. Never throws on malformed content.</returns>
    public static MailFormat Sniff(IRandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0)
        {
            return MailFormat.Unknown;
        }

        byte[] head = new byte[4096];
        int n = source.ReadFully(0, head);
        ReadOnlySpan<byte> h = head.AsSpan(0, n);

        // MS-OXMSG 2.1: a message is a compound file; MS-CFB 2.2 gives the signature.
        if (h.StartsWith(CfbMagic))
        {
            return RecursiveAI.OneMail.Msg.MsgFile.IsMsg(source) ? MailFormat.Msg : MailFormat.CompoundFile;
        }

        // MS-PST 2.2.2.6: dwMagic "!BDN", then dwCRCPartial (4), then wMagicClient.
        if (h.StartsWith("!BDN"u8))
        {
            return n >= 10 && BinaryPrimitives.ReadUInt16LittleEndian(h[8..]) == 0x4F53 ? MailFormat.Ost : MailFormat.Pst;
        }

        // MS-OXTNEF 2.1.1: the TNEF signature 0x223E9F78, little-endian.
        if (h.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(h) == 0x223E9F78)
        {
            return MailFormat.Tnef;
        }

        StoreFormat store = StoreSniffer.Sniff(source);
        if (store == StoreFormat.Mbox)
        {
            return MailFormat.Mbox;
        }

        if (store == StoreFormat.Emlx)
        {
            return MailFormat.Emlx;
        }

        return SniffText(h);
    }

    /// <summary>Names the format of a directory.</summary>
    /// <param name="path">The directory.</param>
    /// <returns>A Maildir folder or root, an Apple Mail bundle, or <see cref="MailFormat.Unknown"/> (for example a directory of loose messages).</returns>
    public static MailFormat SniffDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return StoreSniffer.SniffDirectory(new PhysicalStoreFileSystem(path)) switch
        {
            StoreFormat.MaildirFolder => MailFormat.MaildirFolder,
            StoreFormat.MaildirRoot => MailFormat.MaildirRoot,
            StoreFormat.AppleMailBundle => MailFormat.AppleMailBundle,
            _ => MailFormat.Unknown,
        };
    }

    // The text formats start with a recognisable first line.
    private static MailFormat SniffText(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith(Utf8Bom))
        {
            h = h[3..];
        }

        int i = 0;
        while (i < h.Length && h[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        ReadOnlySpan<byte> t = h[i..];
        if (StartsWithIgnoreCase(t, "BEGIN:VCALENDAR"u8))
        {
            return MailFormat.Ics;
        }

        if (StartsWithIgnoreCase(t, "BEGIN:VCARD"u8))
        {
            return MailFormat.VCard;
        }

        // RFC 5322 §3.6: a message starts with a header field: a name of printable ASCII, then a colon, on the first line.
        // Leading blank lines mean the message has no headers, which is indistinguishable from any text file.
        if (h.Length == 0 || h[0] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
        {
            return MailFormat.Unknown;
        }

        // RFC 5322 §4.2 obsolete syntax allows blanks between the field name and the colon.
        int end = 0;
        while (end < h.Length && h[end] is >= 0x21 and <= 0x7E and not (byte)':')
        {
            end++;
        }

        int colon = end;
        while (colon < h.Length && h[colon] is (byte)' ' or (byte)'\t')
        {
            colon++;
        }

        return end > 0 && colon < h.Length && h[colon] == (byte)':' ? MailFormat.Eml : MailFormat.Unknown;
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> upperAscii)
    {
        if (data.Length < upperAscii.Length)
        {
            return false;
        }

        for (int i = 0; i < upperAscii.Length; i++)
        {
            if ((data[i] & 0xDF) != upperAscii[i] && data[i] != upperAscii[i])
            {
                return false;
            }
        }

        return true;
    }
}
