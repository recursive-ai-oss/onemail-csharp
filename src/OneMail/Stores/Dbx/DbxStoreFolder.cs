using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Dbx;

/// <summary>One message of an Outlook Express store folder, with the folder it belongs to.</summary>
/// <param name="Folder">The folder's path from <c>Folders.dbx</c> (names joined by <c>/</c>, without the root), or the file's name without extension when no folder lists the file.</param>
/// <param name="File">The message file's path in the store folder's file system.</param>
/// <param name="Message">The message record.</param>
/// <param name="Defects">What was irregular about the file the message is in: <see cref="StoreDefects.DbxUnlistedFile"/> or none.</param>
public readonly record struct DbxStoreMessage(string Folder, string File, DbxMessageEntry Message, StoreDefects Defects = StoreDefects.None);

/// <summary>
/// An Outlook Express store folder: a directory with <c>Folders.dbx</c> and the message files it names. Reads the folder
/// hierarchy and enumerates the messages folder by folder.
/// </summary>
/// <remarks>
/// A folder record names its message file (<c>Inbox.dbx</c>); the match to the directory's listing ignores case, as Windows does.
/// Message files in the directory that no folder names are read too, as folders named after the file, with
/// <see cref="StoreDefects.DbxUnlistedFile"/>. <c>Folders.dbx</c>, <c>Pop3uidl.dbx</c> and <c>Offline.dbx</c> hold no mail and are skipped.
/// </remarks>
public sealed class DbxStoreFolder
{
    private readonly IStoreFileSystem _fs;
    private readonly string _path;
    private readonly DbxReaderOptions? _options;
    private readonly List<DbxFolderEntry> _folders = [];
    private readonly Dictionary<int, DbxFolderEntry> _byIndex = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens the store folder and reads <c>Folders.dbx</c> if there is one.</summary>
    /// <param name="fileSystem">The file system holding the directory.</param>
    /// <param name="path">The directory; empty for the file system's root.</param>
    /// <param name="options">Reader options for every file, or null.</param>
    public DbxStoreFolder(IStoreFileSystem fileSystem, string path = "", DbxReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fs = fileSystem;
        _path = path.Trim('/');
        _options = options;
        foreach (StoreFileInfo f in _fs.List(_path))
        {
            if (!f.IsDirectory)
            {
                _names.TryAdd(f.Name, Combine(_path, f.Name));
            }
        }

        string? folders = FindFile("Folders.dbx");
        if (folders is null)
        {
            return;
        }

        using IRandomAccessSource src = _fs.OpenRead(folders);
        DbxReader r = new(src, options);
        foreach (DbxFolderEntry f in r.EnumerateFolders())
        {
            _folders.Add(f);
            _byIndex.TryAdd(f.Index, f);
        }
    }

    /// <summary>The folder records in index order; empty without <c>Folders.dbx</c>.</summary>
    public IReadOnlyList<DbxFolderEntry> Folders => _folders;

    /// <summary>Recognises a directory as an Outlook Express store folder: it holds a <c>Folders.dbx</c> that is a folder database.</summary>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="path">The directory; empty for the root.</param>
    /// <returns>True for a store folder.</returns>
    public static bool Sniff(IStoreFileSystem fileSystem, string path = "")
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        path = path.Trim('/');
        foreach (StoreFileInfo f in fileSystem.List(path))
        {
            if (!f.IsDirectory && f.Name.Equals("Folders.dbx", StringComparison.OrdinalIgnoreCase))
            {
                using IRandomAccessSource s = fileSystem.OpenRead(Combine(path, f.Name));
                Span<byte> head = stackalloc byte[8];
                int n = s.ReadFully(0, head);
                return DbxReader.Detect(head[..n]) == DbxFileKind.FolderDatabase;
            }
        }

        return false;
    }

    /// <summary>The path of a folder: its name and its ancestors' names joined by <c>/</c>, without the root ("Outlook Express").</summary>
    /// <param name="folder">A folder of this store.</param>
    /// <returns>The path. A cycle or a missing parent ends the climb where it is found.</returns>
    public string PathOf(DbxFolderEntry folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        List<string> names = [];
        DbxFolderEntry? f = folder;
        for (int depth = 0; f is not null && depth < 64; depth++)
        {
            if (f.ParentIndex == -1)
            {
                break;
            }

            names.Add(f.Name ?? string.Empty);
            f = _byIndex.GetValueOrDefault(f.ParentIndex);
        }

        names.Reverse();
        return string.Join('/', names);
    }

    /// <summary>Enumerates the messages of every message file: listed folders in index order, then unlisted files by name.</summary>
    /// <returns>The messages. Each message file is open only while its messages are yielded.</returns>
    public IEnumerable<DbxStoreMessage> EnumerateMessages()
    {
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        List<StoreFileInfo> files = [];
        foreach (StoreFileInfo f in _fs.List(_path))
        {
            if (!f.IsDirectory)
            {
                files.Add(f);
            }
        }

        foreach (DbxFolderEntry folder in _folders)
        {
            string? name = folder.FileName;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            string? file = FindFile(name);
            if (file is null || !used.Add(file))
            {
                continue;
            }

            string folderPath = PathOf(folder);
            foreach (DbxStoreMessage m in ReadFile(file, folderPath, listed: true))
            {
                yield return m;
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (StoreFileInfo f in files)
        {
            string file = Combine(_path, f.Name);
            if (f.Name.Equals("Folders.dbx", StringComparison.OrdinalIgnoreCase) || used.Contains(file))
            {
                continue;
            }

            string stem = f.Name.EndsWith(".dbx", StringComparison.OrdinalIgnoreCase) ? f.Name[..^4] : f.Name;
            foreach (DbxStoreMessage m in ReadFile(file, stem, listed: false))
            {
                yield return m;
            }
        }
    }

    /// <summary>Reads one message's bytes.</summary>
    /// <param name="message">A message from <see cref="EnumerateMessages"/>.</param>
    /// <param name="info">The chain's length, segment count and defects.</param>
    /// <returns>The message bytes.</returns>
    public byte[] ReadMessage(in DbxStoreMessage message, out DbxChainInfo info)
    {
        using IRandomAccessSource src = _fs.OpenRead(message.File);
        return DbxReader.ReadMessage(src, message.Message, _options, out info);
    }

    /// <summary>Walks one message's chain headers to find its length and defects.</summary>
    /// <param name="message">A message from <see cref="EnumerateMessages"/>.</param>
    /// <returns>The chain's length, segment count and defects.</returns>
    public DbxChainInfo MeasureMessage(in DbxStoreMessage message)
    {
        using IRandomAccessSource src = _fs.OpenRead(message.File);
        return new DbxReader(src, _options).MeasureMessage(message.Message);
    }

    private IEnumerable<DbxStoreMessage> ReadFile(string file, string folder, bool listed)
    {
        using IRandomAccessSource src = _fs.OpenRead(file);
        DbxReader r = new(src, _options);
        if (r.Kind != DbxFileKind.MessageDatabase)
        {
            yield break;
        }

        if (!listed)
        {
            _options?.DefectHandler?.Invoke(new StoreDefect(StoreDefects.DbxUnlistedFile, 0));
        }

        foreach (DbxMessageEntry e in r.EnumerateMessages())
        {
            yield return new DbxStoreMessage(folder, file, e, listed ? StoreDefects.None : StoreDefects.DbxUnlistedFile);
        }
    }

    // Case-insensitive lookup of a file in the directory.
    private string? FindFile(string name) => _names.GetValueOrDefault(name);

    private static string Combine(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;
}
