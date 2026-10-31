using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Emlx;
using RecursiveAI.OneMail.Stores.Maildir;
using RecursiveAI.OneMail.Stores.Mbox;

namespace RecursiveAI.OneMail;

/// <summary>
/// Thrown when a file or directory is not in a format that can be read through the shared view: either nothing recognised it
/// (<see cref="Format"/> is <see cref="MailFormat.Unknown"/>), or it was recognised and its reader is not part of the library yet.
/// The format is decided by content, never by extension.
/// </summary>
public sealed class UnsupportedMailFormatException : NotSupportedException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="format">What the sniffer found.</param>
    /// <param name="location">The path, or null.</param>
    public UnsupportedMailFormatException(MailFormat format, string? location)
        : base(format == MailFormat.Unknown
            ? "The content is not a recognised mail format" + (location is null ? "." : ": " + location)
            : "The content is " + format + ", which is recognised but cannot be read through the shared view yet" + (location is null ? "." : ": " + location))
    {
        Format = format;
        Location = location;
    }

    /// <summary>What the sniffer found; <see cref="MailFormat.Unknown"/> when nothing was recognised.</summary>
    public MailFormat Format { get; }

    /// <summary>The path that was opened, or null.</summary>
    public string? Location { get; }
}

/// <summary>
/// What <c>Mail.Open</c> returns: one container of items. A single message is a collection of one; an mbox, a Maildir or an Apple Mail
/// bundle is a collection of its messages. Enumerating reads the container forward and holds one message's worth of state, so a large
/// store enumerates in constant memory; an item parses its bytes only when first used and is valid until the collection is disposed.
/// </summary>
public sealed class MailContainer : IAsyncEnumerable<IMailItem>, IDisposable, IAsyncDisposable
{
    private readonly IRandomAccessSource? _source;
    private readonly bool _ownsSource;
    private readonly string? _directory;
    private readonly MimeReaderOptions? _mime;
    private readonly MboxReaderOptions? _mbox;
    private MboxReader? _messageReader;
    private bool _disposed;

    private MailContainer(MailFormat format, string? location, IRandomAccessSource? source, bool ownsSource, string? directory, MimeReaderOptions? mime, MboxReaderOptions? mbox)
    {
        Format = format;
        Location = location;
        _source = source;
        _ownsSource = ownsSource;
        _directory = directory;
        _mime = mime;
        _mbox = mbox;
    }

    /// <summary>The format found by sniffing the content.</summary>
    public MailFormat Format { get; }

    /// <summary>The path that was opened, or null when opened from memory or a stream.</summary>
    public string? Location { get; }

    /// <summary>True when the container holds many messages (everything but <see cref="MailFormat.Eml"/> and <see cref="MailFormat.Emlx"/>).</summary>
    public bool IsStore => Format is not (MailFormat.Eml or MailFormat.Emlx);

    /// <summary>Opens a file or a directory, deciding what it is from its content.</summary>
    /// <param name="path">A file, or a Maildir / Maildir++ / Apple Mail bundle directory.</param>
    /// <param name="mime">The MIME reader's own options (limits and so on), or null for the defaults.</param>
    /// <param name="mbox">The mbox reader's own options, or null. Used only for mbox files.</param>
    /// <returns>The collection. Dispose it to close the file.</returns>
    /// <exception cref="UnsupportedMailFormatException">The content is not recognised, or is recognised but not readable yet (PST, OST, MSG, TNEF, ICS, vCard, other compound files).</exception>
    /// <exception cref="FileNotFoundException">There is no file or directory at <paramref name="path"/>.</exception>
    public static MailContainer Open(string path, MimeReaderOptions? mime = null, MboxReaderOptions? mbox = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Directory.Exists(path))
        {
            MailFormat dir = MailFormatSniffer.SniffDirectory(path);
            if (dir is not (MailFormat.MaildirFolder or MailFormat.MaildirRoot or MailFormat.AppleMailBundle))
            {
                throw new UnsupportedMailFormatException(MailFormat.Unknown, path);
            }

            return new MailContainer(dir, path, null, false, path, mime, mbox);
        }

