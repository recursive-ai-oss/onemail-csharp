using RecursiveAI.OneMail.Conversion.Stores;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Adding, replacing, moving and deleting folders and items in an existing PST.</summary>
public sealed class PstWritableStoreTests
{
    private static MapiMessage Msg(string subject, int recipients = 1, int attachments = 0, bool read = true, string? body = null)
    {
        MapiMessage m = PstBuilderTests.Message(subject, recipients, attachments, body ?? ("body of " + subject));
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E07, MapiType.Integer32), BitConverter.GetBytes(read ? 1 : 0)));
        return m;
    }

    private static Nid FolderNamed(PstStore s, string name) => s.EnumerateFolders(TestContext.Current.CancellationToken).First(f => f.Name == name).Nid;

    private static void AssertSound(string path)
    {
        NdbEditorTests.AssertClean(path);
        Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
        using PstStore store = s!;
        Assert.Empty(store.Defects);
        Assert.Equal([], StoreChecks.Consistency(store));
        Assert.Equal(0, NdbEditorTests.NonZeroFreeBytes(path));
    }

    [Fact]
    public void AddedItemsAreReadBackPropertyExactAndTheCountsFollow()
    {
        string path = NdbEditorTests.Build(5, folders: 2);
        try
        {
            var added = new Dictionary<Nid, MapiMessage>();
            Nid folder;
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                folder = FolderNamed(w.View, "Folder 1");
                for (int i = 0; i < 40; i++)
                {
                    MapiMessage m = Msg("added " + i, recipients: 2, attachments: i % 5 == 0 ? 1 : 0, read: i % 2 == 0);
                    added[w.AddMessage(folder, m)] = m;
                }

                w.Commit();
            }

            AssertSound(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            PstFolder f = store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == folder);
            Assert.Equal(45, f.ContentCount);
            Assert.Equal(25, f.GetInt32(0x3603));   // 20 added and unread, plus the five the builder wrote with no read flag
            foreach ((Nid nid, MapiMessage original) in added)
            {
                Assert.True(store.TryOpenMessage(nid, out PstMessage? read));
                Assert.Equal(original.GetString(0x0037), read!.GetString(0x0037));
                Assert.Equal(original.Recipients.Count, read.Recipients.Count);
                Assert.Equal(original.Attachments.Count, read.Attachments.Count);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RefusedEditsThrowTypedExceptionsAndLeaveTheFileAsCommitted()
    {
        string path = NdbEditorTests.Build(3);
        try
        {
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                Nid folder = FolderNamed(w.View, "Folder 0");
                Nid item = w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == folder).EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken).First();
                int count = w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == folder).ContentCount ?? 0;

                Assert.Equal(PstEditFailure.FolderNotFound, Assert.Throws<PstEditException>(() => w.AddMessage(new Nid((0x7FFFu << 5) | 2), Msg("x"))).Reason);
                Assert.Equal(PstEditFailure.ItemNotFound, Assert.Throws<PstEditException>(() => w.DeleteMessage(new Nid((0x7FFFu << 5) | 4))).Reason);
                Assert.Equal(PstEditFailure.InvalidFolderOperation, Assert.Throws<PstEditException>(() => w.MoveFolder(folder, folder)).Reason);

                string before = w.View.TryOpenMessage(item, out PstMessage? m) ? m!.GetString(0x0037)! : "";
                // A value that does not fit its type is not an error: the property is left out and reported.
                MapiMessage bad = Msg("bad value");
                bad.Properties.Add(MapiProperty.Create(new MapiTag(0x3FF1, MapiType.Integer32), [1, 2]));
                w.AddMessage(folder, bad);
                Assert.Contains(w.Defects, d => d.Kind == PstWriteDefectKind.UnsupportedType);
                count++;

                // The store is still usable, and the refused edits left nothing behind.
                w.AddMessage(folder, Msg("fine"));
                count++;
                w.Commit();
                Assert.True(w.View.TryOpenMessage(item, out PstMessage? after));
                Assert.Equal(before, after!.GetString(0x0037));
                Assert.Equal(count, w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == folder).ContentCount ?? 0);
            }

            AssertSound(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheBuilderWritesTheLastAllocatedIndexAsTheHeaderCounter()
    {
        // MS-PST 2.2.2.6: rgnid[type] is the last nidIndex allocated. The first allocation after it gets the next index, so no item is handed a used NID.
        string path = NdbEditorTests.Build(7, folders: 3);
        try
        {
            byte[] header = File.ReadAllBytes(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            var folders = store.EnumerateFolders(TestContext.Current.CancellationToken).ToList();
            uint lastFolder = folders.Where(f => f.Nid.Index >= 0x400).Max(f => f.Nid.Index);
            uint lastMessage = folders.SelectMany(f => f.EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken)).Max(n => n.Index);
            Assert.Equal(lastFolder, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44 + ((int)NidType.NormalFolder * 4))));
            Assert.Equal(lastMessage, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44 + ((int)NidType.NormalMessage * 4))));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADeletedItemIsGoneItsSpaceIsZeroAndTheCountsFollow()
    {
        string path = NdbEditorTests.Build(30, attachments: 2);
        try
        {
            Dictionary<string, string> before;
            Nid gone;
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                PstFolder f = w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Name == "Folder 0");
                var ids = f.EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken).ToList();
                gone = ids[3];
                before = new Dictionary<string, string>(StoreChecks.Snapshot(w.View));
                w.DeleteMessage(gone);
                w.DeleteMessage(ids[4]);
                w.Commit();
            }

            AssertSound(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            Assert.False(store.TryOpenMessage(gone, out _));
            Assert.Equal(28, store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Name == "Folder 0").ContentCount);
            SortedDictionary<string, string> after = StoreChecks.Snapshot(store);
            Assert.Equal(before.Count - 2, after.Count);
            foreach ((string key, string value) in after.Where(kv => kv.Key.StartsWith("msg/", StringComparison.Ordinal)))
            {
                Assert.Equal(before[key], value);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReplacingAnItemKeepsItsNidAndChangesItsContent()
    {
        string path = NdbEditorTests.Build(10, attachments: 1);
        try
        {
            Nid target;
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                target = w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Name == "Folder 0").EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken).First();
                w.ReplaceMessage(target, Msg("replaced", recipients: 3, attachments: 2, body: new string('z', 50_000)));
                w.Commit();
            }

            AssertSound(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            Assert.True(store.TryOpenMessage(target, out PstMessage? m));
            Assert.Equal("replaced", m!.GetString(0x0037));
            Assert.Equal(3, m.Recipients.Count);
            Assert.Equal(2, m.Attachments.Count);
            Assert.Equal(10, store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Name == "Folder 0").ContentCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFolderMovedTwiceInOneCommitLeavesNoTraceInTheMiddleParent()
    {
        string path = NdbEditorTests.Build(4, folders: 3);
        try
        {
            Nid f0, f1, f2;
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                f0 = FolderNamed(w.View, "Folder 0");
                f1 = FolderNamed(w.View, "Folder 1");
                f2 = FolderNamed(w.View, "Folder 2");
                Nid top = FolderNamed(w.View, "Top of Personal Folders");
                w.MoveFolder(f0, f1);     // top -> f1 -> f2, then f0 -> f1 -> back to top: moved away and home again in one commit
                w.MoveFolder(f0, f2);
                w.MoveFolder(f0, top);
                w.MoveFolder(f2, f1);
                w.MoveFolder(f2, f0);
                w.Commit();
            }

            AssertSound(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            PstFolder fold1 = store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == f1);
            Assert.Empty(fold1.EnumerateSubfolders(TestContext.Current.CancellationToken));
            PstFolder fold0 = store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == f0);
            Assert.Equal([f2], fold0.EnumerateSubfolders(TestContext.Current.CancellationToken).Select(x => x.Nid).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FoldersCanBeCreatedRenamedMovedAndDeleted()
    {
        string path = NdbEditorTests.Build(8, folders: 2);
        try
        {
            Nid top, a, b, moved;
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                top = FolderNamed(w.View, "Top of Personal Folders");
                a = w.CreateFolder(top, "New A");
                b = w.CreateFolder(a, "New B", "IPF.Appointment");
                for (int i = 0; i < 6; i++)
                {
                    w.AddMessage(b, Msg("in b " + i, read: i > 2));
                }

                w.AddMessage(a, Msg("in a"));
                w.Commit();
            }

            AssertSound(path);
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                w.RenameFolder(b, "Renamed B");
                moved = FolderNamed(w.View, "Folder 0");
                w.MoveFolder(moved, a);
                w.MoveMessage(w.View.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == b).EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken).First(), moved);
                w.Commit();
            }

            AssertSound(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using (PstStore store = s!)
            {
                PstFolder fa = store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == a);
                Assert.Equal(["Folder 0", "Renamed B"], fa.EnumerateSubfolders(TestContext.Current.CancellationToken).Select(x => x.Name!).Order().ToArray());
                Assert.Equal(9, store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == moved).ContentCount);
                Assert.Equal(5, store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Nid == b).ContentCount);
            }

            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                w.DeleteFolder(a);
            }

            AssertSound(path);
            Assert.True(PstStore.TryOpen(path, out s, out _));
            using (PstStore store = s!)
            {
                Assert.DoesNotContain(store.EnumerateFolders(TestContext.Current.CancellationToken), x => x.Nid == a || x.Nid == b || x.Nid == moved);
                Assert.Single(store.EnumerateFolders(TestContext.Current.CancellationToken), x => x.Name == "Folder 1");
                Assert.Equal(8, store.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).Count());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NamedPropertiesAreAddedToTheMapAndKeepTheirIds()
    {
        string path = NdbEditorTests.Build(3);
        try
        {
            var guid = Guid.Parse("00020329-0000-0000-c000-000000000046");
            Nid first, second;
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                Nid folder = FolderNamed(w.View, "Folder 0");
                MapiMessage m = Msg("named one");
                m.Properties.Add(MapiProperty.Create(new MapiTag(0x8000, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("v1"), new MapiNamedProperty(guid, "Custom A")));
                first = w.AddMessage(folder, m);
                w.Commit();
                MapiMessage m2 = Msg("named two");
                m2.Properties.Add(MapiProperty.Create(new MapiTag(0x8000, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("v2"), new MapiNamedProperty(guid, "Custom A")));
                m2.Properties.Add(MapiProperty.Create(new MapiTag(0x8001, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("v3"), new MapiNamedProperty(guid, "Custom B")));
                second = w.AddMessage(folder, m2);
                w.Commit();
            }

            AssertSound(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            Assert.True(store.TryOpenMessage(first, out PstMessage? a));
            Assert.True(store.TryOpenMessage(second, out PstMessage? b));
            Assert.Equal("v1", a!.Properties.Get(new MapiNamedProperty(guid, "Custom A"))?.AsString());
            Assert.Equal("v2", b!.Properties.Get(new MapiNamedProperty(guid, "Custom A"))?.AsString());
            Assert.Equal("v3", b.Properties.Get(new MapiNamedProperty(guid, "Custom B"))?.AsString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThousandsOfRowsSplitTheContentsTableAcrossBlocks()
    {
        string path = NdbEditorTests.Build(3);
        try
        {
            using (PstWritableStore w = PstStore.OpenForWriting(path, new PstWriteOptions { AutoCommitRows = 700 }))
            {
                Nid folder = FolderNamed(w.View, "Folder 0");
                for (int i = 0; i < 4000; i++)
                {
                    w.AddMessage(folder, Msg("bulk " + i, recipients: 0, read: i % 3 != 0, body: "x"));
                }

                w.Commit();
            }

            AssertSound(path);
            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            PstFolder f = store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Name == "Folder 0");
            Assert.Equal(4003, f.ContentCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SuspiciousFilesAreRefusedWithReasonsAndAcceptedOnRequest()
    {
        var made = new List<string>();
        string path = NdbEditorTests.Build(3);
        made.Add(path);
        try
        {
            // a header checksum that does not match
            byte[] bytes = File.ReadAllBytes(path);
            bytes[40] ^= 0x01;   // dwUnique is covered by both header CRCs
            File.WriteAllBytes(path, bytes);
            NdbEditRefusedException e = Assert.Throws<NdbEditRefusedException>(() => PstStore.OpenForWriting(path));
            Assert.Contains(e.Refusals, r => r.Reason == NdbRefusalReason.HeaderChecksum && r.CanAllow);
            using (PstStore.OpenForWriting(path, new PstWriteOptions { Allow = NdbOpenRisks.HeaderChecksum }))
            {
            }

            // allocation maps marked invalid
            path = NdbEditorTests.Build(3);
            made.Add(path);
            bytes = File.ReadAllBytes(path);
            bytes[180 + 68] = 0;
            RecomputeHeaderCrc(bytes);
            File.WriteAllBytes(path, bytes);
            e = Assert.Throws<NdbEditRefusedException>(() => PstStore.OpenForWriting(path));
            Assert.Contains(e.Refusals, r => r.Reason == NdbRefusalReason.AllocationMaps);
            using (PstWritableStore w = PstStore.OpenForWriting(path, new PstWriteOptions { Allow = NdbOpenRisks.AllocationMaps }))
            {
                w.AddMessage(FolderNamed(w.View, "Folder 0"), Msg("after rebuild"));
                w.Commit();
            }

            AssertSound(path);

            // ANSI, OST and unknown versions are never opened
            foreach ((ushort ver, ushort magic, NdbRefusalReason reason) in new[] { ((ushort)14, (ushort)0x4D53, NdbRefusalReason.Ansi), ((ushort)23, (ushort)0x4F53, NdbRefusalReason.OfflineStore), ((ushort)36, (ushort)0x4D53, NdbRefusalReason.LargePages) })
            {
                path = NdbEditorTests.Build(1);
                made.Add(path);
                bytes = File.ReadAllBytes(path);
                BitConverter.TryWriteBytes(bytes.AsSpan(10), ver);
                BitConverter.TryWriteBytes(bytes.AsSpan(8), magic);
                RecomputeHeaderCrc(bytes);
                File.WriteAllBytes(path, bytes);
                e = Assert.Throws<NdbEditRefusedException>(() => PstStore.OpenForWriting(path, new PstWriteOptions { Allow = (NdbOpenRisks)15 }));
                Assert.Contains(e.Refusals, r => r.Reason == reason && !r.CanAllow);
            }
        }
        finally
        {
            foreach (string f in made)
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static void RecomputeHeaderCrc(byte[] h)
    {
        BitConverter.TryWriteBytes(h.AsSpan(4), NdbChecksums.Crc32(h.AsSpan(8, 471)));
        BitConverter.TryWriteBytes(h.AsSpan(524), NdbChecksums.Crc32(h.AsSpan(8, 516)));
    }

    [Fact]
    public void UncommittedChangesAreDroppedOnDispose()
    {
        string path = NdbEditorTests.Build(4);
        try
        {
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                w.AddMessage(FolderNamed(w.View, "Folder 0"), Msg("never committed"));
            }

            Assert.True(PstStore.TryOpen(path, out PstStore? s, out _));
            using PstStore store = s!;
            Assert.Equal(4, store.EnumerateFolders(TestContext.Current.CancellationToken).First(x => x.Name == "Folder 0").ContentCount);
            Assert.Empty(store.Database.Defects);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>The happy path: add converted mail to a PST that already exists.</summary>
public sealed class PstAddToExistingTests
{
    [Fact]
    public async Task ConvertingIntoAnExistingPstAddsInPlaceAndCommitsOnCompletion()
    {
        string pst = NdbEditorTests.Build(4);
        string dir = Path.Combine(Path.GetTempPath(), "onemail-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(dir, $"m{i}.eml"), $"From: a@example.com\r\nTo: b@example.com\r\nSubject: added {i}\r\nDate: Mon, 06 May 2024 07:08:09 +0000\r\nMessage-ID: <{i}@example.com>\r\n\r\nbody {i}\r\n", TestContext.Current.CancellationToken);
            }

            var report = await Mail.ConvertAsync(dir, ConvertFormat.Pst, pst, new StoreConversionOptions { AddToExistingPst = true }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(3, report.ItemsWritten);
            NdbEditorTests.AssertClean(pst);
            Assert.True(PstStore.TryOpen(pst, out PstStore? s, out _));
            using PstStore store = s!;
            Assert.Equal([], StoreChecks.Consistency(store));
            Assert.Equal(3, store.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).Count(m => m.GetString(0x0037)?.StartsWith("added ", StringComparison.Ordinal) == true));
        }
        finally
        {
            File.Delete(pst);
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>Compaction after deletes.</summary>
public sealed class PstCompactionTests
{
    [Fact]
    public async Task ACompactedStoreHoldsTheSameItemsAndIsSmallerAfterDeletes()
    {
        string path = NdbEditorTests.Build(150, attachments: 2, folders: 2);
        string compact = NdbEditorTests.TempFile();
        try
        {
            using (PstWritableStore w = PstStore.OpenForWriting(path))
            {
                foreach (PstFolder f in w.View.EnumerateFolders(TestContext.Current.CancellationToken).Where(f => f.Name!.StartsWith("Folder ", StringComparison.Ordinal)).ToList())
                {
                    foreach (Nid id in f.EnumerateMessageIds(cancellationToken: TestContext.Current.CancellationToken).Where((_, i) => i % 3 != 0).ToList())
                    {
                        w.DeleteMessage(id);
                    }
                }

                w.Commit();
            }

            await PstCompaction.CompactAsync(path, compact, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(new FileInfo(compact).Length < new FileInfo(path).Length);
            Assert.True(PstStore.TryOpen(path, out PstStore? live, out _));
            Assert.True(PstStore.TryOpen(compact, out PstStore? packed, out _));
            using (live)
            using (packed)
            {
                PstStoreCompare.Result r = PstStoreCompare.Compare(live!.EnumerateMessages(PstMessageSet.All, TestContext.Current.CancellationToken).ToList(), packed!);
                Assert.Equal(0, r.Unmatched);
                Assert.Equal(0, r.PropertiesNotCarried);
                Assert.Equal(r.Source, r.Written);
                Assert.Equal(100, packed!.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).Count());
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(compact);
        }
    }
}
