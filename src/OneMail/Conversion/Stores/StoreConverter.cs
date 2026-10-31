using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Forms;
using RecursiveAI.OneMail.Stores;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>
/// Reads any store, file or directory OneMail can read and writes it to another place, one item at a time. Folder structure is kept
/// where the destination has folders; flags, keywords and dates are mapped where both sides have them; calendar and contact items go
/// to <c>.ics</c> and <c>.vcf</c> files. Memory does not grow with the number of items: each item is read, converted, written and
/// forgotten, and the report keeps counts and loss totals, not items.
/// </summary>
/// <remarks>
/// A source that is a directory but not a store is walked, and each file in it is opened on its own; the folder of its items is
/// the directory (and, for a file holding many items, the file's name) relative to the source. An item that cannot be read or
/// converted is skipped with the reason <c>Failed</c> and an entry in <see cref="StoreConversionReport.Errors"/>; a failure of the
/// destination (disk full, permission) stops the conversion with the exception. Cancelling stops between items, finishes the
/// destination so that what was written is whole, and throws <see cref="OperationCanceledException"/>; <see cref="Report"/> then
/// holds the counts so far.
/// </remarks>
public sealed class StoreConverter
{
    private readonly StoreConversionOptions _options;

    /// <summary>Creates a converter.</summary>
    /// <param name="options">Options, or null for the defaults.</param>
    public StoreConverter(StoreConversionOptions? options = null)
    {
        _options = options ?? new StoreConversionOptions();
        Report = new StoreConversionReport(_options.MaxErrors, _options.MaxDistinctLosses);
    }

    /// <summary>The report of the conversion, updated after every item.</summary>
    public StoreConversionReport Report { get; }

