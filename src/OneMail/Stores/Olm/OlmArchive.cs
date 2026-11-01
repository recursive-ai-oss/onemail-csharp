using System.Runtime.CompilerServices;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Olm;

/// <summary>
/// An Outlook for Mac archive (<c>.olm</c>): a zip of XML records. Mail, appointments, contacts, tasks, notes and groups each live in XML
/// entries, one record per element, and attachments are separate entries named by the record. This class reads the zip through OneArchive's
/// reader, lists the folders, and streams the records one at a time, so enumerating a large archive holds one record at a time plus the
/// zip directory (OneArchive keeps it in memory, about 1 KB per entry, so the working set grows with the entry count) and a table of the attachment entries.
/// </summary>
/// <remarks>
/// <para>Content decides what a file is: the sniffer looks for the archive's own folders in the zip directory, never at the extension.</para>
/// <para>Reading never throws on malformed content. Damage becomes an <see cref="OlmDefect"/>, on the archive (<see cref="Defects"/>) or on the item.</para>
/// <para>Not thread-safe: items and their attachment streams share the archive's one reader. Items and attachments are valid until the archive is disposed.</para>
/// </remarks>
public sealed class OlmArchive : IDisposable, IAsyncDisposable
{
    internal const string AttachmentsFolder = "com.microsoft.__Attachments";
    internal const string MessagesRoot = "com.microsoft.__Messages";

    private readonly IRandomAccessSource _source;
    private readonly bool _ownsSource;
    private readonly OlmReaderOptions _options;
    private readonly ZipReader _index;
    private readonly Dictionary<string, ArchiveEntry> _attachments = new(StringComparer.Ordinal);
    private readonly List<ArchiveEntry> _records = [];
    private readonly SortedSet<string> _folders = new(StringComparer.Ordinal);
    private readonly List<OlmDefect> _defects = [];
    private readonly List<OlmDamage> _damage = [];
    private readonly Dictionary<string, List<OlmDamage>> _entryDamage = new(StringComparer.Ordinal);
    private readonly List<long> _headerOffsets = [];
    private readonly Dictionary<string, bool> _verified = new(StringComparer.Ordinal);
    private int _damageSeen;
    private bool _disposed;

    private OlmArchive(IRandomAccessSource source, bool ownsSource, string? location, OlmReaderOptions options, ZipReader index)
    {
        _source = source;
        _ownsSource = ownsSource;
        Location = location;
        _options = options;
        _index = index;
    }

    /// <summary>The path the archive was opened from, or null.</summary>
    public string? Location { get; }

    /// <summary>Defects of the archive as a whole and of the entries read so far (a damaged XML entry, a refused zip). Entries are read as the items are enumerated, so this grows during enumeration.</summary>
    public IReadOnlyList<OlmDefect> Defects => _defects;

    /// <summary>
    /// Recovery mode only (<see cref="OlmReaderOptions.Recovery"/>): where the zip was skipped over or found damaged, in the order found, with offsets in the file. Grows while
    /// entries are read. Always empty when recovery is off and for an undamaged archive.
    /// </summary>
    public IReadOnlyList<OlmDamage> Damage
    {
        get
        {
            SyncDamage();
            return _damage;
        }
    }

    /// <summary>The number of attachment-like entries (every file that is not XML) found in the zip directory.</summary>
    public int AttachmentEntryCount => _attachments.Count;

    /// <summary>
    /// True when the source is a zip that holds an Outlook for Mac archive's folders: an entry under <c>Local/</c> in the message, address book, calendar,
    /// task or note tree. The extension is never consulted. Never throws.
    /// </summary>
    /// <param name="source">The bytes. Only the zip directory is read.</param>
    /// <returns>True for an OLM archive.</returns>
    public static bool IsOlm(IRandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length < 22 || !source.SupportsSyncRead)
        {
            return false;
        }

        Span<byte> sig = stackalloc byte[4];
        if (source.ReadFully(0, sig) < 4 || !sig.SequenceEqual("PK\x03\x04"u8))
        {
            return false;
        }

