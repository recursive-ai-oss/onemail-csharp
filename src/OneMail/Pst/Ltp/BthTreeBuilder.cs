using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// Builds a BTH (MS-PST 2.3.2) inside a heap: a header allocation, then leaf nodes of fixed-size records in key order, then
/// as many index levels as it takes to reach a single root. <see cref="Bth"/> reads the result.
/// </summary>
/// <remarks>
/// <para>Records may be added in any order; they are sorted by key (compared as little-endian unsigned integers, MS-PST 2.3.2) when the tree is
/// built, and a repeated key is refused. Leaf and index nodes are heap allocations of up to 3,580 bytes, filled completely except for
/// the last of each level. An empty tree is only its header, with a null root.</para>
/// <para>Memory is the records added so far: <c>count × (keySize + dataSize)</c> bytes plus a four-byte sort index per record.</para>
/// <para>Not thread-safe.</para>
/// </remarks>
public sealed class BthTreeBuilder
{
    private const byte BthSignature = 0xB5;   // MS-PST 2.3.2.1 bType
    private const int IndexPointerSize = 4;   // an index record is a key and the HID of the child

    private readonly HeapNodeBuilder _heap;
    private readonly int _recordSize;
    private byte[] _records = new byte[256];
    private int _count;
    private bool _sorted = true;
    private bool _built;
    private readonly byte[] _keyScratch = new byte[8];
    private readonly List<Hid> _leaves = [];
    private int _perLeaf;

    /// <summary>Creates a tree and allocates its BTHHEADER, so a tree created first in a heap is the heap's first allocation.</summary>
    /// <param name="heap">The heap the tree is built in.</param>
    /// <param name="keySize">cbKey: 2, 4, 8 or 16.</param>
    /// <param name="dataSize">cbEnt: 1 to 32.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is one the format does not allow.</exception>
    public BthTreeBuilder(HeapNodeBuilder heap, int keySize, int dataSize)
    {
        ArgumentNullException.ThrowIfNull(heap);
        if (keySize is not (2 or 4 or 8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(keySize), keySize, "cbKey is 2, 4, 8 or 16 (MS-PST 2.3.2.1)");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(dataSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataSize, 32);
        _heap = heap;
        KeySize = keySize;
        DataSize = dataSize;
        _recordSize = keySize + dataSize;
        Header = heap.Allocate(8);
    }

    /// <summary>The HID of the BTHHEADER: the value a client structure stores to point at the tree.</summary>
    public Hid Header { get; }

    /// <summary>cbKey.</summary>
    public int KeySize { get; }

    /// <summary>cbEnt.</summary>
    public int DataSize { get; }

    /// <summary>The records added so far.</summary>
    public int Count => _count;

    /// <summary>Adds a record.</summary>
    /// <param name="key">Exactly <see cref="KeySize"/> bytes, little-endian.</param>
    /// <param name="data">Exactly <see cref="DataSize"/> bytes.</param>
    /// <exception cref="ArgumentException">A length is wrong.</exception>
    /// <exception cref="InvalidOperationException">The tree is built.</exception>
    public void Add(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (_built)
        {
            throw new InvalidOperationException("the tree is already built");
        }

        if (key.Length != KeySize || data.Length != DataSize)
        {
            throw new ArgumentException($"a record is a {KeySize}-byte key and {DataSize} bytes of data");
        }

        if ((long)(_count + 1) * _recordSize > Array.MaxLength)
        {
            throw new InvalidOperationException("too many records");
        }

        if ((_count + 1) * _recordSize > _records.Length)
        {
            Array.Resize(ref _records, (int)Math.Min(Array.MaxLength, Math.Max((long)(_count + 1) * _recordSize, _records.Length * 2L)));
        }

        if (_count > 0 && _sorted && Compare(key, _records.AsSpan((_count - 1) * _recordSize, KeySize)) <= 0)
        {
            _sorted = false;
        }

        key.CopyTo(_records.AsSpan(_count * _recordSize));
        data.CopyTo(_records.AsSpan((_count * _recordSize) + KeySize));
        _count++;
    }

    /// <summary>Adds a record whose key is an integer of up to eight bytes.</summary>
    /// <param name="key">The key; written little-endian in <see cref="KeySize"/> bytes. Must fit.</param>
    /// <param name="data">Exactly <see cref="DataSize"/> bytes.</param>
    /// <exception cref="ArgumentException">The key size is 16, the key does not fit it, or the data length is wrong.</exception>
    public void Add(ulong key, ReadOnlySpan<byte> data)
    {
        if (KeySize > 8 || (KeySize < 8 && (key >> (8 * KeySize)) != 0))
        {
            throw new ArgumentException($"key 0x{key:x} does not fit in cbKey {KeySize}", nameof(key));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(_keyScratch, key);
        Add(_keyScratch.AsSpan(0, KeySize), data);
    }

    // Little-endian unsigned integers compare from the last byte.
    private static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = a.Length - 1; i >= 0; i--)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    /// <summary>
    /// Replaces the data of a record after <see cref="Build"/>. A property context uses it to allocate the tree's nodes before the values the
    /// records point to, so the values' HIDs are known only after the nodes exist.
    /// </summary>
    /// <param name="rank">The record's position in key order, from zero.</param>
    /// <param name="data">Exactly <see cref="DataSize"/> bytes.</param>
    /// <exception cref="InvalidOperationException">The tree is not built yet.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rank"/> is not a record.</exception>
    /// <exception cref="ArgumentException">The length is wrong.</exception>
    public void SetData(int rank, ReadOnlySpan<byte> data)
    {
        if (!_built)
        {
            throw new InvalidOperationException("the tree is not built yet");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(rank);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rank, _count);
        if (data.Length != DataSize)
        {
            throw new ArgumentException($"a record has {DataSize} bytes of data", nameof(data));
        }

        data.CopyTo(_heap.GetSpan(_leaves[rank / _perLeaf])[(((rank % _perLeaf) * _recordSize) + KeySize)..]);
    }

    /// <summary>Writes the nodes and fills in the header.</summary>
    /// <returns>The HID of the BTHHEADER (<see cref="Header"/>).</returns>
    /// <exception cref="InvalidOperationException">Called twice, or two records have the same key.</exception>
    public Hid Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("the tree is already built");
        }

