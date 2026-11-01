namespace RecursiveAI.OneMail.Stores.Olm;

/// <summary>
/// The decompressed bytes of one OLM entry, kept so that damage can be judged for the whole entry before its records are handed out without
/// decompressing the entry a second time. Small entries stay in memory; larger ones go to a temporary file that is deleted when the stream is
/// closed. If reading the entry failed part-way, the bytes read before the failure are replayed and the same exception is thrown after them, so a
/// reader sees what it would have seen reading the zip directly.
/// </summary>
internal sealed class OlmEntrySpool : Stream
{
    // Records are small (one message per entry is the norm), so this keeps the common case off the disk without letting one huge entry hold memory.
    private const int MemoryLimit = 8 << 20;

    private readonly Stream _store;
    private readonly Exception? _failure;

    private OlmEntrySpool(Stream store, Exception? failure)
    {
        _store = store;
        _failure = failure;
    }

    /// <summary>Copies the entry to the end or to the failure. Failures that <paramref name="isReadFailure"/> accepts are kept for replay; others propagate.</summary>
    /// <param name="source">The entry's decompressed stream.</param>
    /// <param name="isReadFailure">Which exceptions are damage in the entry rather than bugs.</param>
    /// <param name="failure">The damage met, or null when the entry read clean.</param>
    public static OlmEntrySpool Fill(Stream source, Func<Exception, bool> isReadFailure, out Exception? failure)
    {
        failure = null;
        var buffer = new byte[1 << 16];
        var holder = new List<Exception>(1);
        Stream? store = new MemoryStream();
        try
        {
            try
            {
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (store is MemoryStream && store.Length + n > MemoryLimit)
                    {
                        var file = new FileStream(Path.Combine(Path.GetTempPath(), "onemail-olm-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite,
                            FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
                        store.Position = 0;
                        store.CopyTo(file);
                        store.Dispose();
                        store = file;
                    }

                    store.Write(buffer, 0, n);
                }
            }
            catch (Exception ex) when (isReadFailure(ex))
            {
                holder.Add(ex);
            }

            store.Position = 0;
            failure = holder.Count > 0 ? holder[0] : null;
            var spool = new OlmEntrySpool(store, failure);
            store = null;
            return spool;
        }
        finally
        {
            store?.Dispose();
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = _store.Read(buffer);
        if (n == 0 && buffer.Length > 0 && _failure is not null)
        {
            // The entry was cut off here when it was copied; say so the way the zip reader did.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
        }

        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _store.Dispose();
        }

        base.Dispose(disposing);
    }
}
