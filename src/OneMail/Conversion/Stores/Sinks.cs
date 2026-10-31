using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Emlx;
using RecursiveAI.OneMail.Stores.Maildir;
using RecursiveAI.OneMail.Stores.Mbox;

namespace RecursiveAI.OneMail.Conversion.Stores;

/// <summary>One converted item on its way into a destination.</summary>
public sealed class StoreEntry
{
    /// <summary>The source item, for a sink that writes from the item rather than from <see cref="Content"/> (an MSG or PST writer reads the property bag of a <see cref="MapiMailItem"/>). Null for a calendar or card file.</summary>
    public IMailItem? Item { get; init; }

    /// <summary>What the content is.</summary>
    public PayloadKind Kind { get; init; }

    /// <summary>The source folder, <c>/</c> separated; empty for the top.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>The converted bytes: a MIME message, TNEF stream, iCalendar, vCard or their JSON or XML forms.</summary>
    public ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The file extension for a file-per-item destination, with the dot.</summary>
    public string Extension { get; init; } = ".eml";

    /// <summary>Flags the source kept for the item.</summary>
    public MessageMarks Marks { get; init; }

    /// <summary>Keywords or categories the source kept for the item.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>The arrival date when the source has one, else the sent date; null when it has neither.</summary>
    public DateTimeOffset? Received { get; init; }

    /// <summary>The sent date, or null.</summary>
    public DateTimeOffset? Sent { get; init; }

    /// <summary>The subject or title, used to name files; may be null.</summary>
    public string? Subject { get; init; }

    /// <summary>The first sender's address, used for the mbox envelope; may be null.</summary>
    public string? Sender { get; init; }

    /// <summary>The item's loss report; a destination adds an entry for what it cannot keep.</summary>
    public LossReport Report { get; init; } = new();
}

/// <summary>
/// A place the conversion writes to. The engine calls <see cref="WriteAsync"/> once per converted item, in source order, then
/// <see cref="CompleteAsync"/>; a sink is disposed even when the conversion is cancelled. Writers for formats the library writes
/// through other layers (MSG, PST) plug in here.
/// </summary>
public interface IStoreSink : IAsyncDisposable
{
    /// <summary>Writes one item.</summary>
    /// <param name="entry">The item.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Where it was written (a path or other locator), or null.</returns>
    ValueTask<string?> WriteAsync(StoreEntry entry, CancellationToken cancellationToken);

    /// <summary>Finishes the destination after the last item (flushes and closes files).</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the destination is whole.</returns>
    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>Makes the destinations for <see cref="ConvertFormat"/>.</summary>
public static class StoreSinks
{
    /// <summary>Creates the destination for a format.</summary>
    /// <param name="format">What to write.</param>
    /// <param name="destination">A directory for every format; for <see cref="ConvertFormat.Mbox"/> a name with an extension that is not an existing directory means one file.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The sink; dispose it.</returns>
    /// <exception cref="IOException">A file-per-item destination already holds files.</exception>
    public static IStoreSink Create(ConvertFormat format, string destination, StoreConversionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        options ??= new StoreConversionOptions();
        return format switch
        {
            ConvertFormat.Mbox => new MboxSink(destination, options),
            ConvertFormat.Maildir => new MaildirSink(destination, options),
            ConvertFormat.AppleMail => new AppleMailSink(destination, options),
            ConvertFormat.Eml => new EmlSink(destination),
            _ => new DirectorySink(destination, requireEmpty: true),
        };
    }
}

internal static class StorePaths
{
    private const int MaxSegment = 100;

    /// <summary>A name that is safe on every file system; the second value says whether it differs from the input.</summary>
    public static (string Name, bool Changed) Segment(string name)
    {
        StringBuilder sb = new(Math.Min(name.Length, MaxSegment));
        foreach (char c in name)
        {
            if (sb.Length >= MaxSegment)
            {
                break;
            }

            sb.Append(c < 0x20 || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c);
        }

        string s = sb.ToString().TrimEnd('.', ' ');
        if (s.Length == 0 || s is "." or "..")
        {
            s = "_";
        }

        return (s, s != name);
    }

    public static List<string> Segments(string folder, out bool changed)
    {
        changed = false;
        List<string> list = [];
        if (folder.Length == 0)
        {
            return list;
        }

        foreach (string part in folder.Split('/'))
        {
            (string n, bool c) = Segment(part);
            changed |= c;
            list.Add(n);
        }

        return list;
    }

