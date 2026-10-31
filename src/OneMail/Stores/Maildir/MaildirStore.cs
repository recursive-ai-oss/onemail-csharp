using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Maildir;

/// <summary>Which delivery directory of a Maildir folder a message is in.</summary>
public enum MaildirSubdirectory
{
    /// <summary><c>new</c>: delivered and not yet seen by a mail client. Entries here carry <see cref="MessageMarks.Recent"/>.</summary>
    New = 0,

    /// <summary><c>cur</c>: messages a client has taken in; the file name carries the flags.</summary>
    Cur = 1,
}

/// <summary>A message file of a Maildir folder.</summary>
/// <param name="Folder">The folder name: empty for the Maildir itself (the inbox), otherwise the Maildir++ name such as <c>Archive.2024</c>.</param>
/// <param name="Subdirectory">Whether the file is in <c>new</c> or <c>cur</c>.</param>
/// <param name="FileName">The file name.</param>
/// <param name="Path">The file's path in the store's file system.</param>
/// <param name="Name">The file name taken apart.</param>
/// <param name="Marks">The standard flags from the name, plus <see cref="MessageMarks.Recent"/> for files in <c>new</c>.</param>
/// <param name="Keywords">Keyword names from the folder's keyword files (Dovecot letters or Courier keyword list); unknown letters appear as the letter.</param>
/// <param name="Size">The file size in bytes.</param>
/// <param name="Modified">The file's modification time, which Maildir readers use as the message's arrival date.</param>
/// <param name="Uid">The IMAP UID from <c>dovecot-uidlist</c>, or 0 when the folder has none for this message.</param>
/// <param name="Defects">What was irregular about this entry.</param>
public readonly record struct MaildirEntry(
    string Folder,
    MaildirSubdirectory Subdirectory,
    string FileName,
    string Path,
    MaildirName Name,
    MessageMarks Marks,
    IReadOnlyList<string> Keywords,
    long Size,
    DateTimeOffset Modified,
    long Uid,
    StoreDefects Defects);

/// <summary>Options for <see cref="MaildirStore"/>.</summary>
public sealed class MaildirStoreOptions
{
    /// <summary>
    /// Called to make the unique part of a delivered file name. The default builds
    /// <c>seconds.M{microseconds}P{pid}Q{counter}.{host}</c> (the qmail/Maildir convention) from <see cref="TimeProvider"/>.
    /// Supply one for reproducible names.
    /// </summary>
    public Func<string>? UniqueNameFactory { get; init; }

    /// <summary>The clock for default unique names. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The host name written into default unique names. Default: this machine's name.</summary>
    public string? HostName { get; init; }

    /// <summary>Called for every defect found while reading. Default none.</summary>
    public Action<StoreDefect>? DefectHandler { get; init; }
}

/// <summary>
/// A Maildir (and Maildir++) mail store over an <see cref="IStoreFileSystem"/>: enumerates message files with their
/// flags, keywords and UIDs, opens them lazily, delivers new messages the way the Maildir protocol prescribes and updates flags.
/// </summary>
/// <remarks>
/// <para>
/// Reading covers Maildir's <c>cur</c> and <c>new</c>, Courier and Dovecot conventions: flags in the file name's
/// <c>:2,</c> info section, Dovecot keyword letters resolved through the folder's <c>dovecot-keywords</c>, Dovecot UIDs from
/// <c>dovecot-uidlist</c>, Courier keywords from <c>courierimapkeywords/:list</c>, and Maildir++ subfolders (a directory
/// named <c>.Name</c> that holds <c>cur</c>/<c>new</c>). <c>tmp</c> is never listed: its files are deliveries in progress.
/// Entries come in a stable order: <c>cur</c> before <c>new</c>, each by file name (ordinal).
/// </para>
/// <para>Writing follows the Maildir delivery protocol: the message is written under <c>tmp</c> with a name that did not exist, then renamed into <c>new</c>, or into <c>cur</c> with flags when it has any.</para>
/// </remarks>
public sealed class MaildirStore
{
    private readonly IStoreFileSystem _fs;
    private readonly string _root;
    private readonly MaildirStoreOptions _options;
    private long _counter;

