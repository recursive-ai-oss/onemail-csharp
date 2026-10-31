using System.Collections.Concurrent;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores;

/// <summary>One entry of a directory listing.</summary>
/// <param name="Name">The entry's name, without any directory part.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="Length">The size in bytes of a file; 0 for directories.</param>
/// <param name="Modified">The last modification time, in UTC.</param>
public readonly record struct StoreFileInfo(string Name, bool IsDirectory, long Length, DateTimeOffset Modified);

/// <summary>
/// The small file-system surface the directory stores (Maildir, Apple Mail bundles) need, so they run over a real
/// directory, over memory (tests, browser WebAssembly) or over any storage the caller can map onto these calls.
/// </summary>
/// <remarks>
/// Paths use <c>/</c> as the separator and are relative to the file system's root; the empty string is the root.
/// Implementations are synchronous, because the stores are consumed lazily and a directory listing is small next to
/// the messages. Failures other than "does not exist" throw <see cref="IOException"/>.
/// </remarks>
public interface IStoreFileSystem
{
    /// <summary>Lists the entries of a directory. A missing directory lists nothing. Order is unspecified.</summary>
    /// <param name="directory">The directory path.</param>
    /// <returns>The entries, produced lazily where the implementation can.</returns>
    IEnumerable<StoreFileInfo> List(string directory);

    /// <summary>Whether the path names an existing directory.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True for an existing directory.</returns>
    bool DirectoryExists(string path);

    /// <summary>Whether the path names an existing file.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True for an existing file.</returns>
    bool FileExists(string path);

    /// <summary>Opens a file for random-access reading.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>A source the caller disposes.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    IRandomAccessSource OpenRead(string path);

    /// <summary>Creates a new file for writing. It fails rather than replacing an existing file.</summary>
    /// <param name="path">The file path. The parent directory must exist.</param>
    /// <returns>A write stream the caller disposes; the file's content is complete when it is disposed.</returns>
    /// <exception cref="IOException">The file exists.</exception>
    Stream CreateNew(string path);

    /// <summary>Renames a file. By default it fails if the destination exists.</summary>
    /// <param name="oldPath">The current path.</param>
    /// <param name="newPath">The new path; its directory must exist.</param>
    /// <param name="overwrite">True replaces an existing destination in one step.</param>
    void Rename(string oldPath, string newPath, bool overwrite = false);

    /// <summary>Deletes a file. A missing file is not an error.</summary>
    /// <param name="path">The path.</param>
    void Delete(string path);

    /// <summary>Creates a directory and any missing parents. An existing directory is not an error.</summary>
    /// <param name="path">The path.</param>
    void CreateDirectory(string path);
}

/// <summary>An <see cref="IStoreFileSystem"/> over a directory of the local file system.</summary>
public sealed class PhysicalStoreFileSystem : IStoreFileSystem
{
    private readonly string _root;

    /// <summary>Creates a file system rooted at a local directory. Paths passed to the members are relative to it.</summary>
    /// <param name="rootDirectory">The root directory. It need not exist yet.</param>
    public PhysicalStoreFileSystem(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
    }

    private string Map(string path)
    {
        string full = Path.GetFullPath(Path.Combine(_root, path));
        // A path with ".." must not escape the root.
        string rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (full != _root && !full.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new ArgumentException("The path leaves the store's root directory.", nameof(path));
        }

        return full;
    }

    /// <inheritdoc/>
    public IEnumerable<StoreFileInfo> List(string directory)
    {
        string dir = Map(directory);
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (FileSystemInfo info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (info is FileInfo f)
            {
                yield return new StoreFileInfo(f.Name, false, f.Length, f.LastWriteTimeUtc);
            }
            else
            {
                yield return new StoreFileInfo(info.Name, true, 0, info.LastWriteTimeUtc);
            }
        }
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path) => Directory.Exists(Map(path));

    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(Map(path));

    /// <inheritdoc/>
    public IRandomAccessSource OpenRead(string path) => FileSource.Open(Map(path));

