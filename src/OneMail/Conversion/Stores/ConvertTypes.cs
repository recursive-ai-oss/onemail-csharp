using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Stores.Mbox;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>What a conversion writes. The mail stores take messages; the calendar and contact formats take the items of that family.</summary>
public enum ConvertFormat
{
    /// <summary>One <c>.eml</c> file per message (RFC 5322), in a directory tree that follows the source's folders.</summary>
    Eml = 0,

    /// <summary>mbox: one file per folder inside a directory, or a single file when the destination names one (folders are then flattened and the report says so).</summary>
    Mbox,

    /// <summary>A Maildir with Maildir++ subfolders; flags and keywords go into the file names.</summary>
    Maildir,

    /// <summary>An Apple Mail bundle tree: <c>&lt;folder&gt;.mbox/Messages/&lt;n&gt;.emlx</c>.</summary>
    AppleMail,

    /// <summary>One iCalendar (<c>.ics</c>) file per appointment or task.</summary>
    Ics,

    /// <summary>One vCard (<c>.vcf</c>) file per contact.</summary>
    VCard,

    /// <summary>jCal (RFC 7265, <c>.jcal</c>), one file per calendar item.</summary>
    JCal,

    /// <summary>jCard (RFC 7095, <c>.jcard</c>), one file per contact.</summary>
    JCard,

    /// <summary>xCal (RFC 6321, <c>.xcal</c>), one file per calendar item.</summary>
    XCal,

    /// <summary>xCard (RFC 6351, <c>.xcard</c>), one file per contact.</summary>
    XCard,

    /// <summary>One TNEF stream (<c>.tnef</c>, MS-OXTNEF) per message, carrying every property.</summary>
    Tnef,

    /// <summary>One Outlook item file (<c>.msg</c>, MS-OXMSG) per message, appointment, contact, task, note or distribution list; a MIME message is converted to an Outlook item first (MS-OXCMAIL).</summary>
    Msg,

    /// <summary>One Unicode PST file (MS-PST): the source's folders as PST folders, every item written with all its properties; a MIME message is converted to an Outlook item first (MS-OXCMAIL). The destination names the file, which must not exist.</summary>
    Pst,
}

/// <summary>What a mail-store target does with calendar and contact items.</summary>
public enum OtherItemsMode
{
    /// <summary>Appointments and tasks are written as <c>.ics</c> files and contacts as <c>.vcf</c> files in a side directory (<c>_items</c>) of the destination.</summary>
    Route = 0,

    /// <summary>They are skipped and counted in the report.</summary>
    Skip,
}

/// <summary>How the store conversion treats the items it reads and the stores it writes.</summary>
public sealed class StoreConversionOptions
{
    /// <summary>Calendar and contact items met on the way to a mail store (<c>Eml</c>, <c>Mbox</c>, <c>Maildir</c>, <c>AppleMail</c>). Default <see cref="OtherItemsMode.Route"/>.</summary>
    public OtherItemsMode OtherItems { get; init; } = OtherItemsMode.Route;

    /// <summary>The mbox variant written. Default mboxrd.</summary>
    public MboxVariant MboxVariant { get; init; } = MboxVariant.Mboxrd;

    /// <summary>Whether the mbox envelope lines and separators end in CR LF. Default false. Message bytes are never changed by this.</summary>
    public bool MboxCrlf { get; init; }

    /// <summary>How Outlook items become MIME (Bcc, transport headers, TNEF form); null for that converter's defaults.</summary>
    public OxcMailExportOptions? MapiToMime { get; init; }

    /// <summary>The MIME reader's limits for the sources, or null for the defaults.</summary>
    public MimeReaderOptions? Mime { get; init; }

    /// <summary>Only the folders at or below this path (compared case-insensitively, <c>/</c> separated); null or empty for all.</summary>
    public string? FolderFilter { get; init; }

    /// <summary>The longest list of reasons kept in <see cref="StoreConversionReport.Errors"/>. Default 100.</summary>
    public int MaxErrors { get; init; } = 100;

    /// <summary>The most distinct loss codes kept in the totals before the rest are folded into one <c>(other)</c> row. Default 10,000.</summary>
    public int MaxDistinctLosses { get; init; } = 10_000;

    /// <summary>
    /// For <see cref="ConvertFormat.Pst"/>: add the items to the existing Unicode PST at the destination, in place, instead of making a new file. The
    /// file is opened exclusively and refused if it looks suspicious (see <see cref="Pst.Ndb.NdbOpenRisks"/>); the changes are committed once, when the
    /// conversion completes, and dropped if it fails. Items go in the folder of the same path under "Top of Personal Folders", created as needed.
    /// </summary>
    public bool AddToExistingPst { get; init; }

}

/// <summary>What happened to one item.</summary>
public enum ItemOutcome
{
    /// <summary>The item was converted and written.</summary>
    Written = 0,

    /// <summary>The item was not written; <see cref="StoreItemResult.Reason"/> says why.</summary>
    Skipped,
}

