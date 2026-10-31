using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>One damaged file per defect kind: the reader reports the defect and does not throw.</summary>
public class NdbHostileTests
{
    private const int Max = 8192 - 16;

    // A Unicode file with a plain node, a node whose data needs an XBLOCK, and a node with subnodes.
    private static NdbTestBuilder.Result Good(NdbFormat format = NdbFormat.Unicode, TestBuild? o = null) =>
        NdbTestBuilder.Build(format,
        [
            new TestNode { Nid = 0x21, Data = NdbTests.Pattern(100, 1) },
            new TestNode { Nid = 0x8082, Data = NdbTests.Pattern((Max * 2) + 5, 3) },
            new TestNode { Nid = 0x8003, Data = NdbTests.Pattern(40, 2), Subnodes = [new TestNode { Nid = 0x671, Data = NdbTests.Pattern(9, 5) }, new TestNode { Nid = 0x692, Data = NdbTests.Pattern(30, 6) }] },
        ], o);

    // Opens, then touches everything a reader would: both trees, every data tree and subnode tree, every block, allocation, scan.
    private static HashSet<NdbDefectKind> Kinds(byte[] bytes, NdbOptions? options = null)
    {
        var kinds = new HashSet<NdbDefectKind>();
        bool ok = NdbFile.TryOpen(bytes, out NdbFile? f, out IReadOnlyList<NdbDefect> early, options);
        foreach (NdbDefect d in early) { kinds.Add(d.Kind); }
        if (!ok) { return kinds; }
        using (f)
        {
            var buf = new byte[f!.MaxBlockDataSize];
            foreach (NodeEntry n in f.Nodes)
            {
                using NdbDataTree? t = f.OpenDataTree(n.Data);
                if (t is not null) { t.Read(0, buf.AsSpan(0, (int)Math.Min(t.Length, buf.Length))); }
                if (!n.Sub.IsNull)
                {
                    SubnodeTree? st = f.OpenSubnodes(n.Sub);
                    if (st is not null)
                    {
                        foreach (SubnodeEntry se in st.Entries) { using NdbDataTree? x = f.OpenDataTree(se.Data); }
                    }
                }
            }

            foreach (BlockEntry b in f.Blocks) { f.TryReadBlock(b, buf, out _, out _); }
            f.Nodes.TryGet(new Nid(0x8082), out _);
            f.ValidateAllocation(TestContext.Current.CancellationToken);
            f.ReadDensityList();
            foreach (NdbScanItem _ in f.Scan(new NdbScanOptions { IncludeDamaged = true }, TestContext.Current.CancellationToken)) { }
            foreach (NdbDefect d in f.Defects) { kinds.Add(d.Kind); }
        }

        return kinds;
    }

    private static void Expect(NdbDefectKind kind, byte[] bytes, NdbOptions? options = null) =>
        Assert.Contains(kind, Kinds(bytes, options));

    private static byte[] Copy(NdbTestBuilder.Result r) => (byte[])r.Bytes.Clone();

