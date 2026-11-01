using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Pst.Ltp;

/// <summary>
/// Builds a property context (MS-PST 2.3.3): a BTH keyed by property ID whose 6-byte entries are a type and either the value
/// (types of four bytes or fewer), an HID (a value that fits a heap allocation) or a subnode NID (a larger value). The result is what
/// <see cref="PropertyContext"/> reads.
/// </summary>
/// <remarks>
/// <para>Values are raw bytes in the form the type defines (UTF-16 for PtypString, the offset-table layout for multi-valued strings and
/// binaries); nothing is converted here. Fixed-size types must be given exactly their size.</para>
/// <para>Where a value goes (MS-PST 2.3.3.3): Integer16, Integer32, Floating32, ErrorCode and Boolean sit in the entry; an empty variable-size
/// value is a zero HNID; any other value up to <see cref="HeapNodeBuilder.MaxAllocation"/> bytes is a heap allocation; a larger one is a
/// subnode, written to the blocks as soon as <c>Set</c> is called so that a stream is never held whole.</para>
/// <para>Memory: the heap being built, which is the size of the context's small values and index, plus one block image at a time.</para>
/// <para>Not thread-safe; one builder builds one context.</para>
/// </remarks>
public sealed class PropertyContextBuilder
{
    private readonly NdbBlockBuilder _blocks;
    private readonly LtpValuePlacer _values;
    private readonly SortedDictionary<ushort, Pending> _properties = [];
    private bool _built;

    private readonly record struct Pending(ushort Type, uint Hnid, byte[]? Small);

    /// <summary>Creates a builder.</summary>
    /// <param name="blocks">Where the heap and any value subnodes are written.</param>
    /// <param name="nextSubnodeNid">Supplies the NIDs of value subnodes; null for NID_TYPE_LTP NIDs counting up from index 1. A NID with type bits zero is refused.</param>
    public PropertyContextBuilder(NdbBlockBuilder blocks, Func<Nid>? nextSubnodeNid = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        _blocks = blocks;
        _values = new LtpValuePlacer(blocks, nextSubnodeNid);
    }

    /// <summary>The number of properties set.</summary>
    public int Count => _properties.Count;

    /// <summary>Sets a property.</summary>
    /// <param name="id">The property ID.</param>
    /// <param name="type">The property type code.</param>
    /// <param name="value">The value bytes: exactly the type's size for fixed-size types, any length for variable-size ones.</param>
    /// <exception cref="ArgumentException">The ID is already set, or a fixed-size value has the wrong length.</exception>
    /// <exception cref="InvalidOperationException">The context is built.</exception>
    public void Set(ushort id, ushort type, ReadOnlySpan<byte> value)
    {
        Begin(id);
        int fixedSize = LtpPropertyTypes.FixedSize(type);
        if (fixedSize > 0 && value.Length != fixedSize)
        {
            throw new ArgumentException($"property type 0x{type:x4} is {fixedSize} bytes, not {value.Length}", nameof(value));
        }

        if (fixedSize is > 0 and <= 4)
        {
            // MS-PST 2.3.3.3: the value is the HNID field itself, zero-extended.
            uint v = 0;
            for (int i = value.Length - 1; i >= 0; i--)
            {
                v = (v << 8) | value[i];
            }

            _properties.Add(id, new Pending(type, v, null));
        }
        else if (value.Length <= HeapNodeBuilder.MaxAllocation)
        {
            // Held until Build so the heap lays out its index first and the values after it.
            _properties.Add(id, new Pending(type, 0, value.IsEmpty ? null : value.ToArray()));
        }
        else
        {
            _properties.Add(id, new Pending(type, _values.PlaceLarge(value), null));
        }
    }

    /// <summary>Sets a property whose value is read from a stream. Values above the heap limit are copied block by block.</summary>
    /// <param name="id">The property ID.</param>
    /// <param name="type">The property type code.</param>
    /// <param name="source">A readable stream positioned at the first byte of the value.</param>
    /// <param name="length">The value's length, declared so limits are checked before reading.</param>
    /// <exception cref="ArgumentException">The ID is already set, or a fixed-size type has another length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or above <see cref="NdbBlockBuilder.MaxDataTreeBytes"/>.</exception>
    /// <exception cref="EndOfStreamException">The stream ends early.</exception>
    public void Set(ushort id, ushort type, Stream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, NdbBlockBuilder.MaxDataTreeBytes);
        if (length <= HeapNodeBuilder.MaxAllocation || LtpPropertyTypes.FixedSize(type) > 0)
        {
            byte[] small = new byte[length];
            source.ReadExactly(small);
            Set(id, type, small);
            return;
        }

        Begin(id);
        _properties.Add(id, new Pending(type, _values.PlaceStream(source, length), null));
    }

    /// <summary>
    /// Sets a property whose HNID is the NID of a subnode the caller writes itself: an attachment's embedded message (PtypObject), say.
    /// The builder does not create the subnode; add it with <see cref="LtpNodeResult.WriteSubnodes"/>.
    /// </summary>
    /// <param name="id">The property ID.</param>
    /// <param name="type">The property type code.</param>
    /// <param name="subnode">The subnode's NID. Type bits must not be zero.</param>
    /// <exception cref="ArgumentException">The ID is already set or the NID's type is zero.</exception>
    public void SetNodeReference(ushort id, ushort type, Nid subnode)
    {
        Begin(id);
        if ((subnode.Value & 0x1F) == 0)
        {
            throw new ArgumentException($"{subnode} has type 0, which reads back as a heap ID", nameof(subnode));
        }

        _properties.Add(id, new Pending(type, subnode.Value, null));
    }

    private void Begin(ushort id)
    {
        if (_built)
        {
            throw new InvalidOperationException("the property context is built");
        }

        if (_properties.ContainsKey(id))
        {
            throw new ArgumentException($"property 0x{id:x4} is already set", nameof(id));
        }
    }

    /// <summary>Writes the heap and returns where it and the value subnodes are.</summary>
    /// <returns>The result: the heap's BID and the subnodes the values needed.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public LtpNodeResult Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("the property context is built");
        }

        _built = true;
        var heap = new HeapNodeBuilder(0xBC);   // bTypePC
        var bth = new BthTreeBuilder(heap, 2, 6);
        heap.UserRoot = bth.Header;
        Span<byte> entry = stackalloc byte[6];
        foreach ((ushort id, Pending p) in _properties)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry, p.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[2..], p.Hnid);
            bth.Add(id, entry);
        }

        // The tree's nodes come first in the heap and the values after them, the order the property contexts in real files mostly have
        // (MS-PST 3.8 shows it too); the records are then patched with the values' HIDs.
        bth.Build();
        int rank = 0;
        foreach ((_, Pending p) in _properties)
        {
            if (p.Small is not null)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(entry, p.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[2..], heap.Allocate(p.Small).Value);
                bth.SetData(rank, entry);
            }

            rank++;
        }

        return LtpNodeResult.WriteHeap(_blocks, heap, _values.Subnodes);
    }
}