    public static string Slug(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "item";
        }

        StringBuilder sb = new();
        foreach (char c in subject)
        {
            if (sb.Length >= 40)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        string s = sb.ToString().Trim('-');
        return s.Length == 0 ? "item" : s;
    }

    public static string Join(string root, IEnumerable<string> segments) => Path.Combine([root, .. segments]);

    public static void SetTime(string path, DateTimeOffset? when)
    {
        if (when is { } w && w.Year >= 1970)
        {
            File.SetLastWriteTimeUtc(path, w.UtcDateTime);
        }
    }

    public static void ReportFlagsDropped(StoreEntry e)
    {
        if (e.Kind != PayloadKind.Message)
        {
            return;
        }

        if (e.Marks.HasFlag(MessageMarks.Seen) || (e.Marks & ~(MessageMarks.Seen | MessageMarks.Recent)) != MessageMarks.None)
        {
            e.Report.Add("store", "flags", LossKind.Dropped, "FlagsNotCarried", "The destination keeps no read, answered, flagged or draft state.");
        }

        if (e.Keywords.Count > 0)
        {
            e.Report.Add("store", "keywords", LossKind.Dropped, "KeywordsNotCarried", "The destination keeps no keywords or categories.");
        }
    }
}

/// <summary>One file per item under a directory tree that follows the source's folders. Used for EML, ICS, vCard, the JSON and XML forms and TNEF, and for the side items of the mail stores.</summary>
internal sealed class DirectorySink : IStoreSink
{
    private readonly string _root;
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    public DirectorySink(string root, bool requireEmpty)
    {
        _root = root;
        if (requireEmpty && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException("The destination directory is not empty: " + root);
        }

        Directory.CreateDirectory(root);
    }

