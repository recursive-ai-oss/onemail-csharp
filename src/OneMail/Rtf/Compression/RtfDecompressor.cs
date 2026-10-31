using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMail.Rtf.Compression;

/// <summary>The compression type field of a compressed-RTF header (MS-OXRTFCP 2.1.3.1.1).</summary>
public enum RtfCompressionType
{
    /// <summary>The field holds neither known value; the stream is corrupt.</summary>
    Unknown = 0,

    /// <summary><c>LZFu</c> (0x75465A4C): LZ77 over a 4096-byte dictionary pre-loaded with common RTF text.</summary>
    Compressed = 1,

    /// <summary><c>MELA</c> (0x414C454D): the contents are the RTF itself, stored as is.</summary>
    Uncompressed = 2,
}

/// <summary>The 16-byte header in front of compressed RTF (the value of <c>PidTagRtfCompressed</c>).</summary>
/// <param name="CompressedSize">
/// The COMPSIZE field: the number of bytes that follow it, i.e. contents plus 12. The whole stream is this plus 4.
/// </param>
/// <param name="RawSize">The RAWSIZE field: the size of the RTF after decompression.</param>
/// <param name="Type">The COMPTYPE field.</param>
/// <param name="Crc">The CRC field: over the contents for <see cref="RtfCompressionType.Compressed"/>, zero otherwise.</param>
public readonly record struct RtfCompressionHeader(uint CompressedSize, uint RawSize, RtfCompressionType Type, uint Crc)
{
    /// <summary>The size of the header in bytes.</summary>
    public const int Size = 16;

    internal const uint LzfuMagic = 0x75465A4C;
    internal const uint MelaMagic = 0x414C454D;

    /// <summary>Reads a header from the start of <paramref name="data"/>.</summary>
    /// <param name="data">The compressed stream.</param>
    /// <param name="header">The fields; <see cref="Type"/> is <see cref="RtfCompressionType.Unknown"/> for an unrecognised type.</param>
    /// <returns><see langword="false"/> when fewer than 16 bytes are available. Never throws.</returns>
    public static bool TryRead(ReadOnlySpan<byte> data, out RtfCompressionHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        RtfCompressionType type = magic switch
        {
            LzfuMagic => RtfCompressionType.Compressed,
            MelaMagic => RtfCompressionType.Uncompressed,
            _ => RtfCompressionType.Unknown,
        };
        header = new RtfCompressionHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(data), BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), type,
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        return true;
    }
}

/// <summary>Limits for <see cref="RtfDecompressor"/>. Both are on by default and are checked before any allocation.</summary>
public sealed class RtfDecompressionOptions
{
    /// <summary>The most bytes the decompressor produces. Default 256 MiB. Larger claims or output stop with <see cref="RtfDefectKind.OutputLimitExceeded"/>.</summary>
    public long MaxOutputBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// The largest accepted ratio of the header's raw size to the compressed input length. Default 20. A dictionary
    /// reference expands two bytes into at most 17, so honest data never goes much beyond 8.5; the margin covers tiny
    /// inputs. A header claiming more is refused with <see cref="RtfDefectKind.ExpansionRatioExceeded"/>.
    /// </summary>
    public double MaxExpansionRatio { get; init; } = 20;
}

/// <summary>What <see cref="RtfDecompressor.Decompress"/> found.</summary>
public sealed class RtfDecompressionResult
{
    internal RtfDecompressionResult(byte[] data, RtfCompressionHeader? header, IReadOnlyList<RtfDefect> defects, bool crcVerified)
    {
        Data = data;
        Header = header;
        Defects = defects;
        CrcVerified = crcVerified;
    }

    /// <summary>The RTF bytes recovered; empty when nothing could be (see <see cref="Defects"/>).</summary>
    public byte[] Data { get; }

    /// <summary>The header, or <see langword="null"/> when the input was shorter than a header.</summary>
    public RtfCompressionHeader? Header { get; }

    /// <summary>Everything repaired or refused, in the order found.</summary>
    public IReadOnlyList<RtfDefect> Defects { get; }

    /// <summary><see langword="true"/> when the type is <c>LZFu</c> and the stored CRC matched the contents.</summary>
    public bool CrcVerified { get; }

    /// <summary><see langword="true"/> when no defect was found.</summary>
    public bool IsClean => Defects.Count == 0;
}

