using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Msg;

/// <summary>
/// A message read from an MSG or OFT file: the top-level message or an embedded one. Its recipients and attachments are read
/// from the file the first time they are asked for.
/// </summary>
public sealed class MsgMessage : MapiMessage, IMsgItem
{
    private readonly MsgContainer _container;
    private readonly string _path;
    private readonly MsgObjectReader.Result _children;
    private readonly int _depth;

    internal MsgMessage(MsgContainer container, MapiObject? parent, CfbEntry storage, string path, MsgObjectKind kind, int depth)
        : base(parent)
    {
        _container = container;
        _path = path;
        _depth = depth;
        _children = MsgObjectReader.Populate(container, this, storage, path, kind);
        Layout = _children.Layout;
    }

    /// <inheritdoc/>
    public MsgObjectLayout Layout { get; }

    /// <summary>How deep this message is: 0 for the file's message, 1 for a message attached to it, and so on.</summary>
    public int Depth => _depth;

    /// <inheritdoc/>
    protected override IReadOnlyList<MapiRecipient> LoadRecipients()
    {
        var list = new List<MapiRecipient>(Math.Min(_children.Recipients.Count, _container.Options.MaxRecipients));
        foreach ((int _, CfbEntry entry) in _children.Recipients)
        {
            if (list.Count >= _container.Options.MaxRecipients)
            {
                _container.Report(this, MapiDefectKind.LimitReached, _path, 0, $"more than {_container.Options.MaxRecipients} recipient storages; the rest are not read");
                break;
            }

            list.Add(new MsgRecipient(_container, this, entry, _path + "/" + entry.RawName));
        }

        return list;
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<MapiAttachment> LoadAttachments()
    {
        var list = new List<MapiAttachment>(Math.Min(_children.Attachments.Count, _container.Options.MaxAttachments));
        foreach ((int _, CfbEntry entry) in _children.Attachments)
        {
            if (list.Count >= _container.Options.MaxAttachments)
            {
                _container.Report(this, MapiDefectKind.LimitReached, _path, 0, $"more than {_container.Options.MaxAttachments} attachment storages; the rest are not read");
                break;
            }

            list.Add(new MsgAttachment(_container, this, entry, _path + "/" + entry.RawName, _depth));
        }

        return list;
    }
}

/// <summary>A recipient read from a <c>__recip_version1.0_#XXXXXXXX</c> storage.</summary>
public sealed class MsgRecipient : MapiRecipient, IMsgItem
{
    internal MsgRecipient(MsgContainer container, MapiObject parent, CfbEntry storage, string path)
        : base(parent)
    {
        Layout = MsgObjectReader.Populate(container, this, storage, path, MsgObjectKind.Recipient).Layout;
    }

    /// <inheritdoc/>
    public MsgObjectLayout Layout { get; }
}

/// <summary>An attachment read from an <c>__attach_version1.0_#XXXXXXXX</c> storage.</summary>
public sealed class MsgAttachment : MapiAttachment, IMsgItem
{
    private readonly MsgContainer _container;
    private readonly string _path;
    private readonly CfbEntry? _objectStorage;
    private readonly int _depth;
    private bool? _hasMessage;

    internal MsgAttachment(MsgContainer container, MapiObject parent, CfbEntry storage, string path, int parentDepth)
        : base(parent)
    {
        _container = container;
        _path = path;
        _depth = parentDepth + 1;
        MsgObjectReader.Result r = MsgObjectReader.Populate(container, this, storage, path, MsgObjectKind.Attachment);
        Layout = r.Layout;
        _objectStorage = r.ObjectStorage;
    }

    /// <inheritdoc/>
    public MsgObjectLayout Layout { get; }

    private bool HasMessage
    {
        get
        {
            // An object storage that has a property stream is a message, whatever the attach method says; an OLE storage has none.
            return _hasMessage ??= _objectStorage is { } s && HasPropertyStream(s);

            bool HasPropertyStream(CfbEntry storage)
            {
                foreach (CfbEntry k in _container.Children(storage))
                {
                    if (k.ObjectType == CfbObjectType.Stream && k.RawName.Equals(MsgObjectReader.PropertyStreamName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <inheritdoc/>
    protected override MapiMessage? LoadEmbeddedMessage()
    {
        if (_objectStorage is null)
        {
            return null;
        }

        if (!HasMessage)
        {
            if (AttachMethodValue == 5)
            {
                _container.Report(this, MapiDefectKind.EmbeddedObjectMissing, _path + "/" + MsgObjectReader.EmbeddedObjectName, 0, "the attachment method says an embedded message, but its storage has no property stream; it is kept as a raw storage");
            }

            return null;
        }

        if (_depth > _container.Options.MaxEmbeddingDepth)
        {
            _container.Report(this, MapiDefectKind.EmbeddingTooDeep, _path, 0, $"embedded messages are nested deeper than {_container.Options.MaxEmbeddingDepth}; the storage is kept raw");
            return null;
        }

        return new MsgMessage(_container, this, _objectStorage, _path + "/" + MsgObjectReader.EmbeddedObjectName, MsgObjectKind.EmbeddedMessage, _depth);
    }

    /// <inheritdoc/>
    protected override MapiRawStorage? LoadObjectStorage()
    {
        if (_objectStorage is null || (HasMessage && _depth <= _container.Options.MaxEmbeddingDepth))
        {
            return null;
        }

        return new MsgRawStorage(_container, _objectStorage, _path + "/" + MsgObjectReader.EmbeddedObjectName);
    }
}

/// <summary>A storage of an MSG file kept raw: the content of an OLE attachment.</summary>
internal sealed class MsgRawStorage : MapiRawStorage
{
    private readonly MsgContainer _container;
    private readonly CfbEntry _root;
    private readonly string _path;
    private readonly Dictionary<string, CfbEntry> _streams = new(StringComparer.OrdinalIgnoreCase);
    private List<MapiRawStorageEntry>? _entries;

    public MsgRawStorage(MsgContainer container, CfbEntry root, string path)
    {
        _container = container;
        _root = root;
        _path = path;
    }

    public override Guid ClassId => _root.ClassId;

    public override IReadOnlyList<MapiRawStorageEntry> Entries
    {
        get
        {
            if (_entries is null)
            {
                var list = new List<MapiRawStorageEntry>();
                var pending = new Stack<(CfbEntry Storage, string Prefix)>();
                pending.Push((_root, string.Empty));
                int budget = _container.Options.MaxProperties;
                while (pending.Count > 0 && list.Count < budget)
                {
                    (CfbEntry storage, string prefix) = pending.Pop();
                    foreach (CfbEntry k in _container.Children(storage))
                    {
                        string p = prefix + k.RawName;
                        if (k.ObjectType == CfbObjectType.Stream)
                        {
                            list.Add(new MapiRawStorageEntry(p, false, k.Size));
                            _streams[p] = k;
                        }
                        else
                        {
                            list.Add(new MapiRawStorageEntry(p, true, 0));
                            pending.Push((k, p + "/"));
                        }
                    }
                }

                _entries = list;
            }

            return _entries;
        }
    }

    public override Stream OpenStream(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _ = Entries;
        return _streams.TryGetValue(path, out CfbEntry? e)
            ? _container.OpenLocked(e, _path + "/" + path)
            : throw new FileNotFoundException("No stream at that path in the storage.", path);
    }
}