    // Recomputes dwCRCPartial and dwCRCFull so a changed header field is the only thing wrong with it.
    private static void ResealHeader(byte[] f)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), NdbChecksums.Crc32(f.AsSpan(8, 471)));
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(524), NdbChecksums.Crc32(f.AsSpan(8, 516)));
    }

    // Unicode header fields (MS-PST 2.2.2.6).
    private const int RootAt = 180, NbtBid = RootAt + 36, NbtIb = RootAt + 44, BbtBid = RootAt + 52, BbtIb = RootAt + 60, FileEof = RootAt + 4, FreeAt = RootAt + 20, AMapFlag = RootAt + 68;

    private static (long Ib, BlockEntry Entry) FindBlock(NdbTestBuilder.Result r, Func<BlockEntry, bool> pick)
    {
        Assert.True(NdbFile.TryOpen(r.Bytes, out NdbFile? f, out _));
        using (f)
        {
            foreach (BlockEntry b in f!.Blocks) { if (pick(b)) { return ((long)b.Block.Ib, b); } }
        }

        throw new InvalidOperationException("no such block");
    }

    // Offset of a BBT leaf entry in the (single-page) block tree.
    private static long BbtEntryAt(NdbTestBuilder.Result r, Bid bid)
    {
        Assert.True(NdbFile.TryOpen(r.Bytes, out NdbFile? f, out _));
        using (f)
        {
            int i = 0;
            foreach (BlockEntry b in f!.Blocks) { if (b.Bid == bid) { return r.BbtPages[0] + (i * 24L); } i++; }
        }

        throw new InvalidOperationException();
    }

    private static long NbtEntryAt(NdbTestBuilder.Result r, uint nid)
    {
        Assert.True(NdbFile.TryOpen(r.Bytes, out NdbFile? f, out _));
        using (f)
        {
            int i = 0;
            foreach (NodeEntry n in f!.Nodes) { if (n.Nid.Value == nid) { return r.NbtPages[0] + (i * 32L); } i++; }
        }

        throw new InvalidOperationException();
    }

    private static int BlockTrailer(BlockEntry e) => ((e.Size + 16 + 63) / 64 * 64) - 16;

    [Fact] public void TheGoodFileIsClean() => Assert.Empty(Kinds(Good().Bytes));

    // ---- header ----

    [Fact] public void FileTooShort() => Expect(NdbDefectKind.FileTooShort, Good().Bytes[..100]);

    [Fact] public void BadMagic() { var b = Copy(Good()); b[0] = (byte)'X'; Expect(NdbDefectKind.BadMagic, b); }

    [Fact] public void UnknownClientMagic() { var b = Copy(Good()); b[8] = 0x11; ResealHeader(b); Expect(NdbDefectKind.UnknownClientMagic, b); }

    [Fact] public void UnsupportedVersion() { var b = Copy(Good()); b[10] = 5; ResealHeader(b); Expect(NdbDefectKind.UnsupportedVersion, b); }

    [Fact] public void HeaderPartialCrcMismatch() { var b = Copy(Good()); b[100] ^= 1; Expect(NdbDefectKind.HeaderPartialCrcMismatch, b); }

    [Fact] public void HeaderFullCrcMismatch() { var b = Copy(Good()); b[520] ^= 1; Expect(NdbDefectKind.HeaderFullCrcMismatch, b); }

    [Fact] public void BadSentinel() { var b = Copy(Good()); b[512] = 0; ResealHeader(b); Expect(NdbDefectKind.BadSentinel, b); }

    [Fact] public void UnknownEncryption() { var b = Copy(Good()); b[513] = 7; ResealHeader(b); Expect(NdbDefectKind.UnknownEncryption, b); }

    [Fact] public void ProtectedContent() { var b = Copy(Good()); b[513] = 0x10; ResealHeader(b); Expect(NdbDefectKind.ProtectedContent, b); }

    [Fact] public void AMapFlagInvalid() { var b = Copy(Good()); b[AMapFlag] = 0; ResealHeader(b); Expect(NdbDefectKind.AMapFlagInvalid, b); }

    [Fact] public void FileSizeMismatch() { var b = Copy(Good()).Concat(new byte[1024]).ToArray(); Expect(NdbDefectKind.FileSizeMismatch, b); }

    [Fact] public void RootOutOfRange() { var b = Copy(Good()); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(NbtIb), 1UL << 40); ResealHeader(b); Expect(NdbDefectKind.RootOutOfRange, b); }

    [Fact]
    public void AmbiguousPageSize()
    {
        // A version that does not say the page size, and a root page that matches neither trailer position.
        var r = Good();
        var b = Copy(r).Concat(new byte[8192]).ToArray();   // room to look at the root as a 4 KiB page
        b[10] = 37;
        ResealHeader(b);
        b[r.NbtPages[0] + 496] = 0;
        Expect(NdbDefectKind.AmbiguousPageSize, b);
    }

    // ---- pages ----

    [Fact] public void PageOutOfRange() { var r = Good(); var b = Copy(r); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(NbtIb), (ulong)b.Length + 4096); ResealHeader(b); Expect(NdbDefectKind.PageOutOfRange, b); }

    [Fact] public void PageTruncated() { var r = Good(); Expect(NdbDefectKind.PageTruncated, r.Bytes[..(int)(r.NbtPages[0] + 100)]); }

    [Fact] public void PageTypeMismatch() { var r = Good(); var b = Copy(r); b[r.NbtPages[0] + 496] = 0x80; Expect(NdbDefectKind.PageTypeMismatch, b); }

    [Fact] public void PageTypeRepeatMismatch() { var r = Good(); var b = Copy(r); b[r.NbtPages[0] + 497] = 0x55; Expect(NdbDefectKind.PageTypeRepeatMismatch, b); }

    [Fact] public void PageSignatureMismatch() { var r = Good(); var b = Copy(r); b[r.NbtPages[0] + 498] ^= 0xFF; Expect(NdbDefectKind.PageSignatureMismatch, b); }

    [Fact] public void PageCrcMismatch() { var r = Good(); var b = Copy(r); b[r.NbtPages[0] + 10] ^= 1; Expect(NdbDefectKind.PageCrcMismatch, b); }

    [Fact] public void PageBidMismatch() { var r = Good(); var b = Copy(r); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(NbtBid), 0x1234); ResealHeader(b); Expect(NdbDefectKind.PageBidMismatch, b); }

    // ---- B-tree geometry ----

    private static void SetFooter(byte[] b, long page, int offset, byte value) => b[page + 488 + offset] = value;

    [Fact] public void BTreeBadGeometry() { var r = Good(); var b = Copy(r); SetFooter(b, r.NbtPages[0], 2, 4); Expect(NdbDefectKind.BTreeBadGeometry, b); }

    [Fact]
    public void BTreeLevelMismatch()
    {
        var r = NdbTestBuilder.Build(NdbFormat.Unicode, Enumerable.Range(1, 12).Select(i => new TestNode { Nid = (uint)i * 32 + 4, Data = [1] }).ToList(), new TestBuild { LeafCapacity = 3 });
        var b = Copy(r);
        SetFooter(b, r.NbtPages[0], 3, 5);   // a leaf that claims to be level 5
        Expect(NdbDefectKind.BTreeLevelMismatch, b);
    }

    [Fact] public void BTreeTooDeep() { var r = Good(); var b = Copy(r); SetFooter(b, r.NbtPages[0], 3, 9); Expect(NdbDefectKind.BTreeTooDeep, b); }

    [Fact]
    public void BTreeKeyOrder()
    {
        var r = Good();
        var b = Copy(r);
        // Swap the first two NBT entries' keys so the leaf is no longer ascending.
        long p = r.NbtPages[0];
        for (int i = 0; i < 8; i++) { (b[p + i], b[p + 32 + i]) = (b[p + 32 + i], b[p + i]); }
        Expect(NdbDefectKind.BTreeKeyOrder, b);
    }

    [Fact]
    public void BTreeTooManyPages()
    {
        // A root whose entries all point at the same child, which in turn points at the same leaf 20 times: more visits than the file has pages.
        var r = NdbTestBuilder.Build(NdbFormat.Unicode, Enumerable.Range(1, 6).Select(i => new TestNode { Nid = (uint)i * 32 + 4, Data = [1] }).ToList(), new TestBuild { LeafCapacity = 2 });
        var b = Copy(r);
        long leaf = r.NbtPages[0];
        long mid = r.NbtPages[^1];
        var leafTrailer = b.AsSpan((int)leaf + 496, 16);
        ulong leafBid = BinaryPrimitives.ReadUInt64LittleEndian(leafTrailer[8..]);
        // Rewrite the root (level >= 1) into 20 entries of (key, leafBid, leafIb) and make it level 1 over leaves.
        long root = mid;
        for (int i = 0; i < 20; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)root + (i * 24)), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)root + (i * 24) + 8), leafBid);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)root + (i * 24) + 16), (ulong)leaf);
        }

        b[root + 488] = 20; b[root + 490] = 24;
        Assert.Contains(NdbDefectKind.BTreeTooManyPages, Kinds(RepeatTwoLevels(r, b)));
    }

    // Builds the 20 x 20 shape on top of the index page: the level-1 page's twenty entries each point at the one leaf, and the
    // root's twenty entries each point at that level-1 page.
    private static byte[] RepeatTwoLevels(NdbTestBuilder.Result r, byte[] b)
    {
        long mid = r.NbtPages[^1];
        long leaf = r.NbtPages[0];
        ulong midBid = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)mid + 496 + 8));
        // Make the root header point at a copy: reuse the leaf page's slot as the level-1 page.
        var level1 = b.AsSpan((int)mid, 512).ToArray();
        level1.CopyTo(b.AsSpan((int)r.NbtPages[1], 512));
        for (int i = 0; i < 20; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)mid + (i * 24) + 8), BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)r.NbtPages[1] + 496 + 8)));
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)mid + (i * 24) + 16), (ulong)r.NbtPages[1]);
        }

        b[mid + 488 + 3] = 2;
        b[r.NbtPages[1] + 488 + 3] = 1;
        _ = midBid; _ = leaf;
        return b;
    }

    // ---- blocks ----

    [Fact]
    public void BlockOutOfRange()
    {
        var r = Good(); var b = Copy(r);
        (long _, BlockEntry e) = FindBlock(r, x => !x.Bid.IsInternal);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)BbtEntryAt(r, e.Bid) + 8), 1UL << 40);
        Expect(NdbDefectKind.BlockOutOfRange, b);
    }

    [Fact]
    public void BlockTruncated()
    {
        var r = Good();
        var b = Copy(r);
        (long _, BlockEntry e) = FindBlock(r, x => !x.Bid.IsInternal);
        // The BBT says the block starts 100 bytes before the end of the file, so it runs off the end.
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)BbtEntryAt(r, e.Bid) + 8), (ulong)b.Length - 100);
        Expect(NdbDefectKind.BlockTruncated, b);
    }

    [Fact]
    public void BlockTooLarge()
    {
        var r = Good(); var b = Copy(r);
        (long _, BlockEntry e) = FindBlock(r, x => !x.Bid.IsInternal);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)BbtEntryAt(r, e.Bid) + 16), 0xFFFF);
        Expect(NdbDefectKind.BlockTooLarge, b);
    }

    [Fact]
    public void BlockSizeMismatch()
    {
        var r = Good(); var b = Copy(r);
        (long _, BlockEntry e) = FindBlock(r, x => !x.Bid.IsInternal && x.Size == 100);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)BbtEntryAt(r, e.Bid) + 16), 90);
        Expect(NdbDefectKind.BlockSizeMismatch, b);
    }

    [Fact]
    public void BlockSignatureMismatch()
    {
        var r = Good(); var b = Copy(r);
        (long ib, BlockEntry e) = FindBlock(r, x => !x.Bid.IsInternal && x.Size == 100);
        b[ib + BlockTrailer(e) + 2] ^= 0xFF;
        Expect(NdbDefectKind.BlockSignatureMismatch, b);
    }

    [Fact]
    public void BlockCrcMismatch()
    {
        var r = Good(); var b = Copy(r);
        (long ib, BlockEntry _) = FindBlock(r, x => !x.Bid.IsInternal && x.Size == 100);
        b[ib + 3] ^= 1;
        Expect(NdbDefectKind.BlockCrcMismatch, b);
    }

    [Fact]
    public void BlockBidMismatch()
    {
        var r = Good(); var b = Copy(r);
        (long _, BlockEntry e) = FindBlock(r, x => !x.Bid.IsInternal && x.Size == 100);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)BbtEntryAt(r, e.Bid)), 0x7770);
        // The BBT entry is now out of key order too; the block is still reached through the NBT's reference only if the BID matches,
        // so point the node at the new BID.
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)NbtEntryAt(r, 0x21) + 8), 0x7770);
        Expect(NdbDefectKind.BlockBidMismatch, b);
    }

    [Fact]
    public void BlockNotFound()
    {
        var r = Good(); var b = Copy(r);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)NbtEntryAt(r, 0x21) + 8), 0x7770);
        Expect(NdbDefectKind.BlockNotFound, b);
    }

    [Fact]
    public void BlockDecompressionFailed()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();
        var r = NdbTestBuilder.Build(NdbFormat.Unicode4K, [new TestNode { Nid = 0x21, Data = data }], new TestBuild { Compress = true });
        var b = Copy(r);
        long ib = r.BlockOffsets.Values.Min();
        for (int i = 2; i < 12; i++) { b[ib + i] = 0xFF; }   // garbage after the zlib header
        Expect(NdbDefectKind.BlockDecompressionFailed, b);
    }

    [Fact]
    public void AmbiguousCompressedEncodedBlock()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();
        var r = NdbTestBuilder.Build(NdbFormat.Unicode4K, [new TestNode { Nid = 0x21, Data = data }], new TestBuild { Compress = true });
        var b = Copy(r);
        b[513] = 1;
        ResealHeader(b);
        Expect(NdbDefectKind.AmbiguousCompressedEncodedBlock, b);
    }

    // ---- data trees and subnode trees ----

    private static (NdbTestBuilder.Result R, byte[] B, long Ib) XBlock()
    {
        var r = Good();
        var b = Copy(r);
        (long ib, BlockEntry _) = FindBlock(r, x => x.Bid.IsInternal && x.Size == 8 + (2 * 8) + 8);
        return (r, b, ib);
    }

    [Fact] public void DataTreeBadHeader() { var (_, b, ib) = XBlock(); b[ib] = 9; Expect(NdbDefectKind.DataTreeBadHeader, b); }

    [Fact] public void DataTreeTooDeep() { var (_, b, ib) = XBlock(); b[ib + 1] = 3; Expect(NdbDefectKind.DataTreeTooDeep, b); }

    [Fact] public void DataTreeSizeMismatch() { var (_, b, ib) = XBlock(); b[ib + 4] ^= 0x40; Expect(NdbDefectKind.DataTreeSizeMismatch, b); }

    [Fact]
    public void DataTreeChildMissing()
    {
        var (_, b, ib) = XBlock();
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)ib + 8), 0x7770);
        Expect(NdbDefectKind.DataTreeChildMissing, b);
    }

    [Fact] public void DataTreeTooLarge() => Expect(NdbDefectKind.DataTreeTooLarge, Good().Bytes, new NdbOptions { MaxDataTreeLeaves = 1 });

    private static (byte[] B, long Ib) SlBlock()
    {
        var r = Good();
        var b = Copy(r);
        (long ib, BlockEntry _) = FindBlock(r, x => x.Bid.IsInternal && x.Size == 8 + (2 * 24));
        return (b, ib);
    }

    [Fact] public void SubnodeBadHeader() { var (b, ib) = SlBlock(); b[ib] = 9; Expect(NdbDefectKind.SubnodeBadHeader, b); }

    [Fact] public void SubnodeTooLarge() => Expect(NdbDefectKind.SubnodeTooLarge, Good().Bytes, new NdbOptions { MaxSubnodeEntries = 1 });

    [Fact]
    public void SubnodeChildMissing()
    {
        var (b, ib) = SlBlock();
        // Turn the SLBLOCK into a SIBLOCK (cLevel 1) with one (nid, bid) entry whose child is not in the block tree.
        b[ib + 1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)ib + 2), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)ib + 16), 0x7770);
        Expect(NdbDefectKind.SubnodeChildMissing, b);
    }

    // ---- allocation ----

    [Fact] public void AllocationPageInvalid() { var r = Good(); var b = Copy(r); b[r.AMapOffset + 3] ^= 0xFF; Expect(NdbDefectKind.AllocationPageInvalid, b); }

    [Fact] public void AMapFreeSpaceMismatch() { var b = Copy(Good()); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(FreeAt), 12345); ResealHeader(b); Expect(NdbDefectKind.AMapFreeSpaceMismatch, b); }

    [Fact] public void DensityListInvalid() { var b = Copy(Good()); b[0x4200 + 1] = 255; Expect(NdbDefectKind.DensityListInvalid, b); }

    [Fact]
    public void DefectLimitReached()
    {
        var r = Good();
        var b = Copy(r);
        for (int i = 0; i < 40; i++) { b[r.NbtPages[0] + 1 + i] ^= 0xFF; }
        Expect(NdbDefectKind.DefectLimitReached, b, new NdbOptions { MaxDefects = 3 });
        // And the damaged file never throws.
        _ = Kinds(b);
    }

    [Fact]
    public void EveryDefectKindHasATest()
    {
        var tested = typeof(NdbHostileTests).GetMethods().Select(m => m.Name).ToHashSet();
        var missing = Enum.GetValues<NdbDefectKind>().Where(k => !tested.Contains(k.ToString())).ToList();
        Assert.Empty(missing);
    }

    [Theory, MemberData(nameof(NdbTests.Formats), MemberType = typeof(NdbTests))]
    public void RandomDamageNeverThrows(NdbFormat format)
    {
        var r = Good(format);
        var rng = new Random(77);
        for (int round = 0; round < 300; round++)
        {
            var b = Copy(r);
            int hits = 1 + rng.Next(6);
            for (int i = 0; i < hits; i++) { b[rng.Next(b.Length)] = (byte)rng.Next(256); }
            if (round % 5 == 0) { b = b[..rng.Next(b.Length)]; }
            _ = Kinds(b);
        }
    }
}

public class NdbFuzzRegressions
{
    // Found by the fuzzer (recipes in testdata/fuzz, replayed by `fuzz/run.sh replay ndb`): a changed wVer made the
    // 8-byte header fields hold ANSI bytes, so ibAMapLast was enormous and ValidateAllocation tried to read billions of AMaps.
    [Fact]
    public void HugeAMapLastDoesNotHang()
    {
        byte[] file = NdbTestBuilder.Build(NdbFormat.Ansi, [new TestNode { Nid = 0x21, Data = [1, 2, 3] }]).Bytes;
        file[10] = 0xFF;
        Assert.True(NdbFile.TryOpen(file, out NdbFile? f, out _));
        using (f)
        {
            Assert.True(f!.Header.Root.AMapLast > (ulong)file.Length);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            NdbAllocationSummary a = f.ValidateAllocation(TestContext.Current.CancellationToken);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
            Assert.True(a.AMapCount <= 2);
            Assert.Contains(f.Defects, d => d.Kind == NdbDefectKind.AllocationPageInvalid);
        }
    }
}