    public ValueTask<string?> WriteAsync(StoreEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> segs = StorePaths.Segments(entry.Folder, out bool changed);
        if (changed)
        {
            entry.Report.Add("store", entry.Folder, LossKind.Approximated, "FolderNameChanged", "A character the file system cannot hold was replaced in a folder name.");
        }

        string dir = StorePaths.Join(_root, segs);
        Directory.CreateDirectory(dir);
        int n = _counters.GetValueOrDefault(dir) + 1;
        _counters[dir] = n;
        string path = Path.Combine(dir, n.ToString("D6", CultureInfo.InvariantCulture) + "-" + StorePaths.Slug(entry.Subject) + entry.Extension);
        using (FileStream f = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            f.Write(entry.Content.Span);
        }

        StorePaths.SetTime(path, entry.Received);
        StorePaths.ReportFlagsDropped(entry);
        return new ValueTask<string?>(path);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Calendar and contact items that reach a mail store are kept beside it as .ics and .vcf files.
internal abstract class MailSinkBase : IStoreSink
{
    private DirectorySink? _side;
    private readonly string _sideRoot;

    protected MailSinkBase(string sideRoot) => _sideRoot = sideRoot;

    public async ValueTask<string?> WriteAsync(StoreEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Kind != PayloadKind.Message)
        {
            _side ??= new DirectorySink(_sideRoot, requireEmpty: false);
            return await _side.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return await WriteMessageAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    protected abstract ValueTask<string?> WriteMessageAsync(StoreEntry entry, CancellationToken cancellationToken);

    public abstract ValueTask CompleteAsync(CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();
}

/// <summary>One <c>.eml</c> file per message; calendar and contact items go to <c>_items</c>.</summary>
internal sealed class EmlSink : MailSinkBase
{
    private readonly DirectorySink _messages;

    public EmlSink(string root)
        : base(Path.Combine(root, "_items")) => _messages = new DirectorySink(root, requireEmpty: true);

    protected override ValueTask<string?> WriteMessageAsync(StoreEntry entry, CancellationToken cancellationToken) => _messages.WriteAsync(entry, cancellationToken);

    public override ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>mbox: one file per folder, or one file for everything.</summary>
internal sealed class MboxSink : MailSinkBase
{
    private const int MaxOpen = 16;
    private readonly string _destination;
    private readonly bool _single;
    private readonly StoreConversionOptions _options;
    private readonly Dictionary<string, (FileStream Stream, MboxWriter Writer)> _open = new(StringComparer.Ordinal);

    public MboxSink(string destination, StoreConversionOptions options)
        : base(Directory.Exists(destination) || !Path.HasExtension(destination) ? Path.Combine(destination, "_items") : destination + ".items")
    {
        _destination = destination;
        _options = options;
        _single = !Directory.Exists(destination) && Path.HasExtension(destination);
        Directory.CreateDirectory(_single ? Path.GetDirectoryName(Path.GetFullPath(destination))! : destination);
    }

    protected override ValueTask<string?> WriteMessageAsync(StoreEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path;
        if (_single)
        {
            path = _destination;
            if (entry.Folder.Length > 0)
            {
                entry.Report.Add("store", entry.Folder, LossKind.Dropped, "FolderFlattened", "A single mbox file has no folders; the message is written to it without its folder.");
            }
        }
        else
        {
            List<string> segs = StorePaths.Segments(entry.Folder, out bool changed);
            if (changed)
            {
                entry.Report.Add("store", entry.Folder, LossKind.Approximated, "FolderNameChanged", "A character the file system cannot hold was replaced in a folder name.");
            }

            string name = segs.Count == 0 ? "_root" : segs[^1];
            path = Path.Combine(StorePaths.Join(_destination, segs.Take(Math.Max(0, segs.Count - 1))), name + ".mbox");
        }

        byte[] message = StatusHeaders.Apply(entry);
        if (message.Length > 0 && message[^1] != (byte)'\n')
        {
            // A record ends in a line ending (the format has no other way to show where the message stops), so the writer adds one.
            entry.Report.Add("mime", "message", LossKind.Approximated, "FinalLineEndingAdded", "The message did not end in a line ending; mbox adds one.");
        }

        MboxWriter w = WriterFor(path);
        DateTimeOffset date = entry.Received ?? DateTimeOffset.UnixEpoch;
        if (entry.Received is null)
        {
            entry.Report.Add("store", "From_", LossKind.Approximated, "EnvelopeDateUnknown", "The message has no arrival or sent date; the envelope line carries the Unix epoch.");
        }

        w.Append(message, entry.Sender, date);
        return new ValueTask<string?>(path);
    }

    private MboxWriter WriterFor(string path)
    {
        if (_open.TryGetValue(path, out var cur))
        {
            return cur.Writer;
        }

        if (_open.Count >= MaxOpen)
        {
            CloseAll();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var w = new MboxWriter(fs, new MboxWriterOptions { Variant = _options.MboxVariant, Crlf = _options.MboxCrlf, AppendToExisting = true });
        _open[path] = (fs, w);
        return w;
    }

    private void CloseAll()
    {
        foreach ((FileStream s, _) in _open.Values)
        {
            s.Dispose();
        }

        _open.Clear();
    }

    public override ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        CloseAll();
        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        CloseAll();
        return ValueTask.CompletedTask;
    }
}

// mbox keeps flags in header fields of the message: the Status and X-Status letters, and X-Keywords for keyword lists.
internal static class StatusHeaders
{
    public static byte[] Apply(StoreEntry e)
    {
        byte[] bytes = e.Content.ToArray();
        MessageMarks have = MboxMetadata.Parse(bytes).Flags;
        MessageMarks want = e.Marks & ~MessageMarks.Recent;
        if (want == MessageMarks.None && e.Keywords.Count == 0)
        {
            return bytes;
        }

        if (have != MessageMarks.None || bytes.Length == 0 || bytes[0] is (byte)'\r' or (byte)'\n')
        {
            // The message already states its own flags (or has no header block to put them in): they stay as written.
            if (want != have)
            {
                e.Report.Add("store", "flags", LossKind.Approximated, "FlagsNotCarried", "The message's own Status fields differ from the source's flags and are kept as written.");
            }

            return bytes;
        }

        string eol = bytes.AsSpan().IndexOf((byte)'\n') is int i and > 0 && bytes[i - 1] == (byte)'\r' ? "\r\n" : "\n";
        StringBuilder sb = new();
        if (want.HasFlag(MessageMarks.Seen))
        {
            sb.Append("Status: R").Append(eol);
        }

        string x = string.Concat(want.HasFlag(MessageMarks.Answered) ? "A" : "", want.HasFlag(MessageMarks.Flagged) ? "F" : "", want.HasFlag(MessageMarks.Deleted) ? "D" : "", want.HasFlag(MessageMarks.Draft) ? "T" : "");
        if (x.Length > 0)
        {
            sb.Append("X-Status: ").Append(x).Append(eol);
        }

        if (e.Keywords.Count > 0)
        {
            sb.Append("X-Keywords: ").Append(string.Join(',', e.Keywords.Select(k => k.ReplaceLineEndings(" ")))).Append(eol);
        }

        if (want.HasFlag(MessageMarks.Forwarded))
        {
            e.Report.Add("store", "flags", LossKind.Dropped, "FlagsNotCarried", "mbox has no letter for forwarded.");
        }

        if (sb.Length == 0)
        {
            return bytes;
        }

        e.Report.Add("store", "Status", LossKind.Internal, "FlagsWrittenAsHeaders", "The flags are written as Status, X-Status and X-Keywords fields in front of the message's own.");
        byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] result = new byte[head.Length + bytes.Length];
        head.CopyTo(result, 0);
        bytes.CopyTo(result, head.Length);
        return result;
    }
}

/// <summary>A Maildir with Maildir++ subfolders.</summary>
internal sealed class MaildirSink : MailSinkBase
{
    private readonly MaildirStore _store;
    private readonly string _root;
    private readonly HashSet<string> _made = new(StringComparer.Ordinal);

    public MaildirSink(string destination, StoreConversionOptions options)
        : base(Path.Combine(destination, "_items"))
    {
        _ = options;
        _root = destination;
        Directory.CreateDirectory(destination);
        _store = new MaildirStore(new PhysicalStoreFileSystem(destination));
    }

    protected override ValueTask<string?> WriteMessageAsync(StoreEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Maildir++ names a subfolder ".Parent.Child"; a dot inside a name would be read as a level, so it becomes an underscore.
        List<string> segs = [];
        bool changed = false;
        if (entry.Folder.Length > 0)
        {
            foreach (string part in entry.Folder.Split('/'))
            {
                (string n, bool c) = StorePaths.Segment(part.Replace('.', '_'));
                changed |= c || part.Contains('.', StringComparison.Ordinal);
                segs.Add(n);
            }
        }

        if (changed)
        {
            entry.Report.Add("store", entry.Folder, LossKind.Approximated, "FolderNameChanged", "A character a Maildir++ folder name cannot hold was replaced.");
        }

        string folder = string.Join('.', segs);
        if (_made.Add(folder))
        {
            _store.CreateFolder(folder);
        }

        string delivered;
        try
        {
            delivered = _store.DeliverPath(entry.Content, folder, entry.Marks & ~MessageMarks.Recent, entry.Keywords);
        }
        catch (InvalidOperationException)
        {
            // More than 26 distinct keywords in one folder: the flags go in, the keywords do not.
            delivered = _store.DeliverPath(entry.Content, folder, entry.Marks & ~MessageMarks.Recent);
            entry.Report.Add("store", "keywords", LossKind.Dropped, "KeywordsNotCarried", "A Maildir folder holds at most 26 distinct keywords.");
        }

        StorePaths.SetTime(Path.Combine(_root, delivered.Replace('/', Path.DirectorySeparatorChar)), entry.Received);
        return new ValueTask<string?>(delivered);
    }

    public override ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>An Apple Mail tree of <c>.mbox</c> bundles holding <c>Messages/&lt;n&gt;.emlx</c>.</summary>
internal sealed class AppleMailSink : MailSinkBase
{
    private readonly string _root;
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    public AppleMailSink(string destination, StoreConversionOptions options)
        : base(Path.Combine(destination, "_items"))
    {
        _ = options;
        _root = destination;
        Directory.CreateDirectory(destination);
    }

    protected override ValueTask<string?> WriteMessageAsync(StoreEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> segs = StorePaths.Segments(entry.Folder, out bool changed);
        if (changed)
        {
            entry.Report.Add("store", entry.Folder, LossKind.Approximated, "FolderNameChanged", "A character the file system cannot hold was replaced in a folder name.");
        }

        string dir = Path.Combine(StorePaths.Join(_root, segs.Select(s => s + ".mbox")), "Messages");
        Directory.CreateDirectory(dir);
        int n = _counters.GetValueOrDefault(dir) + 1;
        _counters[dir] = n;
        string path = Path.Combine(dir, n.ToString(CultureInfo.InvariantCulture) + ".emlx");
        using (FileStream f = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            EmlxWriter.Write(f, entry.Content.Span, entry.Marks & ~MessageMarks.Recent, entry.Received, entry.Sent);
        }

        StorePaths.SetTime(path, entry.Received);
        if (entry.Keywords.Count > 0)
        {
            entry.Report.Add("store", "keywords", LossKind.Dropped, "KeywordsNotCarried", "An .emlx file keeps no keyword list.");
        }

        return new ValueTask<string?>(path);
    }

    public override ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
