using System.Text;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;

namespace RecursiveAI.OneMail.Tests.Rtf;

public class CompressionTests
{
    // MS-OXRTFCP 3.1.1.1 and 3.1.2.1: the specification's two worked examples, byte for byte.
    private static readonly byte[] Example1 = Convert.FromHexString(
        "2d0000002b0000004c5a4675f1c5c7a7" + "03000a007263706731323542320af320" + "68656c0900206277" + "05b06c647d0a800f" + "a0");

    private static readonly byte[] Example2 = Convert.FromHexString("1a0000001c0000004c5a4675e2d44b51" + "410004205758595a0d6e7d010eb0");

    [Fact]
    public void InitialDictionaryIs207Bytes() => Assert.Equal(207, RtfDecompressor.InitialDictionary.Length);

    [Fact]
    public void SpecExample1Decompresses()
    {
        RtfDecompressionResult r = RtfDecompressor.Decompress(Example1);
        Assert.True(r.IsClean, string.Join(";", r.Defects));
        Assert.True(r.CrcVerified);
        Assert.Equal(0x2b, r.Data.Length);
        Assert.StartsWith("{\\rtf1\\ansi", Encoding.ASCII.GetString(r.Data), StringComparison.Ordinal);
        Assert.Contains("hello world", Encoding.ASCII.GetString(r.Data), StringComparison.Ordinal);
    }

    [Fact]
    public void SpecExample2ReadsAReferenceThatCrossesTheWritePosition()
    {
        RtfDecompressionResult r = RtfDecompressor.Decompress(Example2);
        Assert.True(r.IsClean, string.Join(";", r.Defects));
        Assert.Equal("{\\rtf1 WXYZWXYZWXYZWXYZWXYZ}", Encoding.ASCII.GetString(r.Data));
    }

    [Fact]
    public void CompressorReproducesTheSpecExamples()
    {
        Assert.Equal(Example1, RtfCompressor.Compress(RtfDecompressor.Decompress(Example1).Data));
        Assert.Equal(Example2, RtfCompressor.Compress("{\\rtf1 WXYZWXYZWXYZWXYZWXYZ}"u8));
    }