        try
        {
            using Stream s = source.OpenRead(0, source.Length);
            using var zip = new ZipReader(s, new ZipReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = int.MaxValue, MaxTotalSize = long.MaxValue }, MaxCentralDirectorySize = 4L << 30 }, leaveOpen: true);
            ArchiveEntry? e;
            while ((e = zip.GetNextEntry()) is not null)
            {
                if (OlmPaths.IsOlmMarker(e.Name))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (OlmPaths.IsReadFailure(ex))
        {
            return false;
        }

        return false;
    }

    /// <summary>Opens an archive file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>The archive. Dispose it to close the file.</returns>
    /// <exception cref="InvalidDataException">The file is not a zip, or its directory cannot be read at all. Damaged-archive recovery is not attempted.</exception>
    public static OlmArchive Open(string path, OlmReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        FileSource f = FileSource.Open(path);
        try
        {
            return Open(f, ownsSource: true, path, options);
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    /// <summary>Opens an archive from a source.</summary>
    /// <param name="source">A source that can be read synchronously (a file, a stream or memory). The reader needs to seek, so ranged sources that are async-only are refused.</param>
    /// <param name="ownsSource">True to dispose the source with the archive.</param>
    /// <param name="location">The path to report in provenance, or null.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>The archive.</returns>
    /// <exception cref="InvalidDataException">The source is not a readable zip, or cannot be read synchronously.</exception>
    public static OlmArchive Open(IRandomAccessSource source, bool ownsSource = false, string? location = null, OlmReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= OlmReaderOptions.Default;
        if (!source.SupportsSyncRead)
        {
            throw new InvalidDataException("An OLM archive needs a source that can be read synchronously.");
        }

        ZipReader zip;
        try
        {
            zip = NewZip(source, options);
        }
        catch (Exception ex) when (OlmPaths.IsReadFailure(ex))
        {
            throw new InvalidDataException("The OLM archive's zip directory cannot be read: " + ex.Message, ex);
        }

        var a = new OlmArchive(source, ownsSource, location, options, zip);
        if (!a.BuildIndex())
        {
            zip.Dispose();
            throw new InvalidDataException("The OLM archive's zip directory cannot be read: " + a._defects[0]);
        }

        if (options.Recovery)
        {
            a.SyncDamage();
            if (a._damage.Count > 0)
            {
                a.Report(OlmDefectKind.ArchiveRecovered, string.Empty, a._damage.Count + " damaged place(s)");
            }
        }

        return a;
    }

    /// <summary>The folders of the archive: the message folders (<see cref="OlmFolder.IsMail"/>) and the per-kind folders. In path order.</summary>
    /// <returns>The folders, root excluded.</returns>
    public IReadOnlyList<OlmFolder> GetFolders()
    {
        ThrowIfDisposed();
        var list = new List<OlmFolder>(_folders.Count);
        foreach (string f in _folders)
        {
            bool mail = f.StartsWith('M');
            string path = f[1..];
            int i = path.LastIndexOf('/');
            list.Add(new OlmFolder(path, i < 0 ? path : path[(i + 1)..], mail));
        }

        return list;
    }

    /// <summary>Enumerates every record of every XML entry in zip order: mail, appointments, contacts, tasks, notes, groups and categories.</summary>
    /// <returns>The items, each valid until the archive is disposed. One record is built at a time.</returns>
    public IEnumerable<OlmItem> Enumerate()
    {
        ThrowIfDisposed();
        int index = 0;
        foreach (ArchiveEntry entry in _records)
        {
            ThrowIfDisposed();
            if (_options.Recovery && !VerifyEntry(entry))
            {
                Report(OlmDefectKind.EntryDamaged, entry.Name, "records before the damage are kept");
            }

            // On failure OpenEntry has already put the defect on the archive.
            Stream? data = OpenEntry(entry, out _);
            if (data is null)
            {
                continue;
            }

            long offset = entry is ZipEntry ze ? ze.LocalHeaderOffset : -1;
            using Stream dataToDispose = data;
            OlmRecordReader reader;
            try
            {
                reader = new OlmRecordReader(data, _options, entry.Name, Report);
            }
            catch (Exception ex) when (OlmPaths.IsReadFailure(ex))
            {
                Report(OlmPaths.Classify(ex, OlmDefectKind.EntryUnreadable), entry.Name, ex.GetType().Name);
                continue;
            }

            using (reader)
            {
                bool rooted = TryRoot(reader, entry.Name);
                OlmItemKind? kind = rooted ? OlmPaths.KindOfRoot(reader.RootName!) : null;
                if (rooted && kind is null)
                {
                    Report(OlmDefectKind.UnrecognisedRoot, entry.Name, reader.RootName!);
                }

                if (kind is not { } k)
                {
                    continue;
                }

                string folder = OlmPaths.FolderOf(entry.Name, out bool mail);
                int inEntry = 0;
                while (TryNext(reader, entry.Name) is { } rec)
                {
                    yield return new OlmItem(this, k, rec, entry.Name, folder, mail, new MailProvenance(MailFormat.Olm, Location, folder.Length == 0 ? null : folder, offset, entry.Size, index++), inEntry++);
                }

                if (reader.DeclaredCount >= 0 && !reader.Damaged && reader.DeclaredCount != reader.Count)
                {
                    Report(OlmDefectKind.ElementCountMismatch, entry.Name, reader.DeclaredCount + " declared, " + reader.Count + " found");
                }
            }
        }
    }

    /// <summary>Enumerates the items without blocking the caller between records.</summary>
    /// <param name="cancellationToken">Cancels the enumeration between records.</param>
    /// <returns>The items.</returns>
    public async IAsyncEnumerable<OlmItem> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (OlmItem item in Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    /// <summary>Closes the archive and, when it owns it, the source.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _index.Dispose();
        if (_ownsSource)
        {
            _source.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal void Report(OlmDefectKind kind, string entry, string detail) => Report(new OlmDefect(kind, entry, detail));

    internal void Report(OlmDefect d) => _defects.Add(d);

    internal bool TryGetAttachmentEntry(string name, out ArchiveEntry entry) => _attachments.TryGetValue(name, out entry!);

    // Opens an entry's content (a record file or an attachment) from the one zip reader. Returns null, with a defect on the archive, when it cannot be opened.
    internal Stream? OpenEntry(ArchiveEntry entry, out OlmDefectKind? failure)
    {
        ThrowIfDisposed();
        failure = null;
        try
        {
            return _index.SupportsRandomAccess ? _index.OpenEntryStream(entry) : OpenForward(entry);
        }
        catch (Exception ex) when (OlmPaths.IsReadFailure(ex))
        {
            failure = OlmPaths.Classify(ex, OlmDefectKind.EntryUnreadable);
            Report(failure.Value, entry.Name, ex.GetType().Name);
            return null;
        }
    }

    // A zip read forward through its local headers cannot reopen an entry, so a fresh forward reader starts at the entry's own local header.
    // Its damage records are relative to that start and are moved to file offsets here.
    private ForwardEntryStream OpenForward(ArchiveEntry entry)
    {
        if (entry is not ZipEntry ze)
        {
            throw new InvalidOperationException("The entry did not come from the zip reader.");
        }

        long start = ze.LocalHeaderOffset;
        Stream raw = _source.OpenRead(start, _source.Length - start);
        ZipReader? zr = null;
        try
        {
            var zo = new ZipReaderOptions
            {
                Limits = new ArchiveLimits { MaxEntryCount = _options.MaxEntryCount, MaxTotalSize = _options.MaxTotalSize, MaxExpansionRatio = _options.MaxExpansionRatio },
                VerifyChecksum = _options.VerifyChecksums,
                ForceStreaming = true,
                Recovery = true,
            };
            zr = new ZipReader(raw, zo, leaveOpen: false);
            ArchiveEntry? first = zr.GetNextEntry();
            if (first is null || first.Name != entry.Name)
            {
                throw new InvalidDataException("The entry's local header is not where the scan found it.");
            }

            return new ForwardEntryStream(zr.OpenEntryStream(), zr, this, start);
        }
        catch
        {
            if (zr is not null)
            {
                zr.Dispose();
            }
            else
            {
                raw.Dispose();
            }

            throw;
        }
    }

    // Copies a forward reader's damage records, moved to file offsets, into the archive's list.
    internal void AddDamage(IEnumerable<ZipDamage> records, long shift)
    {
        foreach (ZipDamage z in records)
        {
            var d = new OlmDamage((OlmDamageKind)(int)z.Kind, z.Offset + shift, z.Length, z.EntryName, z.Reason);
            if (_damage.Contains(d))
            {
                continue;
            }

            _damage.Add(d);
            if (d.Entry is not null)
            {
                if (!_entryDamage.TryGetValue(d.Entry, out List<OlmDamage>? l))
                {
                    _entryDamage[d.Entry] = l = [];
                }

                l.Add(d);
            }
        }
    }

    private void SyncDamage()
    {
        if (!_options.Recovery)
        {
            return;
        }

        IReadOnlyList<ZipDamage> z = _index.Damage;
        if (z.Count > _damageSeen)
        {
            AddDamage(z.Skip(_damageSeen), 0);
            _damageSeen = z.Count;
        }
    }

    // Reads an entry to its end and throws the bytes away, so that damage, a short entry or a bad CRC is known before the records of the entry
    // are handed out. Cached by name. Returns true when the entry reads clean.
    internal bool VerifyEntry(ArchiveEntry entry)
    {
        if (_verified.TryGetValue(entry.Name, out bool known))
        {
            return known;
        }

        bool ok = true;
        Stream? s = null;
        try
        {
            s = OpenEntry(entry, out _);
            if (s is null)
            {
                ok = false;
            }
            else
            {
                byte[] buf = new byte[1 << 16];
                while (s.Read(buf, 0, buf.Length) > 0)
                {
                }
            }
        }
        catch (Exception ex) when (OlmPaths.IsReadFailure(ex))
        {
            ok = false;
        }
        finally
        {
            s?.Dispose();
        }

        SyncDamage();
        if (!ok && !_entryDamage.ContainsKey(entry.Name))
        {
            long at = entry is ZipEntry z ? z.LocalHeaderOffset : -1;
            AddDamage([new ZipDamage(ZipDamageKind.DamagedEntry, at, 0, entry.Name, "the entry could not be read to its end")], 0);
        }

        _verified[entry.Name] = ok && !_entryDamage.ContainsKey(entry.Name);
        return _verified[entry.Name];
    }

    /// <summary>The ranges and damage that decide a record's recovery tag; null when recovery is off.</summary>
    internal RecoveryProvenance? RecoveryOf(string recordEntry, IEnumerable<string> attachmentEntries, bool attachmentMissing)
    {
        if (!_options.Recovery)
        {
            return null;
        }

        var damage = new List<OlmDamage>();
        var ranges = new List<ByteRange>();
        var defects = new List<MailDefect>();
        bool partial = false;
        foreach (string name in new[] { recordEntry }.Concat(attachmentEntries))
        {
            if (!_recordIndex.TryGetValue(name, out ArchiveEntry? e) && !_attachments.TryGetValue(name, out e))
            {
                continue;
            }

            if (!VerifyEntry(e))
            {
                partial = true;
            }

            if (e is ZipEntry ze)
            {
                ranges.Add(EntryRange(ze));
            }

            if (_entryDamage.TryGetValue(name, out List<OlmDamage>? l))
            {
                damage.AddRange(l);
            }
        }

        if (attachmentMissing)
        {
            SyncDamage();
            foreach (OlmDamage d in _damage)
            {
                // Bytes skipped or a cut-off archive may have taken the attachment's entry with them.
                if (d.Kind is OlmDamageKind.SkippedBytes or OlmDamageKind.Truncated or OlmDamageKind.DirectoryUnusable)
                {
                    partial = true;
                    damage.Add(d);
                }
            }
        }

        return Build(partial, ranges, damage, defects);
    }

    private static RecoveryProvenance Build(bool partial, List<ByteRange> ranges, List<OlmDamage> damage, List<MailDefect> defects)
    {
        foreach (OlmDamage d in damage)
        {
            defects.Add(new MailDefect("olm-recovery", d.Kind.ToString(), d.Offset, false));
        }

        var lost = new List<ByteRange>();
        foreach (OlmDamage d in damage)
        {
            if (d.Length > 0)
            {
                lost.Add(new ByteRange(d.Offset, d.Length));
            }
        }

        return new RecoveryProvenance(partial ? RecoveryStatus.Partial : RecoveryStatus.Live)
        {
            Format = MailFormat.Olm,
            Ranges = RecoveryProvenance.Merge(ranges),
            Lost = lost,
            Defects = defects,
            Reason = partial ? "the zip entry holding the record is damaged or cut short" : "read from an intact zip entry",
        };
    }

    // The bytes an entry occupies: from its local header to the next entry's local header (or the end of the file).
    private ByteRange EntryRange(ZipEntry e)
    {
        if (_sortedOffsets is null)
        {
            _sortedOffsets = [.. _headerOffsets];
            Array.Sort(_sortedOffsets);
        }

        int i = Array.BinarySearch(_sortedOffsets, e.LocalHeaderOffset);
        long end = i >= 0 && i + 1 < _sortedOffsets.Length ? _sortedOffsets[i + 1] : _source.Length;
        return new ByteRange(e.LocalHeaderOffset, Math.Max(0, end - e.LocalHeaderOffset));
    }

    private long[]? _sortedOffsets;
    private readonly Dictionary<string, ArchiveEntry> _recordIndex = new(StringComparer.Ordinal);

    private const OlmDefectKind ArchiveDefect = OlmDefectKind.ArchiveUnreadable;

    private static ZipReader NewZip(IRandomAccessSource source, OlmReaderOptions o)
    {
        var limits = new ArchiveLimits
        {
            MaxEntryCount = o.MaxEntryCount,
            MaxTotalSize = o.MaxTotalSize,
            MaxExpansionRatio = o.MaxExpansionRatio,
        };
        // LazyDirectory reads the central directory as entries are asked for, so the zip reader's memory does not grow with the entry count
        // (mail exports hold hundreds of thousands of entries). The directory is still validated as a whole before the first entry is returned.
        // The limits are the large-archive profile with this reader's own entry and size bounds on top.
        // Recovery keeps the directory in memory: the damage checks need it, and a damaged archive is the rare case.
        var zo = new ZipReaderOptions { Limits = limits, VerifyChecksum = o.VerifyChecksums, MaxCentralDirectorySize = o.MaxCentralDirectorySize, LazyDirectory = !o.Recovery, Recovery = o.Recovery };
        Stream s = source.OpenRead(0, source.Length);
        try
        {
            return new ZipReader(s, zo, leaveOpen: false);
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    // One pass over the directory: remember the attachment entries (by name) and the folder names. Record entries are read later, one by one.
    // Returns false when the directory yielded nothing before it failed: then there is no archive to read.
    private bool BuildIndex()
    {
        int seen = 0;
        try
        {
            ArchiveEntry? e;
            while ((e = _index.GetNextEntry()) is not null)
            {
                seen++;
                string name = e.Name;
                if (_options.Recovery && e is ZipEntry zh)
                {
                    _headerOffsets.Add(zh.LocalHeaderOffset);
                }

                if (e.Kind == ArchiveEntryKind.Directory)
                {
                    AddFolder(name);
                }
                else if (e.Kind == ArchiveEntryKind.File)
                {
                    if (OlmPaths.IsRecordFile(name))
                    {
                        _records.Add(e);
                        if (_options.Recovery)
                        {
                            _recordIndex[name] = e;
                        }

                        AddFolder(name);
                    }
                    else
                    {
                        _attachments[name] = e;
                    }
                }
                else
                {
                    // A link or device node has no place in an archive of records; it is not read.
                    Report(OlmDefectKind.UnrecognisedEntry, name, e.Kind.ToString());
                }
            }
        }
        catch (Exception ex) when (OlmPaths.IsReadFailure(ex))
        {
            Report(OlmPaths.Classify(ex, OlmDefectKind.ArchiveUnreadable), string.Empty, ex.GetType().Name);
            return seen > 0;
        }

        return true;
    }

    // Folders are stored with a one-letter prefix ('M' mail, 'D' other) so one sorted set keeps both kinds; GetFolders strips it.
    private void AddFolder(string entryName)
    {
        string dir = entryName.EndsWith('/') ? entryName.TrimEnd('/') : (entryName.LastIndexOf('/') is var i and >= 0 ? entryName[..i] : string.Empty);
        if (dir.Length == 0 || OlmPaths.IsAttachmentPath(dir) || dir.StartsWith("Accounts", StringComparison.Ordinal))
        {
            return;
        }

        string rel = OlmPaths.RelativeFolder(dir, out bool mail);
        if (rel.Length == 0)
        {
            return;
        }

        // Every ancestor is a folder too.
        for (int pos = 0; pos <= rel.Length; pos++)
        {
            if (pos == rel.Length || rel[pos] == '/')
            {
                _folders.Add((mail ? "M" : "D") + rel[..pos]);
            }
        }
    }

    private bool TryRoot(OlmRecordReader reader, string entry)
    {
        try
        {
            return reader.ReadRoot();
        }
        catch (Exception ex) when (OlmPaths.IsReadFailure(ex))
        {
            Report(OlmPaths.Classify(ex, OlmDefectKind.EntryUnreadable), entry, ex.GetType().Name);
            return false;
        }
    }

    private OlmElement? TryNext(OlmRecordReader reader, string entry)
    {
        try
        {
            return reader.Next();
        }
        catch (Exception ex) when (OlmPaths.IsReadFailure(ex))
        {
            Report(OlmPaths.Classify(ex, OlmDefectKind.EntryUnreadable), entry, ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>Path rules of the OLM layout, shared by the archive and the sniffer.</summary>
internal static class OlmPaths
{
    public static bool IsAttachmentPath(string path) =>
        path.Contains("/" + OlmArchive.AttachmentsFolder, StringComparison.Ordinal) || path.StartsWith(OlmArchive.AttachmentsFolder, StringComparison.Ordinal);

    // A file the reader parses as a record file: XML that is not inside an attachments folder.
    public static bool IsRecordFile(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !IsAttachmentPath(name);

    // The archive's own folders. Any one of them under Local/ marks an Outlook for Mac archive.
    public static bool IsOlmMarker(string name) =>
        name.StartsWith("Local/com.microsoft.__Messages/", StringComparison.Ordinal)
        || name.StartsWith("Local/Address Book/", StringComparison.Ordinal)
        || name.StartsWith("Local/Calendar/", StringComparison.Ordinal)
        || name.StartsWith("Local/Tasks/", StringComparison.Ordinal)
        || name.StartsWith("Local/Notes/", StringComparison.Ordinal);

    public static OlmItemKind? KindOfRoot(string root) => root switch
    {
        "emails" => OlmItemKind.Email,
        "appointments" => OlmItemKind.Appointment,
        "contacts" => OlmItemKind.Contact,
        "tasks" => OlmItemKind.Task,
        "notes" => OlmItemKind.Note,
        "groups" => OlmItemKind.Group,
        "categories" => OlmItemKind.Category,
        _ => null,
    };

    // The folder an entry's records belong to: "Local/" and, for mail, "com.microsoft.__Messages/" are the archive's own roots.
    public static string FolderOf(string entryName, out bool mail)
    {
        int i = entryName.LastIndexOf('/');
        return RelativeFolder(i < 0 ? string.Empty : entryName[..i], out mail);
    }

    public static string RelativeFolder(string dir, out bool mail)
    {
        mail = false;
        string d = dir;
        if (d.StartsWith("Local/", StringComparison.Ordinal))
        {
            d = d[6..];
        }
        else if (d == "Local")
        {
            d = string.Empty;
        }

        if (d.StartsWith(OlmArchive.MessagesRoot + "/", StringComparison.Ordinal))
        {
            mail = true;
            d = d[(OlmArchive.MessagesRoot.Length + 1)..];
        }
        else if (d == OlmArchive.MessagesRoot)
        {
            mail = true;
            d = string.Empty;
        }

        return d;
    }

    // Everything a corrupt, hostile or unsupported zip can throw from OneArchive's reader or the streams under it.
    public static bool IsReadFailure(Exception ex) =>
        ex is System.Xml.XmlException or CompressionException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException or OverflowException or EndOfStreamException;

    public static OlmDefectKind Classify(Exception ex, OlmDefectKind fallback) =>
        ex is ArchiveSafetyException or CompressionLimitException ? OlmDefectKind.ArchiveLimitExceeded : fallback;
}

/// <summary>
/// An entry's stream from a forward-only zip reader started at the entry's local header. Disposing it hands the reader's damage records, moved to
/// file offsets, to the archive and then closes the reader.
/// </summary>
internal sealed class ForwardEntryStream(Stream inner, ZipReader reader, OlmArchive archive, long start) : Stream
{
    private bool _closed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            inner.Dispose();
            archive.AddDamage(reader.Damage, start);
            reader.Dispose();
        }

        base.Dispose(disposing);
    }
}
