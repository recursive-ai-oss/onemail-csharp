using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Msg;

/// <summary>
/// Writes a <see cref="MapiMessage"/> as an Outlook item file (<c>.msg</c>, or <c>.oft</c> for a template) following MS-OXMSG on OneArchive's compound file
/// writer. It is the other half of <see cref="MsgFile"/>: whatever that reads from a file (properties, named properties, recipients, attachments,
/// embedded messages, OLE storages) this accepts, and a model written and read back has the same properties.
/// </summary>
/// <remarks>
/// <para>
/// The model is checked completely before the first byte is written, so a model that breaks a limit raises <see cref="MsgWriteException"/> and leaves the
/// output untouched. Things that can be repaired are repaired and listed in <see cref="MsgWriteResult.Defects"/> (a string with no characters is left out,
/// a short fixed-size value is padded ...). The rules are in <c>docs/msg.md</c>.
/// </para>
/// <para>
/// Values that come from a stream (<see cref="MapiStreamValue"/>, or the lazy values of an opened file) are copied from their source one at a time. The compound
/// file container needs every stream's size and holds stream data in memory until the file is finished, so a write needs memory for the data it writes, up to
/// <see cref="MsgWriterOptions.MaxTotalBytes"/>.
/// </para>
/// <para>The output is deterministic: the same model and options give the same bytes. No time is stored.</para>
/// </remarks>
public static class MsgWriter
{
    /// <summary>Checks a model without writing it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>Every finding up to the first error (an error is last), or the warnings a write would return. Empty when the model is written exactly as it is.</returns>
    public static IReadOnlyList<MsgWriteDefect> Check(MapiMessage message, MsgWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            return new MsgPlanner(options ?? new MsgWriterOptions()).Plan(message).Defects;
        }
        catch (MsgWriteException e)
        {
            return e.Defects;
        }
    }

    /// <summary>Writes the message to a stream.</summary>
    /// <param name="message">The message, with its recipients, attachments and embedded messages. Lazy values are read from their source.</param>
    /// <param name="output">A writable stream; left open. Nothing is written when the model fails its checks.</param>
    /// <param name="options">Options, or null for the defaults (Unicode strings, a message rather than a template).</param>
    /// <returns>What was written and every repair made.</returns>
    /// <exception cref="MsgWriteException">The model breaks a limit; nothing was written.</exception>
    /// <exception cref="IOException">The output cannot be written, or a value's source fails while it is copied.</exception>
    public static MsgWriteResult Write(MapiMessage message, Stream output, MsgWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= new MsgWriterOptions();
        MsgWritePlan plan = new MsgPlanner(options).Plan(message);
        long start = output.CanSeek ? output.Position : 0;
        using (var w = new CompoundFileWriter(output, WriterOptions(options), leaveOpen: true))
        {
            w.WriteEntry(RootEntry());
            foreach (MsgPart part in plan.Parts)
            {
                if (part.IsStorage)
                {
                    w.WriteEntry(StorageEntry(part));
                    continue;
                }

                using Stream content = Open(part);
                w.WriteEntry(StreamEntry(part), content);
            }

            w.Finish();
        }

        return Result(plan, output, start);
    }

    /// <summary>Writes the message to a stream without blocking the caller on the output.</summary>
    /// <param name="message">The message.</param>
    /// <param name="output">A writable stream; left open.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the write between streams. The output may then hold a partial file.</param>
    /// <returns>What was written and every repair made.</returns>
    /// <exception cref="MsgWriteException">The model breaks a limit; nothing was written.</exception>
    public static async ValueTask<MsgWriteResult> WriteAsync(MapiMessage message, Stream output, MsgWriterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= new MsgWriterOptions();
        MsgWritePlan plan = new MsgPlanner(options).Plan(message);
        long start = output.CanSeek ? output.Position : 0;
        var w = new CompoundFileWriter(output, WriterOptions(options), leaveOpen: true);
        await using (w.ConfigureAwait(false))
        {
            await w.WriteEntryAsync(RootEntry(), null, cancellationToken).ConfigureAwait(false);
            foreach (MsgPart part in plan.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (part.IsStorage)
                {
                    await w.WriteEntryAsync(StorageEntry(part), null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                Stream content = Open(part);
                await using (content.ConfigureAwait(false))
                {
                    await w.WriteEntryAsync(StreamEntry(part), content, cancellationToken).ConfigureAwait(false);
                }
            }

            await w.FinishAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result(plan, output, start);
    }

    /// <summary>Writes the message to a new file. An existing file is replaced; a file the write did not complete is deleted.</summary>
    /// <param name="message">The message.</param>
    /// <param name="path">The file path.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>What was written and every repair made.</returns>
    /// <exception cref="MsgWriteException">The model breaks a limit; no file is created.</exception>
    public static MsgWriteResult Write(MapiMessage message, string path, MsgWriterOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new MsgWriterOptions();
        // The checks run first so that a refused model creates no file.
        _ = new MsgPlanner(options).Plan(message);
        bool complete = false;
        try
        {
            using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            MsgWriteResult r = Write(message, f, options);
            complete = true;
            return r;
        }
        finally
        {
            if (!complete)
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Writes the message into memory.</summary>
    /// <param name="message">The message.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="MsgWriteException">The model breaks a limit.</exception>
    public static byte[] ToArray(MapiMessage message, MsgWriterOptions? options = null)
    {
        using var ms = new MemoryStream();
        Write(message, ms, options);
        return ms.ToArray();
    }

    private static CompoundFileWriterOptions WriterOptions(MsgWriterOptions o) =>
        new() { MaxBufferedBytes = Math.Min(long.MaxValue - (1L << 28), o.MaxTotalBytes + (1L << 28)) };

    private static CfbEntry RootEntry() => new() { Name = string.Empty, RawName = string.Empty, Kind = ArchiveEntryKind.Directory, ClassId = MsgPlanner.RootClassId };

    private static CfbEntry StorageEntry(MsgPart part) => new() { Name = part.Path, RawName = part.Path, Kind = ArchiveEntryKind.Directory, ClassId = part.ClassId };

    private static ArchiveEntry StreamEntry(MsgPart part) => new() { Name = part.Path, Kind = ArchiveEntryKind.File, Size = part.Size };

    private static Stream Open(MsgPart part) => part.Open is { } open ? open() : new MemoryReadStream(part.Data);

    private static MsgWriteResult Result(MsgWritePlan plan, Stream output, long start) =>
        new(plan.Defects, output.CanSeek ? output.Position - start : 0, plan.Properties, plan.Recipients, plan.Attachments, plan.NamedProperties);

    /// <summary>A read-only seekable stream over memory, so a value held in the model is not copied to be given to the compound file writer.</summary>
    private sealed class MemoryReadStream(ReadOnlyMemory<byte> data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)Math.Clamp(value, 0, data.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, data.Length - _position);
            data.Span.Slice(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => data.Length + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
