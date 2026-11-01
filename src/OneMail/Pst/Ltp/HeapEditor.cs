using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// Allocates, frees and rewrites allocations in an existing heap node (MS-PST 2.3.1, 2.6.2.1) block by block: only the heap blocks that change are written again, and
/// the data tree over them is rewritten around the unchanged ones (<see cref="NdbDataTreeEditor"/>).
/// </summary>
/// <remarks>
/// A freed allocation moves the later ones in its block up, so the block stays packed (2.6.2.1.3); its slot in the page map stays, with no bytes, and a later allocation
/// in that block may take the slot again, so a HID never changes meaning while it is in use. Fill levels (2.3.1.2) are brought up to date for every block that changed.
/// Anything in the heap that is not laid out as this layer writes it makes <see cref="TryOpen"/> return null, and the caller rewrites the table whole.
/// </remarks>
internal sealed class HeapEditor : IDisposable
{
    private const int Capacity = NdbBlockBuilder.MaxBlockData;
    private const int HnhdrSize = 12;
    private const int BitmapHeaderSize = 66;
    private const int PageHeaderSize = 2;
    private const int MaxSlots = HeapNodeBuilder.MaxAllocationsPerBlock;

    private sealed class Page
    {
        public readonly byte[] Data = new byte[Capacity];
        public readonly List<int> Starts = [];   // rgibAlloc without its final entry
        public int Used;                          // rgibAlloc[cAlloc]: the end of the last allocation
        public int Free;                          // cFree
        public bool Dirty;
    }

    private readonly NdbFile _file;
    private readonly NdbDataTree _tree;
    private readonly NdbEditor _editor;
    private readonly Dictionary<int, Page> _pages = [];
    private int _pageCount;

    private HeapEditor(NdbEditor editor, NdbFile file, NdbDataTree tree)
    {
        _editor = editor;
        _file = file;
        _tree = tree;
        _pageCount = tree.LeafCount;
    }

    /// <summary>The user root of the heap (HNHDR.hidUserRoot).</summary>
    public Hid UserRoot { get; private set; }

    /// <summary>The heap's client signature (bClientSig).</summary>
    public byte ClientSignature { get; private set; }

    /// <summary>True when a block was changed or added.</summary>
    public bool Changed { get; private set; }

    /// <summary>Opens the heap whose data is <paramref name="data"/>; null when it is unreadable or not laid out the way this layer writes.</summary>
    public static HeapEditor? TryOpen(NdbEditor editor, NdbFile file, Bid data)
    {
        NdbDataTree? tree = data.IsNull ? null : file.OpenDataTree(data);
        if (tree is null || tree.LeafCount == 0 || tree.LeafCount > HeapNodeBuilder.MaxBlocks)
        {
            tree?.Dispose();
            return null;
        }

        var h = new HeapEditor(editor, file, tree);
        Page? p0 = h.Load(0);
        if (p0 is null || p0.Data[2] != 0xEC)
        {
            h.Dispose();
            return null;
        }

        h.ClientSignature = p0.Data[3];
        h.UserRoot = new Hid(BinaryPrimitives.ReadUInt32LittleEndian(p0.Data.AsSpan(4)));
        return h;
    }

    private static int HeaderSize(int block) => block == 0 ? HnhdrSize : block >= 8 && (block - 8) % 128 == 0 ? BitmapHeaderSize : PageHeaderSize;

    private Page? Load(int index)
    {
        if (_pages.TryGetValue(index, out Page? cached))
        {
            return cached;
        }

        if ((uint)index >= (uint)_tree.LeafCount)
        {
            return null;
        }

        var p = new Page();
        if (!_file.TryReadBlock(_tree.GetLeaf(index), p.Data, out int length, out _))
        {
            return null;
        }

        // MS-PST 2.3.1.5: ibHnpm locates the HNPAGEMAP, whose cAlloc + 1 offsets must lie between the header and the map.
        int header = HeaderSize(index);
        if (length < header + 4)
        {
            return null;
        }

        int mapAt = BinaryPrimitives.ReadUInt16LittleEndian(p.Data);
        if (mapAt < header || mapAt + 4 > length)
        {
            return null;
        }

        int cAlloc = BinaryPrimitives.ReadUInt16LittleEndian(p.Data.AsSpan(mapAt));
        p.Free = BinaryPrimitives.ReadUInt16LittleEndian(p.Data.AsSpan(mapAt + 2));
        if (cAlloc > MaxSlots || mapAt + 4 + (2 * (cAlloc + 1)) > length)
        {
            return null;
        }

        int previous = header;
        for (int i = 0; i <= cAlloc; i++)
        {
            int at = BinaryPrimitives.ReadUInt16LittleEndian(p.Data.AsSpan(mapAt + 4 + (2 * i)));
            if (at < previous || at > mapAt)
            {
                return null;
            }

            previous = at;
            if (i < cAlloc)
            {
                p.Starts.Add(at);
            }
            else
            {
                p.Used = at;
            }
        }

        // The bytes after the allocations (the alignment byte and the old map) are not data; they are cleared so a changed block carries nothing stale.
        p.Data.AsSpan(p.Used).Clear();
        _pages[index] = p;
        return p;
    }

