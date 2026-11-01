using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>
/// Writes converted items into a new Unicode PST: each item goes into the folder it came from under "Top of Personal Folders", with every
/// property it has. What the PST builder changed or left out is added to the item's loss report.
/// </summary>
internal sealed class PstSink : IStoreSink
{
    private const string TopFolder = "Top of Personal Folders";
    private readonly string _path;
    private readonly PstBuilder _builder;
    private readonly Dictionary<string, PstFolderWriter> _folders = new(StringComparer.Ordinal);
    private bool _completed;

    public PstSink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("The destination already exists: " + path);
        }

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        _path = path;
        _builder = PstBuilder.Create(path);
    }

    public ValueTask<string?> WriteAsync(StoreEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Message is null)
        {
            throw new InvalidOperationException("the entry has no MAPI message");
        }

        // A PST source names its folders from the root, so its top folder is the one the new store already has.
        string folder = entry.Folder;
        if (folder == TopFolder)
        {
            folder = string.Empty;
        }
        else if (folder.StartsWith(TopFolder + "/", StringComparison.Ordinal))
        {
            folder = folder[(TopFolder.Length + 1)..];
        }

        if (!_folders.TryGetValue(folder, out PstFolderWriter? target))
        {
            target = _builder.IpmSubtree.GetOrCreatePath(folder);
            _folders[folder] = target;
        }

        int before = _builder.Defects.Count;
        bool associated = entry.Item is MapiMailItem { Message: PstMessage { IsAssociated: true } };
        Nid nid = target.AddMessage(entry.Message, associated);
        for (int i = before; i < _builder.Defects.Count; i++)
        {
            PstWriteDefect d = _builder.Defects[i];
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

        return new ValueTask<string?>(_path + "#" + nid);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        _builder.Complete();
        _completed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _builder.Dispose();
        if (!_completed)
        {
            // A store that was not finished has an empty header and no use to anyone.
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }
}