    public static IEnumerable<object[]> Inputs()
    {
        var rng = new Random(12);
        byte[] random = new byte[10_000];
        rng.NextBytes(random);
        byte[] texty = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => "\\par line " + (i % 97) + " {\\b bold}\r\n")));
        byte[] runs = new byte[100_000];
        Array.Fill(runs, (byte)'a');
        byte[] allBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        yield return new object[] { Array.Empty<byte>() };
        yield return new object[] { new byte[] { 0x7B } };
        yield return new object[] { "{\\rtf1}"u8.ToArray() };
        yield return new object[] { random };
        yield return new object[] { texty };
        yield return new object[] { runs };
        yield return new object[] { allBytes };
        yield return new object[] { Enumerable.Range(0, 8).Select(i => (byte)(i * 31)).ToArray() }; // exactly one full run of literals
        yield return new object[] { Enumerable.Range(0, 16).Select(i => (byte)(i * 31 + 1)).ToArray() };
        yield return new object[] { Encoding.ASCII.GetBytes(new string('x', 4095) + "y" + new string('x', 4097)) }; // distances at the window edge
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void CompressedOutputRoundTrips(byte[] input)
    {
        byte[] packed = RtfCompressor.Compress(input);
        RtfDecompressionResult r = RtfDecompressor.Decompress(packed);
        Assert.True(r.IsClean, string.Join(";", r.Defects));
        Assert.True(r.CrcVerified);
        Assert.Equal(input, r.Data);
        Assert.True(packed.Length <= RtfCompressor.MaxCompressedLength(input.Length));

        byte[] stored = RtfCompressor.Compress(input, RtfCompressionType.Uncompressed);
        RtfDecompressionResult s = RtfDecompressor.Decompress(stored);
        Assert.True(s.IsClean);
        Assert.Equal(input, s.Data);
    }

    [Fact]
    public void RepetitiveRtfCompressesWell()
    {
        byte[] texty = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => "\\par line " + (i % 97) + " {\\b bold}\r\n")));
        Assert.True(RtfCompressor.Compress(texty).Length < texty.Length / 4);
    }

    [Fact]
    public void ShortHeaderIsADefect()
    {
        RtfDecompressionResult r = RtfDecompressor.Decompress(new byte[15]);
        Assert.Empty(r.Data);
        Assert.Equal(RtfDefectKind.HeaderTruncated, Assert.Single(r.Defects).Kind);
    }

    [Fact]
    public void UnknownTypeIsADefect()
    {
        byte[] bad = (byte[])Example1.Clone();
        bad[8] = (byte)'X';
        RtfDecompressionResult r = RtfDecompressor.Decompress(bad);
        Assert.Empty(r.Data);
        Assert.Equal(RtfDefectKind.UnknownCompressionType, Assert.Single(r.Defects).Kind);
    }

    [Fact]
    public void FlippedByteGivesACrcDefectAndStillReturnsOutput()
    {
        byte[] bad = (byte[])Example1.Clone();
        bad[0x15] ^= 0x01; // a literal
        RtfDecompressionResult r = RtfDecompressor.Decompress(bad);
        Assert.False(r.CrcVerified);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.CrcMismatch);
        Assert.Equal(0x2b, r.Data.Length);
    }

    [Fact]
    public void CutOffDataReportsMissingEndMarkerAndSize()
    {
        byte[] cut = Example1[..30];
        RtfDecompressionResult r = RtfDecompressor.Decompress(cut);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.CompressedSizeMismatch);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.MissingEndMarker);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.RawSizeMismatch);
        Assert.NotEmpty(r.Data);
    }

    [Fact]
    public void ExtraBytesAfterTheDeclaredEndAreIgnoredWithADefect()
    {
        byte[] more = [.. Example1, 0, 0, 0];
        RtfDecompressionResult r = RtfDecompressor.Decompress(more);
        Assert.Equal(RtfDecompressor.Decompress(Example1).Data, r.Data);
        Assert.Equal(RtfDefectKind.CompressedSizeMismatch, Assert.Single(r.Defects).Kind);
    }

    [Fact]
    public void HugeRawSizeIsRefusedBeforeAllocating()
    {
        byte[] bomb = (byte[])Example1.Clone();
        BitConverter.GetBytes(0x20000000u).CopyTo(bomb, 4);
        RtfDecompressionResult r = RtfDecompressor.Decompress(bomb, new RtfDecompressionOptions { MaxOutputBytes = long.MaxValue });
        Assert.Empty(r.Data);
        Assert.Equal(RtfDefectKind.ExpansionRatioExceeded, Assert.Single(r.Defects).Kind);

        RtfDecompressionResult r2 = RtfDecompressor.Decompress(bomb);
        Assert.Equal(RtfDefectKind.OutputLimitExceeded, Assert.Single(r2.Defects).Kind);
    }

    [Fact]
    public void OutputLimitStopsEarly()
    {
        byte[] packed = RtfCompressor.Compress(new byte[50_000]);
        RtfDecompressionResult r = RtfDecompressor.Decompress(packed, new RtfDecompressionOptions { MaxOutputBytes = 1000 });
        Assert.Empty(r.Data);
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.OutputLimitExceeded);
    }

    [Fact]
    public void LyingRawSizeIsRepairedWithADefect()
    {
        byte[] packed = RtfCompressor.Compress("{\\rtf1 hello hello hello}"u8);
        BitConverter.GetBytes(5u).CopyTo(packed, 4);
        RtfDecompressionResult r = RtfDecompressor.Decompress(packed);
        Assert.Equal("{\\rtf1 hello hello hello}", Encoding.ASCII.GetString(r.Data));
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.RawSizeMismatch);
    }

    [Fact]
    public void EveryTruncationAndBitFlipIsHandled()
    {
        byte[] good = RtfCompressor.Compress(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("{\\rtf1\\ansi hello \\par world}", 40))));
        for (int n = 0; n < good.Length; n++)
        {
            RtfDecompressor.Decompress(good.AsSpan(0, n));
        }

        for (int i = 0; i < good.Length; i++)
        {
            byte[] b = (byte[])good.Clone();
            b[i] ^= 0x55;
            RtfDecompressor.Decompress(b);
        }
    }

    [Fact]
    public void NothingCompressesToJustAnEndMarker()
    {
        byte[] packed = RtfCompressor.Compress([]);
        Assert.Equal("01" + "0CF0", Convert.ToHexString(packed[16..]));
        Assert.Empty(RtfDecompressor.Decompress(packed).Data);
    }

    [Fact]
    public void OutlooksEmptyFormIsOneNulByte()
    {
        // MS-OXRTFCP 2.3.3.2 step 8's note: Outlook writes "02 00 0D 00" for an empty body.
        byte[] contents = [0x02, 0x00, 0x0D, 0x00];
        byte[] stream = new byte[16 + contents.Length];
        BitConverter.GetBytes((uint)(contents.Length + 12)).CopyTo(stream, 0);
        BitConverter.GetBytes(1u).CopyTo(stream, 4);
        BitConverter.GetBytes(0x75465A4Cu).CopyTo(stream, 8);
        BitConverter.GetBytes(RtfCrc.Update(0, contents)).CopyTo(stream, 12);
        contents.CopyTo(stream, 16);
        RtfDecompressionResult r = RtfDecompressor.Decompress(stream);
        Assert.True(r.IsClean, string.Join(";", r.Defects));
        Assert.Equal(new byte[] { 0 }, r.Data);
    }

    [Fact]
    public void StoredFormIgnoresTheCrcFieldAndReportsASizeMismatch()
    {
        byte[] stored = RtfCompressor.Compress("{\\rtf1}"u8, RtfCompressionType.Uncompressed);
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(stored, 12);
        Assert.True(RtfDecompressor.Decompress(stored).IsClean);
        BitConverter.GetBytes(99u).CopyTo(stored, 4);
        Assert.Equal(RtfDefectKind.RawSizeMismatch, Assert.Single(RtfDecompressor.Decompress(stored).Defects).Kind);
    }

    [Fact]
    public void CrcMatchesTheSpecificationsReflectedPolynomial()
    {
        // A zero-start, no-final-xor CRC differs from the zip CRC-32 of the same bytes; the stored CRC of spec example 1 pins it.
        Assert.Equal(0xA7C7C5F1u, RtfCrc.Update(0, Example1.AsSpan(16)));
    }
}