/// <summary>
/// MS-OXRTFCP decompression: the <c>LZFu</c> and <c>MELA</c> forms of <c>PidTagRtfCompressed</c>. Safe code over spans;
/// input is never trusted: sizes are checked against the limits before the output buffer is allocated, and damage is
/// reported as defects rather than exceptions.
/// </summary>
public static class RtfDecompressor
{
    // MS-OXRTFCP 2.1.2.1: the dictionary starts with this text at offset 0, and writing continues at offset 207.
    internal static readonly byte[] InitialDictionary =
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx"u8.ToArray();

    internal const int DictionarySize = 4096;
    internal const int InitialWritePosition = 207;

    /// <summary>
    /// Decompresses a <c>PidTagRtfCompressed</c> value. The CRC and the size fields are verified; the output is returned
    /// even when they disagree, with the disagreement as a defect.
    /// </summary>
    /// <param name="input">The compressed stream, header included.</param>
    /// <param name="options">Limits; <see langword="null"/> uses the defaults of <see cref="RtfDecompressionOptions"/>.</param>
    /// <returns>
    /// The RTF and the defects. Never throws for malformed data. A short header, an unknown type, or a claim above the
    /// limits yields empty data and one defect. Data that stops early yields what was decoded.
    /// </returns>
    public static RtfDecompressionResult Decompress(ReadOnlySpan<byte> input, RtfDecompressionOptions? options = null)
    {
        options ??= new RtfDecompressionOptions();
        var defects = new RtfDefectList();
        if (!RtfCompressionHeader.TryRead(input, out RtfCompressionHeader h))
        {
            defects.Add(RtfDefectKind.HeaderTruncated, 0, "fewer than 16 bytes");
            return new RtfDecompressionResult([], null, defects.Items, false);
        }

        if (h.Type == RtfCompressionType.Unknown)
        {
            defects.Add(RtfDefectKind.UnknownCompressionType, 8, "compression type is neither LZFu nor MELA");
            return new RtfDecompressionResult([], h, defects.Items, false);
        }

        // COMPSIZE counts the bytes after itself; the contents are everything after the 16-byte header up to there.
        long declaredEnd = (long)h.CompressedSize + 4;
        int end;
        if (declaredEnd < RtfCompressionHeader.Size)
        {
            defects.Add(RtfDefectKind.CompressedSizeMismatch, 0, "compressed size is smaller than the header");
            end = input.Length;
        }
        else if (declaredEnd > input.Length)
        {
            defects.Add(RtfDefectKind.CompressedSizeMismatch, 0, "compressed size is larger than the data present");
            end = input.Length;
        }
        else
        {
            end = (int)declaredEnd;
            if (end < input.Length)
            {
                defects.Add(RtfDefectKind.CompressedSizeMismatch, end, "bytes after the declared end were ignored");
            }
        }

        ReadOnlySpan<byte> contents = input[RtfCompressionHeader.Size..end];

        if (h.Type == RtfCompressionType.Uncompressed)
        {
            // MS-OXRTFCP 2.2.3.1: the contents are copied as they are and the CRC field is not validated.
            if (h.RawSize != contents.Length)
            {
                defects.Add(RtfDefectKind.RawSizeMismatch, 4, "raw size differs from the stored contents");
            }

            if (contents.Length > options.MaxOutputBytes)
            {
                defects.Add(RtfDefectKind.OutputLimitExceeded, 0, "stored contents exceed the output limit");
                contents = contents[..(int)Math.Min(options.MaxOutputBytes, int.MaxValue)];
            }

            return new RtfDecompressionResult(contents.ToArray(), h, defects.Items, false);
        }

        // Refuse implausible claims before allocating for them.
        if (h.RawSize > options.MaxOutputBytes || h.RawSize > Array.MaxLength)
        {
            defects.Add(RtfDefectKind.OutputLimitExceeded, 4, "raw size exceeds the output limit");
            return new RtfDecompressionResult([], h, defects.Items, false);
        }

        if (h.RawSize > Math.Max(input.Length, 64) * options.MaxExpansionRatio)
        {
            defects.Add(RtfDefectKind.ExpansionRatioExceeded, 4, "raw size is implausibly large for this input");
            return new RtfDecompressionResult([], h, defects.Items, false);
        }

        byte[] output = new byte[h.RawSize];
        int produced = Inflate(contents, ref output, options.MaxOutputBytes, defects, out bool sawEnd);

        if (!sawEnd)
        {
            defects.Add(RtfDefectKind.MissingEndMarker, input.Length, "the data ended before the end marker");
        }

        // MS-OXRTFCP 2.2.3: the CRC covers every byte of the contents, padding after the end marker included.
        uint crc = RtfCrc.Update(0, contents);
        bool crcOk = crc == h.Crc;
        if (!crcOk)
        {
            defects.Add(RtfDefectKind.CrcMismatch, 12, "stored CRC does not match the contents");
        }

        if (produced != h.RawSize)
        {
            defects.Add(RtfDefectKind.RawSizeMismatch, 4, "decoded " + produced + " bytes, header says " + h.RawSize);
        }

        if (produced != output.Length)
        {
            Array.Resize(ref output, produced);
        }

        return new RtfDecompressionResult(output, h, defects.Items, crcOk);
    }

