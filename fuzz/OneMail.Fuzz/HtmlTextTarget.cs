using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneMail.Bodies;

namespace RecursiveAI.OneMail.Fuzz;

// Fuzz target for the body conversions. The first byte picks the conversion and, with the second, its options; the rest is the input.
// Invariants are checked on every input: the same input gives the same output, output stays within its limit, HTML made from text
// parses without errors, and format=flowed written by Encode reads back to the same paragraphs.
internal static class HtmlTextTarget
{
    private const int Limit = 1 << 20;

    internal static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int mode = data[0] & 3;
        int flags = data[1];
        string text = Encoding.UTF8.GetString(data[2..]);
        switch (mode)
        {
            case 0:
                HtmlToTextRun(text, flags);
                break;
            case 1:
                TextToHtmlRun(text, flags);
                break;
            case 2:
                FlowedRun(text, flags);
                break;
            default:
                SelectRun(data[2..]);
                break;
        }
    }

    private static void HtmlToTextRun(string html, int flags)
    {
        HtmlToTextOptions o = new()
        {
            LineWidth = (flags & 1) == 0 ? 0 : 10 + (flags >> 4),
            Links = (LinkStyle)((flags >> 1) % 3),
            Images = (ImageStyle)((flags >> 3) % 3),
            ApplyStyleSheets = (flags & 128) == 0,
            MaxOutputChars = Limit,
            MaxNesting = 8,
            MaxTableCells = 200,
        };
        BodyConversion a = HtmlToText.Convert(html, o);
        Require(a.Text.Length <= Limit + 4096 + (o.LineWidth > 0 ? Limit : 0), "output beyond the limit");
        BodyConversion b = HtmlToText.Convert(html, o);
        Require(a.Text == b.Text, "not deterministic");
        Require(!a.Text.Contains('\r', StringComparison.Ordinal), "carriage return in the output");
    }

    private static void TextToHtmlRun(string text, int flags)
    {
        TextToHtmlOptions o = new() { Flowed = (flags & 1) != 0, DelSp = (flags & 2) != 0, DetectLinks = (flags & 4) == 0, MaxOutputChars = Limit };
        BodyConversion c = TextToHtml.Convert(text, o);
        Require(c.Text == TextToHtml.Convert(text, o).Text, "not deterministic");
        Document doc = Document.Parse(c.Text, new ParseOptions { RecordErrors = true });
        if (doc.ParseErrors.Count > 0 && !c.Issues.Any(i => i.Kind == BodyIssueKind.OutputTruncated))
        {
            throw new InvalidOperationException("HTML made from text has parse errors: " + doc.ParseErrors[0]);
        }
    }

    private static void FlowedRun(string text, int flags)
    {
        bool delSp = (flags & 1) != 0;
        int width = 20 + (flags >> 1);
        IReadOnlyList<FlowedParagraph> first = FormatFlowed.Parse(text, delSp);
        string encoded = FormatFlowed.Encode(first, width, delSp);
        IReadOnlyList<FlowedParagraph> back = FormatFlowed.Parse(encoded, delSp);
        if (back.Count != first.Count)
        {
            throw new InvalidOperationException($"paragraph count {first.Count} became {back.Count}; first: {Describe(first)}; back: {Describe(back)}");
        }

        for (int i = 0; i < first.Count; i++)
        {
            string expected = first[i].Text.Replace('\r', ' ').Replace('\n', ' ');
            if (expected != "-- ")
            {
                expected = expected.TrimEnd(' ');
            }

            Require(back[i].QuoteDepth == first[i].QuoteDepth && back[i].Text == expected, "paragraph changed through Encode and Parse");
        }

        _ = FormatFlowed.Decode(text, delSp);
    }

    private static string Describe(IReadOnlyList<FlowedParagraph> p) =>
        string.Join("|", p.Take(12).Select(x => x.QuoteDepth + ":" + x.Text.Replace("\n", "\\n", StringComparison.Ordinal)[..Math.Min(x.Text.Length, 30)]));

    private static void SelectRun(ReadOnlySpan<byte> mime)
    {
        IMailItem item = Mail.Parse(mime.ToArray());
        SelectedBody s = BodySelector.Select(item);
        Require((s.Kind == BodyKind.None) == (s.Body is null), "kind and body disagree");
        _ = BodySelector.GetText(item);
        _ = BodySelector.GetHtml(item);
        _ = item.BestBody;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
