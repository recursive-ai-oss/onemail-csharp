using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// Builds a heap-on-node (MS-PST 2.3.1): allocations are placed in heap blocks, each block gets its header, page map and
/// fill levels, and every allocation is addressed by the HID <see cref="Allocate(int)"/> returns. The blocks it produces are the
/// data blocks of one node: <see cref="HeapNode"/> reads them back, and <see cref="NdbBlockBuilder.BeginDataTree"/> writes them.
/// </summary>
/// <remarks>
/// <para>Allocations are packed in the order they are made, a new block starting when the next one would not fit with its page map.
/// Blocks are filled to 8,176 bytes (the most a block holds), so a heap of N bytes takes about N / 8,176 blocks.
/// The whole heap is held in memory until <see cref="Finish"/>; its size is the size of the structure being built. A heap made with a
/// <see cref="NdbBlockBuilder.DataTreeWriter"/> instead hands each block over as soon as no later allocation can change it, so a large table
/// keeps only its last block, block 0 (whose header is written last) and the block-bitmap block of the 128 blocks in progress.</para>
/// <para>Not thread-safe.</para>
/// </remarks>
public sealed class HeapNodeBuilder
{
    /// <summary>The largest single allocation: 3,580 bytes (MS-PST 2.6.2.1.2). Larger values go in a subnode.</summary>
    public const int MaxAllocation = 3580;

    /// <summary>The most heap blocks: hidBlockIndex has 16 bits (MS-PST 2.3.1.6.2).</summary>
    public const int MaxBlocks = 1 << 16;

    /// <summary>The most allocations in one block: hidIndex has 11 bits and is 1-based (MS-PST 2.3.1.1).</summary>
    public const int MaxAllocationsPerBlock = (1 << 11) - 1;

    private const int HnhdrSize = 12;
    private const int BitmapHeaderSize = 66;
    private const int PageHeaderSize = 2;
    private const int BlockCapacity = NdbBlockBuilder.MaxBlockData;

    private sealed class Block
    {
        public readonly byte[] Data = new byte[BlockCapacity];
        public readonly List<int> Starts = [];   // rgibAlloc without its final entry
        public int Used;                          // end of the last allocation
    }

    private readonly List<Block?> _blocks = [];
    private readonly List<byte> _levels = [];   // the fill level of every finished block, kept after the block itself is written
    private readonly NdbBlockBuilder.DataTreeWriter? _tree;
    private int _blockCount;
    private bool _finished;

    /// <summary>Starts a heap with one empty block.</summary>
    /// <param name="clientSignature">bClientSig: 0xBC for a property context, 0x7C for a table context, 0xB5 for a bare BTH.</param>
    public HeapNodeBuilder(byte clientSignature)
        : this(clientSignature, null)
    {
    }

    /// <summary>Starts a heap that writes its blocks into a data tree as they are finished.</summary>
    /// <param name="clientSignature">bClientSig: 0xBC for a property context, 0x7C for a table context, 0xB5 for a bare BTH.</param>
    /// <param name="tree">The data tree of the node. The heap reserves one leaf per block, in order, so a block's index in its HID is its position in the tree; call <see cref="Seal"/> and then complete the tree.</param>
    public HeapNodeBuilder(byte clientSignature, NdbBlockBuilder.DataTreeWriter? tree)
    {
        ClientSignature = clientSignature;
        _tree = tree;
        AddBlock();
    }

    private Block AddBlock()
    {
        var b = new Block { Used = HeaderSize(_blockCount) };
        _blocks.Add(b);
        _blockCount++;
        if (_tree is not null)
        {
            long slot = _tree.ReserveBlock();
            if (slot != _blockCount - 1)
            {
                throw new InvalidOperationException("the data tree already has blocks, so heap block indexes would not match leaf positions");
            }
        }

        return b;
    }

    /// <summary>bClientSig as given.</summary>
    public byte ClientSignature { get; }

    /// <summary>hidUserRoot: the allocation the client structure starts at. Set it before <see cref="Finish"/>.</summary>
    public Hid UserRoot { get; set; }

    /// <summary>The number of heap blocks so far.</summary>
    public int BlockCount => _blockCount;

    /// <summary>The number of bytes the heap takes so far, counting the last block as full only to its page map. Blocks already written to a data tree are not counted.</summary>
    public long Length
    {
        get
        {
            long n = 0;
            foreach (Block? b in _blocks)
            {
                if (b is not null)
                {
                    n += PageMapAt(b) + PageMapSize(b.Starts.Count);
                }
            }

            return n;
        }
    }

