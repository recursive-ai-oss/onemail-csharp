using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;

namespace RecursiveAI.OneMail.Tests.Rtf;

public class RtfHtmlTests
{
    private const string Head = "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss\\fcharset0 Arial;}{\\f1\\froman Times New Roman;}{\\f2\\fmodern\\fcharset0 Courier New;}}" +
        "{\\colortbl;\\red255\\green0\\blue0;\\red0\\green128\\blue0;\\red255\\green255\\blue0;}";

    private static RtfHtmlResult Conv(string body, RtfHtmlOptions? options = null) =>
        RtfHtmlConverter.Convert(Encoding.Latin1.GetBytes(Head + body + "}"), options);

    private static MapiProperty S(ushort id, string v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    private static MapiProperty Bin(ushort id, byte[] v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.Binary), v);

    private static string Inner(RtfHtmlResult r)
    {
        const string open = "style=\"font-family:'Arial',sans-serif;font-size:12pt\">\n";
        int i = r.Html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(i >= 0, r.Html);
        return r.Html[(i + open.Length)..^"</div>\n".Length];
    }

    [Fact]
    public void CharacterFormattingBecomesSemanticTags()
    {
        RtfHtmlResult r = Conv("\\pard plain {\\b bold} {\\i italic} {\\ul under} {\\strike gone} x{\\super 2} H{\\sub 2}O {\\cf1 red} {\\highlight3 marked}\\par");
        string h = Inner(r);
        Assert.Contains("<b>bold</b>", h, StringComparison.Ordinal);
        Assert.Contains("<i>italic</i>", h, StringComparison.Ordinal);
        Assert.Contains("<u>under</u>", h, StringComparison.Ordinal);
        Assert.Contains("<s>gone</s>", h, StringComparison.Ordinal);
        Assert.Contains("<sup>2</sup>", h, StringComparison.Ordinal);
        Assert.Contains("<sub>2</sub>", h, StringComparison.Ordinal);
        Assert.Contains("<span style=\"color:#ff0000\">red</span>", h, StringComparison.Ordinal);
        Assert.Contains("background-color:#ffff00", h, StringComparison.Ordinal);
        Assert.Equal("plain bold italic under gone x2 H2O red marked\r\n", r.Text);
    }

    [Fact]
    public void FontAndSizeDifferingFromTheBodyAreInline()
    {
        RtfHtmlResult r = Conv("\\pard\\fs24 body body body {\\f2\\fs20 code}\\par");
        string h = Inner(r);
        Assert.Contains("<span style=\"font-family:'Courier New',monospace;font-size:10pt\">code</span>", h, StringComparison.Ordinal);
    }

