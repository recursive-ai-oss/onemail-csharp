using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneMail.Client.Sasl;

/// <summary>
/// MD4 (RFC 1320). The .NET class library does not carry it, and NTLM's password hash is defined on it (MS-NLMP 3.3.1). It is
/// broken as a general hash and is here for that one purpose only.
/// </summary>
public static class Md4
{
    /// <summary>The digest size in bytes.</summary>
    public const int HashSize = 16;

    /// <summary>Hashes a message.</summary>
    /// <param name="data">The message.</param>
    /// <returns>The 16-byte digest.</returns>
    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
        Span<uint> x = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];
        int offset = 0;
        // RFC 1320 §3.1-3.2: pad with 0x80 and zeros to 56 mod 64, then the bit length as a 64-bit little-endian value.
        long bitLength = (long)data.Length * 8;
        int total = ((data.Length + 8) / 64 + 1) * 64;
        while (offset < total)
        {
            block.Clear();
            int available = Math.Clamp(data.Length - offset, 0, 64);
            data.Slice(Math.Min(offset, data.Length), available).CopyTo(block);
            if (available < 64 && offset + available == data.Length)
            {
                block[available] = 0x80;
            }

            if (offset + 64 == total)
            {
                BinaryPrimitives.WriteInt64LittleEndian(block[56..], bitLength);
            }

            for (int i = 0; i < 16; i++)
            {
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);
            }

            uint aa = a, bb = b, cc = c, dd = d;
            // Round 1: F(x,y,z) = xy | ~xz
            for (int i = 0; i < 16; i++)
            {
                int k = i;
                int s = (i & 3) switch { 0 => 3, 1 => 7, 2 => 11, _ => 19 };
                uint t = BitOperations.RotateLeft(a + ((b & c) | (~b & d)) + x[k], s);
                (a, b, c, d) = (d, t, b, c);
            }

            // Round 2: G(x,y,z) = xy | xz | yz, constant 0x5a827999
            for (int i = 0; i < 16; i++)
            {
                int k = (i % 4) * 4 + i / 4;
                int s = (i & 3) switch { 0 => 3, 1 => 5, 2 => 9, _ => 13 };
                uint t = BitOperations.RotateLeft(a + ((b & c) | (b & d) | (c & d)) + x[k] + 0x5a827999, s);
                (a, b, c, d) = (d, t, b, c);
            }

            // Round 3: H(x,y,z) = x^y^z, constant 0x6ed9eba1
            for (int i = 0; i < 16; i++)
            {
                // 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15: the bit-reversed order of the index
                int k = ((i & 1) << 3) | ((i & 2) << 1) | ((i & 4) >> 1) | ((i & 8) >> 3);
                int s = (i & 3) switch { 0 => 3, 1 => 9, 2 => 11, _ => 15 };
                uint t = BitOperations.RotateLeft(a + (b ^ c ^ d) + x[k] + 0x6ed9eba1, s);
                (a, b, c, d) = (d, t, b, c);
            }

            a += aa; b += bb; c += cc; d += dd;
            offset += 64;
        }

        byte[] digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0), a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d);
        return digest;
    }
}
