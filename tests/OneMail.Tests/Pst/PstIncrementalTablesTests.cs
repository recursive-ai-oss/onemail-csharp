using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>A commit that changes a big folder row by row must leave the same store as one that writes its tables whole.</summary>
public sealed class PstIncrementalTablesTests
{
    private static MapiMessage Msg(string subject, bool read)
    {
        MapiMessage m = PstBuilderTests.Message(subject, 1, 0, "body of " + subject);
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E07, MapiType.Integer32), BitConverter.GetBytes(read ? 1 : 0)));
        return m;
    }

    private static string BuildBig(int perFolder, int folders)
    {
        string path = NdbEditorTests.TempFile();
        using PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero), StoreId = Guid.Parse("11111111-2222-3333-4444-555555555555") });
        for (int f = 0; f < folders; f++)
        {
            PstFolderWriter folder = b.IpmSubtree.CreateFolder("Folder " + f);
            for (int i = 0; i < perFolder; i++)
            {
                folder.AddMessage(PstBuilderTests.Message($"message {f}/{i}", 0, 0, "x"));
            }
        }

        b.Complete();
        return path;
    }

    // Every table of every folder as row ID -> cells: what a reader of the tables sees, whatever order the matrix keeps its rows in.
    private static SortedDictionary<string, string> Tables(string path)
    {
        Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
        using PstStore store = s!;
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (PstFolder f in store.EnumerateFolders(TestContext.Current.CancellationToken))
        {
            foreach (NidType type in new[] { NidType.ContentsTable, NidType.AssocContentsTable, NidType.HierarchyTable })
            {
                using OldTable t = OldTable.Open(store.Database, PstWritableStore.TableNid(f.Nid, type), []);
                map[$"{f.Nid}/{type}/columns"] = string.Join(",", t.Columns.Select(static c => $"{c.Id:x4}.{c.Type:x4}"));
                foreach ((uint id, byte[]?[] cells) in t.Rows())
                {
                    map[$"{f.Nid}/{type}/{id:x8}"] = string.Join("|", cells.Select(static c => c is null ? "-" : Convert.ToHexString(c)));
                }
            }
        }

        return map;
    }

    private static int Edit(string path, PstTableUpdates mode, int seed, int rounds, int adds, int deletes, int moves, int replaces)
    {
        var rnd = new Random(seed);
        int inPlace = 0;
        using PstWritableStore w = PstStore.OpenForWriting(path, new PstWriteOptions { TableUpdates = mode, AutoCommitRows = 0 });
        Nid a = w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(f => f.Name == "Folder 0").Nid;
        Nid b = w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(f => f.Name == "Folder 1").Nid;
        for (int r = 0; r < rounds; r++)
        {
            List<Nid> inA = Ids(w, a);
            List<Nid> inB = Ids(w, b);
            for (int i = 0; i < adds; i++)
            {
                w.AddMessage(i % 2 == 0 ? a : b, Msg($"added {r}/{i}", read: i % 3 != 0));
            }

            for (int i = 0; i < deletes && inA.Count > 0; i++)
            {
                int at = rnd.Next(inA.Count);
                w.DeleteMessage(inA[at]);
                inA.RemoveAt(at);
            }

            for (int i = 0; i < moves && inA.Count > 0; i++)
            {
                int at = rnd.Next(inA.Count);
                w.MoveMessage(inA[at], b);
                inA.RemoveAt(at);
            }

            for (int i = 0; i < replaces && inB.Count > 0; i++)
            {
                w.ReplaceMessage(inB[rnd.Next(inB.Count)], Msg($"replaced {r}/{i}", read: i % 2 == 0));
            }

            if (r == 1)
            {
                // A new subfolder and a rename change the hierarchy table and the counts in it.
                w.CreateFolder(a, "Sub " + r);
            }

            w.Commit();
        }

        inPlace = w.TablesUpdatedInPlace;
        return inPlace;
    }

    private static List<Nid> Ids(PstWritableStore w, Nid folder)
    {
        PstFolder f = w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == folder);
        var ids = new List<Nid>(f.EnumerateMessageIds());
        ids.Sort(static (x, y) => x.Value.CompareTo(y.Value));
        return ids;
    }

    private static void SameStore(string expected, string actual, bool wiped = true)
    {
        NdbEditorTests.AssertClean(actual);
        Assert.True(PstStore.TryOpen(actual, out PstStore? s, out _));
        using (PstStore store = s!)
        {
            Assert.Empty(store.Defects);
            Assert.Equal([], StoreChecks.Consistency(store));
        }

        if (wiped)
        {
            Assert.Equal(0, NdbEditorTests.NonZeroFreeBytes(actual));
        }

        Assert.True(PstStore.TryOpen(expected, out PstStore? e, out _));
        using PstStore want = e!;
        Assert.True(PstStore.TryOpen(actual, out PstStore? g, out _));
        using PstStore got = g!;
        Assert.Equal(StoreChecks.Snapshot(want), StoreChecks.Snapshot(got));
        want.Dispose();
        got.Dispose();
        Assert.Equal(Tables(expected), Tables(actual));
    }

    [Theory]
    [InlineData(800, 1, 1, 40, 25, 10, 5)]
    [InlineData(800, 2, 6, 3, 2, 1, 1)]
    [InlineData(2000, 3, 120, 90, 40, 20, 4)]
    public void ChangingRowsInPlaceLeavesTheStoreTheTableRewriteLeaves(int perFolder, int seed, int rounds, int adds, int deletes, int moves, int replaces)
    {
        string basePath = BuildBig(perFolder, 2);
        string whole = NdbEditorTests.TempFile();
        string rows = NdbEditorTests.TempFile();
        try
        {
            File.Copy(basePath, whole);
            File.Copy(basePath, rows);
            int rewritten = Edit(whole, PstTableUpdates.Rewrite, seed, Math.Min(rounds, 4), adds, deletes, moves, replaces);
            int inPlace = Edit(rows, PstTableUpdates.Incremental, seed, Math.Min(rounds, 4), adds, deletes, moves, replaces);
            Assert.Equal(0, rewritten);
            Assert.True(inPlace > 0, "no table was changed row by row");
            SameStore(whole, rows);
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(whole);
            File.Delete(rows);
        }
    }

    [Fact]
    public void ABigTableChangesAcrossManyHeapBlocks()
    {
        string basePath = BuildBig(12_000, 2);
        string whole = NdbEditorTests.TempFile();
        string rows = NdbEditorTests.TempFile();
        try
        {
            File.Copy(basePath, whole);
            File.Copy(basePath, rows);
            Edit(whole, PstTableUpdates.Rewrite, 7, 2, 600, 700, 300, 50);
            int inPlace = Edit(rows, PstTableUpdates.Incremental, 7, 2, 600, 700, 300, 50);
            Assert.True(inPlace > 0);
            SameStore(whole, rows);
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(whole);
            File.Delete(rows);
        }
    }

    [Theory]
    [MemberData(nameof(PstWritableCorpusTests.Files), MemberType = typeof(PstWritableCorpusTests))]
    public void AnOutlookFileGetsTheSameStoreWhicheverWayItsTablesChange(string name)
    {
        string? whole = PstWritableCorpusTests.Copy(name);
        string? rows = PstWritableCorpusTests.Copy(name);
        if (whole is null || rows is null)
        {
            return;   // corpus not fetched here
        }

        try
        {
            foreach ((string path, PstTableUpdates mode) in new[] { (whole, PstTableUpdates.Rewrite), (rows, PstTableUpdates.Incremental) })
            {
                using PstWritableStore w = PstStore.OpenForWriting(path, new PstWriteOptions { TableUpdates = mode, AutoCommitRows = 0 });
                var folders = w.View.EnumerateFolders(TestContext.Current.CancellationToken).Where(f => !f.IsSearchFolder && f.ParentFolder is not null).ToList();
                PstFolder biggest = folders.OrderByDescending(f => f.ContentCount ?? 0).ThenBy(f => f.Nid.Value).First();
                PstFolder other = folders.First(f => f.Nid != biggest.Nid);
                var ids = new List<Nid>(biggest.EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken));
                ids.Sort(static (x, y) => x.Value.CompareTo(y.Value));
                for (int i = 0; i < 60; i++)
                {
                    w.AddMessage(biggest.Nid, Msg("added " + i, i % 2 == 0));
                }

                for (int i = 0; i < ids.Count && i < 6; i += 2)
                {
                    w.DeleteMessage(ids[i]);
                }

                if (ids.Count > 3)
                {
                    w.MoveMessage(ids[3], other.Nid);
                }

                w.Commit();
                w.AddMessage(biggest.Nid, Msg("second commit", true));
                w.Commit();
            }

            SameStore(whole, rows, wiped: false);
        }
        finally
        {
            File.Delete(whole);
            File.Delete(rows);
        }
    }

    [Fact]
    public void EmptyingAFolderRowByRowStillLeavesAnOpenableTable()
    {
        string basePath = BuildBig(300, 2);
        string whole = NdbEditorTests.TempFile();
        string rows = NdbEditorTests.TempFile();
        try
        {
            File.Copy(basePath, whole);
            File.Copy(basePath, rows);
            foreach ((string p, PstTableUpdates mode) in new[] { (whole, PstTableUpdates.Rewrite), (rows, PstTableUpdates.Incremental) })
            {
                using PstWritableStore w = PstStore.OpenForWriting(p, new PstWriteOptions { TableUpdates = mode, AutoCommitRows = 0 });
                Nid a = w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(f => f.Name == "Folder 0").Nid;
                foreach (Nid id in Ids(w, a).Take(295))
                {
                    w.DeleteMessage(id);
                }

                w.Commit();
                foreach (Nid id in Ids(w, a))
                {
                    w.DeleteMessage(id);
                }

                w.Commit();
                w.AddMessage(a, Msg("again", true));
                w.Commit();
            }

            SameStore(whole, rows);
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(whole);
            File.Delete(rows);
        }
    }
}