    [Fact]
    public void ParagraphsCarryAlignmentIndentAndSpacing()
    {
        RtfHtmlResult r = Conv("\\pard\\qc\\sb120\\sa240\\li720\\fi-360 centred\\par\\pard\\qr right\\par\\pard\\par\\pard after\\par");
        string h = Inner(r);
        Assert.Contains("<p style=\"text-align:center;margin-top:6pt;margin-bottom:12pt;margin-left:36pt;text-indent:-18pt\">centred</p>", h, StringComparison.Ordinal);
        Assert.Contains("text-align:right", h, StringComparison.Ordinal);
        Assert.Contains("<p style=\"margin-top:0;margin-bottom:0\"><br></p>", h, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingEmptyParagraphsAreDropped()
    {
        RtfHtmlResult r = Conv("\\pard text\\par\\par\\par");
        Assert.Equal("text\r\n", r.Text);
    }

    [Fact]
    public void UnicodeEscapesAndFallbackAreRead()
    {
        RtfHtmlResult r = Conv("\\pard caf\\'e9 \\u8364? \\uc2\\u26085\\'93\\'fa x \\uc1\\u-10179?\\u-8704? done\\par");
        Assert.Equal("caf\u00e9 \u20ac \u65e5 x \ud83d\ude00 done\r\n", r.Text);
    }

    [Fact]
    public void CodePageFromTheFontDecidesHighBytes()
    {
        string rtf = "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss\\fcharset0 Arial;}{\\f1\\fnil\\fcharset204 Arial;}{\\f2\\fnil\\fcharset128 MS Gothic;}}\\pard {\\f1\\'cf\\'f0\\'e8\\'e2\\'e5\\'f2} {\\f2\\'93\\'fa\\'96\\'7b}\\par}";
        RtfHtmlResult r = RtfHtmlConverter.Convert(Encoding.Latin1.GetBytes(rtf));
        Assert.Equal("\u041f\u0440\u0438\u0432\u0435\u0442 \u65e5\u672c\r\n", r.Text);
        Assert.Empty(r.Defects);
    }

    [Fact]
    public void HiddenTextHeadersAndUnknownDestinationsAreLeftOut()
    {
        RtfHtmlResult r = Conv("{\\header head}{\\*\\unknown skipped}{\\footnote note}\\pard shown{\\v hidden}\\par");
        Assert.Equal("shown\r\n", r.Text);
    }

    [Fact]
    public void SpecialCharactersAreEscapedAndSpacesKept()
    {
        RtfHtmlResult r = Conv("\\pard a<b & \"c\"  d\\~e\\tab f\\line g\\endash \\bullet \\par");
        string h = Inner(r);
        Assert.Contains("a&lt;b &amp; &quot;c&quot; &nbsp;d&nbsp;e<span style=\"white-space:pre\">\t</span>f<br>g\u2013\u2022", h, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperlinksBecomeAnchorsAndTextShowsTheTarget()
    {
        RtfHtmlResult r = Conv("\\pard see {\\field{\\*\\fldinst{HYPERLINK \"http://example.com/a?b=1&c=2\"}}{\\fldrslt{\\cf1\\ul the page}}} and {\\field{\\*\\fldinst HYPERLINK \"mailto:a@example.com\"}{\\fldrslt a@example.com}} and {\\field{\\*\\fldinst PAGE}{\\fldrslt 3}}\\par");
        string h = Inner(r);
        Assert.Contains("<a href=\"http://example.com/a?b=1&amp;c=2\"><span style=\"color:#ff0000\"><u>the page</u></span></a>", h, StringComparison.Ordinal);
        Assert.Contains("<a href=\"mailto:a@example.com\">a@example.com</a>", h, StringComparison.Ordinal);
        Assert.Equal("see the page <http://example.com/a?b=1&c=2> and a@example.com and 3\r\n", r.Text);
    }

    [Fact]
    public void ListsFromTheListTableNestAndNumber()
    {
        const string tables = "{\\*\\listtable{\\list\\listtemplateid1{\\listlevel\\levelnfc23\\levelstartat1{\\leveltext\\'01\\u-3913 ?;}{\\levelnumbers;}\\li720}{\\listlevel\\levelnfc23\\levelstartat1{\\leveltext\\'01o;}{\\levelnumbers;}}\\listid10}" +
            "{\\list\\listtemplateid2{\\listlevel\\levelnfc0\\levelstartat1{\\leveltext\\'02\\'00.;}{\\levelnumbers\\'01;}}{\\listlevel\\levelnfc4\\levelstartat1{\\leveltext\\'02\\'01.;}{\\levelnumbers\\'01;}}\\listid20}}" +
            "{\\*\\listoverridetable{\\listoverride\\listid10\\listoverridecount0\\ls1}{\\listoverride\\listid20\\listoverridecount0\\ls2}}";
        RtfHtmlResult r = RtfHtmlConverter.Convert(Encoding.Latin1.GetBytes(Head.Replace("{\\colortbl", tables + "{\\colortbl") +
            "\\pard\\ls1\\ilvl0{\\listtext\\'b7\\tab}one\\par\\pard\\ls1\\ilvl1{\\listtext o\\tab}nested\\par\\pard\\ls1\\ilvl0 two\\par" +
            "\\pard between\\par" +
            "\\pard\\ls2\\ilvl0 first\\par\\pard\\ls2\\ilvl1 sub a\\par\\pard\\ls2\\ilvl1 sub b\\par\\pard\\ls2\\ilvl0 second\\par}"));
        string h = Inner(r);
        Assert.Contains("<ul>\n<li>one<ul>\n<li>nested</li>\n</ul>\n</li>\n<li>two</li>\n</ul>\n", h, StringComparison.Ordinal);
        Assert.Contains("<ol>\n<li>first<ol type=\"a\">\n<li>sub a</li>\n<li>sub b</li>\n</ol>\n</li>\n<li>second</li>\n</ol>\n", h, StringComparison.Ordinal);
        Assert.Equal("\u2022 one\r\n  o nested\r\n\u2022 two\r\nbetween\r\n1. first\r\n  a. sub a\r\n  b. sub b\r\n2. second\r\n".Replace("  o nested", "  \u2022 nested"), r.Text);
    }

    [Fact]
    public void OldStyleNumberingMakesLists()
    {
        RtfHtmlResult r = Conv("\\pard{\\*\\pn\\pnlvlblt\\pnf1{\\pntxtb\\'b7}}{\\pntext\\'b7\\tab}apple\\par\\pard{\\*\\pn\\pnlvlblt{\\pntxtb\\'b7}}{\\pntext\\'b7\\tab}pear\\par" +
            "\\pard{\\*\\pn\\pnlvlbody\\pndec\\pnstart3{\\pntxta.}}{\\pntext 3.\\tab}three\\par");
        string h = Inner(r);
        Assert.Contains("<ul>\n<li>apple</li>\n<li>pear</li>\n</ul>\n<ol start=\"3\">\n<li>three</li>\n</ol>\n", h, StringComparison.Ordinal);
    }

    [Fact]
    public void TablesKeepRowsCellsAndMerges()
    {
        RtfHtmlResult r = Conv("\\trowd\\trgaph108\\clmgf\\clbrdrt\\brdrs\\brdrw10\\cellx3000\\clmrg\\cellx6000\\clvmgf\\cellx9000\\pard\\intbl merged\\cell\\cell top\\cell\\row" +
            "\\trowd\\cellx3000\\cellx6000\\clvmrg\\cellx9000\\pard\\intbl a\\cell b\\cell\\cell\\row\\pard after\\par");
        string h = Inner(r);
        Assert.Contains("<table style=\"border-collapse:collapse\">", h, StringComparison.Ordinal);
        Assert.Contains("<td colspan=\"2\" style=\"width:300pt;vertical-align:top;border-top:0.5pt solid #000000\"><p style=\"margin-top:0;margin-bottom:0\">merged</p>\n</td>", h, StringComparison.Ordinal);
        Assert.Contains("rowspan=\"2\"", h, StringComparison.Ordinal);
        Assert.Equal("merged\ttop\r\na\tb\r\nafter\r\n", r.Text);
    }

    [Fact]
    public void PicturesAreInlinedOrReferencedAndMetafilesBecomePlaceholders()
    {
        const string png = "89504e470d0a1a0a0000000d49484452";
        RtfHtmlResult r = Conv("\\pard before {\\pict\\pngblip\\picw10\\pich10\\picwgoal300\\pichgoal150\\picscalex200\\picscaley100 " + png + "}" +
            "{\\*\\shppict{\\pict\\jpegblip ffd8ffe0}}{\\nonshppict{\\pict\\wmetafile8 d7cdc69a0000}} {\\pict\\emfblip 01000000" + new string('0', 64) + "20454d46} after\\par");
        Assert.Equal(3, r.Images.Count);
        Assert.Equal("image/png", r.Images[0].ContentType);
        Assert.Equal(40, r.Images[0].WidthPx);
        Assert.Equal(10, r.Images[0].HeightPx);
        Assert.Equal("image/jpeg", r.Images[1].ContentType);
        Assert.False(r.Images[2].IsDisplayable);
        string h = Inner(r);
        Assert.Contains("<img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==\" alt=\"\" width=\"40\" height=\"10\">", h, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rtf-picture\" data-format=\"emf\">[emf picture]</span>", h, StringComparison.Ordinal);

        RtfHtmlResult cid = Conv("\\pard {\\pict\\pngblip " + png + "}\\par", new RtfHtmlOptions { ImageMode = RtfImageMode.ContentId });
        Assert.Contains("<img src=\"cid:rtfimage1\"", cid.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentPlaceholdersAreCounted()
    {
        RtfHtmlResult r = Conv("\\pard see \\objattph \\'20here and \\objattph \\'20there\\par");
        Assert.Equal(2, r.AttachmentPlaceholders);
        Assert.Contains("see <span data-rtf-attachment=\"0\"></span>here and <span data-rtf-attachment=\"1\"></span>there", Inner(r), StringComparison.Ordinal);
        Assert.Equal("see here and there\r\n", r.Text);
    }

    [Fact]
    public void HeadingStylesBecomeHeadings()
    {
        RtfHtmlResult r = RtfHtmlConverter.Convert(Encoding.Latin1.GetBytes(Head.Replace("{\\colortbl", "{\\stylesheet{\\s0 Normal;}{\\s1\\sbasedon0 heading 1;}{\\s2 Heading 2;}}{\\colortbl") +
            "\\pard\\s1 Title\\par\\pard\\s2 Sub\\par\\pard text\\par}"));
        Assert.Contains("<h1 ", r.Html, StringComparison.Ordinal);
        Assert.Contains("<h2 ", r.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void HorizontalRuleShapes()
    {
        RtfHtmlResult r = Conv("\\pard a\\par\\pard{\\pict{\\*\\picprop{\\sp{\\sn fHorizRule}{\\sv 1}}}\\wmetafile8 d7cdc69a}\\par\\pard b\\par");
        Assert.Contains("<hr>", r.Html, StringComparison.Ordinal);
        Assert.Empty(r.Images);
    }

    [Fact]
    public void MalformedInputGivesDefectsNotExceptions()
    {
        RtfHtmlResult unclosed = RtfHtmlConverter.Convert("{\\rtf1\\ansi\\pard {\\b broken"u8);
        Assert.Contains("broken", unclosed.Text, StringComparison.Ordinal);
        Assert.Contains(unclosed.Defects, d => d.Kind == RtfDefectKind.UnclosedGroups);

        RtfHtmlResult notRtf = RtfHtmlConverter.Convert("hello"u8);
        Assert.Equal("", notRtf.Html);
        Assert.Contains(notRtf.Defects, d => d.Kind == RtfDefectKind.NotRtf);

        RtfHtmlResult deep = RtfHtmlConverter.Convert(Encoding.ASCII.GetBytes("{\\rtf1 " + new string('{', 5000) + "x"), new RtfHtmlOptions { MaxGroupDepth = 100 });
        Assert.Contains(deep.Defects, d => d.Kind == RtfDefectKind.NestingLimitExceeded);

        RtfHtmlResult badPicture = Conv("\\pard {\\pict\\pngblip zz12 not hex}\\par");
        Assert.Contains(badPicture.Defects, d => d.Kind == RtfDefectKind.InvalidPictureData);
        Assert.All(badPicture.Images, i => Assert.False(i.IsDisplayable));
    }

    [Fact]
    public void LimitsStopHugeInput()
    {
        var sb = new StringBuilder("{\\rtf1 ");
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("a\\par ");
        }

        RtfHtmlResult r = RtfHtmlConverter.Convert(Encoding.ASCII.GetBytes(sb.ToString() + "}"), new RtfHtmlOptions { MaxNodes = 100 });
        Assert.Contains(r.Defects, d => d.Kind == RtfDefectKind.ContentLimitExceeded);

        RtfHtmlResult pict = Conv("\\pard {\\pict\\pngblip 89504e470d0a1a0a0000000d49484452}\\par", new RtfHtmlOptions { MaxPictureBytes = 4 });
        Assert.Contains(pict.Defects, d => d.Kind == RtfDefectKind.PictureLimitExceeded);
        Assert.False(pict.Images[0].IsDisplayable);
    }

    [Fact]
    public void SameInputGivesTheSameHtml()
    {
        RtfHtmlResult a = Conv("\\pard {\\b x} {\\f2 y}\\par");
        RtfHtmlResult b = Conv("\\pard {\\b x} {\\f2 y}\\par");
        Assert.Equal(a.Html, b.Html);
    }

    [Fact]
    public void MalformedAndMutatedDocumentsNeverThrow()
    {
        byte[] doc = Encoding.Latin1.GetBytes(Head.Replace("{\\colortbl", "{\\stylesheet{\\s1 heading 1;}}{\\colortbl", StringComparison.Ordinal) +
            "\\pard\\s1 Title\\par\\pard\\ls1\\ilvl2{\\listtext\\'b7}item\\par\\trowd\\clmgf\\cellx100\\clmrg\\cellx200\\clvmgf\\cellx300\\pard\\intbl x\\cell\\cell y\\cell\\row" +
            "{\\field{\\*\\fldinst HYPERLINK \"u\"}{\\fldrslt t}}{\\pict\\pngblip 89504e470d0a1a0a0000000d49484452}{\\*\\shppict{\\pict\\jpegblip ffd8ffe0}}\\u-3?\\u55357?\\objattph \\'20\\par}");
        // Cut at every length.
        for (int n = 0; n <= doc.Length; n++)
        {
            RtfHtmlResult r = RtfHtmlConverter.Convert(doc.AsSpan(0, n));
            Assert.NotNull(r.Html);
            Assert.NotNull(r.Text);
        }

        // Seeded damage: flip, drop and duplicate bytes.
        var rng = new Random(12345);
        for (int i = 0; i < 3000; i++)
        {
            byte[] d = (byte[])doc.Clone();
            for (int k = rng.Next(1, 6); k > 0; k--)
            {
                int at = rng.Next(d.Length);
                switch (rng.Next(3))
                {
                    case 0: d[at] = (byte)rng.Next(256); break;
                    case 1: d[at] = (byte)"{}\\;'"[rng.Next(5)]; break;
                    default: d = [.. d.AsSpan(0, at), .. d.AsSpan(Math.Min(at + rng.Next(1, 8), d.Length))]; break;
                }
            }

            RtfHtmlResult r = RtfHtmlConverter.Convert(d, new RtfHtmlOptions { MaxGroupDepth = 50 });
            Assert.NotNull(r.Html);
            Assert.NotNull(r.Text);
        }
    }

    [Fact]
    public void MailItemTakesItsHtmlFromRtfWrittenAsRtf()
    {
        var native = new MapiMessage();
        native.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress(Encoding.Latin1.GetBytes(Head + "\\pard Plain {\\b bold}\\par}"))));
        var view = new MapiMailItem(native, default);
        Assert.Equal(BodyOrigin.RtfConverted, view.HtmlBody?.Origin);
        Assert.Contains("<b>bold</b>", view.HtmlBody?.ReadText(), StringComparison.Ordinal);
        Assert.Equal("Plain bold\r\n", view.TextBody?.ReadText());
        Assert.Equal(BodyOrigin.RtfConverted, view.TextBody?.Origin);
        Assert.Equal(BodyOrigin.Stored, view.RtfBody?.Origin);

        var encapsulated = new MapiMessage();
        encapsulated.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress(RtfEncapsulator.EncapsulateHtml("<i>x</i>"))));
        Assert.Equal(BodyOrigin.RtfEncapsulated, new MapiMailItem(encapsulated, default).HtmlBody?.Origin);

        var stored = new MapiMessage();
        stored.Properties.Add(S(MapiPid.Body, "stored"));
        stored.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress(Encoding.Latin1.GetBytes(Head + "\\pard rtf\\par}"))));
        Assert.Equal(BodyOrigin.Stored, new MapiMailItem(stored, default).TextBody?.Origin);
    }

    [Fact]
    public void TheNamespaceReferencesNothingElseInOneMail()
    {
        // The RTF code is meant to be lifted out: its source may name only the base class library and itself.
        string dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "..", "src", "OneMail", "Rtf"));
        string[] files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"^\s*using\s+(static\s+)?([\w.]+)\s*;", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                string ns = m.Groups[2].Value;
                Assert.True(ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("RecursiveAI.OneMail.Rtf", StringComparison.Ordinal), $"{Path.GetFileName(file)} uses {ns}");
            }

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"RecursiveAI\.\w+(\.\w+)*"))
            {
                Assert.True(m.Value.StartsWith("RecursiveAI.OneMail.Rtf", StringComparison.Ordinal), $"{Path.GetFileName(file)} names {m.Value}");
            }
        }
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;
}
