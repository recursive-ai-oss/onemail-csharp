using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>
/// Damages and edits PST files for the recovery tests and the damaged-fixture script: header and root pages zeroed, random corruption, truncation,
/// allocation maps and density list zeroed, and (for the forensic fixtures) entries removed from the node and block trees the way a deleting writer
/// leaves them, with an older copy of the leaf page kept or not. Works on any generation for the damage classes; the tree edits are for 512-byte
/// pages (Unicode and ANSI trailers differ, so only Unicode is supported). Public API only, so the oracle tool can compile it.
/// </summary>
public static class PstDamage
{
    private const int Page = 512;

    private static (NdbFormat Format, int PageSize, long FirstAMap, long Interval, long Density, NdbHeader Header) Layout(Stream f)
    {
        byte[] h = new byte[564];
        f.Position = 0;
        int n = f.Read(h, 0, h.Length);
        var early = new List<NdbDefect>();
        if (!NdbHeader.TryParse(h.AsSpan(0, n), out NdbHeader? header, early) || header is null)
        {
            throw new InvalidDataException("header unreadable");
        }

        return header.Format switch
        {
            NdbFormat.Unicode4K => (header.Format, 4096, 0x22000, 4072L * 8 * 512, 0x21000, header),
            _ => (header.Format, 512, 0x4400, 253_952, 0x4200, header),
        };
    }

    /// <summary>Zeroes the header (the first 564 bytes).</summary>
    public static void ZeroHeader(Stream f)
    {
        f.Position = 0;
        f.Write(new byte[564]);
    }

    /// <summary>Zeroes the root pages of the node and block trees.</summary>
    public static void ZeroRoots(Stream f)
    {
        var l = Layout(f);
        foreach (ulong ib in new[] { l.Header.Root.NodeBTree.Ib, l.Header.Root.BlockBTree.Ib })
        {
            f.Position = (long)ib;
            f.Write(new byte[l.PageSize]);
        }
    }

    /// <summary>Zeroes the allocation map pages.</summary>
    public static void ZeroAllocationMaps(Stream f)
    {
        var l = Layout(f);
        for (long at = l.FirstAMap; at + l.PageSize <= f.Length; at += l.Interval)
        {
            f.Position = at;
            f.Write(new byte[l.PageSize]);
        }
    }

    /// <summary>Zeroes the density list.</summary>
    public static void ZeroDensityList(Stream f)
    {
        var l = Layout(f);
        f.Position = l.Density;
        f.Write(new byte[l.PageSize]);
    }

    /// <summary>
    /// Overwrites 16 random bytes of a random fraction of the page-sized units after the allocation area.
    /// </summary>
    /// <returns>The number of units damaged.</returns>
    public static int Corrupt(Stream f, double rate, int seed)
    {
        var l = Layout(f);
        var rng = new Random(seed);
        int hit = 0;
        long start = l.Density;
        byte[] noise = new byte[16];
        for (long at = start; at + l.PageSize <= f.Length; at += l.PageSize)
        {
            if (rng.NextDouble() >= rate)
            {
                continue;
            }

            rng.NextBytes(noise);
            // Inside the unit, at a random place (a trailer or a body byte alike).
            long where = at + rng.Next(0, l.PageSize - noise.Length);
            f.Position = where;
            f.Write(noise);
            hit++;
        }

        return hit;
    }

    /// <summary>Cuts the file to the given fraction of its length, rounded down to 64 bytes.</summary>
    public static void Truncate(Stream f, double keep) => f.SetLength((long)(f.Length * keep) / 64 * 64);

    // ---- editing the trees (forensic fixtures) ----

    private static void SealPage(byte[] page, ulong ib, ulong bid)
    {
        int t = Page - 16;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(t + 2), NdbChecksums.Signature(ib, bid));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(t + 4), NdbChecksums.Crc32(page.AsSpan(0, t)));
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(t + 8), bid);
    }

    /// <summary>
    /// Removes entries from the leaf pages of the node tree (by NID) and of the block tree (by BID) of a Unicode file, in place. With
    /// <paramref name="keepStalePages"/> each edited leaf page is first copied to the end of the file under an older BID, the way a writer that
    /// never overwrites pages leaves the previous version behind.
    /// </summary>
    /// <returns>The number of entries removed.</returns>
    public static int RemoveFromTrees(Stream f, IReadOnlyCollection<uint> nids, IReadOnlyCollection<ulong> bids, bool keepStalePages)
    {
        var l = Layout(f);
        if (l.Format != NdbFormat.Unicode)
        {
            throw new NotSupportedException("tree edits are for 512-byte Unicode pages");
        }

        int removed = 0;
        byte[] page = new byte[Page];
        long length = f.Length;
        var append = new List<(long Ib, byte[] Page)>();
        for (long at = l.Density + Page; at + Page <= length; at += Page)
        {
            f.Position = at;
            if (f.Read(page, 0, Page) != Page)
            {
                break;
            }

            byte type = page[Page - 16];
            if (type is not (0x80 or 0x81) || page[Page - 15] != type)
            {
                continue;
            }

            // MS-PST 2.2.2.7.7.1 BTPAGE: cEnt, cEntMax, cbEnt, cLevel at 488.
            int cEnt = page[488], cbEnt = page[490], cLevel = page[491];
            if (cLevel != 0 || cbEnt == 0 || cEnt * cbEnt > 488)
            {
                continue;
            }

            bool nbt = type == 0x81;
            var drop = new List<int>();
            for (int i = 0; i < cEnt; i++)
            {
                ulong key = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(i * cbEnt));
                if (nbt ? nids.Contains((uint)key) : bids.Contains(key & ~1UL))
                {
                    drop.Add(i);
                }
            }

            if (drop.Count == 0)
            {
                continue;
            }

            ulong bid = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(Page - 8));
            if (keepStalePages)
            {
                byte[] copy = (byte[])page.Clone();
                long newIb = length + (append.Count * Page);
                SealPage(copy, (ulong)newIb, bid - 4);
                append.Add((newIb, copy));
            }

            int w = 0;
            for (int i = 0; i < cEnt; i++)
            {
                if (drop.Contains(i))
                {
                    continue;
                }

                if (w != i)
                {
                    Buffer.BlockCopy(page, i * cbEnt, page, w * cbEnt, cbEnt);
                }

                w++;
            }

            Array.Clear(page, w * cbEnt, (cEnt - w) * cbEnt);
            page[488] = (byte)w;
            SealPage(page, (ulong)at, bid);
            f.Position = at;
            f.Write(page, 0, Page);
            removed += drop.Count;
        }

        foreach ((long ib, byte[] p) in append)
        {
            f.Position = ib;
            f.Write(p, 0, Page);
        }

        return removed;
    }
}