        FileSource source = FileSource.Open(path);
        try
        {
            return FromSource(source, path, true, mime, mbox);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Opens a stream. The stream must be seekable; it is left open when the collection is disposed.</summary>
    /// <param name="stream">The bytes.</param>
    /// <param name="mime">The MIME reader's own options, or null.</param>
    /// <param name="mbox">The mbox reader's own options, or null.</param>
    /// <returns>The collection.</returns>
    /// <exception cref="UnsupportedMailFormatException">As for the path overload.</exception>
    public static MailContainer Open(Stream stream, MimeReaderOptions? mime = null, MboxReaderOptions? mbox = null) =>
        FromSource(new StreamSource(stream, leaveOpen: true), null, true, mime, mbox);

    /// <summary>Opens a random-access source. The source is left open when the collection is disposed.</summary>
    /// <param name="source">The bytes.</param>
    /// <param name="mime">The MIME reader's own options, or null.</param>
    /// <param name="mbox">The mbox reader's own options, or null.</param>
    /// <returns>The collection.</returns>
    /// <exception cref="UnsupportedMailFormatException">As for the path overload.</exception>
    public static MailContainer Open(IRandomAccessSource source, MimeReaderOptions? mime = null, MboxReaderOptions? mbox = null) =>
        FromSource(source, null, false, mime, mbox);

    private static MailContainer FromSource(IRandomAccessSource source, string? path, bool owns, MimeReaderOptions? mime, MboxReaderOptions? mbox)
    {
        ArgumentNullException.ThrowIfNull(source);
        MailFormat f = MailFormatSniffer.Sniff(source);
        if (f is not (MailFormat.Eml or MailFormat.Mbox or MailFormat.Emlx))
        {
            throw new UnsupportedMailFormatException(f, path);
        }

        // A stream wrapper made by Open(Stream) is ours to drop; a caller's source is not.
        return new MailContainer(f, path, source, owns && (path is not null || source is StreamSource), null, mime, mbox);
    }

    /// <summary>Enumerates the items in the container's order.</summary>
    /// <returns>The items, each valid until the collection is disposed.</returns>
    public IEnumerable<IMailItem> Enumerate()
    {
        ThrowIfDisposed();
        switch (Format)
        {
            case MailFormat.Eml:
                yield return new MimeMailItem(Mime.MimeEntity.Load(_source!, _mime), new MailProvenance(MailFormat.Eml, Location, null, 0, _source!.Length, 0));
                break;
            case MailFormat.Emlx:
                yield return new EmlxMimeItem(this, _source, null, Location, 0);
                break;
            case MailFormat.Mbox:
                using (MboxReader r = new(_source!, _mbox))
                {
                    while (r.Read())
                    {
                        yield return new MboxItem(this, r.Current, null);
                    }
                }

                break;
            case MailFormat.MaildirFolder or MailFormat.MaildirRoot:
                MaildirStore store = new(new PhysicalStoreFileSystem(_directory!));
                int n = 0;
                foreach (string folder in store.ListFolders())
                {
                    foreach (MaildirEntry e in store.Enumerate(folder))
                    {
                        yield return new MaildirItem(this, store, e, n++);
                    }
                }

                break;
            case MailFormat.AppleMailBundle:
                PhysicalStoreFileSystem fs = new(_directory!);
                AppleMailBundle bundle = new(fs);
                int k = 0;
                foreach (Stores.Emlx.EmlxItem it in bundle.Enumerate())
                {
                    yield return new EmlxMimeItem(this, null, fs, it.Path, k++);
                }

                break;
        }
    }

    /// <summary>Enumerates the items without blocking on I/O.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The items, each valid until the collection is disposed.</returns>
    public async IAsyncEnumerator<IMailItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        switch (Format)
        {
            case MailFormat.Eml:
                yield return new MimeMailItem(await Mime.MimeEntity.LoadAsync(_source!, _mime, cancellationToken).ConfigureAwait(false),
                    new MailProvenance(MailFormat.Eml, Location, null, 0, _source!.Length, 0));
                break;
            case MailFormat.Mbox:
                using (MboxReader r = new(_source!, _mbox))
                {
                    while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        MboxEntry entry = r.Current;
                        byte[]? preload = _source!.SupportsSyncRead ? null : await MessageReader().ReadMessageAsync(entry, true, cancellationToken).ConfigureAwait(false);
                        yield return new MboxItem(this, entry, preload);
                    }
                }

                break;
            case MailFormat.Emlx or MailFormat.MaildirFolder or MailFormat.MaildirRoot or MailFormat.AppleMailBundle:
                // Directory stores and single files do their I/O one small file at a time; the sync enumeration is already cheap per item.
                foreach (IMailItem item in Enumerate())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                }

