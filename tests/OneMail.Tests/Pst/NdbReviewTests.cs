using System.Buffers.Binary;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Findings of the review of in-place writing: crafted headers and trees, and the independent node database checker.</summary>
public sealed class NdbReviewTests
{
    private static readonly DateTimeOffset Stamp = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static void FixHeader(byte[] f)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), NdbChecksums.Crc32(f.AsSpan(8, 471)));
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(524), NdbChecksums.Crc32(f.AsSpan(8, 516)));
    }

    private static void FixPage(byte[] f, int ib)
    {
        Span<byte> p = f.AsSpan(ib, 512);
        ulong bid = BinaryPrimitives.ReadUInt64LittleEndian(p[504..]);
        BinaryPrimitives.WriteUInt16LittleEndian(p[498..], NdbChecksums.Signature((ulong)ib, bid));
        BinaryPrimitives.WriteUInt32LittleEndian(p[500..], NdbChecksums.Crc32(p[..496]));
    }

    private static byte[] BuiltFile(int messages = 20)
    {
        string path = NdbEditorTests.Build(messages, 1, 2);
        try
        {
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static T WithTimeout<T>(Func<T> work)
    {
        Task<T> t = Task.Run(work);
        Assert.True(t.Wait(TimeSpan.FromSeconds(30)), "the operation did not finish in 30 seconds");
        return t.Result;
    }

    private static Nid FirstFolder(PstStore store) => store.EnumerateFolders(CancellationToken.None).First(x => !x.IsSearchFolder && x.ParentFolder is not null).Nid;

    private static NdbOpenRisks Everything => NdbOpenRisks.HeaderChecksum | NdbOpenRisks.AllocationMaps | NdbOpenRisks.DensityList | NdbOpenRisks.Damage;

    [Fact]
    public void TheCheckerFindsNothingInFilesTheBuilderWroteAndFindsEachKindOfDamage()
    {
        byte[] good = BuiltFile();
        NdbChecker.Report clean = NdbChecker.Check(good);
        Assert.Empty(clean.Errors);
        Assert.True(clean.Pages > 2 && clean.Blocks > 50 && clean.Nodes > 50);

        // cbAMapFree
        byte[] a = (byte[])good.Clone();
        a[180 + 20]++;
        FixHeader(a);
        Assert.Contains(NdbChecker.Check(a).Errors, e => e.Contains("cbAMapFree", StringComparison.Ordinal));

        // a block's slot marked free in the AMap
        byte[] b = (byte[])good.Clone();
        int map = 0x4400;
        int slot = Array.FindIndex(b, map + 8, 100, x => x == 0xFF) - map;
        Assert.True(slot > 0);
        b[map + slot] = 0;
        FixPage(b, map);
        Assert.Contains(NdbChecker.Check(b).Errors, e => e.Contains("marked free", StringComparison.Ordinal));

        // a reference count lowered: find a leaf block-tree page and drop the first entry's count to 1
        byte[] c = (byte[])good.Clone();
        long bbtIb = (long)BinaryPrimitives.ReadUInt64LittleEndian(good.AsSpan(180 + 60));
        long leaf = bbtIb;
        while (c[(int)leaf + 491] != 0)
        {
            leaf = (long)BinaryPrimitives.ReadUInt64LittleEndian(c.AsSpan((int)leaf + 16));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan((int)leaf + 18), 1);
        FixPage(c, (int)leaf);
        Assert.Contains(NdbChecker.Check(c).Errors, e => e.Contains("reference count", StringComparison.Ordinal));

        // bidNextB below a block's BID
        byte[] d = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(516), 8);
        FixHeader(d);
        Assert.Contains(NdbChecker.Check(d).Errors, e => e.Contains("bidNextB", StringComparison.Ordinal));
    }

    // The first leaf entry of the block tree: its page offset, the entry's offset in the page, and the block's file offset and size.
    private static (int Page, int Entry, long Ib, int Cb, ulong Bid) FirstBlock(byte[] f)
    {
        long leaf = (long)BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(180 + 60));
        while (f[(int)leaf + 491] != 0)
        {
            leaf = (long)BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan((int)leaf + 16));
        }

        // skip internal blocks so the one chosen is a plain data block
        for (int i = 0; i < f[(int)leaf + 488]; i++)
        {
            int at = (int)leaf + (i * 24);
            ulong bid = BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(at));
            if ((bid & 2) == 0)
            {
                return ((int)leaf, at, (long)BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(at + 8)), BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(at + 16)), bid);
            }
        }

        throw new InvalidOperationException("no data block in the first leaf");
    }

    [Fact]
    public void MapsThatCallLiveSpaceFreeAreRefusedAndRebuiltOnlyWhenAllowed()
    {
        byte[] f = BuiltFile();
        (_, _, long ib, _, _) = FirstBlock(f);
        int map = 0x4400;
        int usedSlot = (int)((ib - map) / 64);
        int freeSlot = Enumerable.Range(0, 3968).First(i => i > usedSlot && (f[map + (i >> 3)] & (0x80 >> (i & 7))) == 0);

        // swap one used and one free slot: the maps' free total, and so the header check, still agree
        f[map + (usedSlot >> 3)] &= (byte)~(0x80 >> (usedSlot & 7));
        f[map + (freeSlot >> 3)] |= (byte)(0x80 >> (freeSlot & 7));
        FixPage(f, map);

        using var strict = new MemoryStream(f.ToArray());
        NdbEditRefusedException refused = Assert.Throws<NdbEditRefusedException>(() => NdbEditor.Open(strict, leaveOpen: true).Dispose());
        Assert.Contains(refused.Refusals, r => r.Reason == NdbRefusalReason.AllocationMaps && r.CanAllow);
        Assert.Equal(f, strict.ToArray());

        using var allowed = new MemoryStream(f.ToArray());
        using (PstWritableStore w = PstStore.OpenForWriting(allowed, leaveOpen: true, new PstWriteOptions { Allow = NdbOpenRisks.AllocationMaps, Timestamp = Stamp, AutoCommitRows = 0 }))
        {
            w.AddMessage(FirstFolder(w.View), new MapiMessage());
            w.Commit();
        }

        Assert.Empty(NdbChecker.Check(allowed.ToArray()).Errors);
    }

    [Fact]
    public void ABlockWhoseEntryDisagreesWithItsTrailerIsNotFreedOrWiped()
    {
        byte[] f = BuiltFile();
        (int page, int entry, long ib, int cb, ulong bid) = FirstBlock(f);

        // the tree entry claims one byte less than the block's own trailer; the rounded-up size, and so the rest of the file, is unchanged
        Assert.Equal((cb + 16 + 63) / 64, (cb - 1 + 16 + 63) / 64);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(entry + 16), (ushort)(cb - 1));
        FixPage(f, page);
        byte[] before = f.AsSpan((int)ib, cb).ToArray();

        using var ms = new MemoryStream(f.ToArray());
        using (NdbEditor ed = NdbEditor.Open(ms, leaveOpen: true))
        {
            ed.Release(new Bid(bid));
            ed.Release(new Bid(bid));
            ed.Commit();
        }

        Assert.Equal(before, ms.ToArray().AsSpan((int)ib, cb).ToArray());
    }

    [Fact]
    public void LaggingBidCountersDoNotLetNewBlocksReuseBidsInUse()
    {
        byte[] f = BuiltFile();
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(516), 0x100);     // bidNextB far below the BIDs the block tree holds
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(32), 4);          // bidNextP too
        FixHeader(f);
        Assert.NotEmpty(NdbChecker.Check(f).Errors);

        using var ms = new MemoryStream(f.ToArray());
        using (PstWritableStore w = PstStore.OpenForWriting(ms, leaveOpen: true, new PstWriteOptions { Timestamp = Stamp, AutoCommitRows = 0 }))
        {
            Nid folder = FirstFolder(w.View);
            for (int i = 0; i < 5; i++)
            {
                w.AddMessage(folder, new MapiMessage());
            }

            w.Commit();
        }

        NdbChecker.Report r = NdbChecker.Check(ms.ToArray());
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void ANewNidIsAboveTheHeaderCounterWhichIsTheLastIndexAllocated()
    {
        // MS-PST 2.2.2.6: rgnid[type] holds the last nidIndex allocated. An Outlook file uses the counter's value for the embedded message of an attachment
        // (a subnode), so a new top-level item must not start there.
        byte[] f = BuiltFile();
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(44 + (4 * (int)NidType.NormalMessage)), 0x20008);
        FixHeader(f);
        using var ms = new MemoryStream(f);
        using NdbEditor e = NdbEditor.Open(ms, leaveOpen: true);
        Nid first = e.AllocateNid(NidType.NormalMessage);
        Nid second = e.AllocateNid(NidType.NormalMessage);
        Assert.Equal(0x20009u, first.Index);
        Assert.Equal(0x2000Au, second.Index);
    }

    [Fact]
    public void ARolledBackTransactionThatGrewTheFileCutsItsWritesPastTheRecordedEnd()
    {
        byte[] f = BuiltFile(10);
        using var ms = new MemoryStream();
        ms.Write(f);
        using NdbEditor e = NdbEditor.Open(ms, leaveOpen: true);
        for (int i = 0; i < 12; i++)
        {
            DataTreeResult t = e.Blocks.WriteData(new byte[100_000]);
            e.UpsertNode(new NodeEntry(e.AllocateNid(NidType.NormalMessage), t.Root, default, default));
        }

        Assert.True(ms.Length > f.Length, "the transaction wrote past the end");
        e.Rollback();
        Assert.Equal(f.Length, ms.Length);
    }

    [Fact]
    public void ALoweredEndOfFileIsRefusedAndTheFileIsLeftAlone()
    {
        byte[] f = BuiltFile();
        long length = f.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(184), 0x10000);
        FixHeader(f);
        string path = NdbEditorTests.TempFile();
        File.WriteAllBytes(path, f);
        try
        {
            Assert.Throws<NdbEditRefusedException>(() => NdbEditor.Open(path, new NdbEditorOptions { Allow = Everything }).Dispose());
            Assert.Equal(length, new FileInfo(path).Length);
            Assert.Throws<NdbEditRefusedException>(() => NdbEditor.Open(path).Dispose());
            Assert.Equal(length, new FileInfo(path).Length);
            Assert.Equal(f, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData(1UL << 62)]
    [InlineData(0UL)]
    public void ANonsenseEndOfFileIsRefusedWithoutAnUntypedException(ulong eof)
    {
        byte[] f = BuiltFile();
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(184), eof);
        FixHeader(f);
        foreach (NdbOpenRisks allow in new[] { NdbOpenRisks.None, Everything })
        {
            using var ms = new MemoryStream();
            ms.Write(f);
            try
            {
                WithTimeout(() =>
                {
                    NdbEditor.Open(ms, leaveOpen: true, new NdbEditorOptions { Allow = allow }).Dispose();
                    return 0;
                });
            }
            catch (AggregateException e) when (e.InnerException is NdbEditRefusedException)
            {
            }
            catch (NdbEditRefusedException)
            {
            }

            Assert.Equal(f.Length, ms.Length);
        }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL << 40)]
    [InlineData(ulong.MaxValue)]
    public void ANonsenseLastMapOffsetNeitherHangsNorGrowsTheFile(ulong last)
    {
        byte[] f = BuiltFile();
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(180 + 12), last);
        f[180 + 68] = 0;
        FixHeader(f);
        using var ms = new MemoryStream();
        ms.Write(f);
        WithTimeout(() =>
        {
            try
            {
                using PstWritableStore w = PstStore.OpenForWriting(ms, leaveOpen: true, new PstWriteOptions { Allow = Everything, Timestamp = Stamp, AutoCommitRows = 0 });
                w.AddMessage(FirstFolder(w.View), new MapiMessage());
                w.Commit();
            }
            catch (NdbEditRefusedException)
            {
            }

            return 0;
        });
        Assert.True(ms.Length < f.Length + (4 << 20), $"the file grew from {f.Length} to {ms.Length}");
        Assert.DoesNotContain(NdbChecker.Check(ms.ToArray()).Errors, e => !e.Contains("allocation map", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIndexPageThatNamesItselfAsAChildCannotMakeTheEditorLoop()
    {
        byte[] f = BuiltFile(60);
        long root = (long)BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(180 + 44));
        Assert.True(f[(int)root + 491] > 0, "the node tree has an index root");
        ulong bid = BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(180 + 36));
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan((int)root + 8), bid);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan((int)root + 16), (ulong)root);
        FixPage(f, (int)root);
        foreach (NdbOpenRisks allow in new[] { NdbOpenRisks.None, Everything })
        {
            using var ms = new MemoryStream();
            ms.Write(f);
            WithTimeout(() =>
            {
                try
                {
                    using NdbEditor e = NdbEditor.Open(ms, leaveOpen: true, new NdbEditorOptions { Allow = allow });
                    for (uint n = 0x21; n < 0x3000; n += 0x20)
                    {
                        e.TryGetNode(new Nid(n), out _);
                    }
                }
                catch (NdbEditRefusedException)
                {
                }
                catch (InvalidDataException)
                {
                }

                return 0;
            });
        }
    }
}
