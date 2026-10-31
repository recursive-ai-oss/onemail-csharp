using System.Globalization;
using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Emlx;

/// <summary>A message file of an Apple Mail mailbox bundle.</summary>
/// <param name="Path">The file's path in the store's file system.</param>
/// <param name="Number">The number the file name starts with (<c>1234.emlx</c>, <c>1234.partial.emlx</c>), or -1.</param>
/// <param name="IsPartial">Whether the name ends in <c>.partial.emlx</c>: Mail stored the message without its attachments.</param>
/// <param name="AttachmentsDirectory">
/// The directory beside <c>Messages</c> where Mail keeps the attachments of a partial message (<c>Attachments/&lt;number&gt;</c>),
/// whether or not it exists. Empty for a complete message.
/// </param>
public readonly record struct EmlxItem(string Path, long Number, bool IsPartial, string AttachmentsDirectory);

/// <summary>
/// An Apple Mail mailbox bundle (<c>Name.mbox</c> directory): finds the <c>Messages</c> directories under it, at any depth up
/// to eight levels (older layouts hold <c>Messages</c> directly, newer ones nest it under <c>&lt;GUID&gt;/Data/…</c>) and
/// enumerates the <c>.emlx</c> files in them.
/// </summary>
public sealed class AppleMailBundle
{
    private const int MaxDepth = 8;
    private readonly IStoreFileSystem _fs;
    private readonly string _root;

    /// <summary>Creates a view of a bundle. Nothing is read yet.</summary>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="bundlePath">The bundle's path; empty for the file system's root.</param>
    public AppleMailBundle(IStoreFileSystem fileSystem, string bundlePath = "")
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fs = fileSystem;
        _root = bundlePath.Trim('/');
    }

    /// <summary>Whether the directory holds at least one <c>Messages</c> directory with an <c>.emlx</c> file.</summary>
    /// <returns>True for a bundle.</returns>
    public bool Exists() => Messages().Any(_ => true);

    /// <summary>Enumerates the message files: by directory, then by number, then by name.</summary>
    /// <returns>The items.</returns>
    public IEnumerable<EmlxItem> Enumerate() => Messages().SelectMany(Items);

    /// <summary>Enumerates the message files, honouring cancellation between items.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The items.</returns>
    public async IAsyncEnumerable<EmlxItem> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (EmlxItem item in Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Opens a message file.</summary>
    /// <param name="item">An item of this bundle.</param>
    /// <returns>A source the caller disposes; pass it to <see cref="EmlxReader.Read(IRandomAccessSource)"/>.</returns>
    public IRandomAccessSource Open(in EmlxItem item) => _fs.OpenRead(item.Path);

    /// <summary>Reads the bundle's <c>Info.plist</c> if it has one.</summary>
    /// <returns>The parsed top-level dictionary, or null when there is none or it is not a property list.</returns>
    public Dictionary<string, object?>? ReadInfo()
    {
        string path = _root.Length == 0 ? "Info.plist" : _root + "/Info.plist";
        if (!_fs.FileExists(path))
        {
            return null;
        }

        using IRandomAccessSource s = _fs.OpenRead(path);
        if (s.Length is 0 or > (1 << 20))
        {
            return null;
        }

        byte[] b = new byte[s.Length];
        int n = s.ReadFully(0, b);
        return Plist.TryParse(b.AsSpan(0, n), out object? root) ? root as Dictionary<string, object?> : null;
    }

    private List<string> Messages()
    {
        Stack<(string Dir, int Depth)> stack = new();
        stack.Push((_root, 0));
        List<string> found = [];
        while (stack.Count > 0)
        {
            (string dir, int depth) = stack.Pop();
            List<StoreFileInfo> subs = [.. _fs.List(dir).Where(f => f.IsDirectory)];
            subs.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name)); // popped in ascending order
            foreach (StoreFileInfo f in subs)
            {
                string path = dir.Length == 0 ? f.Name : dir + "/" + f.Name;
                if (f.Name == "Messages")
                {
                    if (_fs.List(path).Any(m => !m.IsDirectory && m.Name.EndsWith(".emlx", StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(path);
                    }
                }
                else if (depth + 1 < MaxDepth && f.Name != "Attachments")
                {
                    stack.Push((path, depth + 1));
                }
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private List<EmlxItem> Items(string messagesDir)
    {
        string parent = messagesDir[..Math.Max(0, messagesDir.LastIndexOf('/'))];
        List<EmlxItem> items = [];
        foreach (StoreFileInfo f in _fs.List(messagesDir))
        {
            if (f.IsDirectory || !f.Name.EndsWith(".emlx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool partial = f.Name.EndsWith(".partial.emlx", StringComparison.OrdinalIgnoreCase);
            int dot = f.Name.IndexOf('.', StringComparison.Ordinal);
            long number = dot > 0 && long.TryParse(f.Name.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : -1;
            string attachments = partial && number >= 0
                ? (parent.Length == 0 ? "Attachments/" : parent + "/Attachments/") + number.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            items.Add(new EmlxItem(messagesDir + "/" + f.Name, number, partial, attachments));
        }

        items.Sort((a, b) => a.Number != b.Number ? a.Number.CompareTo(b.Number) : string.CompareOrdinal(a.Path, b.Path));
        return items;
    }
}