    private Page Get(int index) => Load(index) ?? throw new HeapEditUnsupported($"heap block {index} cannot be read");

    private static int Size(Page p, int slot) => (slot + 1 < p.Starts.Count ? p.Starts[slot + 1] : p.Used) - p.Starts[slot];

    private (Page Page, int Slot) Locate(Hid hid)
    {
        int slot = hid.Index - 1;
        if (hid.Type != 0 || slot < 0)
        {
            throw new HeapEditUnsupported($"HID {hid} is not a heap ID");
        }

        Page p = Get(hid.BlockIndex);
        if (slot >= p.Starts.Count)
        {
            throw new HeapEditUnsupported($"HID {hid} is past the allocations of its block");
        }

        return (p, slot);
    }

    /// <summary>The bytes of an allocation, to read.</summary>
    public ReadOnlySpan<byte> Read(Hid hid)
    {
        (Page p, int slot) = Locate(hid);
        int size = Size(p, slot);
        if (size == 0)
        {
            throw new HeapEditUnsupported($"HID {hid} is a freed allocation");
        }

        return p.Data.AsSpan(p.Starts[slot], size);
    }

    /// <summary>The bytes of an allocation, to change in place; the block is written again.</summary>
    public Span<byte> Write(Hid hid)
    {
        (Page p, int slot) = Locate(hid);
        int size = Size(p, slot);
        if (size == 0)
        {
            throw new HeapEditUnsupported($"HID {hid} is a freed allocation");
        }

        p.Dirty = true;
        Changed = true;
        return p.Data.AsSpan(p.Starts[slot], size);
    }

    /// <summary>Frees an allocation and moves the later ones in its block up.</summary>
    public void Free(Hid hid)
    {
        (Page p, int slot) = Locate(hid);
        int size = Size(p, slot);
        if (size == 0)
        {
            throw new HeapEditUnsupported($"HID {hid} is already free");
        }

        int start = p.Starts[slot];
        Buffer.BlockCopy(p.Data, start + size, p.Data, start, p.Used - (start + size));
        p.Data.AsSpan(p.Used - size, size).Clear();
        for (int i = slot + 1; i < p.Starts.Count; i++)
        {
            p.Starts[i] -= size;
        }

        p.Used -= size;
        p.Free++;
        p.Dirty = true;
        Changed = true;
    }

    // The slot a new allocation can take over: one with no bytes, but only while every empty slot is known to be a freed one (cFree counts them).
    private static int FreeSlot(Page p)
    {
        if (p.Free == 0)
        {
            return -1;
        }

        int first = -1, empty = 0;
        for (int i = 0; i < p.Starts.Count; i++)
        {
            if (Size(p, i) == 0)
            {
                empty++;
                if (first < 0)
                {
                    first = i;
                }
            }
        }

        return empty <= p.Free ? first : -1;
    }

    private static bool Fits(Page p, int size, int reuse)
    {
        int slots = p.Starts.Count + (reuse >= 0 ? 0 : 1);
        if (reuse < 0 && p.Starts.Count >= MaxSlots)
        {
            return false;
        }

        return ((p.Used + size + 1) & ~1) + 4 + (2 * (slots + 1)) <= Capacity;
    }

    /// <summary>Allocates <paramref name="size"/> bytes, in block <paramref name="preferred"/> when they fit there.</summary>
    /// <returns>The new HID; its bytes are zero.</returns>
    public Hid Allocate(int size, int preferred = -1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, HeapNodeBuilder.MaxAllocation);
        if (preferred >= 0 && preferred < _pageCount && TryAllocate(preferred, size, out Hid hid))
        {
            return hid;
        }

        if (_pageCount > 0 && TryAllocate(_pageCount - 1, size, out hid))
        {
            return hid;
        }

        // Blocks already loaded know exactly what they have free; the rest are judged by the fill levels their header blocks carry.
        foreach (int k in new List<int>(_pages.Keys))
        {
            if (TryAllocate(k, size, out hid))
            {
                return hid;
            }
        }

        for (int k = 0; k < _tree.LeafCount; k++)
        {
            if (!_pages.ContainsKey(k) && FreeAtLeast(k) >= size + 4 && TryAllocate(k, size, out hid))
            {
                return hid;
            }
        }

        if (_pageCount >= HeapNodeBuilder.MaxBlocks)
        {
            throw new HeapEditUnsupported("the heap has the most blocks it can");
        }

        var np = new Page { Used = HeaderSize(_pageCount), Dirty = true };
        int index = _pageCount++;
        _pages[index] = np;
        Changed = true;
        if (!TryAllocate(index, size, out hid))
        {
            throw new HeapEditUnsupported("an allocation does not fit an empty block");
        }

