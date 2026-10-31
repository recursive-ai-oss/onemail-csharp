using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneCompress;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Msg;

/// <summary>
/// An open Outlook item file: an MSG message or an OFT template (MS-OXMSG, over the compound file container). Opening reads the
/// container's directory, the top-level property stream and the named property map; value streams, recipients, attachments and
/// embedded messages are read when they are asked for.
/// </summary>
/// <remarks>
/// <para>
/// MSG and OFT files have the same structure, so the library does not tell them apart by content; a template is a message that
/// has not been sent (its <c>MSGFLAG_UNSENT</c> bit in PidTagMessageFlags is set), and callers that care look at that or at the extension.
/// </para>
/// <para>
/// Reading is lenient: damage in the file becomes <see cref="MapiDefect"/> values on <see cref="Defects"/> and on the objects, and the
/// parts that can be read are still returned. The only failure is a container that is not a compound file or whose structure is too
/// damaged for OneArchive's reader, which <c>TryOpen</c> reports as <c>false</c> with the defect.
/// </para>
/// <para>
/// A <see cref="MsgFile"/> and the objects it returns can be used from one thread at a time; their reads of the file are serialised
/// on an internal lock, so streams opened from property values may be read from other threads.
/// </para>
/// </remarks>
public sealed class MsgFile : IDisposable
{
    private static ReadOnlySpan<byte> CompoundFileSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly MsgContainer _container;

    private MsgFile(MsgContainer container, MsgMessage message, long length)
    {
        _container = container;
        Message = message;
        Length = length;
    }

    /// <summary>The message the file holds, with its properties read and its recipients and attachments still unread.</summary>
    public MsgMessage Message { get; }

    /// <summary>The length of the file in bytes.</summary>
    public long Length { get; }

    /// <summary>
    /// What was found wrong so far, in the order found. Parts of the file that have not been read yet (attachments, recipients, values)
    /// add theirs when they are read; <see cref="ReadEverything"/> reads them all.
    /// </summary>
    public IReadOnlyList<MapiDefect> Defects => _container.Defects;

    /// <summary>The number of defects found after <see cref="MsgReaderOptions.MaxDefects"/> was reached and therefore not kept.</summary>
    public int DroppedDefects => _container.Dropped;

    /// <summary>
    /// Whether PidTagStoreSupportMask of the top-level message says its strings are Unicode (MS-OXMSG 2.1.1.1): true for Unicode,
    /// false for 8-bit, null when the property is missing.
    /// </summary>
    public bool? HasUnicodeStrings => _container.UnicodeStrings;

    /// <summary>Whether the first bytes are the compound file signature. A cheap test that does not parse anything.</summary>
    /// <param name="header">At least the first 8 bytes of the file.</param>
    /// <returns>True when they match.</returns>
    public static bool HasCompoundFileSignature(ReadOnlySpan<byte> header) => header.StartsWith(CompoundFileSignature);

    /// <summary>
    /// Content sniffing: whether the source is a compound file whose root holds a <c>__properties_version1.0</c> stream, which is
    /// what makes it an Outlook item. The extension is never consulted. Reads the container's directory.
    /// </summary>
    /// <param name="source">The file's bytes.</param>
    /// <returns>True for an MSG or OFT.</returns>
    public static bool IsMsg(IRandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] sig = new byte[8];
        if (source.ReadFully(0, sig) < 8 || !HasCompoundFileSignature(sig))
        {
            return false;
        }

