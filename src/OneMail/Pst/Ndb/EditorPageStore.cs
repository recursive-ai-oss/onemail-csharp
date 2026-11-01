using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// The B-tree pages of one transaction (MS-PST 2.2.2.7.7). A page of the committed tree is never written: changing it makes a copy in new space (MS-PST
/// 2.6.1.1.8), and the copy is kept in memory, and changed freely, until the commit writes it out. A page made in the transaction is its own.
/// </summary>
internal sealed class EditorPageStore(EditorIo io, EditorMaps maps, ulong nextPageBid)
{
    private sealed class Dirty(byte[] page, ulong bid, byte ptype)
    {
        public readonly byte[] Page = page;
        public readonly ulong Bid = bid;
        public readonly byte Ptype = ptype;
    }

    private readonly Dictionary<long, Dirty> _dirty = [];

    public ulong NextPageBid { get; private set; } = nextPageBid;

    public ulong TakePageBid() => NextPageBid++;

    /// <summary>Makes sure the next page BID is at least <paramref name="minimum"/>.</summary>
    public void RaiseNextBid(ulong minimum) => NextPageBid = Math.Max(NextPageBid, minimum);

    public int DirtyCount => _dirty.Count;

    public bool IsOwned(long ib) => _dirty.ContainsKey(ib);

    /// <summary>Reads a page. A page made in this transaction comes back as the live buffer; any other is a fresh copy, checked against its trailer.</summary>
    public byte[] Read(Bref at, byte ptype)
    {
        long ib = (long)at.Ib;
        if (_dirty.TryGetValue(ib, out Dirty? d))
        {
            return d.Page;
        }

        var page = new byte[EditorMaps.PageSize];
        if (io.Read(ib, page) != page.Length)
        {
            throw new InvalidDataException($"the B-tree page at 0x{ib:x} is outside the file");
        }

        if (page[496] != ptype || page[497] != ptype
            || BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(500)) != NdbChecksums.Crc32(page.AsSpan(0, 496))
            || BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(504)) != at.Bid.Value)
        {
            throw new InvalidDataException($"the B-tree page at 0x{ib:x} fails its trailer checks");
        }

        // MS-PST 2.2.2.7.7.1: BTENTRY and BBTENTRY are 24 bytes, NBTENTRY 32, and the entries fit in the 488 bytes before the footer. A page whose footer says
        // otherwise would send the editor's index arithmetic outside the page.
        int stride = ptype == 0x81 && page[491] == 0 ? 32 : 24;
        if (page[490] != stride || page[488] > 488 / stride || page[491] > 8)
        {
            throw new InvalidDataException($"the B-tree page at 0x{ib:x} has an impossible footer (cEnt {page[488]}, cbEnt {page[490]}, cLevel {page[491]})");
        }

        return page;
    }

    /// <summary>Makes a new, empty page of the given shape.</summary>
    public (Bref Ref, byte[] Page) New(byte ptype, int level, int stride, int max)
    {
        long ib = maps.Allocate(EditorMaps.PageSize, EditorMaps.PageSize / EditorMaps.Slot);
        ulong bid = NextPageBid++;
        var page = new byte[EditorMaps.PageSize];
        page[489] = (byte)max;
        page[490] = (byte)stride;
        page[491] = (byte)level;
        _dirty[ib] = new Dirty(page, bid, ptype);
        return (new Bref(new Bid(bid), (ulong)ib), page);
    }

    /// <summary>Returns the page for changing: itself if the transaction made it, otherwise a copy in new space with the old page queued for freeing.</summary>
    public (Bref Ref, byte[] Page) Own(Bref at, byte[] page, byte ptype)
    {
        if (_dirty.TryGetValue((long)at.Ib, out Dirty? d))
        {
            return (at, d.Page);
        }

        long ib = maps.Allocate(EditorMaps.PageSize, EditorMaps.PageSize / EditorMaps.Slot);
        ulong bid = NextPageBid++;
        var copy = (byte[])page.Clone();
        _dirty[ib] = new Dirty(copy, bid, ptype);
        Free(at);
        return (new Bref(new Bid(bid), (ulong)ib), copy);
    }

    /// <summary>Frees a page: at once if the transaction made it, otherwise when the commit has switched to the new tree.</summary>
    public void Free(Bref at)
    {
        long ib = (long)at.Ib;
        _dirty.Remove(ib);
        maps.Free(ib, EditorMaps.PageSize);
    }

    /// <summary>Writes every page the transaction made, each with its trailer (MS-PST 2.2.2.7.1: type twice, signature, CRC, BID).</summary>
    public void Flush()
    {
        foreach (KeyValuePair<long, Dirty> e in _dirty)
        {
            byte[] page = e.Value.Page;
            byte ptype = e.Value.Ptype;
            page[496] = ptype;
            page[497] = ptype;
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(498), NdbChecksums.Signature((ulong)e.Key, e.Value.Bid));
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(500), NdbChecksums.Crc32(page.AsSpan(0, 496)));
            BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(504), e.Value.Bid);
            io.Write(e.Key, page);
        }
    }

    public void Clear() => _dirty.Clear();
}
