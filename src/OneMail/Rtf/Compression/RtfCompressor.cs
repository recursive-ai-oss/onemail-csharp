using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Rtf.Compression;

/// <summary>
/// MS-OXRTFCP compression: writes the <c>LZFu</c> form (or the stored <c>MELA</c> form) of an RTF document. The
/// output decompresses back to the input exactly with <see cref="RtfDecompressor"/> and with Outlook's reader; it
/// is not required to match Outlook's own compressed bytes, because the match search is ours.
/// </summary>
public static class RtfCompressor
{
    private const int MaxMatch = 17; // 4-bit length field plus two
    private const int Window = RtfDecompressor.DictionarySize - 1; // a distance of 4096 would be the end marker's offset
    private const int MaxChain = 128; // candidates tried per position: bounds the time at a small cost in size

    /// <summary>The most bytes <see cref="Compress"/> can produce for <paramref name="rawLength"/> bytes of input.</summary>
    /// <param name="rawLength">The input length; must not be negative.</param>
    /// <returns>An upper bound on the compressed length, header included.</returns>
    public static long MaxCompressedLength(int rawLength) =>
        RtfCompressionHeader.Size + (long)rawLength + (rawLength / 8) + 1 + 3;

    /// <summary>Compresses (or stores) RTF.</summary>
    /// <param name="rtf">The RTF bytes.</param>
    /// <param name="type"><see cref="RtfCompressionType.Compressed"/> (default) or <see cref="RtfCompressionType.Uncompressed"/>.</param>
    /// <returns>The stream to store in <c>PidTagRtfCompressed</c>, header included.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is <see cref="RtfCompressionType.Unknown"/>.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> rtf, RtfCompressionType type = RtfCompressionType.Compressed)
    {
        if (type == RtfCompressionType.Uncompressed)
        {
            // MS-OXRTFCP 2.3.3.1: copy the data after the header; the CRC field is zero.
            byte[] stored = new byte[RtfCompressionHeader.Size + rtf.Length];
            WriteHeader(stored, (uint)rtf.Length + 12, (uint)rtf.Length, RtfCompressionHeader.MelaMagic, 0);
            rtf.CopyTo(stored.AsSpan(RtfCompressionHeader.Size));
            return stored;
        }

        if (type != RtfCompressionType.Compressed)
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        byte[] buffer = new byte[MaxCompressedLength(rtf.Length)];
        int length = CompressLzfu(rtf, buffer);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static void WriteHeader(Span<byte> dst, uint compSize, uint rawSize, uint magic, uint crc)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, compSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], magic);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[12..], crc);
    }

    // MS-OXRTFCP 2.3.3.2. The dictionary is the last 4095 bytes of "initial text followed by the input so far", so the
    // search runs over one flat buffer of that stream, and a dictionary offset is the stream position modulo 4096.
    // Matches may overlap the bytes being written (distance < length); the decoder reads them back one at a time, so
    // that is the same as comparing against the stream itself.
    private static int CompressLzfu(ReadOnlySpan<byte> rtf, byte[] output)
    {
        int init = RtfDecompressor.InitialWritePosition;
        int total = init + rtf.Length;
        byte[] stream = ArrayPool<byte>.Shared.Rent(total);
        int[] head = ArrayPool<int>.Shared.Rent(65536); // last position + 1 of each byte pair; 0 = none
        int[] prev = ArrayPool<int>.Shared.Rent(RtfDecompressor.DictionarySize); // chain by position modulo 4096, same encoding
        try
        {
            RtfDecompressor.InitialDictionary.CopyTo(stream, 0);
            rtf.CopyTo(stream.AsSpan(init));
            Array.Clear(head, 0, 65536);

            int inserted = 0; // positions below this are in the hash chains
            int cur = init;
            int op = RtfCompressionHeader.Size;
            int controlAt = op;
            int control = 0;
            int bit = 0;
            op++; // room for the first control byte

            while (true)
            {
                bool end = cur >= total;
                // Make every position before 'cur' findable (a position needs its successor byte to form a pair).
                while (inserted < cur && inserted + 1 < total)
                {
                    int key = (stream[inserted] << 8) | stream[inserted + 1];
                    prev[inserted & (RtfDecompressor.DictionarySize - 1)] = head[key];
                    head[key] = inserted + 1;
                    inserted++;
                }

                int matchLength = 0;
                int matchPos = 0;
                if (!end && total - cur >= 2)
                {
                    int maxLen = Math.Min(MaxMatch, total - cur);
                    int cand = head[(stream[cur] << 8) | stream[cur + 1]] - 1;
                    int tries = 0;
                    while (cand >= 0 && cur - cand <= Window && tries++ < MaxChain)
                    {
                        int l = 0;
                        while (l < maxLen && stream[cand + l] == stream[cur + l])
                        {
                            l++;
                        }

                        // Candidates come newest first; on a tie the older one wins, as in the specification's oldest-first scan,
                        // so the output equals the reference bytes whenever the chain limit does not cut the search short.
                        if (l >= 2 && l >= matchLength)
                        {
                            matchLength = l;
                            matchPos = cand;
                        }

                        int next = prev[cand & (RtfDecompressor.DictionarySize - 1)] - 1;
                        if (next >= cand)
                        {
                            break;
                        }

                        cand = next;
                    }
                }

                if (end)
                {
                    // Step 8: the end marker is a reference to the current write offset, length zero.
                    int wo = cur & (RtfDecompressor.DictionarySize - 1);
                    control |= 1 << bit;
                    output[op++] = (byte)(wo >> 4);
                    output[op++] = (byte)((wo & 0xF) << 4);
                    output[controlAt] = (byte)control;
                    break;
                }

                if (matchLength >= 2)
                {
                    int off = matchPos & (RtfDecompressor.DictionarySize - 1);
                    control |= 1 << bit;
                    output[op++] = (byte)(off >> 4);
                    output[op++] = (byte)(((off & 0xF) << 4) | (matchLength - 2));
                    cur += matchLength;
                }
                else
                {
                    output[op++] = stream[cur++];
                }

                if (++bit == 8)
                {
                    output[controlAt] = (byte)control;
                    controlAt = op++;
                    control = 0;
                    bit = 0;
                }
            }

            ReadOnlySpan<byte> contents = output.AsSpan(RtfCompressionHeader.Size, op - RtfCompressionHeader.Size);
            WriteHeader(output, (uint)contents.Length + 12, (uint)rtf.Length, RtfCompressionHeader.LzfuMagic, RtfCrc.Update(0, contents));
            return op;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stream);
            ArrayPool<int>.Shared.Return(head);
            ArrayPool<int>.Shared.Return(prev);
        }
    }
}