        try
        {
            using var reader = new CompoundFileReader(source.OpenRead(0, source.Length), null, leaveOpen: false);
            foreach (CfbEntry e in reader.GetChildren(reader.Root))
            {
                if (e.ObjectType == CfbObjectType.Stream && e.RawName.Equals(MsgObjectReader.PropertyStreamName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception e) when (e is CompressionException or IOException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Opens an MSG or OFT from a file on disk.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="file">The opened file, or null when <paramref name="defects"/> says it could not be read.</param>
    /// <param name="defects">Why it could not be opened; empty on success (later defects are on <see cref="Defects"/>).</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>True when a message was found.</returns>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    public static bool TryOpen(string path, [NotNullWhen(true)] out MsgFile? file, out IReadOnlyList<MapiDefect> defects, MsgReaderOptions? options = null) =>
        TryOpen(FileSource.Open(path), leaveOpen: false, out file, out defects, options);

    /// <summary>Opens an MSG or OFT held in memory. The bytes must not change while the file is in use.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="file">The opened file, or null.</param>
    /// <param name="defects">Why it could not be opened; empty on success.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>True when a message was found.</returns>
    public static bool TryOpen(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out MsgFile? file, out IReadOnlyList<MapiDefect> defects, MsgReaderOptions? options = null) =>
        Open(() => new CompoundFileReader(data), data.Length, null, out file, out defects, options);

    /// <summary>Opens an MSG or OFT from a random-access source.</summary>
    /// <param name="source">The file's bytes.</param>
    /// <param name="leaveOpen">False to dispose the source with the file (also when opening fails).</param>
    /// <param name="file">The opened file, or null.</param>
    /// <param name="defects">Why it could not be opened; empty on success.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>True when a message was found.</returns>
    public static bool TryOpen(IRandomAccessSource source, bool leaveOpen, [NotNullWhen(true)] out MsgFile? file, out IReadOnlyList<MapiDefect> defects, MsgReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Action? dispose = leaveOpen ? null : source.Dispose;
        if (source.Length < 8)
        {
            file = null;
            defects = [new MapiDefect(MapiDefectKind.NotACompoundFile, string.Empty, 0, $"{source.Length} bytes is shorter than a compound file header")];
            dispose?.Invoke();
            return false;
        }

        byte[] sig = new byte[8];
        source.ReadFully(0, sig);
        if (!HasCompoundFileSignature(sig))
        {
            file = null;
            defects = [new MapiDefect(MapiDefectKind.NotACompoundFile, string.Empty, 0, "the file does not start with the compound file signature")];
            dispose?.Invoke();
            return false;
        }

        return Open(() => new CompoundFileReader(source.OpenRead(0, source.Length), null, leaveOpen: false), source.Length, dispose, out file, out defects, options);
    }

    /// <summary>Opens an MSG or OFT from a seekable stream.</summary>
    /// <param name="stream">A readable, seekable stream positioned at the start of the file.</param>
    /// <param name="leaveOpen">True to leave the stream open when the file is disposed.</param>
    /// <param name="file">The opened file, or null.</param>
    /// <param name="defects">Why it could not be opened; empty on success.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>True when a message was found.</returns>
    public static bool TryOpen(Stream stream, bool leaveOpen, [NotNullWhen(true)] out MsgFile? file, out IReadOnlyList<MapiDefect> defects, MsgReaderOptions? options = null) =>
        TryOpen(new StreamSource(stream, leaveOpen), leaveOpen: false, out file, out defects, options);

    private static bool Open(Func<CompoundFileReader> create, long length, Action? onDispose, [NotNullWhen(true)] out MsgFile? file, out IReadOnlyList<MapiDefect> defects, MsgReaderOptions? options)
    {
        options ??= new MsgReaderOptions();
        CompoundFileReader? reader = null;
        try
        {
            reader = create();
            CfbEntry root = reader.Root;
            var container = new MsgContainer(reader, options, onDispose);
            IReadOnlyList<CfbEntry> top = container.Children(root);
            bool isItem = false;
            CfbEntry? nameId = null;
            foreach (CfbEntry e in top)
            {
                string n = e.RawName;
                if (n.Equals(MsgObjectReader.NameIdStorageName, StringComparison.OrdinalIgnoreCase) && e.ObjectType != CfbObjectType.Stream)
                {
                    nameId = e;
                    isItem = true;
                }
                else if (n.Equals(MsgObjectReader.PropertyStreamName, StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith(MsgObjectReader.RecipientPrefix, StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith(MsgObjectReader.AttachmentPrefix, StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith(MsgObjectReader.ValuePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    isItem = true;
                }
            }

            if (!isItem)
            {
                file = null;
                defects = [new MapiDefect(MapiDefectKind.NotAMessage, string.Empty, 0, "the compound file has no message property stream, recipient, attachment or named property storage in its root")];
                container.Dispose();
                return false;
            }

            // The map is read before the message so that named properties get their names as they are read.
            // Its defects belong to the file, not to an object.
            if (nameId is not null)
            {
                container.NameMap = MsgNameMap.Read(container, nameId, "/" + MsgObjectReader.NameIdStorageName, null);
            }

            var message = new MsgMessage(container, null, root, string.Empty, MsgObjectKind.Message, 0);
            file = new MsgFile(container, message, length);
            defects = [];
            return true;
        }
        catch (Exception e) when (e is CompressionException or IOException or InvalidDataException)
        {
            reader?.Dispose();
            onDispose?.Invoke();
            file = null;
            defects = [new MapiDefect(MapiDefectKind.CompoundFileInvalid, string.Empty, 0, e.Message)];
            return false;
        }
    }

    /// <summary>
    /// Reads every recipient, attachment and embedded message (not the value streams), so that <see cref="Defects"/> is complete
    /// for the structure of the file.
    /// </summary>
    /// <returns>All defects found.</returns>
    public IReadOnlyList<MapiDefect> ReadEverything()
    {
        Visit(Message);
        return Defects;

        static void Visit(MapiMessage m)
        {
            _ = m.Recipients;
            foreach (MapiAttachment a in m.Attachments)
            {
                if (a.EmbeddedMessage is { } inner)
                {
                    Visit(inner);
                }
                else
                {
                    _ = a.ObjectStorage;
                }
            }
        }
    }

    /// <summary>Closes the compound file and the source it was opened from (unless it was opened with <c>leaveOpen</c>). Objects read from the file keep what they already loaded; their lazy values then read as empty.</summary>
    public void Dispose() => _container.Dispose();
}
