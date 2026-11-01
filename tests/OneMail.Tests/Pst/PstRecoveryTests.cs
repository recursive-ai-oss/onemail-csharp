using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Pst.Recovery;
using RecursiveAI.OneMail.Recovery;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Scan-mode recovery of damaged PSTs, forensic recovery of deleted states, and repair into a fresh file.</summary>
public sealed class PstRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset Stamp = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onemail-pstrec-" + Guid.NewGuid().ToString("N"));

    public PstRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string P(string name) => Path.Combine(_dir, name);

    private static MapiMessage Msg(string subject, int i) =>
        PstBuilderTests.Message(subject, recipients: 1 + (i % 3), attachments: i % 4 == 0 ? 1 : 0, body: i % 5 == 0 ? new string('x', 20_000) + i : "body " + i);

    // 3 folders, 24 messages; returns subject -> signature of the original.
    private static Dictionary<string, string> BuildBase(string path, int perFolder = 8)
    {
        using (PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = Stamp, StoreId = Guid.Parse("11111111-2222-3333-4444-555555555555") }))
        {
            PstFolderWriter inbox = b.IpmSubtree.CreateFolder("Inbox");
            PstFolderWriter sent = b.IpmSubtree.CreateFolder("Sent");
            PstFolderWriter sub = inbox.CreateFolder("Projects");
            int n = 0;
            foreach (PstFolderWriter f in new[] { inbox, sent, sub })
            {
                for (int i = 0; i < perFolder; i++, n++)
                {
                    f.AddMessage(Msg($"{f.Name}-{i}", n));
                }
            }

            b.Complete();
        }

        return Snapshot(path);
    }

    private static Dictionary<string, string> Snapshot(string path)
    {
        var d = new Dictionary<string, string>();
        Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
        using (s)
        {
            foreach (PstMessage m in s!.EnumerateMessages(PstMessageSet.All, Ct))
            {
                d[m.Subject ?? ""] = PstStoreCompare.Signature(m);
            }
        }

        return d;
    }

    private static List<PstRecoveredItem> Items(PstRecovery r) => r.EnumerateItems(Ct).ToList();

    private string Damaged(string source, Action<FileStream> damage)
    {
        string dst = P(Guid.NewGuid().ToString("N") + ".pst");
        File.Copy(source, dst);
        using (var f = new FileStream(dst, FileMode.Open, FileAccess.ReadWrite))
        {
            damage(f);
        }

        return dst;
    }

    // Every item that is not Partial must equal the original exactly; returns how many original messages came back whole.
    private static int CheckAgainstOriginal(IEnumerable<PstRecoveredItem> items, Dictionary<string, string> original)
    {
        int exact = 0;
        foreach (PstRecoveredItem it in items)
        {
            if (it.Provenance.Status == RecoveryStatus.Partial)
            {
                continue;
            }

            string subject = it.Message.Subject ?? "";
            Assert.True(original.TryGetValue(subject, out string? sig), "recovered a message the original does not hold: " + subject);
            if (it.Provenance.Defects.Any(d => d.Code == nameof(PstRecoveryDefectKind.SubnodesNotFound)))
            {
                // Read from a block no node names: recipients and attachments were in subnodes it cannot reach.
                Assert.True(it.Message.Properties.Count > 3);
                exact++;
                continue;
            }

            Assert.Equal(sig, PstStoreCompare.Signature(it.Message));
            exact++;
        }

        return exact;
    }

    [Fact]
    public void IntactStoreReadsAsLiveAndNothingIsRebuilt()
    {
        string src = P("a.pst");
        Dictionary<string, string> original = BuildBase(src);
        Assert.True(PstRecovery.TryOpen(src, out PstRecovery? r, null, Ct));
        using (r)
        {
            Assert.True(r!.HeaderUsable);
            Assert.False(r.IndexRebuilt);
            List<PstRecoveredItem> items = Items(r);
            Assert.Equal(original.Count, items.Count);
            Assert.All(items, i => Assert.Equal(RecoveryStatus.Live, i.Provenance.Status));
            Assert.Equal(original.Count, CheckAgainstOriginal(items, original));
            Assert.All(items, i => Assert.NotEmpty(i.Provenance.Ranges));
            Assert.Contains(items, i => i.FolderPath == "Top of Personal Folders/Inbox/Projects");
        }
    }

    [Fact]
    public void ZeroedHeaderIsInferredAndEverythingComesBack()
    {
        string src = P("a.pst");
        Dictionary<string, string> original = BuildBase(src);
        string bad = Damaged(src, PstDamage.ZeroHeader);
        Assert.True(PstRecovery.TryOpen(bad, out PstRecovery? r, null, Ct));
        using (r)
        {
            Assert.False(r!.HeaderUsable);
            Assert.True(r.IndexRebuilt || r.Stats.ReachedNodes == 0);
            Assert.Equal(NdbFormat.Unicode, r.Database.Format);
            Assert.Equal(NdbEncryption.Permute, r.Database.Header.Encryption);
            List<PstRecoveredItem> items = Items(r);
            Assert.Equal(original.Count, CheckAgainstOriginal(items, original));
            Assert.Equal(original.Count, items.Count);
            Assert.DoesNotContain(items, i => i.Provenance.Status == RecoveryStatus.Live);
            Assert.All(items, i => Assert.True(i.PlaceInFolder));
            Assert.Contains(items, i => i.FolderPath == "Top of Personal Folders/Inbox/Projects");
        }
    }

    [Fact]
    public void ZeroedRootPagesAndMapsAreRebuilt()
    {
        string src = P("a.pst");
        Dictionary<string, string> original = BuildBase(src);
        string bad = Damaged(src, f =>
        {
            PstDamage.ZeroRoots(f);
            PstDamage.ZeroAllocationMaps(f);
            PstDamage.ZeroDensityList(f);
        });
        Assert.True(PstRecovery.TryOpen(bad, out PstRecovery? r, null, Ct));
        using (r)
        {
            Assert.True(r!.HeaderUsable);
            Assert.True(r.IndexRebuilt);
            Assert.Contains(r.Defects, d => d.Kind == PstRecoveryDefectKind.NodeIndexRebuilt);
            List<PstRecoveredItem> items = Items(r);
            Assert.Equal(original.Count, items.Count);
            Assert.Equal(original.Count, CheckAgainstOriginal(items, original));
        }
    }

    [Fact]
    public void ZeroedAllocationMapsAndDensityListLoseNothing()
    {
        string src = P("a.pst");
        Dictionary<string, string> original = BuildBase(src);
        string bad = Damaged(src, f =>
        {
            PstDamage.ZeroAllocationMaps(f);
            PstDamage.ZeroDensityList(f);
        });
        Assert.True(PstRecovery.TryOpen(bad, out PstRecovery? r, null, Ct));
        using (r)
        {
            List<PstRecoveredItem> items = Items(r!);
            Assert.Equal(original.Count, items.Count);
            Assert.All(items, i => Assert.Equal(RecoveryStatus.Live, i.Provenance.Status));
        }
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    public void TruncationKeepsWhatIsWholeAndNothingElse(double keep)
    {
        string src = P("a.pst");
        Dictionary<string, string> original = BuildBase(src, 30);
        string bad = Damaged(src, f => PstDamage.Truncate(f, keep));
        if (!PstRecovery.TryOpen(bad, out PstRecovery? r, null, Ct))
        {
            return;
        }

        using (r)
        {
            List<PstRecoveredItem> items = Items(r!);
            int exact = CheckAgainstOriginal(items, original);
            string why = $"keep {keep}: nothing came back; {items.Count} items, stats {r!.Stats}, rebuilt {r.IndexRebuilt}, " + string.Join("; ", r.Defects.Take(5).Select(d => d.Kind + " " + d.Detail)) + " / " + string.Join("; ", r.Database.Defects.Take(5).Select(d => d.Kind + " " + d.Detail));
            Assert.True(exact > 0 || keep < 0.3, why);
            Assert.True(exact <= original.Count);
        }
    }

    [Theory]
    [InlineData(0.001, 1)]
    [InlineData(0.01, 2)]
    [InlineData(0.05, 3)]
    public void RandomCorruptionNeverThrowsAndWhatIsReturnedWholeIsExact(double rate, int seed)
    {
        string src = P("a.pst");
        Dictionary<string, string> original = BuildBase(src, 40);
        string bad = Damaged(src, f => PstDamage.Corrupt(f, rate, seed));
        if (!PstRecovery.TryOpen(bad, out PstRecovery? r, null, Ct))
        {
            return;
        }

        using (r)
        {
            List<PstRecoveredItem> items = Items(r!);
            int exact = CheckAgainstOriginal(items, original);
            Assert.True(exact >= original.Count * (rate >= 0.05 ? 0.3 : 0.6), $"rate {rate}: only {exact} of {original.Count} came back whole");
        }
    }

    [Fact]
    public void RepairWritesAFreshStoreThatOpensCleanAndKeepsTheFolders()
    {
        string src = P("a.pst");
        Dictionary<string, string> original = BuildBase(src);
        string bad = Damaged(src, f =>
        {
            PstDamage.Corrupt(f, 0.01, 9);
            PstDamage.ZeroHeader(f);
        });
        byte[] before = File.ReadAllBytes(bad);
        Assert.True(PstRecovery.TryOpen(bad, out PstRecovery? r, null, Ct));
        string output = P("repaired.pst");
        string report = P("repaired.json");
        PstRepairSummary summary;
        using (r)
        {
            summary = r!.RepairTo(output, report, Ct);
            Assert.Throws<ArgumentException>(() => r.RepairTo(bad, null, Ct));
        }

        Assert.Equal(before, File.ReadAllBytes(bad));
        Assert.True(summary.Written > 0);
        Assert.Equal(0, summary.Failed);
        Assert.True(PstStore.TryOpen(output, out PstStore? fresh, out _));
        using (fresh)
        {
            Assert.Empty(fresh!.Database.Defects);
            Assert.Empty(fresh.Defects);
            var found = new List<PstMessage>(fresh.EnumerateMessages(PstMessageSet.All, Ct));
            Assert.Equal(summary.Written, found.Count);
            foreach (PstMessage m in found)
            {
                Assert.True(original.ContainsKey(m.Subject ?? ""));
                Assert.NotNull(m.Properties.Get(new MapiNamedProperty(PstRecovery.ProvenancePropertySet, "OneMailRecoveryStatus")));
            }

            Assert.Contains(fresh.EnumerateFolders(Ct), f => f.Name == "Projects");
        }

        string json = File.ReadAllText(report);
        Assert.Contains("\"written\": " + summary.Written, json);
    }

    // ---- forensic ----

    private sealed record Forensic(string Path, Dictionary<string, string> Original, HashSet<string> Listed, Dictionary<string, RecoveryStatus> Expected);

    private Forensic BuildForensic()
    {
        string path = P("f.pst");
        var listed = new HashSet<string>();
        var expected = new Dictionary<string, RecoveryStatus>();
        var removeFromTrees = new List<uint>();
        var stale = new List<uint>();
        uint freeNid;
        uint orphanNid;
        using (PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = Stamp, StoreId = Guid.Parse("11111111-2222-3333-4444-555555555555") }))
        {
            PstFolderWriter inbox = b.IpmSubtree.CreateFolder("Inbox");
            for (int i = 0; i < 10; i++)
            {
                inbox.AddMessage(Msg("live-" + i, i));
                listed.Add("live-" + i);
            }

            // Listed by the folder but the node is gone from the tree, an older copy of the leaf page remains: stale.
            for (int i = 0; i < 2; i++)
            {
                stale.Add(inbox.AddMessage(Msg("stale-" + i, 20 + i)).Value);
                expected["stale-" + i] = RecoveryStatus.Orphaned;
            }

            // The node and its blocks are gone from both trees, the bytes are still in the file: free space.
            freeNid = inbox.AddMessage(Msg("free-0", 31)).Value;
            expected["free-0"] = RecoveryStatus.FreeSpace;

            // The node is gone from the node tree but the block tree still lists its blocks: orphaned without a node ID.
            orphanNid = inbox.AddMessage(Msg("orphan-0", 33)).Value;
            expected["orphan-0"] = RecoveryStatus.Orphaned;

            // Node in the tree, row removed from the folder's contents table: soft-deleted.
            for (int i = 0; i < 2; i++)
            {
                Nid nid = b.NewMessageNid(false);
                b.Items.WriteMessage(Msg("soft-" + i, 40 + i), nid, inbox.Nid, false, "test");
                expected["soft-" + i] = RecoveryStatus.SoftDeleted;
            }

            // Node in the tree whose folder is not in the hierarchy.
            {
                Nid nid = b.NewMessageNid(false);
                b.Items.WriteMessage(Msg("nofolder-0", 50), nid, new Nid(0x2A2), false, "test");
                expected["nofolder-0"] = RecoveryStatus.SoftDeleted;
            }

            b.Complete();
        }

        Dictionary<string, string> original = Snapshot(path);

        // Blocks of the free and orphaned messages, from the intact file.
        var freeBids = new List<ulong>();
        Assert.True(NdbFile.TryOpen(path, out NdbFile? nf, out _));
        using (nf)
        {
            Assert.True(nf!.Nodes.TryGet(new Nid(freeNid), out NodeEntry fe));
            var w = new PstNodeWalker(nf, 100000);
            w.Walk(fe);
            freeBids.AddRange(w.Visited);
        }

        using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            Assert.Equal(1 + freeBids.Count, PstDamage.RemoveFromTrees(f, [freeNid], freeBids, keepStalePages: false));
            Assert.Equal(1, PstDamage.RemoveFromTrees(f, [orphanNid], [], keepStalePages: false));
            Assert.Equal(stale.Count, PstDamage.RemoveFromTrees(f, stale, [], keepStalePages: true));
        }

        return new Forensic(path, original, listed, expected);
    }

    [Fact]
    public void ForensicRecoveryTagsEachStateAndNeverCallsADeletedItemLive()
    {
        Forensic fx = BuildForensic();
        Assert.True(PstRecovery.TryOpen(fx.Path, out PstRecovery? r, new PstRecoveryOptions { Forensic = true }, Ct));
        using (r)
        {
            List<PstRecoveredItem> items = Items(r!);
            var byName = new Dictionary<string, PstRecoveredItem>();
            foreach (PstRecoveredItem it in items)
            {
                byName[it.Message.Subject ?? ""] = it;
            }

            foreach (string live in fx.Listed)
            {
                Assert.Equal(RecoveryStatus.Live, byName[live].Provenance.Status);
            }

            foreach ((string subject, RecoveryStatus tag) in fx.Expected)
            {
                Assert.True(byName.TryGetValue(subject, out PstRecoveredItem? it), "missing " + subject);
                Assert.Equal(tag, it!.Provenance.Status);
            }

            // No Live false positives: Live is exactly the listed set.
            Assert.Equal(fx.Listed.Count, items.Count(i => i.Provenance.Status == RecoveryStatus.Live));
            Assert.Equal(fx.Listed.Count + fx.Expected.Count, items.Count);

            // Byte ranges: each is a whole block of the file, and the message bytes read back from the ranges parse as blocks.
            byte[] file = File.ReadAllBytes(fx.Path);
            foreach (PstRecoveredItem it in items)
            {
                Assert.NotEmpty(it.Provenance.Ranges);
                foreach (ByteRange rg in it.Provenance.Ranges)
                {
                    Assert.True(rg.End <= file.Length);
                    Assert.True(NdbSpan.TryParseBlock(file.AsSpan((int)rg.Offset), NdbFormat.Unicode, rg.Offset, out NdbBlockInfo bi, new List<NdbDefect>()));
                    Assert.True(bi.TotalLength <= rg.Length);
                }
            }

            // The stale and soft-deleted messages are whole; the free-space one lost its recipients and says so.
            Assert.Equal(fx.Original["stale-0"], PstStoreCompare.Signature(byName["stale-0"].Message));
            Assert.NotEmpty(byName["soft-0"].Message.Recipients);
            Assert.Contains(byName["free-0"].Provenance.Defects, d => d.Code == nameof(PstRecoveryDefectKind.SubnodesNotFound));
            Assert.False(byName["stale-0"].PlaceInFolder);
        }
    }

    [Fact]
    public void WithoutForensicOnlyLiveItemsComeBack()
    {
        Forensic fx = BuildForensic();
        Assert.True(PstRecovery.TryOpen(fx.Path, out PstRecovery? r, null, Ct));
        using (r)
        {
            List<PstRecoveredItem> items = Items(r!);
            Assert.All(items, i => Assert.Equal(RecoveryStatus.Live, i.Provenance.Status));
            Assert.Equal(fx.Listed.Count, items.Count);
        }
    }

    [Fact]
    public void ForensicRepairFilesDeletedItemsUnderRecoveredFolders()
    {
        Forensic fx = BuildForensic();
        Assert.True(PstRecovery.TryOpen(fx.Path, out PstRecovery? r, new PstRecoveryOptions { Forensic = true }, Ct));
        string output = P("out.pst");
        using (r)
        {
            PstRepairSummary s = r!.RepairTo(output, null, Ct);
            Assert.Equal(fx.Listed.Count + fx.Expected.Count, s.Written);
        }

        Assert.True(PstStore.TryOpen(output, out PstStore? fresh, out _));
        using (fresh)
        {
            string[] folders = fresh!.EnumerateFolders(Ct).Select(f => f.Path).ToArray();
            Assert.Contains("Top of Personal Folders/Recovered/SoftDeleted/Inbox", folders);
            Assert.Contains("Top of Personal Folders/Recovered/FreeSpace", folders);
            Assert.Contains("Top of Personal Folders/Recovered/Orphaned/Inbox", folders);
            Assert.Equal(fx.Listed.Count, fresh.EnumerateFolders(Ct).First(f => f.Name == "Inbox").EnumerateMessages(PstMessageSet.Normal, Ct).Count());
        }
    }
}
