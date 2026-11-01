using System.Diagnostics;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Durability;

/// <summary>Builds a store of many items, then edits it in place and reports the peak working set of each phase.</summary>
internal static class Memory
{
    public static int Run(string dir, int items, int edits)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"memory-{items}.pst");
        File.Delete(path);
        var proc = Process.GetCurrentProcess();
        long Peak() { proc.Refresh(); return proc.PeakWorkingSet64 >> 20; }
        var sw = Stopwatch.StartNew();
        const int Folders = 100;
        using (PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = Scenarios.Stamp }))
        {
            var folders = new PstFolderWriter[Folders];
            for (int f = 0; f < Folders; f++)
            {
                folders[f] = b.IpmSubtree.CreateFolder("Folder " + f);
            }

            for (int i = 0; i < items; i++)
            {
                folders[i % Folders].AddMessage(Scenarios.Message($"item {i}", 1, 0, 0, i % 2 == 0, "body " + i));
            }

            b.Complete();
        }

        Console.WriteLine($"build {items} items: {sw.Elapsed.TotalSeconds:F1}s, file {new FileInfo(path).Length >> 20} MiB, peak working set {Peak()} MiB");
        long afterBuild = Peak();
        sw.Restart();
        var rnd = new Random(5);
        using (PstWritableStore w = PstStore.OpenForWriting(path, new PstWriteOptions { Timestamp = Scenarios.Stamp, AutoCommitRows = 0 }))
        {
            var folderIds = w.View.EnumerateFolders().Where(f => f.ParentFolder is not null && !f.IsSearchFolder && f.Name?.StartsWith("Folder ", StringComparison.Ordinal) == true).Select(f => f.Nid).ToList();
            var added = new List<Nid>();
            for (int e = 1; e <= edits; e++)
            {
                added.Add(w.AddMessage(folderIds[rnd.Next(folderIds.Count)], Scenarios.Message($"edit {e}", 1, 0, 0, false, "edit body")));
                if (e % 3 == 0)
                {
                    Nid n = added[rnd.Next(added.Count)];
                    w.DeleteMessage(n);
                    added.Remove(n);
                }

                if (e % 50 == 0)
                {
                    w.Commit();
                }
            }

            w.Commit();
        }

        Console.WriteLine($"edit {edits} ops in place: {sw.Elapsed.TotalSeconds:F1}s, peak working set {Peak()} MiB (after build {afterBuild} MiB)");
        File.Delete(path);
        return 0;
    }
}
