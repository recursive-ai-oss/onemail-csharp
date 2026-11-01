using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Dbx;

/// <summary>
/// Reads one Outlook Express 5/6 database file (<c>.dbx</c>): the message folders (<c>Inbox.dbx</c> …) and <c>Folders.dbx</c>.
/// </summary>
/// <remarks>
/// <para>
/// The file is a header, a file-info block and a middle sector of objects that point at each other by absolute file offset.
/// Every object starts with its own offset (its "marker"). Message records and folder records are indexed-info objects reached
/// through a B-tree of 0x27c-byte nodes; a message's text is a singly linked chain of 0x210-byte segments.
/// (oedbx DBX format notes, OE_Dbx_FileStructure, OE_Dbx_Tree, OE_Dbx_IndexedInfo, OE_Dbx_Message.)
/// </para>
/// <para>
/// Nothing here throws on malformed content: a bad pointer, marker or length is a <see cref="StoreDefects"/> bit and the
/// object, branch or chain is skipped or cut there. Sizes are checked against the file before anything is allocated. Index
/// trees are walked with a stack no deeper than <see cref="DbxReaderOptions.MaxTreeDepth"/> and a node budget derived from the
/// file length, so a cycle ends the walk instead of hanging it; message chains use cycle detection that needs no memory
/// proportional to the chain. Enumeration holds one record at a time. Free (deleted) objects are not followed: that is the
/// forensic reader's job.
/// </para>
/// <para>The reader needs a source that reads synchronously. It does not own the source.</para>
/// </remarks>
public sealed partial class DbxReader
{
    // oedbx DBX format notes, OE_Dbx_FileHeader: the header is 0x24bc bytes; everything after it is the file info and the middle sector.
    internal const int HeaderLength = 0x24BC;

    // OE_Dbx_Tree: a node is 0x18 bytes of header and 0x264 of body; the body holds up to 0x33 entries of three int4.
    private const int TreeNodeSize = 0x27C;
    private const int TreeHeaderSize = 0x18;
    private const int TreeEntrySize = 12;
    private const int TreeMaxEntries = 0x33;

    // OE_Dbx_Message: marker, body length, text length, next pointer; then the text.
    private const int SegmentHeaderSize = 16;

    private readonly IRandomAccessSource _source;
    private readonly DbxReaderOptions _options;
    private byte[]? _header;
    private DbxFileKind _kind;
    private StoreDefects _defects;

    /// <summary>Creates a reader over a source. Nothing is read until the first member that needs the header.</summary>
    /// <param name="source">The file's bytes. Must support synchronous reads; the reader does not dispose it.</param>
    /// <param name="options">Limits and the defect handler, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="NotSupportedException">The source reads asynchronously only.</exception>
    public DbxReader(IRandomAccessSource source, DbxReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.SupportsSyncRead)
        {
            throw new NotSupportedException("The DBX reader needs a source that supports synchronous reads.");
        }