    /// <summary>Converts a source to a destination in the given format.</summary>
    /// <param name="source">A file or directory in any format <see cref="MailContainer"/> reads, an iCalendar or vCard file (or its JSON or XML form), or a directory of such files.</param>
    /// <param name="format">What to write.</param>
    /// <param name="destination">The destination path; see <see cref="StoreSinks.Create"/>.</param>
    /// <param name="progress">Called with each item's result, or null.</param>
    /// <param name="cancellationToken">Cancels between items.</param>
    /// <returns>The aggregate report (also <see cref="Report"/>).</returns>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<StoreConversionReport> ConvertAsync(string source, ConvertFormat format, string destination, IProgress<StoreItemResult>? progress = null, CancellationToken cancellationToken = default)
    {
        await using IStoreSink sink = StoreSinks.Create(format, destination, _options);
        return await ConvertAsync(source, format, sink, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts a source into a sink the caller made (the extension point for destinations this layer does not write).</summary>
    /// <param name="source">As above.</param>
    /// <param name="format">The form the sink receives its content in.</param>
    /// <param name="sink">The destination; the caller disposes it.</param>
    /// <param name="progress">Called with each item's result, or null.</param>
    /// <param name="cancellationToken">Cancels between items.</param>
    /// <returns>The aggregate report.</returns>
    public async Task<StoreConversionReport> ConvertAsync(string source, ConvertFormat format, IStoreSink sink, IProgress<StoreItemResult>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(sink);
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException("No such file or directory.", source);
        }

        long index = 0;
        try
        {
            await foreach (SourceUnit u in Units(source, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!InFilter(u.Folder))
                {
                    continue;
                }

                StoreItemResult r = await ConvertUnitAsync(u, format, sink, index++, cancellationToken).ConfigureAwait(false);
                Report.Add(r);
                progress?.Report(r);
            }
        }
        finally
        {
            // What was written is made whole even when the run is cancelled or fails.
            await sink.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return Report;
    }

    /// <summary>Converts one item without a destination.</summary>
    /// <param name="item">Any item the library reads.</param>
    /// <param name="format">The target form.</param>
    /// <param name="options">Options, or null.</param>
    /// <returns>The bytes and the loss report; the bytes are null when the item has no form in the target (a contact for an iCalendar target) and the report is then empty.</returns>
    public static ConversionResult<byte[]?> ConvertItem(IMailItem item, ConvertFormat format, StoreConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        LossReport report = new();
        Payload? p = ItemTranscoder.Convert(item, format, options ?? new StoreConversionOptions(), report, out _);
        return new ConversionResult<byte[]?>(p?.Content, report);
    }

    private bool InFilter(string folder)
    {
        string? f = _options.FolderFilter?.Trim('/');
        return string.IsNullOrEmpty(f) || folder.Equals(f, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<StoreItemResult> ConvertUnitAsync(SourceUnit u, ConvertFormat format, IStoreSink sink, long index, CancellationToken ct)
    {
        LossReport report = new();
        Payload? payload;
        string reason = string.Empty;
        IMailItem? item = u.Item;
        string? subject = null;
        MessageMarks marks = MessageMarks.None;
        IReadOnlyList<string> keywords = [];
        DateTimeOffset? received = null;
        DateTimeOffset? sent = null;
        string? sender = null;
        try
        {
            if (item is not null)
            {
                subject = item.Subject;
                payload = ItemTranscoder.Convert(item, format, _options, report, out reason);
                if (payload is { Kind: PayloadKind.Message })
                {
                    (marks, keywords) = ItemFlags.Of(item, payload.Content);
                    received = ItemFlags.Received(item);
                    sent = item.Sent?.Value;
                    sender = item.From.Count > 0 ? item.From[0].Address : null;
                }
                else if (payload is not null)
                {
                    received = ItemFlags.Received(item);
                }
            }
            else
            {
                subject = u.Document!.Name;
                payload = u.Document.Convert(format, _options, report, out reason);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report.AddError($"item {index} in '{u.Folder}': {e.GetType().Name}: {e.Message}");
            return new StoreItemResult(index, u.Folder, subject, ItemOutcome.Skipped, "Failed", report, null);
        }

        if (payload is null)
        {
            return new StoreItemResult(index, u.Folder, subject, ItemOutcome.Skipped, reason, report, null);
        }

        var entry = new StoreEntry
        {
            Item = item,
            Kind = payload.Kind,
            Folder = u.Folder,
            Content = payload.Content,
            Extension = payload.Extension,
            Marks = marks,
            Keywords = keywords,
            Received = received,
            Sent = sent,
            Subject = subject,
            Sender = sender,
            Report = report,
        };
        string? where = await sink.WriteAsync(entry, ct).ConfigureAwait(false);
        return new StoreItemResult(index, u.Folder, subject, ItemOutcome.Written, string.Empty, report, where);
    }

    // ---- the source ----

    private async IAsyncEnumerable<SourceUnit> Units(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (SourceUnit u in Walk(path, string.Empty, top: true, ct).ConfigureAwait(false))
        {
            yield return u;
        }
    }

    private async IAsyncEnumerable<SourceUnit> Walk(string path, string prefix, bool top, [EnumeratorCancellation] CancellationToken ct)
    {
        if (Directory.Exists(path) && MailFormatSniffer.SniffDirectory(path) == MailFormat.Unknown)
        {
            // Not a store: a tree of loose files, visited in name order so that the output is the same on every run.
            string[] entries = Directory.GetFileSystemEntries(path);
            Array.Sort(entries, StringComparer.Ordinal);
            foreach (string e in entries)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(e);
                string sub = Directory.Exists(e) ? Join(prefix, name) : prefix;
                await foreach (SourceUnit u in Walk(e, sub, top: false, ct).ConfigureAwait(false))
                {
                    yield return u;
                }
            }

            yield break;
        }

        MailContainer? c = null;
        DocumentUnit? doc = null;
        try
        {
            c = Mail.Open(path, _options.Mime);
        }
        catch (UnsupportedMailFormatException e) when (DocumentUnit.IsDocument(e.Format))
        {
            doc = DocumentUnit.Read(path, e.Format);
        }
        catch (Exception e) when (e is UnsupportedMailFormatException or InvalidDataException or IOException)
        {
            Report.AddError($"'{path}': {e.Message}");
            Report.Add(new StoreItemResult(-1, prefix, null, ItemOutcome.Skipped, "Unreadable", new LossReport(), null));
        }

        if (doc is not null)
        {
            yield return new SourceUnit(null, doc, prefix);
            yield break;
        }

        if (c is null)
        {
            yield break;
        }

        using (c)
        {
            // A file holding many items (an mbox, a PST) is a folder of its own when it was found while walking a directory.
            string filePrefix = !top && c.IsStore && File.Exists(path) ? Join(prefix, Path.GetFileNameWithoutExtension(path)) : prefix;
            await foreach (IMailItem item in c.WithCancellation(ct).ConfigureAwait(false))
            {
                // Maildir++ names a subfolder ".Parent.Child": the dots are the levels.
                string own = item.Provenance.Folder ?? string.Empty;
                if (item.Provenance.Format is MailFormat.MaildirFolder or MailFormat.MaildirRoot)
                {
                    own = own.Replace('.', '/');
                }

                yield return new SourceUnit(item, null, Join(filePrefix, own));
            }
        }
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b;

    private readonly record struct SourceUnit(IMailItem? Item, DocumentUnit? Document, string Folder);

    // A calendar or card file (ICS, vCard, jCal, jCard, xCal, xCard): one unit per file.
    private sealed class DocumentUnit
    {
        private DocumentUnit(string name, MailFormat format, byte[] bytes, ContentDocument document)
        {
            Name = name;
            Format = format;
            Bytes = bytes;
            Document = document;
        }

        public string Name { get; }

        private MailFormat Format { get; }

        private byte[] Bytes { get; }

        private ContentDocument Document { get; }

        public static bool IsDocument(MailFormat f) => f is MailFormat.Ics or MailFormat.VCard or MailFormat.JCal or MailFormat.JCard or MailFormat.XCal or MailFormat.XCard;

        public static DocumentUnit Read(string path, MailFormat format)
        {
            FileInfo fi = new(path);
            if (fi.Length > 256L << 20)
            {
                throw new InvalidDataException("The calendar or card file is larger than 256 MiB.");
            }

            byte[] bytes = File.ReadAllBytes(path);
            ContentDocument doc = format switch
            {
                MailFormat.JCal or MailFormat.JCard => JsonForms.FromJson(bytes).Document,
                MailFormat.XCal or MailFormat.XCard => XmlForms.FromXml(bytes).Document,
                _ => ContentDocument.Parse(bytes),
            };
            return new DocumentUnit(Path.GetFileNameWithoutExtension(path), format, bytes, doc);
        }

        public Payload? Convert(ConvertFormat target, StoreConversionOptions options, LossReport report, out string reason)
        {
            reason = string.Empty;
            bool calendar = Format is MailFormat.Ics or MailFormat.JCal or MailFormat.XCal;
            ConvertFormat own = Format switch
            {
                MailFormat.Ics => ConvertFormat.Ics,
                MailFormat.VCard => ConvertFormat.VCard,
                MailFormat.JCal => ConvertFormat.JCal,
                MailFormat.JCard => ConvertFormat.JCard,
                MailFormat.XCal => ConvertFormat.XCal,
                _ => ConvertFormat.XCard,
            };
            ConvertFormat asIs = calendar ? ConvertFormat.Ics : ConvertFormat.VCard;
            bool toMail = target is ConvertFormat.Eml or ConvertFormat.Mbox or ConvertFormat.Maildir or ConvertFormat.AppleMail;
            if (toMail && options.OtherItems == OtherItemsMode.Skip)
            {
                reason = "OtherItemsSkipped";
                return null;
            }

            ConvertFormat to = toMail ? asIs : target;
            bool targetCalendar = to is ConvertFormat.Ics or ConvertFormat.JCal or ConvertFormat.XCal;
            bool targetCard = to is ConvertFormat.VCard or ConvertFormat.JCard or ConvertFormat.XCard;
            if (!targetCalendar && !targetCard)
            {
                reason = "NotMail";
                return null;
            }

            if (targetCalendar != calendar)
            {
                reason = calendar ? "NotContactItem" : "NotCalendarItem";
                return null;
            }

            // The same form as the source: the file's own bytes, so nothing changes.
            return to == own ? new Payload(calendar ? PayloadKind.Calendar : PayloadKind.Contact, Bytes, Extension(own)) : ItemTranscoder.Encode(Document, to, report, calendar);
        }

        private static string Extension(ConvertFormat f) => f switch
        {
            ConvertFormat.Ics => ".ics",
            ConvertFormat.VCard => ".vcf",
            ConvertFormat.JCal => ".jcal",
            ConvertFormat.JCard => ".jcard",
            ConvertFormat.XCal => ".xcal",
            _ => ".xcard",
        };
    }
}
