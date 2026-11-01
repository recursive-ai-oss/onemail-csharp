using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>In-place editing of PSTs Outlook wrote (the Apache Tika test files), when the corpus is present.</summary>
public sealed class PstWritableCorpusTests
{
    internal static string? Find(string name)
    {
        string root = Environment.GetEnvironmentVariable("ONEMAIL_CORPUS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemail", "corpus");
        foreach (string candidate in new[] { Path.Combine(root, "pst", "tika", name), Path.Combine(root, "pst", name) })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string? Copy(string name)
    {
        string? source = Find(name);
        if (source is null)
        {
            return null;
        }

        string path = NdbEditorTests.TempFile();
        File.Copy(source, path);
        return path;
    }

    public static TheoryData<string> Files => new() { "testPST.pst", "testPST_variousBodyTypes.pst" };

    [Theory]
    [MemberData(nameof(Files))]
    public void AnOutlookFileTakesAdditionsMovesAndDeletes(string name)
    {
        string? path = Copy(name);
        if (path is null)
        {
            return;   // corpus not fetched here
        }

        try
        {
            Dictionary<string, string> before;
            List<string> problemsBefore;
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                before = new Dictionary<string, string>(StoreChecks.Snapshot(w.View));
                problemsBefore = StoreChecks.Consistency(w.View);
                var folders = w.View.EnumerateFolders(TestContext.Current.CancellationToken).Where(f => !f.IsSearchFolder && f.ParentFolder is not null).ToList();
                PstFolder withMail = folders.First(f => f.EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken).Any());
                PstFolder other = folders.First(f => f.Nid != withMail.Nid);
                var ids = withMail.EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken).ToList();
                Nid made = w.CreateFolder(other.Nid, "Edited here");
                for (int i = 0; i < 25; i++)
                {
                    w.AddMessage(made, PstBuilderTests.Message("added " + i, 2, i % 4 == 0 ? 1 : 0, "text " + i));
                }

                w.MoveMessage(ids[0], made);
                if (ids.Count > 1)
                {
                    w.DeleteMessage(ids[1]);
                }

                w.Commit();
            }

            NdbEditorTests.AssertClean(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            Assert.Empty(store.Defects);
            Assert.Equal(problemsBefore, StoreChecks.Consistency(store));
            SortedDictionary<string, string> after = StoreChecks.Snapshot(store);
            // every item that was not deleted is unchanged, whichever folder it is in now
            foreach ((string key, string value) in before.Where(kv => kv.Key.StartsWith("msg/", StringComparison.Ordinal)))
            {
                if (after.TryGetValue(key, out string? now))
                {
                    Assert.Equal(value[(value.IndexOf('|') + 1)..], now[(now.IndexOf('|') + 1)..]);
                }
            }

            Assert.True(after.Count >= before.Count + 25);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