        _source = source;
        _options = options ?? new DbxReaderOptions();
    }

    /// <summary>The kind of file, from the class id in its header. <see cref="DbxFileKind.Unknown"/> for anything else.</summary>
    public DbxFileKind Kind
    {
        get
        {
            LoadHeader();
            return _kind;
        }
    }

    /// <summary>The defects found so far in the file's own structure (header, trees). Grows as enumeration proceeds.</summary>
    public StoreDefects Defects
    {
        get
        {
            LoadHeader();
            return _defects;
        }
    }

    internal IRandomAccessSource Source => _source;

    /// <summary>The length of the file.</summary>
    public long Length => _source.Length;

    /// <summary>The number of entries the header states for the main index tree (all messages, or all folders).</summary>
    public int DeclaredEntryCount
    {
        get
        {
            LoadHeader();
            return (int)U32(_header!, 0xC4);
        }
    }

    /// <summary>The file offset of the root of the main index tree (header position 0xe4), or 0 when the tree is empty.</summary>
    public long MainTreeRoot
    {
        get
        {
            LoadHeader();
            return U32(_header!, 0xE4);
        }
    }

    /// <summary>Recognises a database file from its first bytes.</summary>
    /// <param name="head">At least the first eight bytes of the file.</param>
    /// <returns>The kind, or <see cref="DbxFileKind.Unknown"/>.</returns>
    public static DbxFileKind Detect(ReadOnlySpan<byte> head)
    {
        // OE_Dbx_FileHeader: "cf ad 12 fe" for every database, then a class id naming the kind.
        if (head.Length < 8 || !head.StartsWith((ReadOnlySpan<byte>)[0xCF, 0xAD, 0x12, 0xFE]))
        {
            return DbxFileKind.Unknown;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) switch
        {
            0x6F74FDC5 => DbxFileKind.MessageDatabase,
            0x6F74FDC6 => DbxFileKind.FolderDatabase,
            0x6F74FDC7 => DbxFileKind.Pop3Uidl,
            0x26FE9D30 => DbxFileKind.Offline,
            _ => DbxFileKind.Unknown,
        };
    }

    /// <summary>Enumerates the messages of a message database in index order.</summary>
    /// <returns>One entry per message record. Records without text (<see cref="DbxMessageEntry.HasMessage"/> false) are included, flagged <see cref="StoreDefects.AmbiguousDbxNoMessageText"/>; their message is empty. Empty for other kinds of file.</returns>
    public IEnumerable<DbxMessageEntry> EnumerateMessages()
    {
        if (Kind != DbxFileKind.MessageDatabase)
        {
            yield break;
        }

        int position = 0;
        TreeWalker walker = new(this, MainTreeRoot);
        while (walker.Next(out long pointer))
        {
            DbxRecord? r = ReadRecord(pointer, folder: false, position);
            if (r is DbxMessageEntry m)
            {
                position++;
                yield return m;
            }
        }

        CheckCount(position);
    }

    /// <summary>Asynchronous twin of <see cref="EnumerateMessages"/>. The source is read synchronously; the token is checked between entries.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The entries.</returns>
    public async IAsyncEnumerable<DbxMessageEntry> EnumerateMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (DbxMessageEntry e in EnumerateMessages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Enumerates the folders of <c>Folders.dbx</c> in index order.</summary>
    /// <returns>One entry per folder record. Empty for other kinds of file.</returns>
    public IEnumerable<DbxFolderEntry> EnumerateFolders()
    {
        if (Kind != DbxFileKind.FolderDatabase)
        {
            yield break;
        }

        int position = 0;
        TreeWalker walker = new(this, MainTreeRoot);
        while (walker.Next(out long pointer))
        {
            DbxRecord? r = ReadRecord(pointer, folder: true, position);
            if (r is DbxFolderEntry f)
            {
                position++;
                yield return f;
            }
        }

        CheckCount(position);
    }

    /// <summary>Opens a message's text as a lazy, forward-only stream over its chain.</summary>
    /// <param name="entry">An entry of this file.</param>
    /// <returns>A stream; an entry without text gives an empty one. Dispose it when done. The source must outlive it.</returns>
    public DbxMessageStream OpenMessage(DbxMessageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new DbxMessageStream(this, entry.ChainOffset);
    }

    /// <summary>Walks a message's chain headers (about 3% of the message's bytes) to find its length and defects.</summary>
    /// <param name="entry">An entry of this file.</param>
    /// <returns>The chain's length, segment count and defects. When the record states a different length, that is <see cref="StoreDefects.AmbiguousDbxDeclaredLength"/> and the chain wins.</returns>
    public DbxChainInfo MeasureMessage(DbxMessageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ChainWalker w = new(this, entry.ChainOffset);
        while (w.Advance(out _, out _))
        {
        }

        StoreDefects d = w.Defects;
        if (entry.HasMessage && entry.DeclaredLength >= 0 && entry.DeclaredLength != w.Total)
        {
            d |= StoreDefects.AmbiguousDbxDeclaredLength;
            Report(StoreDefects.AmbiguousDbxDeclaredLength, entry.Offset);
        }

        return new DbxChainInfo(w.Total, w.Segments, d);
    }

    /// <summary>Reads a whole message into an array of exactly its length.</summary>
    /// <param name="entry">An entry of this file.</param>
    /// <param name="info">The chain's length, segment count and defects.</param>
    /// <returns>The message bytes exactly as stored (Outlook Express keeps CR LF line ends).</returns>
    /// <exception cref="InvalidDataException">The message is longer than an array can be.</exception>
    public byte[] ReadMessage(DbxMessageEntry entry, out DbxChainInfo info)
    {
        info = MeasureMessage(entry);
        if (info.Length > Array.MaxLength)
        {
            throw new InvalidDataException("The message is too large to hold in an array.");
        }

        byte[] buffer = new byte[info.Length];
        using DbxMessageStream s = OpenMessage(entry);
        int n = 0;
        int got;
        while (n < buffer.Length && (got = s.Read(buffer, n, buffer.Length - n)) > 0)
        {
            n += got;
        }

        return n == buffer.Length ? buffer : buffer.AsSpan(0, n).ToArray();
    }

    /// <summary>Reads a message from a file without parsing its header: only the chain is followed.</summary>
    /// <param name="source">The file the entry came from.</param>
    /// <param name="entry">An entry of that file.</param>
    /// <param name="options">Limits and the defect handler, or null.</param>
    /// <param name="info">The chain's length, segment count and defects.</param>
    /// <returns>The message bytes.</returns>
    public static byte[] ReadMessage(IRandomAccessSource source, DbxMessageEntry entry, DbxReaderOptions? options, out DbxChainInfo info) =>
        new DbxReader(source, options).ReadMessage(entry, out info);

    // ---- header ----

    private void LoadHeader()
    {
        if (_header is not null)
        {
            return;
        }

        byte[] h = new byte[HeaderLength];
        int n = _source.ReadFully(0, h);
        _header = h;
        _kind = Detect(h.AsSpan(0, n));
        if (_kind != DbxFileKind.Unknown && n < HeaderLength)
        {
            // Without the header's pointers nothing can be found.
            _kind = DbxFileKind.Unknown;
            Report(StoreDefects.DbxTruncatedHeader, n);
        }
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));

    private void Report(StoreDefects kind, long offset)
    {
        _defects |= kind;
        _options.DefectHandler?.Invoke(new StoreDefect(kind, offset));
    }

    private void CheckCount(int yielded)
    {
        // The header repeats the tree's entry count (position 0xc4). A tree walk that was cut short also differs; both report once.
        if (yielded != DeclaredEntryCount)
        {
            Report(StoreDefects.DbxEntryCountMismatch, 0xC4);
        }
    }

    // ---- records ----

    // OE_Dbx_IndexedInfo: 12 header bytes (marker, body length, object length, field count, change count), then the index, then data.
    private DbxRecord? ReadRecord(long pointer, bool folder, int position)
    {
        if (pointer < HeaderLength || pointer + DbxRecord.HeaderLength > _source.Length)
        {
            Report(StoreDefects.DbxBadPointer, pointer);
            return null;
        }

        Span<byte> head = stackalloc byte[DbxRecord.HeaderLength];
        if (_source.ReadFully(pointer, head) != head.Length)
        {
            Report(StoreDefects.DbxBadPointer, pointer);
            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != pointer)
        {
            Report(StoreDefects.DbxBadObjectMarker, pointer);
            return null;
        }

        long body = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        int count = head[10];
        StoreDefects d = StoreDefects.None;
        long avail = _source.Length - (pointer + DbxRecord.HeaderLength);
        if (body > _options.MaxInfoBytes)
        {
            Report(StoreDefects.DbxBadIndexedInfo, pointer);
            return null;
        }

        if (body > avail)
        {
            // A record cut off by the end of the file keeps what is there.
            d |= StoreDefects.DbxBadIndexedInfo;
            Report(StoreDefects.DbxBadIndexedInfo, pointer);
            body = avail;
        }

        if (body < 4 * count)
        {
            Report(StoreDefects.DbxBadIndexedInfo, pointer);
            return null;
        }

        byte[] obj = new byte[DbxRecord.HeaderLength + (int)body];
        head.CopyTo(obj);
        if (_source.ReadFully(pointer + DbxRecord.HeaderLength, obj.AsSpan(DbxRecord.HeaderLength)) != body)
        {
            Report(StoreDefects.DbxBadPointer, pointer);
            return null;
        }

        if (folder)
        {
            return new DbxFolderEntry(pointer, obj, d, position);
        }

        DbxMessageEntry entry = new(pointer, obj, d, position);
        if (!entry.HasMessage)
        {
            // IMAP and news headers can be listed without their text. undbx 0.21 writes such a record as an empty file.
            entry = new DbxMessageEntry(pointer, obj, d | StoreDefects.AmbiguousDbxNoMessageText, position);
            Report(StoreDefects.AmbiguousDbxNoMessageText, pointer);
        }

        return entry;
    }

    // ---- trees ----

    // In-order walk of a B-tree whose nodes hold child, (value, child) x n. Depth and the number of nodes visited are bounded,
    // so a cycle in a damaged file ends the walk. One buffer per level is reused.
    private sealed class TreeWalker
    {
        private readonly DbxReader _r;
        private readonly long[] _offset;
        private readonly int[] _step;
        private readonly int[] _count;
        private readonly byte[]?[] _buf;
        private readonly long _maxNodes;
        private long _visited;
        private int _depth;

        public TreeWalker(DbxReader r, long root)
        {
            _r = r;
            int max = Math.Max(1, r._options.MaxTreeDepth);
            _offset = new long[max];
            _step = new int[max];
            _count = new int[max];
            _buf = new byte[max][];
            _maxNodes = (r._source.Length / TreeNodeSize) + 1;
            if (root != 0)
            {
                Enter(root);
            }
        }

        public bool Next(out long value)
        {
            while (_depth > 0)
            {
                int d = _depth - 1;
                int s = _step[d]++;
                if (s > 2 * _count[d])
                {
                    _depth--;
                    continue;
                }

                byte[] b = _buf[d]!;
                if (s == 0)
                {
                    long child = U32(b, 8);
                    if (child != 0)
                    {
                        Enter(child);
                    }
                }
                else if ((s & 1) == 1)
                {
                    value = U32(b, TreeHeaderSize + (TreeEntrySize * ((s - 1) / 2)));
                    return true;
                }
                else
                {
                    long child = U32(b, TreeHeaderSize + (TreeEntrySize * ((s / 2) - 1)) + 4);
                    if (child != 0)
                    {
                        Enter(child);
                    }
                }
            }

            value = 0;
            return false;
        }

        private void Enter(long pointer)
        {
            if (_depth >= _offset.Length)
            {
                _r.Report(StoreDefects.DbxTreeLoop, pointer);
                return;
            }

            if (++_visited > _maxNodes)
            {
                _r.Report(StoreDefects.DbxTreeLoop, pointer);
                _depth = 0;
                return;
            }

            if (pointer < HeaderLength || pointer + TreeNodeSize > _r._source.Length)
            {
                _r.Report(StoreDefects.DbxBadTreeNode, pointer);
                return;
            }

            byte[] b = _buf[_depth] ??= new byte[TreeNodeSize];
            if (_r._source.ReadFully(pointer, b) != TreeNodeSize || U32(b, 0) != pointer)
            {
                _r.Report(StoreDefects.DbxBadTreeNode, pointer);
                return;
            }

            int n = b[17];
            if (n > TreeMaxEntries)
            {
                _r.Report(StoreDefects.DbxBadTreeNode, pointer);
                n = TreeMaxEntries;
            }

            _offset[_depth] = pointer;
            _step[_depth] = 0;
            _count[_depth] = n;
            _depth++;
        }
    }

    // ---- chains ----

    // Follows the segments of one message. A chain that points at a bad object ends there (DbxChainBroken); a chain that revisits
    // a segment, or holds more than the file could, ends with DbxChainLoop. Revisits are found with Brent's method, which
    // needs two variables instead of a set.
    internal struct ChainWalker
    {
        private readonly DbxReader _r;
        private long _next;
        private long _tortoise;
        private int _power;
        private int _lam;

        public ChainWalker(DbxReader r, long first)
        {
            _r = r;
            _next = first;
            _tortoise = first;
            _power = 1;
            _lam = 0;
            Total = 0;
            Segments = 0;
            Defects = StoreDefects.None;
        }

        public long Total { get; private set; }

        public int Segments { get; private set; }

        public StoreDefects Defects { get; private set; }

        public bool Advance(out long textOffset, out int textLength)
        {
            textOffset = 0;
            textLength = 0;
            long p = _next;
            if (p == 0)
            {
                return false;
            }

            long length = _r._source.Length;
            Span<byte> h = stackalloc byte[SegmentHeaderSize];
            if (p < HeaderLength || p + SegmentHeaderSize > length || _r._source.ReadFully(p, h) != SegmentHeaderSize
                || BinaryPrimitives.ReadUInt32LittleEndian(h) != p)
            {
                Fail(StoreDefects.DbxChainBroken, p);
                return false;
            }

            if (p == _tortoise && Segments > 0)
            {
                Fail(StoreDefects.DbxChainLoop, p);
                return false;
            }

            if (++_lam == _power)
            {
                _tortoise = p;
                _power <<= 1;
                _lam = 0;
            }

            long body = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            long text = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
            long next = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
            long avail = length - (p + SegmentHeaderSize);
            if (text > body || text > avail)
            {
                Mark(StoreDefects.DbxBadSegmentLength, p);
                text = Math.Min(Math.Min(text, body), avail);
            }

            long cap = Math.Min(_r._options.MaxMessageBytes, length);
            if (Total + text > cap)
            {
                Fail(StoreDefects.DbxChainLoop, p);
                text = cap - Total;
                _next = 0;
                if (text <= 0)
                {
                    return false;
                }
            }
            else
            {
                _next = next;
            }

            Total += text;
            Segments++;
            textOffset = p + SegmentHeaderSize;
            textLength = (int)text;
            return true;
        }

        private void Fail(StoreDefects kind, long offset)
        {
            Mark(kind, offset);
            _next = 0;
        }

        private void Mark(StoreDefects kind, long offset)
        {
            if ((Defects & kind) == 0)
            {
                _r.Report(kind, offset);
            }

            Defects |= kind;
        }
    }
}
