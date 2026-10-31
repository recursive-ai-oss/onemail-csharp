using System;
using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>The checksums and obfuscation the PST format defines (MS-PST section 5, "PST Data Algorithms").</summary>
public static class NdbChecksums
{
    // MS-PST 5.3: the CRC is the ordinary reflected CRC-32 polynomial 0xEDB88320, but with no initial or final inversion
    // and a caller-chosen seed (always zero in the format). The specification lists eight 256-entry tables for a
    // slicing-by-8 loop; they are the usual slicing tables, so we derive them instead of carrying 8 KiB of constants.
    private static readonly uint[] SliceTables = BuildTables();

    private static uint[] BuildTables()
    {
        var t = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }

            t[i] = c;
        }

        for (int s = 1; s < 8; s++)
        {
            for (int i = 0; i < 256; i++)
            {
                uint prev = t[((s - 1) * 256) + i];
                t[(s * 256) + i] = (prev >> 8) ^ t[prev & 0xFF];
            }
        }

        return t;
    }

    /// <summary>The PST CRC-32 of <paramref name="data"/> (MS-PST 5.3, seed 0).</summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <param name="seed">The starting value; always zero for fields in the format.</param>
    /// <returns>The checksum.</returns>
    public static uint Crc32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        uint[] t = SliceTables;
        uint crc = seed;
        int i = 0;
        for (; data.Length - i >= 8; i += 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            crc = t[(7 * 256) + (int)(one & 0xFF)] ^ t[(6 * 256) + (int)((one >> 8) & 0xFF)]
                ^ t[(5 * 256) + (int)((one >> 16) & 0xFF)] ^ t[(4 * 256) + (int)(one >> 24)]
                ^ t[(3 * 256) + (int)(two & 0xFF)] ^ t[(2 * 256) + (int)((two >> 8) & 0xFF)]
                ^ t[(1 * 256) + (int)((two >> 16) & 0xFF)] ^ t[(int)(two >> 24)];
        }

        for (; i < data.Length; i++)
        {
            crc = t[(int)((crc ^ data[i]) & 0xFF)] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>
    /// The 16-bit signature stored in page and block trailers (MS-PST 5.5): the offset XOR the BID, then the high and low
    /// 16 bits of the low 32 bits XORed together.
    /// </summary>
    /// <param name="ib">The absolute file offset of the page or block.</param>
    /// <param name="bid">The BID in the trailer.</param>
    /// <returns>The signature.</returns>
    public static ushort Signature(ulong ib, ulong bid)
    {
        uint x = (uint)(ib ^ bid);
        return (ushort)((x >> 16) ^ x);
    }

    /// <summary>Decodes (or encodes) data blocks written with NDB_CRYPT_PERMUTE (MS-PST 5.1), in place.</summary>
    /// <param name="data">The block data; the trailer and padding are never encoded.</param>
    /// <param name="encode">True to encode, false to decode.</param>
    public static void Permute(Span<byte> data, bool encode)
    {
        ReadOnlySpan<byte> table = encode ? NdbCipherTables.R : NdbCipherTables.I;
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = table[data[i]];
        }
    }

    /// <summary>Decodes (or encodes; the cipher is symmetric) data blocks written with NDB_CRYPT_CYCLIC (MS-PST 5.2), in place.</summary>
    /// <param name="data">The block data.</param>
    /// <param name="key">The low 32 bits of the block's BID.</param>
    public static void Cyclic(Span<byte> data, uint key)
    {
        ReadOnlySpan<byte> r = NdbCipherTables.R;
        ReadOnlySpan<byte> s = NdbCipherTables.S;
        ReadOnlySpan<byte> inv = NdbCipherTables.I;
        ushort w = (ushort)(key ^ (key >> 16));
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            b = (byte)(b + (byte)w);
            b = r[b];
            b = (byte)(b + (byte)(w >> 8));
            b = s[b];
            b = (byte)(b - (byte)(w >> 8));
            b = inv[b];
            b = (byte)(b - (byte)w);
            data[i] = b;
            w++;
        }
    }
}
