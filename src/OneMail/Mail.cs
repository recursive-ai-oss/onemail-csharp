using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Mbox;

namespace RecursiveAI.OneMail;

/// <summary>
/// The one-line entry points. Each method is a single call into the layer that does the work; options are that layer's own
/// options types, passed through unchanged.
/// </summary>
public static class Mail
{
    /// <summary>Parses one message held in memory.</summary>
    /// <param name="data">The message bytes; not copied.</param>
    /// <param name="options">The MIME reader's options (limits), or null for the defaults.</param>
    /// <returns>The item. Malformed input never throws; <see cref="IMailItem.Defects"/> reports what was repaired.</returns>
    public static IMailItem Parse(ReadOnlyMemory<byte> data, MimeReaderOptions? options = null) => MimeMailItem.Parse(data, options);

    /// <summary>Parses one message from a stream, reading it to the end.</summary>
    /// <param name="stream">A readable stream; left open.</param>
    /// <param name="options">The MIME reader's options, or null.</param>
    /// <returns>The item.</returns>
    public static IMailItem Parse(Stream stream, MimeReaderOptions? options = null) => MimeMailItem.Parse(stream, options);

    /// <summary>Opens a file or directory and decides what it is from its content: one message, an mbox, a Maildir or an Apple Mail bundle.</summary>
    /// <param name="path">The file or directory.</param>
    /// <param name="mime">The MIME reader's options, or null.</param>
    /// <param name="mbox">The mbox reader's options, or null.</param>
    /// <returns>A collection to enumerate with <c>await foreach</c> or <see cref="MailContainer.Enumerate"/>; dispose it when done.</returns>
    /// <exception cref="UnsupportedMailFormatException">Unrecognised content, or a format whose reader is not available yet.</exception>
    public static MailContainer Open(string path, MimeReaderOptions? mime = null, MboxReaderOptions? mbox = null) => MailContainer.Open(path, mime, mbox);

    /// <summary>Opens a seekable stream; see <see cref="Open(string, MimeReaderOptions?, MboxReaderOptions?)"/>. The stream stays open.</summary>
    /// <param name="stream">The bytes.</param>
    /// <param name="mime">The MIME reader's options, or null.</param>
    /// <param name="mbox">The mbox reader's options, or null.</param>
    /// <returns>The collection.</returns>
    public static MailContainer Open(Stream stream, MimeReaderOptions? mime = null, MboxReaderOptions? mbox = null) => MailContainer.Open(stream, mime, mbox);

    /// <summary>Opens a random-access source; see <see cref="Open(string, MimeReaderOptions?, MboxReaderOptions?)"/>. The source stays open.</summary>
    /// <param name="source">The bytes.</param>
    /// <param name="mime">The MIME reader's options, or null.</param>
    /// <param name="mbox">The mbox reader's options, or null.</param>
    /// <returns>The collection.</returns>
    public static MailContainer Open(IRandomAccessSource source, MimeReaderOptions? mime = null, MboxReaderOptions? mbox = null) => MailContainer.Open(source, mime, mbox);
}