        _built = true;
        int[] order = new int[_count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        byte[] records = _records;
        int recordSize = _recordSize, keySize = KeySize;
        if (!_sorted)
        {
            Array.Sort(order, (x, y) => Compare(records.AsSpan(x * recordSize, keySize), records.AsSpan(y * recordSize, keySize)));
        }

        for (int i = 1; i < order.Length; i++)
        {
            if (Compare(records.AsSpan(order[i] * recordSize, keySize), records.AsSpan(order[i - 1] * recordSize, keySize)) == 0)
            {
                throw new InvalidOperationException("two records have the same key");
            }
        }

        Hid root = default;
        int levels = 0;
        if (_count > 0)
        {
            // Leaves: full nodes of records in key order. The index above them records each child's first key (MS-PST 2.3.2.3).
            int perLeaf = _perLeaf = HeapNodeBuilder.MaxAllocation / _recordSize;
            var level = new List<(int FirstRecord, Hid Node)>();
            for (int start = 0; start < _count; start += perLeaf)
            {
                int n = Math.Min(perLeaf, _count - start);
                Hid hid = _heap.Allocate(n * _recordSize);
                Span<byte> node = _heap.GetSpan(hid);
                for (int k = 0; k < n; k++)
                {
                    records.AsSpan(order[start + k] * recordSize, recordSize).CopyTo(node[(k * recordSize)..]);
                }

                level.Add((order[start], hid));
                _leaves.Add(hid);
            }

            int indexRecord = KeySize + IndexPointerSize;
            int perIndex = HeapNodeBuilder.MaxAllocation / indexRecord;
            while (level.Count > 1)
            {
                levels++;
                var next = new List<(int FirstRecord, Hid Node)>();
                for (int start = 0; start < level.Count; start += perIndex)
                {
                    int n = Math.Min(perIndex, level.Count - start);
                    Hid hid = _heap.Allocate(n * indexRecord);
                    Span<byte> node = _heap.GetSpan(hid);
                    for (int k = 0; k < n; k++)
                    {
                        (int first, Hid child) = level[start + k];
                        records.AsSpan(first * recordSize, keySize).CopyTo(node[(k * indexRecord)..]);
                        BinaryPrimitives.WriteUInt32LittleEndian(node[((k * indexRecord) + keySize)..], child.Value);
                    }

                    next.Add((level[start].FirstRecord, hid));
                }

                level = next;
            }

            root = level[0].Node;
        }

        Span<byte> h = _heap.GetSpan(Header);
        h[0] = BthSignature;
        h[1] = (byte)KeySize;
        h[2] = (byte)DataSize;
        h[3] = (byte)levels;
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], root.Value);
        return Header;
    }
}
