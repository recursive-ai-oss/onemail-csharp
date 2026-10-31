using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneCompress;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Msg;

/// <summary>
/// The open compound file behind an <see cref="MsgFile"/>: serialises access to OneArchive's reader (it keeps one stream position),
/// reads streams whole or piece by piece, and collects defects.
/// </summary>
internal sealed class MsgContainer : IDisposable
{
    private readonly CompoundFileReader _reader;
    private readonly object _gate = new();
    private readonly List<MapiDefect> _defects = [];
    private readonly Action? _onDispose;
    private bool _disposed;

    public MsgContainer(CompoundFileReader reader, MsgReaderOptions options, Action? onDispose)
    {
        _reader = reader;
        Options = options;
        _onDispose = onDispose;
    }

    public MsgReaderOptions Options { get; }

    public IReadOnlyList<MapiDefect> Defects => _defects;

    public int Dropped { get; private set; }

    /// <summary>Whether the root's PidTagStoreSupportMask says strings are Unicode; null until known or when there is none.</summary>
    public bool? UnicodeStrings { get; set; }

    public MsgNameMap NameMap { get; set; } = MsgNameMap.Empty;

    public CfbEntry Root
    {
        get
        {
            lock (_gate)
            {
                return _reader.Root;
            }
        }
    }

    /// <summary>Records a defect on the file and, when given, on the object it was found in.</summary>
    public void Report(MapiObject? owner, MapiDefectKind kind, string location, long offset, string detail)
    {
        if (_defects.Count >= Options.MaxDefects)
        {
            Dropped++;
            return;
        }

        var d = new MapiDefect(kind, location, offset, detail);
        lock (_defects)
        {
            _defects.Add(d);
        }

        owner?.AddDefect(d);
        if (_defects.Count == Options.MaxDefects)
        {
            var last = new MapiDefect(MapiDefectKind.DefectLimitReached, string.Empty, 0, $"more than {Options.MaxDefects} defects; the rest are only counted");
            _defects.Add(last);
        }
    }

    public IReadOnlyList<CfbEntry> Children(CfbEntry storage)
    {
        lock (_gate)
        {
            try
            {
                return _reader.GetChildren(storage);
            }
            catch (Exception e) when (e is CompressionException or IOException or InvalidDataException)
            {
                return [];
            }
        }
    }

    /// <summary>Reads a whole stream, or null (with a defect) when it is larger than the limit or cannot be read.</summary>
    public byte[]? ReadAll(CfbEntry stream, string location, long limit)
    {
        if (stream.Size > limit)
        {
            Report(null, MapiDefectKind.LimitReached, location, 0, $"stream of {stream.Size} bytes is larger than the {limit}-byte limit and was not read");
            return null;
        }

        lock (_gate)
        {
            try
            {
                var buffer = new byte[(int)stream.Size];
                using Stream s = _reader.OpenEntryStream(stream);
                s.ReadExactly(buffer);
                return buffer;
            }
            catch (Exception e) when (e is CompressionException or IOException or InvalidDataException)
            {
                Report(null, MapiDefectKind.CompoundFileInvalid, location, 0, "stream could not be read: " + e.Message);
                return null;
            }
        }
    }

    public Stream OpenLocked(CfbEntry stream, string location)
    {
        lock (_gate)
        {
            try
            {
                return new LockedStream(_reader.OpenEntryStream(stream), _gate, stream.Size);
            }
            catch (Exception e) when (e is CompressionException or IOException or InvalidDataException)
            {
                Report(null, MapiDefectKind.CompoundFileInvalid, location, 0, "stream could not be opened: " + e.Message);
                return new MemoryStream([], writable: false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _reader.Dispose();
        }

        _onDispose?.Invoke();
    }

    /// <summary>A read-only view of a compound file stream whose every call takes the container's lock.</summary>
    private sealed class LockedStream(Stream inner, object gate, long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get { lock (gate) { return inner.Position; } }
            set { lock (gate) { inner.Position = value; } }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            lock (gate)
            {
                return inner.Read(buffer);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (gate)
            {
                return inner.Seek(offset, origin);
            }
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
