using System;
using System.Collections;
using System.Collections.Generic;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>One property of a property context: its ID, its type, and a lazy reference to its value bytes.</summary>
/// <param name="Id">The property ID (the high half of the property tag).</param>
/// <param name="Type">The property type code (MS-OXCDATA 2.11.1; the low half of the property tag).</param>
/// <param name="Value">Where the value bytes are. Nothing has been read.</param>
public readonly record struct PcProperty(ushort Id, ushort Type, LtpValueRef Value)
{
    /// <summary>The 32-bit property tag: the ID in the high half and the type in the low half.</summary>
    public uint Tag => ((uint)Id << 16) | Type;
}

/// <summary>
/// A property context (MS-PST 2.3.3): the properties of one object (a message, folder, attachment, store...) as a BTH keyed
/// by property ID over a heap-on-node. Values are raw: IDs, types and byte references, with nothing decoded beyond what
/// <see cref="LtpValues"/> offers for fixed-size scalars.
/// </summary>
/// <remarks>
/// <para>Property types of four bytes or fewer (Integer16, Integer32, Floating32, ErrorCode, Boolean) are stored in the
/// entry itself and come out as <see cref="LtpValueKind.Inline"/>; everything else is a heap allocation or a subnode
/// (<see cref="LtpValueKind.Heap"/>, <see cref="LtpValueKind.Subnode"/>), or empty when the HNID is zero.</para>
/// <para>Enumerate with <c>foreach</c>. Reading never throws for malformed content; see <see cref="Defects"/>. Not thread-safe.</para>
/// </remarks>
public sealed class PropertyContext : IDisposable, IEnumerable<PcProperty>
{
    private const byte PcClientSignature = 0xBC;   // MS-PST 2.3.1.2 bTypePC
    private readonly bool _ownsHeap;

    private PropertyContext(HeapNode heap, Bth index, bool ownsHeap)
    {
        Heap = heap;
        Index = index;
        _ownsHeap = ownsHeap;
    }

    /// <summary>The heap-on-node the context lives in.</summary>
    public HeapNode Heap { get; }

    /// <summary>The BTH of properties (2-byte keys, 6-byte entries).</summary>
    public Bth Index { get; }

    /// <summary>What was found wrong in this context so far (the heap's list).</summary>
    public IReadOnlyList<LtpDefect> Defects => Heap.Defects;

    /// <summary>Opens the property context of a node, looked up in the node B-tree.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="node">The node's NID.</param>
    /// <param name="context">The context, or null when it cannot be used.</param>
    /// <param name="defects">Why it cannot be used (empty on success; <see cref="Defects"/> has later findings).</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the node is a heap whose root is a property BTH.</returns>
    public static bool TryOpen(NdbFile file, Nid node, out PropertyContext? context, out IReadOnlyList<LtpDefect> defects, LtpOptions? options = null)
    {
        context = null;
        return HeapNode.TryOpen(file, node, out HeapNode? heap, out defects, options) && TryOpenOwned(heap!, out context, out defects);
    }

    /// <summary>Opens the property context of a subnode (an attachment object under a message, say).</summary>
    /// <param name="file">The open file.</param>
    /// <param name="subnode">The subnode entry.</param>
    /// <param name="context">The context, or null when it cannot be used.</param>
    /// <param name="defects">Why it cannot be used.</param>
    /// <param name="options">Limits; null for defaults.</param>
    /// <returns>True when the subnode is a heap whose root is a property BTH.</returns>
    public static bool TryOpen(NdbFile file, in SubnodeEntry subnode, out PropertyContext? context, out IReadOnlyList<LtpDefect> defects, LtpOptions? options = null)
    {
        context = null;
        return HeapNode.TryOpen(file, subnode, out HeapNode? heap, out defects, options) && TryOpenOwned(heap!, out context, out defects);
    }

    /// <summary>Opens a property context over a heap the caller already has. The context does not dispose the heap.</summary>
    /// <param name="heap">The heap.</param>
    /// <param name="context">The context, or null when it cannot be used.</param>
    /// <returns>True when the heap's root is a property BTH; otherwise defects are on the heap.</returns>
    public static bool TryOpen(HeapNode heap, out PropertyContext? context)
    {
        ArgumentNullException.ThrowIfNull(heap);
        return TryOpenCore(heap, ownsHeap: false, out context);
    }

    private static bool TryOpenOwned(HeapNode heap, out PropertyContext? context, out IReadOnlyList<LtpDefect> defects)
    {
        if (TryOpenCore(heap, ownsHeap: true, out context))
        {
            defects = [];
            return true;
        }

        defects = [.. heap.Defects];
        heap.Dispose();
        return false;
    }