    // The first block begins with an HNHDR, blocks 8, 136, 264... with an HNBITMAPHDR, all others with an HNPAGEHDR (MS-PST 2.3.1.2 to 2.3.1.4).
    private static int HeaderSize(int block) => block == 0 ? HnhdrSize : block >= 8 && (block - 8) % 128 == 0 ? BitmapHeaderSize : PageHeaderSize;

    // MS-PST 2.3.1.5: the HNPAGEMAP starts on a 2-byte boundary after the last allocation.
    private static int PageMapAt(Block b) => (b.Used + 1) & ~1;

    // cAlloc, cFree and cAlloc + 1 offsets.
    private static int PageMapSize(int allocations) => 4 + (2 * (allocations + 1));

    /// <summary>Allocates zero-filled space and returns its address. Fill it through <see cref="GetSpan"/>.</summary>
    /// <param name="size">1 to <see cref="MaxAllocation"/> bytes.</param>
    /// <returns>The HID.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is outside 1 to <see cref="MaxAllocation"/>.</exception>
    /// <exception cref="InvalidOperationException">The heap is finished or already has <see cref="MaxBlocks"/> blocks.</exception>
    public Hid Allocate(int size)
    {
        if (_finished)
        {
            throw new InvalidOperationException("the heap is finished");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxAllocation);

        Block b = _blocks[^1]!;
        if (!Fits(b, size))
        {
            if (_blockCount >= MaxBlocks)
            {
                throw new InvalidOperationException($"a heap has at most {MaxBlocks} blocks");
            }

            _levels.Add((byte)FillLevel(b));
            b = AddBlock();
            if (_tree is not null)
            {
                WriteFinishedBlocks();
            }
        }

        b.Starts.Add(b.Used);
        b.Used += size;
        return new Hid((uint)(((_blockCount - 1) << 16) | (b.Starts.Count << 5)));
    }

    private static bool Fits(Block b, int size) =>
        b.Starts.Count < MaxAllocationsPerBlock && ((b.Used + size + 1) & ~1) + PageMapSize(b.Starts.Count + 1) <= BlockCapacity;

    /// <summary>Allocates space for <paramref name="data"/> and copies it in.</summary>
    /// <param name="data">1 to <see cref="MaxAllocation"/> bytes.</param>
    /// <returns>The HID.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is outside 1 to <see cref="MaxAllocation"/>.</exception>
    public Hid Allocate(ReadOnlySpan<byte> data)
    {
        Hid hid = Allocate(data.Length);
        data.CopyTo(GetSpan(hid));
        return hid;
    }

    /// <summary>The bytes of an allocation, for filling or patching before <see cref="Finish"/>.</summary>
    /// <param name="hid">An HID returned by this builder.</param>
    /// <returns>The allocation's bytes.</returns>
    /// <exception cref="ArgumentException">The HID was not allocated here.</exception>
    public Span<byte> GetSpan(Hid hid)
    {
        int block = hid.BlockIndex;
        if (hid.Type != 0 || hid.Index == 0 || (uint)block >= (uint)_blocks.Count || _blocks[block] is not { } b || hid.Index > b.Starts.Count)
        {
            throw new ArgumentException($"HID {hid} was not allocated by this heap, or its block is already written", nameof(hid));
        }

        int start = b.Starts[hid.Index - 1];
        int end = hid.Index < b.Starts.Count ? b.Starts[hid.Index] : b.Used;
        return b.Data.AsSpan(start, end - start);
    }

    /// <summary>
    /// Completes every block: writes the headers, the page maps and the fill levels, and returns the block bytes in order. The heap
    /// cannot be changed afterwards. Not for a heap made with a data tree: use <see cref="Seal"/>.
    /// </summary>
    /// <returns>One entry per heap block, each the data of one data block (never more than 8,176 bytes). Valid for the life of the builder.</returns>
    /// <exception cref="InvalidOperationException">Called twice, or the heap writes to a data tree.</exception>
    public IReadOnlyList<ReadOnlyMemory<byte>> Finish()
    {
        if (_finished || _tree is not null)
        {
            throw new InvalidOperationException(_finished ? "the heap is already finished" : "the heap writes to a data tree; call Seal");
        }

        _finished = true;
        _levels.Add((byte)FillLevel(_blocks[^1]!));
        var result = new List<ReadOnlyMemory<byte>>(_blockCount);
        for (int i = 0; i < _blockCount; i++)
        {
            result.Add(Render(i));
        }

        return result;
    }

