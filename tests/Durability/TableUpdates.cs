using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Durability;

/// <summary>Times one commit that adds, deletes or moves rows of a big folder, with the tables changed row by row or written whole.</summary>
internal static class TableUpdates
{
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written += count;
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Written += buffer.Length;
            inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Writes a store with "Folder 0" of <paramref name="rows"/> items and a small "Folder 1".</summary>
    public static int Make(string path, int rows)
    {
        var sw = Stopwatch.StartNew();
        using (PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = Scenarios.Stamp }))
        {
            PstFolderWriter big = b.IpmSubtree.CreateFolder("Folder 0");
            PstFolderWriter small = b.IpmSubtree.CreateFolder("Folder 1");
            for (int i = 0; i < rows; i++)
            {
                big.AddMessage(Scenarios.Message($"item {i}", 0, 0, 0, i % 2 == 0, "b"));
            }

            for (int i = 0; i < 10; i++)
            {
                small.AddMessage(Scenarios.Message($"other {i}", 0, 0, 0, true, "b"));
            }

            b.Complete();
        }

        Console.WriteLine($"built {rows} rows in {sw.Elapsed.TotalSeconds:F1}s, {new FileInfo(path).Length >> 20} MiB");
        return 0;
    }

    /// <summary>Copies the base store, makes <paramref name="count"/> changes of one kind in a single commit and prints one RESULT line.</summary>
    public static int Run(string basePath, string work, string mode, string op, int count)
    {
        File.Copy(basePath, work, overwrite: true);
        var proc = Process.GetCurrentProcess();
        long Peak() { proc.Refresh(); return proc.PeakWorkingSet64 >> 20; }
        PstTableUpdates updates = mode == "rewrite" ? PstTableUpdates.Rewrite : PstTableUpdates.Incremental;
        var file = new CountingStream(new FileStream(work, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16));
        var opts = new PstWriteOptions { Timestamp = Scenarios.Stamp, AutoCommitRows = 0, TableUpdates = updates };
        using PstWritableStore w = PstStore.OpenForWriting(file, leaveOpen: false, opts);
        Nid big = w.View.EnumerateFolders().First(f => f.Name == "Folder 0").Nid;
        Nid small = w.View.EnumerateFolders().First(f => f.Name == "Folder 1").Nid;
        int rows = w.View.EnumerateFolders().First(f => f.Nid == big).ContentCount ?? 0;

        // The items to delete or move are spread over the folder: scattered edits are the worst case for a row-by-row change.
        var targets = new List<Nid>();
        if (op != "add")
        {
            int stride = Math.Max(1, rows / count);
            int at = 0;
            foreach (Nid id in w.View.EnumerateFolders().First(f => f.Nid == big).EnumerateMessageIds())
            {
                if (at++ % stride == 0 && targets.Count < count)
                {
                    targets.Add(id);
                }
            }
        }

        long before = Peak();
        long writtenBefore = file.Written;
        var sw = Stopwatch.StartNew();
        switch (op)
        {
            case "add":
                for (int i = 0; i < count; i++)
                {
                    w.AddMessage(big, Scenarios.Message($"new {i}", 0, 0, 0, true, "b"));
                }

                break;
            case "delete":
                foreach (Nid id in targets)
                {
                    w.DeleteMessage(id);
                }

                break;
            case "move":
                foreach (Nid id in targets)
                {
                    w.MoveMessage(id, small);
                }

                break;
            default:
                throw new ArgumentException("op is add, delete or move");
        }

        double opMs = sw.Elapsed.TotalMilliseconds;
        long writtenOps = file.Written;
        sw.Restart();
        w.Commit();
        double commitMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"RESULT mode={mode} rows={rows} op={op} count={count} edit_ms={opMs:F0} commit_ms={commitMs:F0} commit_mib={(file.Written - writtenOps) / 1048576.0:F2} edit_mib={(writtenOps - writtenBefore) / 1048576.0:F2} peak_mib_before={before} peak_mib={Peak()} tables_in_place={w.TablesUpdatedInPlace}"));
        return 0;
    }
}
