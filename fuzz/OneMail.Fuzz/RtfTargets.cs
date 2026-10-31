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
        Require(r.Kind != RtfEncapsulation.None || r.Text.Length == 0, "text for a non-encapsulated document");
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

    // RTF to HTML: any bytes, small limits. Only typed results may come out; the HTML must be well formed (every tag closed in order, no
    // raw markup characters in text), the same input must give the same output, and the numbers it reports must be consistent.
    internal static void Html(ReadOnlySpan<byte> data)
    {
        var options = new RtfHtmlOptions { MaxGroupDepth = 64, MaxNodes = 20_000, MaxOutputChars = 1 << 20, MaxPictureBytes = 1 << 16, MaxTotalPictureBytes = 1 << 18 };
        RtfHtmlResult r = RtfHtmlConverter.Convert(data, options);
        Require(r.Html.Length <= (options.MaxOutputChars * 2) + (options.MaxTotalPictureBytes * 2), "HTML beyond the limit");
        Require(!HasLoneSurrogate(r.Html), "lone surrogate in the HTML");
        string text = r.Text;
        Require(!HasLoneSurrogate(text), "lone surrogate in the text");
        Require(IsWellFormed(r.Html), "HTML is not well formed");
        foreach (RtfDefect d in r.Defects)
        {
            Require(d.Offset >= 0 && d.Offset <= data.Length, "defect offset outside the input");
        }

        long picture = 0;
        foreach (RtfImage image in r.Images)
        {
            picture += image.Data.Length;
            Require(image.Data.Length <= options.MaxPictureBytes, "picture over the limit");
        }

        Require(picture <= options.MaxTotalPictureBytes, "pictures over the total limit");
        RtfHtmlResult again = RtfHtmlConverter.Convert(data, options);
        Require(again.Html == r.Html && again.Text == text, "conversion is not deterministic");
    }

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "hr", "img" };

    // The converter writes only tags with quoted attributes, so a small checker is enough: tags nest, and "<" only starts a tag.
    private static bool IsWellFormed(string html)
    {
        var open = new Stack<string>();
        int i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<')
            {
                if (html[i] == '>')
                {
                    return false;
                }

                i++;
                continue;
            }

            int j = i + 1;
            bool closing = j < html.Length && html[j] == '/';
            if (closing)
            {
                j++;
            }

            int nameStart = j;
            while (j < html.Length && char.IsAsciiLetterOrDigit(html[j]))
            {
                j++;
            }

            if (j == nameStart)
            {
                return false;
            }

            string name = html.Substring(nameStart, j - nameStart);
            bool inQuote = false;
            while (j < html.Length && (inQuote || html[j] != '>'))
            {
                if (html[j] == '"')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote && html[j] == '<')
                {
                    return false;
                }

                j++;
            }

            if (j >= html.Length)
            {
                return false;
            }

            if (closing)
            {
                if (open.Count == 0 || open.Pop() != name)
                {
                    return false;
                }
            }
            else if (!Void.Contains(name))
            {
                open.Push(name);
            }

            i = j + 1;
        }

        return open.Count == 0;
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
