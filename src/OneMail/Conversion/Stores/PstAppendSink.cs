using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>
/// Adds converted items to a PST that already exists, in place: each item goes into the folder it came from under "Top of Personal Folders" (made if
/// it is not there), with every property it has. Nothing is committed until the conversion completes.
/// </summary>
internal sealed class PstAppendSink : IStoreSink
{
    private const string TopFolder = "Top of Personal Folders";
    private readonly PstWritableStore _store;
    private readonly Nid _top;
    private readonly Dictionary<string, Nid> _folders = new(StringComparer.Ordinal);

    public PstAppendSink(string path)
    {
        _store = PstStore.OpenForWriting(path);
        try
        {
            PstFolder? top = _store.View.EnumerateFolders().FirstOrDefault(static f => f.Name == TopFolder && f.ParentFolder is not null);
            if (top is null)
            {
                throw new InvalidOperationException("the PST has no \"" + TopFolder + "\" folder to add to");
            }

            _top = top.Nid;
        }
        catch
        {
            _store.Dispose();
            throw;
        }
    }

    public ValueTask<string?> WriteAsync(StoreEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Message is null)
        {
            throw new InvalidOperationException("the entry has no MAPI message");
        }

        string folder = entry.Folder;
        if (folder == TopFolder)
        {
            folder = string.Empty;
        }
        else if (folder.StartsWith(TopFolder + "/", StringComparison.Ordinal))
        {
            folder = folder[(TopFolder.Length + 1)..];
        }

        Nid target = FolderFor(folder);
        int before = _store.Defects.Count;
        bool associated = entry.Item is MapiMailItem { Message: PstMessage { IsAssociated: true } };
        Nid nid = _store.AddMessage(target, entry.Message, associated);
        for (int i = before; i < _store.Defects.Count; i++)
        {
            PstWriteDefect d = _store.Defects[i];
            (LossKind kind, string code) = d.Kind switch
            {
                PstWriteDefectKind.NamedPropertyUnmapped => (LossKind.Dropped, "PstNamedPropertyUnmapped"),
                PstWriteDefectKind.NamedPropertyLimit => (LossKind.Dropped, "PstNamedPropertyLimit"),
                PstWriteDefectKind.DuplicateProperty => (LossKind.Dropped, "PstDuplicatePropertyDropped"),
                PstWriteDefectKind.UnsupportedType => (LossKind.Dropped, "PstUnsupportedType"),
                PstWriteDefectKind.ObjectStorageDropped => (LossKind.Dropped, "PstObjectStorageDropped"),
                PstWriteDefectKind.PropertyAdded => (LossKind.Internal, "PstPropertyAdded"),
                PstWriteDefectKind.EmbeddingTooDeep => (LossKind.Dropped, "PstEmbeddingTooDeep"),
                _ => (LossKind.Dropped, "PstTooManyChildren"),
            };
            entry.Report.Add("mapi", d.Detail, kind, code, d.Detail, d.Location);
        }

        if (entry.Item is not MapiMailItem)
        {
            StorePaths.ReportFlagsDropped(entry);
        }

        return new ValueTask<string?>("#" + nid);
    }

    private Nid FolderFor(string path)
    {
        if (path.Length == 0)
        {
            return _top;
        }

        if (_folders.TryGetValue(path, out Nid known))
        {
            return known;
        }

        Nid at = _top;
        string so = string.Empty;
        foreach (string name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            so = so.Length == 0 ? name : so + "/" + name;
            if (!_folders.TryGetValue(so, out Nid next))
            {
                // A folder made earlier in this run is not in the committed view yet, so those are remembered here.
                PstFolder? existing = _store.View.TryGetFolder(at, out PstFolder? parent) && parent is not null
                    ? parent.EnumerateSubfolders().FirstOrDefault(f => f.Name == name)
                    : null;
                next = existing?.Nid ?? _store.CreateFolder(at, name);
                _folders[so] = next;
            }

            at = next;
        }

        return at;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        _store.Commit();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }
}
