using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Rtf.Compression;

/// <summary>
/// The 32-bit checksum of the compressed-RTF header. It is the common reflected CRC-32 polynomial (0xEDB88320) but with
/// a zero start value and no final inversion, so it differs from the CRC-32 that zip and zlib report.
/// </summary>
internal static class RtfCrc
{
    // The 256-entry table of the compressed-RTF specification equals the table for the reflected polynomial; the other
    // seven are the usual extra tables that let the update consume eight bytes at a time.
    private static readonly uint[] Tables = Build();

    private static uint[] Build()
    {
        uint[] t = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }

            t[n] = c;
        }

        for (int n = 0; n < 256; n++)
        {
            uint c = t[n];
            for (int k = 1; k < 8; k++)
            {
                c = t[(int)(c & 0xFF)] ^ (c >> 8);
                t[(k * 256) + n] = c;
            }
        }

        return t;
    }

    /// <summary>MS-OXRTFCP 2.1.3.2: folds <paramref name="data"/> into <paramref name="crc"/> (start with 0).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Tables;
        int i = 0;
        while (data.Length - i >= 8)
        {
            uint lo = crc ^ BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4));
            crc = t[(7 * 256) + (int)(lo & 0xFF)] ^ t[(6 * 256) + (int)((lo >> 8) & 0xFF)]
                ^ t[(5 * 256) + (int)((lo >> 16) & 0xFF)] ^ t[(4 * 256) + (int)(lo >> 24)]
                ^ t[(3 * 256) + (int)(hi & 0xFF)] ^ t[(2 * 256) + (int)((hi >> 8) & 0xFF)]
                ^ t[256 + (int)((hi >> 16) & 0xFF)] ^ t[(int)(hi >> 24)];
            i += 8;
        }

        for (; i < data.Length; i++)
        {
            crc = t[(int)((crc ^ data[i]) & 0xFF)] ^ (crc >> 8);
        }

        return crc;
    }
}
