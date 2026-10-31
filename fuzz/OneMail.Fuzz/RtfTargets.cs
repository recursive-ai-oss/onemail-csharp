using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;

namespace RecursiveAI.OneMail.Fuzz;

// Fuzz targets for the RTF code. Only typed results may come out (defects, empty results); the invariants below are
// checked on every input, so a wrong answer is as much a finding as a crash.
internal static class RtfTargets
{
    private const int MaxOutput = 1 << 22;

    // Compressed RTF: any bytes through the decompressor with a small output limit; what it produces must compress and
    // decompress back to itself.
    internal static void Decompress(ReadOnlySpan<byte> data)
    {
        RtfDecompressionResult r = RtfDecompressor.Decompress(data, new RtfDecompressionOptions { MaxOutputBytes = MaxOutput });
        Require(r.Data.Length <= MaxOutput, "output beyond the limit");
        Require(r.Header is not null || r.Data.Length == 0, "output without a header");
        if (r.IsClean && r.Header is { Type: RtfCompressionType.Compressed })
        {
            Require(r.CrcVerified, "clean but the CRC was not verified");
        }

        if (r.Data.Length <= 1 << 16)
        {
            RtfDecompressionResult back = RtfDecompressor.Decompress(RtfCompressor.Compress(r.Data));
            Require(back.IsClean && back.CrcVerified && back.Data.AsSpan().SequenceEqual(r.Data), "compress/decompress round trip");
        }
    }

    // RTF text: the tokenizer to the end, the detector, de-encapsulation, and what it extracts must survive encapsulation.
    internal static void Deencapsulate(ReadOnlySpan<byte> data)
    {
        var reader = new RtfReader(data, new RtfReaderOptions { MaxGroupDepth = 64 });
        int end = -1;
        while (reader.Read())
        {
            Require(reader.Offset >= end && reader.End >= reader.Offset, "token offsets go backwards");
            end = reader.End;
        }

        RtfEncapsulation detected = RtfDeencapsulator.Detect(data);
        RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate(data, new RtfDeencapsulationOptions { MaxGroupDepth = 64 });
        Require((r.Kind == RtfEncapsulation.None) == (detected == RtfEncapsulation.None), "Detect and Deencapsulate disagree");
        Require(r.Kind == RtfEncapsulation.None ? r.Text.Length == 0 : r.Text is not null, "text for a non-encapsulated document");
        Require(!HasLoneSurrogate(r.Text), "lone surrogate in the result");
        if (r.Kind == RtfEncapsulation.None || r.Text.Length > 1 << 16)
        {
            return;
        }

        byte[] rtf = r.Kind == RtfEncapsulation.Html ? RtfEncapsulator.EncapsulateHtml(r.Text) : RtfEncapsulator.EncapsulateText(r.Text);
        RtfDeencapsulationResult again = RtfDeencapsulator.Deencapsulate(rtf);
        Require(again.Kind == r.Kind && again.Defects.Count == 0, "encapsulated text is not recognised or has defects");
        Require(again.Text == CrLf(r.Text), "encapsulate/de-encapsulate round trip");
    }

    private static string CrLf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

    private static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException(what);
        }
    }
}