    private static bool TryOpenCore(HeapNode heap, bool ownsHeap, out PropertyContext? context)
    {
        context = null;
        if (heap.ClientSignature != PcClientSignature)
        {
            heap.Report(LtpDefectKind.HeapClientSignatureMismatch, $"bClientSig is 0x{heap.ClientSignature:x2}; a property context has 0xBC");
            return false;
        }

        if (!Bth.TryOpen(heap, heap.UserRoot, out Bth? bth) || bth is null)
        {
            return false;
        }

        // MS-PST 2.3.3.1: the BTH has 2-byte keys (the property ID) and 6-byte entries (type and HNID).
        if (bth.KeySize != 2 || bth.DataSize != 6)
        {
            heap.Report(LtpDefectKind.PcBadShape, $"property BTH has cbKey {bth.KeySize} and cbEnt {bth.DataSize}; expected 2 and 6");
            return false;
        }

        context = new PropertyContext(heap, bth, ownsHeap);
        return true;
    }

    // MS-PST 2.3.3.3: a value of up to four bytes sits in the HNID field; larger and variable-size values are an HID or a subnode NID.
    private static PcProperty Make(ushort id, ReadOnlySpan<byte> entry)
    {
        ushort type = LtpBytes.U16(entry, 0);
        uint hnid = LtpBytes.U32(entry, 2);
        int fixedSize = LtpPropertyTypes.FixedSize(type);
        LtpValueRef value = fixedSize is > 0 and <= 4
            ? LtpValueRef.FromInline(hnid & (uint)((1UL << (8 * fixedSize)) - 1), fixedSize)
            : LtpValueRef.FromHnid(hnid);
        return new PcProperty(id, type, value);
    }

    /// <summary>Finds a property by ID.</summary>
    /// <param name="id">The property ID.</param>
    /// <param name="property">The property, when present.</param>
    /// <returns>True when the context has the property.</returns>
    public bool TryGet(ushort id, out PcProperty property)
    {
        Span<byte> key = [(byte)id, (byte)(id >> 8)];
        Span<byte> entry = [0, 0, 0, 0, 0, 0];
        if (Index.TryFind(key, entry))
        {
            property = Make(id, entry);
            return true;
        }

        property = default;
        return false;
    }

    /// <summary>Counts the properties (walks the BTH).</summary>
    /// <returns>The number of records.</returns>
    public int Count()
    {
        int n = 0;
        BthCursor c = Index.CreateCursor();
        while (c.MoveNext())
        {
            n++;
        }

        return n;
    }

    /// <summary>The length of a property's value in bytes; -1, with a defect, when it cannot be found.</summary>
    /// <param name="property">The property.</param>
    /// <returns>The length.</returns>
    public long GetValueLength(in PcProperty property) => Heap.GetValueLength(property.Value);

    /// <summary>Copies value bytes starting at <paramref name="offset"/>.</summary>
    /// <param name="property">The property.</param>
    /// <param name="offset">Position in the value.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns>The bytes copied.</returns>
    public int ReadValue(in PcProperty property, long offset, Span<byte> destination) => Heap.ReadValue(property.Value, offset, destination);

    /// <summary>Reads a whole value into a new array.</summary>
    /// <param name="property">The property.</param>
    /// <returns>The bytes, or null if unreadable or longer than <see cref="LtpOptions.MaxValueBytes"/>.</returns>
    public byte[]? ReadValue(in PcProperty property) => Heap.ReadValue(property.Value);

    /// <summary>Enumerates the properties in ID order.</summary>
    /// <returns>An enumerator; the context must stay open while it is used.</returns>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<PcProperty> IEnumerable<PcProperty>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Disposes the heap when the context opened it.</summary>
    public void Dispose()
    {
        if (_ownsHeap)
        {
            Heap.Dispose();
        }
    }

    /// <summary>Enumerates the properties of a <see cref="PropertyContext"/>.</summary>
    public struct Enumerator : IEnumerator<PcProperty>
    {
        private readonly BthCursor _cursor;
        private PcProperty _current;

        internal Enumerator(PropertyContext pc) => _cursor = pc.Index.CreateCursor();

        /// <inheritdoc/>
        public readonly PcProperty Current => _current;

        readonly object IEnumerator.Current => _current;

        /// <inheritdoc/>
        public bool MoveNext()
        {
            if (!_cursor.MoveNext())
            {
                return false;
            }

            _current = Make((ushort)_cursor.KeyValue, _cursor.Data);
            return true;
        }

        /// <inheritdoc/>
        public readonly void Reset() => throw new NotSupportedException();

        /// <inheritdoc/>
        public readonly void Dispose()
        {
        }
    }
}
