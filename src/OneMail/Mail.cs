using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.Stores;
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

    /// <summary>Converts a file, store or directory of files into another format, folder by folder, and says what did not carry over.</summary>
    /// <param name="source">What to read; see <see cref="StoreConverter.ConvertAsync(string, ConvertFormat, string, IProgress{StoreItemResult}?, CancellationToken)"/>.</param>
    /// <param name="format">What to write.</param>
    /// <param name="destination">Where to write it (a directory; for mbox a file name with an extension means one file).</param>
    /// <param name="options">The conversion's options, or null for the defaults.</param>
    /// <param name="progress">Called with each item's result, or null.</param>
    /// <param name="cancellationToken">Cancels between items.</param>
    /// <returns>Counts per folder, skipped items by reason, and loss totals.</returns>
    public static Task<StoreConversionReport> ConvertAsync(string source, ConvertFormat format, string destination, StoreConversionOptions? options = null, IProgress<StoreItemResult>? progress = null, CancellationToken cancellationToken = default) =>
        new StoreConverter(options).ConvertAsync(source, format, destination, progress, cancellationToken);

    /// <summary>Converts one item into the bytes of a format.</summary>
    /// <param name="item">Any item the library reads.</param>
    /// <param name="format">The target form.</param>
    /// <param name="options">The conversion's options, or null for the defaults.</param>
    /// <returns>The bytes (null when the item has no form in the target) and the loss report.</returns>
    public static ConversionResult<byte[]?> Convert(IMailItem item, ConvertFormat format, StoreConversionOptions? options = null) =>
        StoreConverter.ConvertItem(item, format, options);
}