    /// <inheritdoc/>
    public Stream CreateNew(string path) => new FileStream(Map(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <inheritdoc/>
    public void Rename(string oldPath, string newPath, bool overwrite = false)
    {
        // Without overwrite File.Move fails when the destination exists, which is what delivery relies on.
        File.Move(Map(oldPath), Map(newPath), overwrite);
    }

    /// <inheritdoc/>
    public void Delete(string path) => File.Delete(Map(path));

    /// <inheritdoc/>
    public void CreateDirectory(string path) => Directory.CreateDirectory(Map(path));
}

/// <summary>An <see cref="IStoreFileSystem"/> held in memory. Safe for concurrent use. Modification times are fixed at the Unix epoch.</summary>
public sealed class MemoryStoreFileSystem : IStoreFileSystem
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _dirs = new(StringComparer.Ordinal);

    private static string Norm(string path) => path.Trim('/');

    /// <summary>Stores a file directly, creating its parent directories.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="content">The file content.</param>
    public void SetFile(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        path = Norm(path);
        MakeParents(path);
        _files[path] = content;
    }

    /// <summary>Returns a file's content, or null if there is no such file.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The bytes, or null.</returns>
    public byte[]? GetFile(string path) => _files.TryGetValue(Norm(path), out byte[]? b) ? b : null;

    /// <summary>All file paths, in ordinal order.</summary>
    /// <returns>The paths.</returns>
    public IReadOnlyList<string> AllFiles() => [.. _files.Keys.Order(StringComparer.Ordinal)];

    private void MakeParents(string path)
    {
        int i = path.LastIndexOf('/');
        while (i > 0)
        {
            string parent = path[..i];
            _dirs[parent] = true;
            i = parent.LastIndexOf('/');
        }
    }

    /// <inheritdoc/>
    public IEnumerable<StoreFileInfo> List(string directory)
    {
        string prefix = Norm(directory);
        if (prefix.Length > 0)
        {
            prefix += "/";
        }

        HashSet<string> seenDirs = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, byte[]> kv in _files)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = kv.Key[prefix.Length..];
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                yield return new StoreFileInfo(rest, false, kv.Value.Length, DateTimeOffset.UnixEpoch);
            }
            else if (seenDirs.Add(rest[..slash]))
            {
                yield return new StoreFileInfo(rest[..slash], true, 0, DateTimeOffset.UnixEpoch);
            }
        }

        foreach (string d in _dirs.Keys)
        {
            if (!d.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = d[prefix.Length..];
            int slash = rest.IndexOf('/');
            string name = slash < 0 ? rest : rest[..slash];
            if (name.Length > 0 && seenDirs.Add(name))
            {
                yield return new StoreFileInfo(name, true, 0, DateTimeOffset.UnixEpoch);
            }
        }
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        path = Norm(path);
        if (path.Length == 0 || _dirs.ContainsKey(path))
        {
            return true;
        }

        string prefix = path + "/";
        return _files.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public bool FileExists(string path) => _files.ContainsKey(Norm(path));

    /// <inheritdoc/>
    public IRandomAccessSource OpenRead(string path) =>
        _files.TryGetValue(Norm(path), out byte[]? b) ? new MemorySource(b) : throw new FileNotFoundException("No such file.", path);

    /// <inheritdoc/>
    public Stream CreateNew(string path)
    {
        path = Norm(path);
        if (_files.ContainsKey(path))
        {
            throw new IOException("The file exists: " + path);
        }

        MakeParents(path);
        return new CommitStream(this, path);
    }

    /// <inheritdoc/>
    public void Rename(string oldPath, string newPath, bool overwrite = false)
    {
        string from = Norm(oldPath);
        string to = Norm(newPath);
        if (!_files.TryGetValue(from, out byte[]? b))
        {
            throw new FileNotFoundException("No such file.", from);
        }

        if (overwrite)
        {
            _files[to] = b;
        }
        else if (!_files.TryAdd(to, b))
        {
            throw new IOException("The destination exists: " + to);
        }

        MakeParents(to);
        _files.TryRemove(from, out _);
    }

    /// <inheritdoc/>
    public void Delete(string path) => _files.TryRemove(Norm(path), out _);

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        path = Norm(path);
        if (path.Length > 0)
        {
            _dirs[path] = true;
            MakeParents(path);
        }
    }

    private sealed class CommitStream(MemoryStoreFileSystem owner, string path) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                if (!owner._files.TryAdd(path, ToArray()))
                {
                    throw new IOException("The file exists: " + path);
                }
            }

            base.Dispose(disposing);
        }

        private bool _committed;
    }
}
