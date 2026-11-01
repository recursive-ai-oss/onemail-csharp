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
/// The whole heap is held in memory until <see cref="Finish"/>; its size is the size of the structure being built.</para>
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

    private readonly List<Block> _blocks = [];
    private bool _finished;

    /// <summary>Starts a heap with one empty block.</summary>
    /// <param name="clientSignature">bClientSig: 0xBC for a property context, 0x7C for a table context, 0xB5 for a bare BTH.</param>
    public HeapNodeBuilder(byte clientSignature)
    {
        ClientSignature = clientSignature;
        _blocks.Add(new Block { Used = HeaderSize(0) });
    }

    /// <summary>bClientSig as given.</summary>
    public byte ClientSignature { get; }

    /// <summary>hidUserRoot: the allocation the client structure starts at. Set it before <see cref="Finish"/>.</summary>
    public Hid UserRoot { get; set; }

    /// <summary>The number of heap blocks so far.</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>The number of bytes the heap takes so far, counting the last block as full only to its page map.</summary>
    public long Length
    {
        get
        {
            long n = 0;
            foreach (Block b in _blocks)
            {
                n += PageMapAt(b) + PageMapSize(b.Starts.Count);
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

        Block b = _blocks[^1];
        if (!Fits(b, size))
        {
            if (_blocks.Count >= MaxBlocks)
            {
                throw new InvalidOperationException($"a heap has at most {MaxBlocks} blocks");
            }

            b = new Block { Used = HeaderSize(_blocks.Count) };
            _blocks.Add(b);
        }

        b.Starts.Add(b.Used);
        b.Used += size;
        return new Hid((uint)(((_blocks.Count - 1) << 16) | (b.Starts.Count << 5)));
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
        if (hid.Type != 0 || hid.Index == 0 || (uint)block >= (uint)_blocks.Count || hid.Index > _blocks[block].Starts.Count)
        {
            throw new ArgumentException($"HID {hid} was not allocated by this heap", nameof(hid));
        }

        Block b = _blocks[block];
        int start = b.Starts[hid.Index - 1];
        int end = hid.Index < b.Starts.Count ? b.Starts[hid.Index] : b.Used;
        return b.Data.AsSpan(start, end - start);
    }

    /// <summary>
    /// Completes every block: writes the headers, the page maps and the fill levels, and returns the block bytes in order. The heap
    /// cannot be changed afterwards.
    /// </summary>
    /// <returns>One entry per heap block, each the data of one data block (never more than 8,176 bytes). Valid for the life of the builder.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public IReadOnlyList<ReadOnlyMemory<byte>> Finish()
    {
        if (_finished)
        {
            throw new InvalidOperationException("the heap is already finished");
        }

        _finished = true;
        var result = new List<ReadOnlyMemory<byte>>(_blocks.Count);
        for (int i = 0; i < _blocks.Count; i++)
        {
            Block b = _blocks[i];
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
                for (int k = 0; k < Math.Min(8, _blocks.Count); k++)
                {
                    SetLevel(d.Slice(8, 4), k, FillLevel(_blocks[k]));
                }
            }
            else if (HeaderSize(i) == BitmapHeaderSize)
            {
                // The map in this block covers the next 128 blocks, this one included.
                for (int k = 0; k < 128 && i + k < _blocks.Count; k++)
                {
                    SetLevel(d.Slice(2, 64), k, FillLevel(_blocks[i + k]));
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
            result.Add(b.Data.AsMemory(0, mapAt + PageMapSize(n)));
        }

        return result;
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