/// <summary>One item's result, reported to the progress callback as it happens.</summary>
/// <param name="Index">Position of the item in the source, from 0.</param>
/// <param name="Folder">The source folder, <c>/</c> separated; empty for the top.</param>
/// <param name="Subject">The subject, or null.</param>
/// <param name="Outcome">Written or skipped.</param>
/// <param name="Reason">For a skipped item a stable code (<c>NotCalendarItem</c>, <c>NotContactItem</c>, <c>OtherItemsSkipped</c>, <c>Unconvertible</c>, <c>Failed</c>); empty when written.</param>
/// <param name="Losses">What did not carry over for this item; empty when everything did.</param>
/// <param name="Location">Where the item was written (a path), or null.</param>
public sealed record StoreItemResult(long Index, string Folder, string? Subject, ItemOutcome Outcome, string Reason, LossReport Losses, string? Location);

/// <summary>A loss entry code with how many items it occurred in.</summary>
/// <param name="Family">See <see cref="LossEntry.Family"/>.</param>
/// <param name="Code">See <see cref="LossEntry.Code"/>.</param>
/// <param name="Kind">See <see cref="LossEntry.Kind"/>.</param>
/// <param name="Items">The number of items with at least one such entry.</param>
/// <param name="Example">The name of the property, header or element of the first occurrence.</param>
public sealed record LossTotal(string Family, string Code, LossKind Kind, long Items, string Example);

/// <summary>Counts for one folder.</summary>
/// <param name="Read">Items read from the source folder.</param>
/// <param name="Written">Items written.</param>
public sealed record FolderCount(long Read, long Written);

/// <summary>
/// The aggregate result of a store conversion. Memory is bounded: per-item reports go to the progress callback and are folded into
/// totals here. Items read always equal items written plus items skipped.
/// </summary>
public sealed class StoreConversionReport
{
    private readonly Dictionary<(string, string, LossKind), (long Items, string Example)> _losses = [];
    private readonly Dictionary<string, long> _skipped = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, (long Read, long Written)> _folders = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];
    private readonly int _maxErrors;
    private readonly int _maxLosses;

    internal StoreConversionReport(int maxErrors, int maxLosses)
    {
        _maxErrors = maxErrors;
        _maxLosses = maxLosses;
    }

    /// <summary>Items read from the source (every enumerated item, whatever became of it).</summary>
    public long ItemsRead { get; private set; }

    /// <summary>Items written to the destination.</summary>
    public long ItemsWritten { get; private set; }

    /// <summary>Items not written; see <see cref="SkippedByReason"/>.</summary>
    public long ItemsSkipped => ItemsRead - ItemsWritten;

    /// <summary>Written items whose report has an entry that is not store bookkeeping.</summary>
    public long ItemsWithContentLoss { get; private set; }

    /// <summary>Skipped items per reason code.</summary>
    public IReadOnlyDictionary<string, long> SkippedByReason => _skipped;

    /// <summary>Per-folder counts, by source folder path.</summary>
    public IReadOnlyDictionary<string, FolderCount> Folders => _folders.ToDictionary(p => p.Key, p => new FolderCount(p.Value.Read, p.Value.Written), StringComparer.Ordinal);

    /// <summary>The loss totals, most frequent first.</summary>
    public IReadOnlyList<LossTotal> Losses => [.. _losses.Select(p => new LossTotal(p.Key.Item1, p.Key.Item2, p.Key.Item3, p.Value.Items, p.Value.Example))
        .OrderByDescending(t => t.Items).ThenBy(t => t.Family, StringComparer.Ordinal).ThenBy(t => t.Code, StringComparer.Ordinal)];

    /// <summary>Why items failed, with their position; at most <see cref="StoreConversionOptions.MaxErrors"/>.</summary>
    public IReadOnlyList<string> Errors => _errors;

    internal void Add(StoreItemResult r)
    {
        ItemsRead++;
        (long read, long written) = _folders.GetValueOrDefault(r.Folder);
        read++;
        if (r.Outcome == ItemOutcome.Written)
        {
            ItemsWritten++;
            written++;
        }
        else
        {
            _skipped[r.Reason] = _skipped.GetValueOrDefault(r.Reason) + 1;
        }

        _folders[r.Folder] = (read, written);
        if (r.Outcome == ItemOutcome.Written && r.Losses.HasContentLoss)
        {
            ItemsWithContentLoss++;
        }

        HashSet<(string, string, LossKind)> seen = [];
        foreach (LossEntry e in r.Losses)
        {
            (string, string, LossKind) key = (e.Family, e.Code, e.Kind);
            if (!seen.Add(key))
            {
                continue;
            }

            if (!_losses.TryGetValue(key, out (long Items, string Example) cur))
            {
                if (_losses.Count >= _maxLosses)
                {
                    key = ("(other)", "(other)", LossKind.Dropped);
                    _losses.TryGetValue(key, out cur);
                }

                cur = (cur.Items, cur.Example ?? e.Name);
            }

            _losses[key] = (cur.Items + 1, cur.Example);
        }
    }

    internal void AddError(string message)
    {
        if (_errors.Count < _maxErrors)
        {
            _errors.Add(message);
        }
    }
}