    /// <summary>Creates a store over a directory of the file system. Nothing is read or created yet.</summary>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="rootPath">The Maildir's path in it; empty for the file system's root.</param>
    /// <param name="options">Options; null means the defaults.</param>
    public MaildirStore(IStoreFileSystem fileSystem, string rootPath = "", MaildirStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fs = fileSystem;
        _root = rootPath.Trim('/');
        _options = options ?? new MaildirStoreOptions();
    }

    /// <summary>The file system the store reads and writes.</summary>
    public IStoreFileSystem FileSystem => _fs;

    /// <summary>The Maildir's path in the file system.</summary>
    public string RootPath => _root;

    // ---- Folders ----

    /// <summary>
    /// Lists the folders: the empty name for the Maildir itself when it has <c>cur</c> or <c>new</c>, then each
    /// Maildir++ subfolder by ordinal name (<c>Archive</c>, <c>Archive.2024</c>).
    /// </summary>
    /// <returns>The folder names.</returns>
    public IReadOnlyList<string> ListFolders()
    {
        List<string> names = [];
        if (IsMaildir(_root))
        {
            names.Add(string.Empty);
        }

        List<string> subs = [];
        foreach (StoreFileInfo f in _fs.List(_root))
        {
            if (f.IsDirectory && f.Name.Length > 1 && f.Name[0] == '.' && IsMaildir(Join(_root, f.Name)))
            {
                subs.Add(f.Name[1..]);
            }
        }

        subs.Sort(StringComparer.Ordinal);
        names.AddRange(subs);
        return names;
    }

    /// <summary>Creates a folder's directories (<c>cur</c>, <c>new</c>, <c>tmp</c>) and, for a subfolder, the <c>maildirfolder</c> marker. An existing folder is left alone.</summary>
    /// <param name="folder">The folder name; empty for the Maildir itself.</param>
    public void CreateFolder(string folder = "")
    {
        string dir = FolderPath(folder);
        _fs.CreateDirectory(Join(dir, "cur"));
        _fs.CreateDirectory(Join(dir, "new"));
        _fs.CreateDirectory(Join(dir, "tmp"));
        if (folder.Length > 0 && !_fs.FileExists(Join(dir, "maildirfolder")))
        {
            using Stream s = _fs.CreateNew(Join(dir, "maildirfolder"));
        }
    }

    // ---- Reading ----

    /// <summary>Enumerates the messages of one folder.</summary>
    /// <param name="folder">The folder name; empty for the Maildir itself.</param>
    /// <returns>The entries, <c>cur</c> first, each directory by file name.</returns>
    public IEnumerable<MaildirEntry> Enumerate(string folder = "")
    {
        string dir = FolderPath(folder);
        FolderContext ctx = LoadContext(folder, dir);
        foreach (MaildirSubdirectory sub in new[] { MaildirSubdirectory.Cur, MaildirSubdirectory.New })
        {
            string subName = sub == MaildirSubdirectory.Cur ? "cur" : "new";
            List<StoreFileInfo> files = [.. _fs.List(Join(dir, subName)).Where(f => !f.IsDirectory && f.Name[0] != '.')];
            files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (StoreFileInfo f in files)
            {
                yield return MakeEntry(folder, dir, sub, subName, f, ctx);
            }
        }
    }

