using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Tnef;

/// <summary>
/// Writes a TNEF stream attribute by attribute (MS-OXTNEF 2.1.3.2): the signature and legacy key first, then level, id, length, data and checksum for each
/// attribute. It adds nothing of its own, so a document read with <see cref="TnefDocument"/> and written back through it is byte-identical.
/// </summary>
public sealed class TnefWriter
{
    /// <summary>The TNEF signature, 0x223E9F78.</summary>
    public const uint Signature = 0x223E9F78;

    private readonly Stream _output;

    /// <summary>Starts a stream by writing the signature and the legacy key.</summary>
    /// <param name="output">Where the bytes go; not closed by the writer.</param>
    /// <param name="legacyKey">The 16-bit key after the signature. Any value is valid (MS-OXTNEF 2.1.3.2).</param>
    public TnefWriter(Stream output, ushort legacyKey = 1)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        Span<byte> head = stackalloc byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Signature);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..], legacyKey);
        _output.Write(head);
    }

    /// <summary>Writes an attribute whose data is in memory, with its checksum computed.</summary>
    /// <param name="level">The level byte.</param>
    /// <param name="id">The 32-bit attribute id.</param>
    /// <param name="data">The data bytes.</param>
    /// <param name="checksum">The checksum to store, or null to compute it. A caller that wants to reproduce a stream with a wrong checksum gives the stored one.</param>
    /// <exception cref="ArgumentException">The data is longer than 2 GiB - 1 (lengths are 32-bit).</exception>
    public void WriteAttribute(byte level, uint id, ReadOnlySpan<byte> data, ushort? checksum = null)
    {
        WriteHeader(level, id, data.Length);
        _output.Write(data);
        uint sum = 0;
        foreach (byte b in data)
        {
            sum += b;
        }

        WriteChecksum(checksum ?? (ushort)sum);
    }

    /// <summary>Writes an attribute whose data is read from a stream, so large attachments are never held in memory.</summary>
    /// <param name="level">The level byte.</param>
    /// <param name="id">The 32-bit attribute id.</param>
    /// <param name="data">The data; exactly <paramref name="length"/> bytes are read from its current position.</param>
    /// <param name="length">The data length.</param>
    /// <param name="checksum">The checksum to store, or null to compute it while copying.</param>
    /// <exception cref="ArgumentException">The length does not fit 32 bits signed.</exception>
    /// <exception cref="EndOfStreamException">The stream ends before <paramref name="length"/> bytes.</exception>
    public void WriteAttribute(byte level, uint id, Stream data, long length, ushort? checksum = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        WriteHeader(level, id, length);
        uint sum = Copy(data, length);
        WriteChecksum(checksum ?? (ushort)sum);
    }

    /// <summary>Starts an attribute whose data the caller then writes with <see cref="WriteData(ReadOnlySpan{byte})"/>, for composite attributes streamed without buffering.</summary>
    /// <param name="level">The level byte.</param>
    /// <param name="id">The 32-bit attribute id.</param>
    /// <param name="length">The data length, which the caller must know before writing.</param>
    public void BeginAttribute(byte level, uint id, long length)
    {
        WriteHeader(level, id, length);
        _sum = 0;
    }

    private uint _sum;

    /// <summary>Writes part of the data of the attribute started with <see cref="BeginAttribute"/>.</summary>
    /// <param name="data">The bytes.</param>
    public void WriteData(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            _sum += b;
        }

        _output.Write(data);
    }

    /// <summary>Copies bytes into the attribute started with <see cref="BeginAttribute"/>.</summary>
    /// <param name="data">The stream to read from.</param>
    /// <param name="length">How many bytes to copy.</param>
    /// <exception cref="EndOfStreamException">The stream ends early.</exception>
    public void WriteData(Stream data, long length) => _sum += Copy(data, length);

    /// <summary>Ends the attribute started with <see cref="BeginAttribute"/> by writing its checksum.</summary>
    /// <param name="checksum">The checksum to store, or null for the sum of the data written.</param>
    public void EndAttribute(ushort? checksum = null) => WriteChecksum(checksum ?? (ushort)_sum);

    /// <summary>Writes bytes as they are, for the cut-off end of a truncated stream or any trailing bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteRaw(ReadOnlySpan<byte> bytes) => _output.Write(bytes);

    private void WriteHeader(byte level, uint id, long length)
    {
        if (length is < 0 or > int.MaxValue)
        {
            throw new ArgumentException("An attribute's data length must fit 31 bits.", nameof(length));
        }

        Span<byte> h = stackalloc byte[9];
        h[0] = level;
        BinaryPrimitives.WriteUInt32LittleEndian(h[1..], id);
        BinaryPrimitives.WriteUInt32LittleEndian(h[5..], (uint)length);
        _output.Write(h);
    }

    private void WriteChecksum(ushort sum)
    {
        Span<byte> c = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(c, sum);
        _output.Write(c);
    }

    private uint Copy(Stream data, long length)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            uint sum = 0;
            long left = length;
            while (left > 0)
            {
                int n = data.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0)
                {
                    throw new EndOfStreamException("The data ended before the declared attribute length.");
                }

                for (int i = 0; i < n; i++)
                {
                    sum += buffer[i];
                }

                _output.Write(buffer, 0, n);
                left -= n;
            }

            return sum;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