    // MS-OXRTFCP 2.2.3.2. Returns the number of bytes written. The output array grows (up to maxOutput) when the data
    // holds more than the header's raw size announced.
    private static int Inflate(ReadOnlySpan<byte> src, ref byte[] output, long maxOutput, RtfDefectList defects, out bool sawEnd)
    {
        byte[] dict = new byte[DictionarySize];
        InitialDictionary.CopyTo(dict, 0);
        int wp = InitialWritePosition; // the dictionary's write offset
        int endOffset = InitialWritePosition; // bytes of the dictionary in use; offsets at or past it hold nothing yet
        int ip = 0;
        int op = 0;
        bool beyondReported = false;
        bool overflowReported = false;
        sawEnd = false;

        while (ip < src.Length)
        {
            int control = src[ip++];
            for (int bit = 0; bit < 8; bit++)
            {
                if (ip >= src.Length)
                {
                    return op;
                }

                if ((control & (1 << bit)) == 0)
                {
                    // A literal: one byte to the output and into the dictionary.
                    byte b = src[ip++];
                    if (op == output.Length && !Grow(ref output, op, maxOutput, defects, ref overflowReported))
                    {
                        return op;
                    }

                    output[op++] = b;
                    dict[wp] = b;
                    wp = (wp + 1) & (DictionarySize - 1);
                    if (endOffset < DictionarySize)
                    {
                        endOffset++;
                    }

                    continue;
                }

                // A reference: 12-bit dictionary offset, 4-bit length minus two, big-endian.
                if (ip + 1 >= src.Length)
                {
                    return op;
                }

                int reference = (src[ip] << 8) | src[ip + 1];
                ip += 2;
                int offset = reference >> 4;
                int length = (reference & 0xF) + 2;
                if (offset == wp)
                {
                    sawEnd = true; // a reference to the write position is the end marker
                    return op;
                }

                if (offset >= endOffset && !beyondReported)
                {
                    beyondReported = true;
                    defects.Add(RtfDefectKind.ReferenceBeyondDictionary, RtfCompressionHeader.Size + ip - 2, "dictionary reference to an unwritten position");
                }

                for (int k = 0; k < length; k++)
                {
                    byte b = dict[offset];
                    offset = (offset + 1) & (DictionarySize - 1);
                    if (op == output.Length && !Grow(ref output, op, maxOutput, defects, ref overflowReported))
                    {
                        return op;
                    }

                    output[op++] = b;
                    dict[wp] = b;
                    wp = (wp + 1) & (DictionarySize - 1);
                    if (endOffset < DictionarySize)
                    {
                        endOffset++;
                    }
                }
            }
        }

        return op;
    }

    private static bool Grow(ref byte[] output, int used, long maxOutput, RtfDefectList defects, ref bool reported)
    {
        if (used >= maxOutput || used >= Array.MaxLength)
        {
            if (!reported)
            {
                reported = true;
                defects.Add(RtfDefectKind.OutputLimitExceeded, 0, "decompressed data exceeds the output limit");
            }

            return false;
        }

        long next = Math.Min(Math.Max(1024L, (long)used * 2), Math.Min(maxOutput, Array.MaxLength));
        Array.Resize(ref output, (int)next);
        return true;
    }
}
