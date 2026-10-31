using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Dbx;
using RecursiveAI.OneMail.Stores.Emlx;
using RecursiveAI.OneMail.Stores.Mbox;

namespace RecursiveAI.OneMail.Stores;

/// <summary>Content-based detection of the mail store formats of this layer. Never decides by file extension.</summary>
public static class StoreSniffer
{
    /// <summary>Recognises a single file: an Outlook Express database (class id in the header), an mbox (starts with an envelope line) or an <c>.emlx</c> (byte-count line, message, then a property list or nothing).</summary>
    /// <param name="source">The file's bytes; only the first 4 KiB and, for EMLX, the bytes at the end of the message are read.</param>
    /// <returns><see cref="StoreFormat.Dbx"/>, <see cref="StoreFormat.Mbox"/>, <see cref="StoreFormat.Emlx"/> or <see cref="StoreFormat.Unknown"/>.</returns>
    public static StoreFormat Sniff(IRandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] head = new byte[4096];
        int n = source.ReadFully(0, head);
        ReadOnlySpan<byte> h = head.AsSpan(0, n);
        if (DbxReader.Detect(h) != DbxFileKind.Unknown)
        {
            return StoreFormat.Dbx;
        }

        if (MboxSniffer.LooksLikeMbox(h))
        {
            return StoreFormat.Mbox;
        }

        EmlxEntry e = EmlxReader.Parse(h, source.Length);
        if ((e.Defects & StoreDefects.BadByteCount) != 0 || e.ByteCount == 0)
        {
            return StoreFormat.Unknown;
        }

        // The message must be followed by the end of the file or an XML property list.
        if (e.TrailerLength == 0 && (e.Defects & StoreDefects.TruncatedMessage) == 0)
        {
            return StoreFormat.Emlx;
        }

        if (e.TrailerLength > 0)
        {
            byte[] t = new byte[(int)Math.Min(e.TrailerLength, 64)];
            int m = source.ReadFully(e.TrailerOffset, t);
            ReadOnlySpan<byte> ts = t.AsSpan(0, m);
            int i = 0;
            while (i < ts.Length && ts[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                i++;
            }

            if (ts[i..].StartsWith("<?xml"u8) || ts[i..].StartsWith("<plist"u8) || ts[i..].StartsWith("<!DOCTYPE"u8))
            {
                return StoreFormat.Emlx;
            }
        }

        return StoreFormat.Unknown;
    }

    /// <summary>
    /// Recognises a directory store: a Maildir folder (<c>cur</c> and <c>new</c>), a Maildir++ root (dot-prefixed Maildir
    /// subfolders), an Apple Mail bundle (<c>Messages</c> directories of <c>.emlx</c> files), or an Outlook Express store folder (<c>Folders.dbx</c>).
    /// </summary>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="path">The directory; empty for the file system's root.</param>
    /// <returns>The format, or <see cref="StoreFormat.Unknown"/>.</returns>
    public static StoreFormat SniffDirectory(IStoreFileSystem fileSystem, string path = "")
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path = path.Trim('/');
        string P(string name) => path.Length == 0 ? name : path + "/" + name;

        bool hasSubfolders = false;
        foreach (StoreFileInfo f in fileSystem.List(path))
        {
            if (f.IsDirectory && f.Name.Length > 1 && f.Name[0] == '.'
                && (fileSystem.DirectoryExists(P(f.Name) + "/cur") || fileSystem.DirectoryExists(P(f.Name) + "/new")))
            {
                hasSubfolders = true;
                break;
            }
        }

        bool isFolder = fileSystem.DirectoryExists(P("cur")) && fileSystem.DirectoryExists(P("new"));
        if (hasSubfolders)
        {
            return StoreFormat.MaildirRoot;
        }

        if (isFolder)
        {
            return StoreFormat.MaildirFolder;
        }

        if (new AppleMailBundle(fileSystem, path).Exists())
        {
            return StoreFormat.AppleMailBundle;
        }

        return DbxStoreFolder.Sniff(fileSystem, path) ? StoreFormat.DbxStoreFolder : StoreFormat.Unknown;
    }
}