    /// <summary>Writes the blocks still held to the data tree and ends the heap. The caller then completes the tree.</summary>
    /// <exception cref="InvalidOperationException">Called twice, or the heap has no data tree.</exception>
    public void Seal()
    {
        if (_finished || _tree is null)
        {
            throw new InvalidOperationException(_finished ? "the heap is already finished" : "the heap has no data tree");
        }

        _finished = true;
        _levels.Add((byte)FillLevel(_blocks[^1]!));
        for (int i = 0; i < _blockCount; i++)
        {
            if (_blocks[i] is not null)
            {
                _tree.FillBlock(i, Render(i).Span);
                _blocks[i] = null;
            }
        }
    }

    // A block can be written once the blocks after it cannot change what it says: an ordinary block when it is finished (the next one has
    // started), and a bitmap block when the 128 blocks it describes are. Block 0 holds the user root and the levels of blocks 0 to 7 and
    // the TCINFO, which are set at the end, so it waits for Seal.
    private void WriteFinishedBlocks()
    {
        int finished = _blockCount - 2;   // the block before the one just started has its level
        if (finished >= 1 && HeaderSize(finished) != BitmapHeaderSize && _blocks[finished] is not null)
        {
            _tree!.FillBlock(finished, Render(finished).Span);
            _blocks[finished] = null;
        }

        // The bitmap block B describes blocks B to B + 127; the last of them has just been finished when finished == B + 127.
        int bitmap = finished - 127;
        if (bitmap >= 8 && (bitmap - 8) % 128 == 0 && _blocks[bitmap] is not null)
        {
            _tree!.FillBlock(bitmap, Render(bitmap).Span);
            _blocks[bitmap] = null;
        }
    }

    // Finishes block i: its header fields, the fill levels it carries, and its page map. Needs the levels of the blocks it describes.
    private ReadOnlyMemory<byte> Render(int i)
    {
        Block b = _blocks[i]!;
        int mapAt = PageMapAt(b);
        int n = b.Starts.Count;
        Span<byte> d = b.Data;
        BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)mapAt);   // ibHnpm, in all three header kinds
        if (i == 0)
        {
            d[2] = 0xEC;   // bSig
            d[3] = ClientSignature;
            BinaryPrimitives.WriteUInt32LittleEndian(d[4..], UserRoot.Value);
            // rgbFillLevel: eight 4-bit levels for blocks 0 to 7.
            for (int k = 0; k < 8 && k < _levels.Count; k++)
            {
                SetLevel(d.Slice(8, 4), k, _levels[k]);
            }
        }
        else if (HeaderSize(i) == BitmapHeaderSize)
        {
            // The map in this block covers the next 128 blocks, this one included.
            for (int k = 0; k < 128 && i + k < _levels.Count; k++)
            {
                SetLevel(d.Slice(2, 64), k, _levels[i + k]);
            }
        }

        // HNPAGEMAP: cAlloc, cFree (always zero: nothing is freed), then cAlloc + 1 offsets, the last being the end of the data.
        Span<byte> map = d[mapAt..];
        BinaryPrimitives.WriteUInt16LittleEndian(map, (ushort)n);
        BinaryPrimitives.WriteUInt16LittleEndian(map[2..], 0);
        for (int k = 0; k < n; k++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(map[(4 + (2 * k))..], (ushort)b.Starts[k]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(map[(4 + (2 * n))..], (ushort)b.Used);
        return b.Data.AsMemory(0, mapAt + PageMapSize(n));
    }

    private static void SetLevel(Span<byte> levels, int index, int level)
    {
        // Two 4-bit values per byte; the lower-numbered block takes the low nibble.
        int shift = (index & 1) * 4;
        levels[index >> 1] = (byte)((levels[index >> 1] & ~(0xF << shift)) | (level << shift));
    }

    // MS-PST 2.3.1.2: how many bytes of the block are free, bucketed.
    private static int FillLevel(Block b)
    {
        int free = BlockCapacity - (PageMapAt(b) + PageMapSize(b.Starts.Count));
        return free switch
        {
            >= 3584 => 0,
            >= 2560 => 1,
            >= 2048 => 2,
            >= 1792 => 3,
            >= 1536 => 4,
            >= 1280 => 5,
            >= 1024 => 6,
            >= 768 => 7,
            >= 512 => 8,
            >= 256 => 9,
            >= 128 => 10,
            >= 64 => 11,
            >= 32 => 12,
            >= 16 => 13,
            >= 8 => 14,
            _ => 15,
        };
    }
}