    /// <summary>Enumerates the messages of one folder, honouring cancellation between entries.</summary>
    /// <param name="folder">The folder name; empty for the Maildir itself.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The entries, as <see cref="Enumerate"/>.</returns>
    public async IAsyncEnumerable<MaildirEntry> EnumerateAsync(string folder = "", [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (MaildirEntry e in Enumerate(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Enumerates the messages of every folder, the Maildir itself first.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The entries.</returns>
    public async IAsyncEnumerable<MaildirEntry> EnumerateAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string folder in ListFolders())
        {
            await foreach (MaildirEntry e in EnumerateAsync(folder, cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
    }

    /// <summary>Opens a message file for random-access reading.</summary>
    /// <param name="entry">An entry of this store.</param>
    /// <returns>A source the caller disposes.</returns>
    public IRandomAccessSource Open(in MaildirEntry entry) => _fs.OpenRead(entry.Path);

    // ---- Writing ----

    /// <summary>
    /// Delivers a message: writes it under <c>tmp</c> and renames it into <c>new</c>, or into <c>cur</c> with the info
    /// section when <paramref name="marks"/> or <paramref name="keywords"/> are given.
    /// </summary>
    /// <param name="message">The message bytes. They are written unchanged.</param>
    /// <param name="folder">The folder; created if missing. Empty for the Maildir itself.</param>
    /// <param name="marks">Standard flags. <see cref="MessageMarks.Recent"/> is not stored.</param>
    /// <param name="keywords">Keyword names, stored as Dovecot letters through the folder's <c>dovecot-keywords</c> (extended as needed).</param>
    /// <returns>The entry that was created.</returns>
    /// <exception cref="InvalidOperationException">More than 26 distinct keywords would be needed in the folder.</exception>
    public MaildirEntry Deliver(ReadOnlyMemory<byte> message, string folder = "", MessageMarks marks = MessageMarks.None, IEnumerable<string>? keywords = null)
    {
        (string unique, string tmp) = BeginDelivery(folder);
        using (Stream s = _fs.CreateNew(tmp))
        {
            s.Write(message.Span);
        }

        return FinishDelivery(folder, unique, tmp, marks, keywords);
    }

    /// <summary>Delivers a message read from a range of a source. See <see cref="Deliver"/>.</summary>
    /// <param name="source">Where the message bytes are.</param>
    /// <param name="offset">First byte of the message.</param>
    /// <param name="length">Length of the message.</param>
    /// <param name="folder">The folder; created if missing.</param>
    /// <param name="marks">Standard flags.</param>
    /// <param name="keywords">Keyword names.</param>
    /// <param name="cancellationToken">Cancels the delivery; the partial file under <c>tmp</c> is removed.</param>
    /// <returns>The entry that was created.</returns>
    public async ValueTask<MaildirEntry> DeliverAsync(IRandomAccessSource source, long offset, long length, string folder = "", MessageMarks marks = MessageMarks.None, IEnumerable<string>? keywords = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        (string unique, string tmp) = BeginDelivery(folder);
        try
        {
            await using (Stream s = _fs.CreateNew(tmp))
            {
                await CopyRange(source, offset, length, s, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _fs.Delete(tmp);
            throw;
        }

        return FinishDelivery(folder, unique, tmp, marks, keywords);
    }

    /// <summary>
    /// Changes a message's flags and keywords by renaming its file into <c>cur</c> with the new info section. Content and
    /// the unique part do not change.
    /// </summary>
    /// <param name="entry">An entry of this store.</param>
    /// <param name="marks">The new standard flags.</param>
    /// <param name="keywords">The new keyword names.</param>
    /// <returns>The entry for the renamed file.</returns>
    public MaildirEntry SetFlags(in MaildirEntry entry, MessageMarks marks, IEnumerable<string>? keywords = null)
    {
        string dir = FolderPath(entry.Folder);
        string letters = KeywordLetters(entry.Folder, dir, keywords);
        string name = MaildirName.Build(entry.Name.Unique, marks, letters, entry.Name.Extra);
        string target = Join(Join(dir, "cur"), name);
        if (target != entry.Path)
        {
            _fs.Rename(entry.Path, target);
        }

        return Enumerate(entry.Folder).First(e => e.Path == target);
    }

    /// <summary>
    /// Copies this store into another place through the model: every folder, every message file under its exact name, and
    /// the folder control files (keyword lists, UID lists, markers) byte for byte. Reading the copy gives the same entries.
    /// </summary>
    /// <param name="destination">The file system to copy into.</param>
    /// <param name="destinationRoot">The directory in it for the copy's root.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The number of message files copied.</returns>
    public async ValueTask<int> CopyToAsync(IStoreFileSystem destination, string destinationRoot = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string destRoot = destinationRoot.Trim('/');
        int copied = 0;
        foreach (string folder in ListFolders())
        {
            string dir = FolderPath(folder);
            string destDir = folder.Length == 0 ? destRoot : Join(destRoot, "." + folder);
            foreach (string sub in new[] { "cur", "new", "tmp" })
            {
                destination.CreateDirectory(Join(destDir, sub));
            }

            foreach (MaildirEntry e in Enumerate(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string subName = e.Subdirectory == MaildirSubdirectory.Cur ? "cur" : "new";
                string tmp = Join(Join(destDir, "tmp"), e.FileName);
                using IRandomAccessSource src = Open(e);
                await using (Stream s = destination.CreateNew(tmp))
                {
                    await CopyRange(src, 0, src.Length, s, cancellationToken).ConfigureAwait(false);
                }

                destination.Rename(tmp, Join(Join(destDir, subName), e.FileName));
                copied++;
            }

            await CopyControlFiles(dir, destination, destDir, cancellationToken).ConfigureAwait(false);
        }

        return copied;
    }

    // ---- Internals ----

    private string FolderPath(string folder) => folder.Length == 0 ? _root : Join(_root, "." + folder);

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;

    private bool IsMaildir(string path) => _fs.DirectoryExists(Join(path, "cur")) || _fs.DirectoryExists(Join(path, "new"));

    private void Report(StoreDefects kind) => _options.DefectHandler?.Invoke(new StoreDefect(kind, 0));

    private sealed class FolderContext
    {
        public string[] KeywordNames { get; init; } = [];

        public Dictionary<string, long> Uids { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> CourierKeywords { get; init; } = new(StringComparer.Ordinal);
    }

    private FolderContext LoadContext(string folder, string dir)
    {
        string[] names = LoadDovecotKeywords(dir);
        Dictionary<string, long> uids = new(StringComparer.Ordinal);
        string uidPath = Join(dir, "dovecot-uidlist");
        if (_fs.FileExists(uidPath))
        {
            ParseUidList(ReadAll(uidPath), uids);
        }

        Dictionary<string, List<string>> courier = new(StringComparer.Ordinal);
        string courierPath = Join(Join(dir, "courierimapkeywords"), ":list");
        if (_fs.FileExists(courierPath))
        {
            ParseCourierList(ReadAll(courierPath), courier);
        }

        return new FolderContext { KeywordNames = names, Uids = uids, CourierKeywords = courier };
    }

    private byte[] ReadAll(string path)
    {
        using IRandomAccessSource s = _fs.OpenRead(path);
        long len = Math.Min(s.Length, 64L << 20);
        byte[] b = new byte[len];
        int n = s.ReadFully(0, b);
        return b.AsSpan(0, n).ToArray();
    }

    // dovecot-keywords: lines "<index> <name>"; letter 'a' is index 0.
    private string[] LoadDovecotKeywords(string dir)
    {
        string path = Join(dir, "dovecot-keywords");
        if (!_fs.FileExists(path))
        {
            return [];
        }

        string[] names = new string[26];
        foreach (string line in Encoding.UTF8.GetString(ReadAll(path)).Split('\n'))
        {
            string l = line.TrimEnd('\r');
            int sp = l.IndexOf(' ', StringComparison.Ordinal);
            if (l.Length == 0)
            {
                continue;
            }

            if (sp > 0 && int.TryParse(l.AsSpan(0, sp), NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && idx is >= 0 and < 26)
            {
                names[idx] = l[(sp + 1)..];
            }
            else
            {
                Report(StoreDefects.MalformedControlFile);
            }
        }

        return names;
    }

    // dovecot-uidlist: a header line, then "<uid> [fields] :<file name>" per message.
    private void ParseUidList(byte[] data, Dictionary<string, long> uids)
    {
        string[] lines = Encoding.UTF8.GetString(data).Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string l = lines[i].TrimEnd('\r');
            if (l.Length == 0)
            {
                continue;
            }

            int at = l.IndexOf(" :", StringComparison.Ordinal);
            int sp = l.IndexOf(' ', StringComparison.Ordinal);
            if (at < 0 || sp < 0 || !long.TryParse(l.AsSpan(0, sp), NumberStyles.None, CultureInfo.InvariantCulture, out long uid))
            {
                Report(StoreDefects.MalformedControlFile);
                continue;
            }

            string file = l[(at + 2)..];
            int colon = file.IndexOf(':', StringComparison.Ordinal);
            uids[colon < 0 ? file : file[..colon]] = uid;
        }
    }

    // courierimapkeywords/:list: keyword names one per line, an empty line, then "<unique name>:<n> <n> ..." where each
    // n is the zero-based position of a keyword in the names.
    private void ParseCourierList(byte[] data, Dictionary<string, List<string>> map)
    {
        List<string> names = [];
        bool inNames = true;
        foreach (string line in Encoding.UTF8.GetString(data).Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (inNames)
            {
                if (l.Length == 0)
                {
                    inNames = false;
                }
                else
                {
                    names.Add(l);
                }

                continue;
            }

            if (l.Length == 0)
            {
                continue;
            }

            int colon = l.LastIndexOf(':');
            if (colon <= 0)
            {
                Report(StoreDefects.MalformedControlFile);
                continue;
            }

            List<string> kws = [];
            foreach (string tok in l[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && idx < names.Count)
                {
                    kws.Add(names[idx]);
                }
            }

            map[l[..colon]] = kws;
        }
    }

    private MaildirEntry MakeEntry(string folder, string dir, MaildirSubdirectory sub, string subName, StoreFileInfo f, FolderContext ctx)
    {
        MaildirName name = MaildirName.Parse(f.Name);
        StoreDefects defects = name.Defects;
        MessageMarks marks = name.Marks;
        if (sub == MaildirSubdirectory.New)
        {
            marks |= MessageMarks.Recent;
        }

        List<string> keywords = [];
        foreach (char c in name.KeywordLetters)
        {
            string? kw = ctx.KeywordNames.Length > 0 ? ctx.KeywordNames[c - 'a'] : null;
            if (string.IsNullOrEmpty(kw))
            {
                defects |= StoreDefects.UnknownKeywordLetter;
                Report(StoreDefects.UnknownKeywordLetter);
                kw = c.ToString();
            }

            keywords.Add(kw);
        }

        if (ctx.CourierKeywords.TryGetValue(name.Unique, out List<string>? ck))
        {
            keywords.AddRange(ck);
        }

        ctx.Uids.TryGetValue(name.Unique, out long uid);
        if ((defects & ~StoreDefects.UnknownKeywordLetter) != StoreDefects.None)
        {
            Report(defects & ~StoreDefects.UnknownKeywordLetter);
        }

        return new MaildirEntry(folder, sub, f.Name, Join(Join(dir, subName), f.Name), name, marks, keywords, f.Length, f.Modified, uid, defects);
    }

    private string UniqueName()
    {
        if (_options.UniqueNameFactory is { } factory)
        {
            return factory();
        }

        // The Maildir convention: seconds, microseconds, process id, a counter and the host (slash and colon escaped).
        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        long seconds = now.ToUnixTimeSeconds();
        long micro = (now.Ticks % TimeSpan.TicksPerSecond) / 10;
        string host = (_options.HostName ?? Environment.MachineName).Replace("/", "\\057", StringComparison.Ordinal).Replace(":", "\\072", StringComparison.Ordinal);
        long q = Interlocked.Increment(ref _counter);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds}.M{micro}P{Environment.ProcessId}Q{q}.{host}");
    }

    private (string Unique, string Tmp) BeginDelivery(string folder)
    {
        CreateFolder(folder);
        string dir = FolderPath(folder);
        string unique = UniqueName();
        return (unique, Join(Join(dir, "tmp"), unique));
    }

    private MaildirEntry FinishDelivery(string folder, string unique, string tmp, MessageMarks marks, IEnumerable<string>? keywords)
    {
        string dir = FolderPath(folder);
        marks &= ~MessageMarks.Recent;
        string letters = KeywordLetters(folder, dir, keywords);
        bool flagged = marks != MessageMarks.None || letters.Length > 0;
        string name = flagged ? MaildirName.Build(unique, marks, letters) : unique;
        string target = Join(Join(dir, flagged ? "cur" : "new"), name);
        _fs.Rename(tmp, target);
        return Enumerate(folder).First(e => e.Path == target);
    }

    // Keyword names -> Dovecot letters, extending dovecot-keywords for names it does not hold yet.
    private string KeywordLetters(string folder, string dir, IEnumerable<string>? keywords)
    {
        if (keywords is null)
        {
            return string.Empty;
        }

        string[] names = LoadDovecotKeywords(dir);
        if (names.Length == 0)
        {
            names = new string[26];
        }

        StringBuilder letters = new();
        bool changed = false;
        foreach (string kw in keywords)
        {
            if (kw.Length == 0 || kw.AsSpan().IndexOfAny(' ', '\r', '\n') >= 0)
            {
                throw new ArgumentException("A keyword must be non-empty and contain no space or line break.", nameof(keywords));
            }

            int idx = Array.IndexOf(names, kw);
            if (idx < 0)
            {
                idx = Array.FindIndex(names, string.IsNullOrEmpty);
                if (idx < 0)
                {
                    throw new InvalidOperationException("A Maildir folder can hold 26 keywords.");
                }

                names[idx] = kw;
                changed = true;
            }

            letters.Append((char)('a' + idx));
        }

        if (changed)
        {
            StringBuilder sb = new();
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]))
                {
                    sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(names[i]).Append('\n');
                }
            }

            // Written beside the messages and swapped in with one rename, as Dovecot requires.
            string tmp = Join(Join(dir, "tmp"), "dovecot-keywords." + UniqueName());
            using (Stream s = _fs.CreateNew(tmp))
            {
                s.Write(Encoding.UTF8.GetBytes(sb.ToString()));
            }

            _fs.Rename(tmp, Join(dir, "dovecot-keywords"), overwrite: true);
        }

        _ = folder;
        return letters.ToString();
    }

    private static async ValueTask CopyRange(IRandomAccessSource source, long offset, long length, Stream destination, CancellationToken ct)
    {
        byte[] buf = new byte[65536];
        long done = 0;
        while (done < length)
        {
            int n = (int)Math.Min(buf.Length, length - done);
            int got = await source.ReadFullyAsync(offset + done, buf.AsMemory(0, n), ct).ConfigureAwait(false);
            if (got == 0)
            {
                throw new EndOfStreamException("The source ended before the range did.");
            }

            await destination.WriteAsync(buf.AsMemory(0, got), ct).ConfigureAwait(false);
            done += got;
        }
    }

    // Everything in a folder directory except cur/new/tmp: dovecot-keywords, dovecot-uidlist, courierimapkeywords/, maildirfolder, ...
    private async ValueTask CopyControlFiles(string dir, IStoreFileSystem destination, string destDir, CancellationToken ct)
    {
        foreach (StoreFileInfo f in _fs.List(dir))
        {
            if (f.IsDirectory)
            {
                if (f.Name is "cur" or "new" or "tmp" || f.Name[0] == '.')
                {
                    continue;
                }

                destination.CreateDirectory(Join(destDir, f.Name));
                await CopyControlFiles(Join(dir, f.Name), destination, Join(destDir, f.Name), ct).ConfigureAwait(false);
            }
            else
            {
                using IRandomAccessSource src = _fs.OpenRead(Join(dir, f.Name));
                if (destination.FileExists(Join(destDir, f.Name)))
                {
                    destination.Delete(Join(destDir, f.Name));
                }

                await using Stream s = destination.CreateNew(Join(destDir, f.Name));
                await CopyRange(src, 0, src.Length, s, ct).ConfigureAwait(false);
            }
        }
    }
}
