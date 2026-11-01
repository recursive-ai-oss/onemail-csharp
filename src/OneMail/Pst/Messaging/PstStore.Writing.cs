using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Messaging;

public sealed partial class PstStore
{
    /// <summary>
    /// Opens an existing Unicode PST for changes, exclusively. The file's header checksums, allocation maps and density list are checked first, and a file
    /// that looks suspicious is refused unless <see cref="PstWriteOptions.Allow"/> accepts what was found. ANSI files, offline stores (OST) and files with
    /// 4 KiB pages are never opened for writing.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The writable store. Dispose it; changes need <see cref="PstWritableStore.Commit"/>.</returns>
    /// <exception cref="IOException">Another program has the file open, or it cannot be opened.</exception>
    /// <exception cref="NdbEditRefusedException">The file is refused; the exception lists every reason and which can be accepted.</exception>
    public static PstWritableStore OpenForWriting(string path, PstWriteOptions? options = null)
    {
        options ??= new PstWriteOptions();
        NdbEditor editor = NdbEditor.Open(path, ToEditorOptions(options));
        return Wrap(editor, options);
    }

    /// <summary>Opens a PST held in a readable, writable, seekable stream (memory, say) for changes, with the same checks as the path overload.</summary>
    /// <param name="stream">The stream; writes go straight to it.</param>
    /// <param name="leaveOpen">True to leave the stream open on dispose.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The writable store.</returns>
    /// <exception cref="NdbEditRefusedException">The file is refused.</exception>
    public static PstWritableStore OpenForWriting(Stream stream, bool leaveOpen, PstWriteOptions? options = null)
    {
        options ??= new PstWriteOptions();
        NdbEditor editor = NdbEditor.Open(stream, leaveOpen, ToEditorOptions(options));
        return Wrap(editor, options);
    }

    private static NdbEditorOptions ToEditorOptions(PstWriteOptions o) => new() { Allow = o.Allow, ZeroFreedSpace = o.ZeroFreedSpace, Ndb = o.Ndb };

    private static PstWritableStore Wrap(NdbEditor editor, PstWriteOptions options)
    {
        try
        {
            return new PstWritableStore(editor, options);
        }
        catch
        {
            editor.Dispose();
            throw;
        }
    }
}