                break;
        }
    }

    /// <summary>Closes the file the collection opened. Items not yet read can no longer be read afterwards.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messageReader?.Dispose();
        if (_ownsSource)
        {
            _source?.Dispose();
        }
    }

    /// <inheritdoc cref="Dispose"/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal MboxReader MessageReader() => _messageReader ??= new MboxReader(_source!, _mbox);

    internal MimeReaderOptions? MimeOptions => _mime;

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // Reads straight into an exactly sized array. Unquoting can only shorten the message, so the array is cut when it did.
    private static byte[] ReadMessage(MboxReader reader, in MboxEntry entry)
    {
        if (entry.MessageLength > Array.MaxLength)
        {
            throw new InvalidDataException("The message is too large to hold in an array.");
        }

        byte[] buf = new byte[entry.MessageLength];
        using Stream s = reader.OpenMessage(entry);
        int n = 0;
        int got;
        while (n < buf.Length && (got = s.Read(buf, n, buf.Length - n)) > 0)
        {
            n += got;
        }

        return n == buf.Length ? buf : buf.AsSpan(0, n).ToArray();
    }

    private static byte[] ReadRange(IRandomAccessSource source, long offset, long length)
    {
        if (length > Array.MaxLength)
        {
            throw new InvalidDataException("The message is too large to hold in an array.");
        }

        byte[] b = new byte[length];
        int n = source.ReadFully(offset, b);
        return n == b.Length ? b : b.AsSpan(0, n).ToArray();
    }

    private static MailDate? StoreDate(DateTimeOffset? d) => d is { } v ? new MailDate(v, MailDateSource.Delivery, false) : null;

    // ---- items read from stores ----

    private sealed class MboxItem(MailContainer owner, MboxEntry entry, byte[]? preload)
        : MimeMailItem(owner._mime, new MailProvenance(MailFormat.Mbox, owner.Location, null, entry.MessageOffset, entry.MessageLength, entry.Index))
    {
        internal override ReadOnlyMemory<byte> LoadBytes()
        {
            owner.ThrowIfDisposed();
            return preload ?? ReadMessage(owner.MessageReader(), entry);
        }

        internal override MailDate? DeliveryDate()
        {
            owner.ThrowIfDisposed();
            MboxEnvelope e = owner.MessageReader().ReadEnvelope(entry);
            return StoreDate(e.Date);
        }

        internal override IEnumerable<MailDefect> StoreDefectsOf() => FromStoreDefects(entry.Defects);
    }

    private sealed class MaildirItem(MailContainer owner, MaildirStore store, MaildirEntry entry, int index)
        : MimeMailItem(owner._mime, new MailProvenance(owner.Format, entry.Path, entry.Folder.Length == 0 ? null : entry.Folder, 0, entry.Size, index))
    {
        internal override ReadOnlyMemory<byte> LoadBytes()
        {
            owner.ThrowIfDisposed();
            using IRandomAccessSource s = store.Open(entry);
            return ReadRange(s, 0, s.Length);
        }

        internal override MailDate? DeliveryDate() => StoreDate(entry.Modified);

        internal override IEnumerable<MailDefect> StoreDefectsOf() => FromStoreDefects(entry.Defects);
    }

    // A file holding one message. A bundle's files are opened for each read and closed again, so enumerating a bundle holds no handles.
    private sealed class EmlxMimeItem : MimeMailItem
    {
        private readonly MailContainer _owner;
        private readonly IRandomAccessSource? _shared;
        private readonly PhysicalStoreFileSystem? _fs;
        private readonly string? _path;
        private readonly EmlxEntry _entry;

        public EmlxMimeItem(MailContainer owner, IRandomAccessSource? shared, PhysicalStoreFileSystem? fs, string? path, int index)
            : this(owner, shared, fs, path, index, ReadEntry(shared, fs, path))
        {
        }

        private EmlxMimeItem(MailContainer owner, IRandomAccessSource? shared, PhysicalStoreFileSystem? fs, string? path, int index, EmlxEntry entry)
            : base(owner._mime, new MailProvenance(MailFormat.Emlx, path ?? owner.Location, null, entry.MessageOffset, entry.MessageLength, index))
        {
            _owner = owner;
            _shared = shared;
            _fs = fs;
            _path = path;
            _entry = entry;
        }

        internal override ReadOnlyMemory<byte> LoadBytes()
        {
            _owner.ThrowIfDisposed();
            return WithFile(f => ReadRange(f, _entry.MessageOffset, _entry.MessageLength));
        }

        internal override MailDate? DeliveryDate()
        {
            _owner.ThrowIfDisposed();
            return WithFile(f => StoreDate(EmlxReader.ReadMetadata(f, _entry).DateReceived));
        }

        internal override IEnumerable<MailDefect> StoreDefectsOf() => FromStoreDefects(_entry.Defects);

        private static EmlxEntry ReadEntry(IRandomAccessSource? shared, PhysicalStoreFileSystem? fs, string? path)
        {
            if (shared is not null)
            {
                return EmlxReader.Read(shared);
            }

            using IRandomAccessSource f = fs!.OpenRead(path!);
            return EmlxReader.Read(f);
        }

        private T WithFile<T>(Func<IRandomAccessSource, T> use)
        {
            if (_shared is not null)
            {
                return use(_shared);
            }

            using IRandomAccessSource f = _fs!.OpenRead(_path!);
            return use(f);
        }
    }
}