        return hid;
    }

    private bool TryAllocate(int index, int size, out Hid hid)
    {
        hid = default;
        Page? p = Load(index);
        if (p is null)
        {
            return false;
        }

        int reuse = FreeSlot(p);
        if (!Fits(p, size, reuse))
        {
            return false;
        }

        int slot;
        if (reuse >= 0)
        {
            slot = reuse;
            int at = p.Starts[slot];
            Buffer.BlockCopy(p.Data, at, p.Data, at + size, p.Used - at);
            p.Data.AsSpan(at, size).Clear();
            for (int i = slot + 1; i < p.Starts.Count; i++)
            {
                p.Starts[i] += size;
            }

            p.Free--;
        }
        else
        {
            slot = p.Starts.Count;
            p.Starts.Add(p.Used);
        }

        p.Used += size;
        p.Dirty = true;
        Changed = true;
        hid = new Hid((uint)((index << 16) | ((slot + 1) << 5)));
        return true;
    }

    // The lower bound of the free bytes the fill level of block k promises (MS-PST 2.3.1.2); 0 when the header block cannot be read.
    private int FreeAtLeast(int k)
    {
        int holder = k < 8 ? 0 : 8 + ((k - 8) / 128 * 128);
        int within = k - holder;
        Page? h = Load(holder);
        if (h is null)
        {
            return 0;
        }

        int levelsAt = holder == 0 ? 8 : 2;
        int level = (h.Data[levelsAt + (within >> 1)] >> ((within & 1) * 4)) & 0xF;
        return LevelFloor[level];
    }

    private static readonly int[] LevelFloor = [3584, 2560, 2048, 1792, 1536, 1280, 1024, 768, 512, 256, 128, 64, 32, 16, 8, 0];

    private static int FillLevel(Page p)
    {
        int free = Capacity - (((p.Used + 1) & ~1) + 4 + (2 * (p.Starts.Count + 1)));
        for (int level = 0; level < 15; level++)
        {
            if (free >= LevelFloor[level])
            {
                return level;
            }
        }

        return 15;
    }

    /// <summary>Writes the changed blocks and the data tree over them.</summary>
    /// <returns>The new root and length of the heap node's data; the root carries the reference the node will hold.</returns>
    public DataTreeResult Finish()
    {
        // Fill levels live in the first block (blocks 0 to 7) and in the bitmap blocks at 8, 136, ... (the block and the 127 after it).
        var changed = new List<int>();
        foreach (KeyValuePair<int, Page> e in _pages)
        {
            if (e.Value.Dirty)
            {
                changed.Add(e.Key);
            }
        }

        foreach (int k in changed)
        {
            int holder = k < 8 ? 0 : 8 + ((k - 8) / 128 * 128);
            Page h = Get(holder);
            int within = k - holder;
            int shift = (within & 1) * 4;
            int at = (holder == 0 ? 8 : 2) + (within >> 1);
            h.Data[at] = (byte)((h.Data[at] & ~(0xF << shift)) | (FillLevel(_pages[k]) << shift));
            h.Dirty = true;
        }

        var editor = new NdbDataTreeEditor(_editor, _tree);
        var dirty = new List<int>();
        foreach (KeyValuePair<int, Page> e in _pages)
        {
            if (e.Value.Dirty)
            {
                dirty.Add(e.Key);
            }
        }

        dirty.Sort();
        foreach (int k in dirty)
        {
            byte[] image = Render(_pages[k]);
            if (k < _tree.LeafCount)
            {
                editor.Replace(k, image);
            }
            else
            {
                if (k != editor.LeafCount)
                {
                    throw new InvalidOperationException("heap blocks were added out of order");
                }

                editor.Append(image);
            }
        }

        return editor.Build();
    }

    // The block as stored: header, allocations, then the page map at a 2-byte boundary (MS-PST 2.3.1.5).
    private static byte[] Render(Page p)
    {
        int mapAt = (p.Used + 1) & ~1;
        int n = p.Starts.Count;
        byte[] image = new byte[mapAt + 4 + (2 * (n + 1))];
        p.Data.AsSpan(0, p.Used).CopyTo(image);
        BinaryPrimitives.WriteUInt16LittleEndian(image, (ushort)mapAt);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(mapAt), (ushort)n);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(mapAt + 2), (ushort)p.Free);
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(mapAt + 4 + (2 * i)), (ushort)p.Starts[i]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(mapAt + 4 + (2 * n)), (ushort)p.Used);
        return image;
    }

    /// <summary>The block index of the last block (where new allocations go first).</summary>
    public int LastBlock => _pageCount - 1;

    /// <summary>Releases the data tree reader.</summary>
    public void Dispose() => _tree.Dispose();
}

/// <summary>An edit the heap editor cannot make; the caller rewrites the table whole instead. Nothing has been written when it is thrown.</summary>
internal sealed class HeapEditUnsupported(string message) : Exception(message);
