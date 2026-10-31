using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Dom;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Tests.Display;

public class SanitizerTests
{
    private const string Host = "leak.invalid:8080";

    private static string DataRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "sanitizer"));

    private static readonly string[] BannedElements = ["script", "iframe", "object", "embed", "form", "input", "svg", "math", "link", "meta", "base", "noscript", "template", "frame", "frameset", "video", "audio", "textarea", "select", "button"];

    private static readonly string[] BannedAttributes = ["srcdoc", "formaction", "action", "ping", "dynsrc", "lowsrc", "longdesc", "data", "poster", "xlink:href", "usemap", "contenteditable", "autofocus"];

    public static IEnumerable<(string Name, string Html)> Vectors()
    {
        string name = "";
        StringBuilder body = new();
        bool have = false;
        foreach (string line in File.ReadAllLines(Path.Combine(DataRoot(), "xss.txt")))
        {
            if (line == "====")
            {
                if (have)
                {
                    yield return (name, body.ToString().Replace("{HOST}", Host, StringComparison.Ordinal));
                }

                have = false;
                body.Clear();
            }
            else if (!have && line.StartsWith("# ", StringComparison.Ordinal) && line[2..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                name = line[2..];
                have = true;
            }
            else if (have)
            {
                body.Append(line).Append('\n');
            }
        }

        if (have)
        {
            yield return (name, body.ToString().Replace("{HOST}", Host, StringComparison.Ordinal));
        }
    }

    public static IEnumerable<object[]> VectorData() => Vectors().Select(v => new object[] { v.Name, v.Html });

    [Fact]
    public void CorpusIsLargeEnough() => Assert.True(Vectors().Count() >= 230);

    [Theory]
    [MemberData(nameof(VectorData))]
    public void EveryVectorIsNeutralised(string name, string html)
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize(html);
        AssertClean(r.Html, name);
        Assert.DoesNotContain(r.Changes, c => c.Detail == "unstable output replaced by text");
        // Block is the default: nothing remote may be left in the output, links aside.
        foreach (RemoteReference rr in r.RemoteReferences.Where(x => x.Kind != RemoteContentKind.Link))
        {
            Assert.NotEqual(RemoteContentOutcome.Allowed, rr.Outcome);
        }

        // The output is a fixed point of the sanitiser's own re-parse: sanitising it again, with the same options, adds nothing dangerous.
        AssertClean(HtmlSanitizer.Sanitize(r.Html).Html, name + " (second pass)");
    }

    /// <summary>The output contains only allowlisted elements and attributes, no script-bearing URL, and no remote fetch.</summary>
    internal static void AssertClean(string output, string name)
    {
        Document doc = Document.Parse(output);
        foreach (Node n in doc.Root.Descendants)
        {
            if (n.Kind == NodeKind.Comment)
            {
                Assert.Fail(name + ": comment in output");
            }

            if (!n.IsElement)
            {
                continue;
            }

            Assert.Equal(ElementNamespace.Html, n.Namespace);
            string e = n.LocalName;
            Assert.DoesNotContain(e, BannedElements);
            foreach (Attr a in n.Attributes)
            {
                string an = a.LocalName;
                Assert.False(an.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"{name}: attribute {an}");
                Assert.DoesNotContain(an, BannedAttributes);
                string v = a.Value.ToString();
                string compact = new string(v.Where(c => c > ' ').ToArray()).ToLowerInvariant();
                Assert.DoesNotContain("javascript:", compact);
                Assert.DoesNotContain("vbscript:", compact);
                Assert.DoesNotContain(Host, v);
                if (an is "href" or "src")
                {
                    Assert.Matches("^(https?:|mailto:|tel:|#|data:image/(png|gif|jpeg|jpg|webp|bmp|avif|x-icon|vnd.microsoft.icon);base64,)", v);
                }
            }
        }

        foreach (Node s in doc.Root.Descendants)
        {
            if (s.IsElement && s.LocalName == "style")
            {
                string css = s.TextContent ?? "";
                string l = css.ToLowerInvariant();
                Assert.DoesNotContain("@import", l);
                Assert.DoesNotContain("expression", l);
                Assert.DoesNotContain("javascript", l);
                Assert.DoesNotContain("behavior", l);
                Assert.DoesNotContain("-moz-binding", l);
                Assert.DoesNotContain(Host, css);
                Assert.DoesNotContain("<", css);
                Assert.DoesNotContain("position:fixed", l);
            }
        }
    }

    [Fact]
    public void LegitimateMarkupSurvives()
    {
        string html = "<html><head><style>.a{color:red} td{padding:4px}</style></head><body bgcolor=\"#ffffff\">"
            + "<table width=\"600\" cellpadding=\"0\" border=\"0\"><tr><td class=\"a\" style=\"font-size:14px;color:#333\">Hello <b>world</b> <a href=\"https://example.com/x?a=1&amp;b=2\">link</a></td></tr></table>"
            + "<img src=\"data:image/gif;base64,R0lGODlhAQABAAAAACw=\" alt=\"dot\" width=\"1\" height=\"1\"><p>caf\u00e9 &amp; tea</p></body></html>";
        SanitizedHtml r = HtmlSanitizer.Sanitize(html);
        Assert.Contains("<table width=\"600\" cellpadding=\"0\" border=\"0\">", r.Html);
        Assert.Contains("class=\"om-a\"", r.Html);
        Assert.Contains("style=\"font-size:14px;color:#333\"", r.Html);
        Assert.Contains("<b>world</b>", r.Html);
        Assert.Contains("href=\"https://example.com/x?a=1&amp;b=2\"", r.Html);
        Assert.Contains("rel=\"noopener noreferrer\"", r.Html);
        Assert.Contains("caf\u00e9 &amp; tea", r.Html);
        Assert.Contains(".omx .om-a{color:red}", r.Html);
        Assert.Contains(".omx td{padding:4px}", r.Html);
        Assert.Contains("background-color:#ffffff", r.Html);
        Assert.Contains("alt=\"dot\"", r.Html);
        Assert.DoesNotContain(r.RemoteReferences, x => x.Kind != RemoteContentKind.Link);
    }

    [Fact]
    public void RemoteImageIsBlockedByDefaultAndReported()
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize("<img src=\"http://" + Host + "/a.png\" alt=\"a\">");
        Assert.DoesNotContain(Host, r.Html);
        Assert.Contains("alt=\"a\"", r.Html);
        RemoteReference rr = Assert.Single(r.RemoteReferences);
        Assert.Equal(RemoteContentOutcome.Blocked, rr.Outcome);
        Assert.Equal(RemoteContentKind.Image, rr.Kind);
        Assert.Equal("http://" + Host + "/a.png", rr.Url);
    }

    [Fact]
    public void AllowPolicyKeepsAndRewritePolicyReplaces()
    {
        string html = "<img src=\"https://x.example/a.png\"><div style=\"background-image:url(https://x.example/b.png)\">t</div>";
        SanitizedHtml allow = HtmlSanitizer.Sanitize(html, new SanitizerOptions { RemoteContent = RemoteContentAction.Allow });
        Assert.Contains("src=\"https://x.example/a.png\"", allow.Html);
        Assert.Contains("url(&quot;https://x.example/b.png&quot;)", allow.Html);
        Assert.All(allow.RemoteReferences, r => Assert.Equal(RemoteContentOutcome.Allowed, r.Outcome));
        Assert.Equal([RemoteContentKind.Image, RemoteContentKind.Background], allow.RemoteReferences.Select(r => r.Kind));

        List<RemoteContentRequest> asked = [];
        SanitizedHtml rw = HtmlSanitizer.Sanitize(html, new SanitizerOptions
        {
            Policy = r =>
            {
                asked.Add(r);
                return RemoteContentDecision.Rewrite("/proxy?u=" + Uri.EscapeDataString(r.Url));
            },
        });
        Assert.Contains("src=\"/proxy?u=https%3A%2F%2Fx.example%2Fa.png\"", rw.Html);
        Assert.Equal(2, asked.Count);
        Assert.All(rw.RemoteReferences, r => Assert.Equal(RemoteContentOutcome.Rewritten, r.Outcome));
    }

    [Fact]
    public void RewriteToScriptUrlIsRefused()
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize("<img src=\"https://x.example/a.png\">", new SanitizerOptions { Policy = _ => RemoteContentDecision.Rewrite("javascript:alert(1)") });
        Assert.DoesNotContain("javascript", r.Html);
        Assert.Equal(RemoteContentOutcome.Blocked, Assert.Single(r.RemoteReferences).Outcome);
    }

    [Fact]
    public void ThrowingPolicyBlocks()
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize("<img src=\"https://x.example/a.png\">", new SanitizerOptions { Policy = _ => throw new InvalidOperationException() });
        Assert.DoesNotContain("x.example", r.Html);
    }

    // One test per way HTML can name something to fetch.
    [Theory]
    [InlineData("<img src=\"http://r.test/1\">", RemoteContentKind.Image)]
    [InlineData("<img srcset=\"http://r.test/1 1x, http://r.test/2 2x\">", RemoteContentKind.Image)]
    [InlineData("<picture><source srcset=\"http://r.test/1\"><img></picture>", RemoteContentKind.Image)]
    [InlineData("<table background=\"http://r.test/1\"></table>", RemoteContentKind.Background)]
    [InlineData("<table><tr><td background=\"http://r.test/1\">x</td></tr></table>", RemoteContentKind.Background)]
    [InlineData("<body background=\"http://r.test/1\">x</body>", RemoteContentKind.Background)]
    [InlineData("<div style=\"background:url(http://r.test/1)\">x</div>", RemoteContentKind.Background)]
    [InlineData("<ul style=\"list-style-image:url(http://r.test/1)\"><li>x</li></ul>", RemoteContentKind.Image)]
    [InlineData("<style>div{background-image:url(http://r.test/1)}</style><div>x</div>", RemoteContentKind.Background)]
    [InlineData("<style>@font-face{font-family:f;src:url(http://r.test/1)}</style>x", RemoteContentKind.Font)]
    [InlineData("<link rel=stylesheet href=\"http://r.test/1\">", RemoteContentKind.Stylesheet)]
    [InlineData("<style>@import url(http://r.test/1);</style>x", RemoteContentKind.Stylesheet)]
    [InlineData("<video poster=\"http://r.test/1\"></video>", RemoteContentKind.Image)]
    [InlineData("<video src=\"http://r.test/1\"></video>", RemoteContentKind.Media)]
    [InlineData("<audio><source src=\"http://r.test/1\"></audio>", RemoteContentKind.Media)]
    [InlineData("<iframe src=\"http://r.test/1\"></iframe>", RemoteContentKind.Media)]
    [InlineData("<embed src=\"http://r.test/1\">", RemoteContentKind.Media)]
    [InlineData("<object data=\"http://r.test/1\"></object>", RemoteContentKind.Media)]
    [InlineData("<a href=\"http://r.test/1\">x</a>", RemoteContentKind.Link)]
    public void EveryFetchVectorIsReportedAndNothingLeaks(string html, RemoteContentKind kind)
    {
        // Even with everything allowed, the removed elements stay removed and the report still lists them.
        foreach (RemoteContentAction mode in new[] { RemoteContentAction.Block, RemoteContentAction.Allow })
        {
            SanitizedHtml r = HtmlSanitizer.Sanitize(html, new SanitizerOptions { RemoteContent = mode });
            Assert.Contains(r.RemoteReferences, x => x.Kind == kind && x.Url.StartsWith("http://r.test/", StringComparison.Ordinal));
            bool allowedElement = kind is RemoteContentKind.Image or RemoteContentKind.Background or RemoteContentKind.Font or RemoteContentKind.Link
                && !html.StartsWith("<video", StringComparison.Ordinal) && !html.StartsWith("<link", StringComparison.Ordinal);
            if (mode == RemoteContentAction.Block && kind != RemoteContentKind.Link)
            {
                Assert.DoesNotContain("r.test", r.Html);
            }

            if (!allowedElement)
            {
                Assert.Contains(r.RemoteReferences, x => x.Outcome == RemoteContentOutcome.ElementDropped || x.Outcome == RemoteContentOutcome.Blocked);
                Assert.DoesNotContain("r.test", r.Html);
            }
        }
    }

    [Fact]
    public void LinksGoThroughThePolicy()
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize("<a href=\"https://t.example/x\">x</a>", new SanitizerOptions
        {
            Policy = q => q.Kind == RemoteContentKind.Link ? RemoteContentDecision.Rewrite("https://warn.example/?u=" + q.Url) : RemoteContentDecision.Block,
        });
        Assert.Contains("href=\"https://warn.example/?u=https://t.example/x\"", r.Html);
    }

    [Fact]
    public void TrackingPixelsAreReported()
    {
        string html = "<img src=\"https://a.example/x.gif\" width=1 height=1>"
            + "<img src=\"https://b.example/img.png\" style=\"display:none\">"
            + "<img src=\"https://c.example/track/open?id=1\">"
            + "<img src=\"https://d.example/hero.jpg\" width=600>";
        SanitizedHtml r = HtmlSanitizer.Sanitize(html);
        Assert.Equal(3, r.TrackingIndications.Count);
        Assert.Equal(TrackingReasons.TinySize, r.TrackingIndications[0].Reasons);
        Assert.Equal(TrackingReasons.Hidden, r.TrackingIndications[1].Reasons);
        Assert.Equal(TrackingReasons.KnownPattern, r.TrackingIndications[2].Reasons);
    }

    [Fact]
    public void ScopedStyleCannotReachTheHostPage()
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize("<style>body{color:red} html body p{margin:0} a:hover,#id.cls>b{color:blue} * {margin:0}</style><p id=id class=cls>x</p>");
        Assert.Contains(".omx{color:red}", r.Html);
        Assert.Contains(".omx p{margin:0}", r.Html);
        Assert.Contains(".omx a:hover,.omx #om-id.om-cls>b{color:blue}", r.Html);
        Assert.Contains(".omx *{margin:0}", r.Html);
        Assert.Contains("id=\"om-id\" class=\"om-cls\"", r.Html);
    }

    [Fact]
    public void LimitsNeverThrow()
    {
        string deep = string.Concat(Enumerable.Repeat("<div>", 5000)) + "x";
        SanitizedHtml d = HtmlSanitizer.Sanitize(deep);
        Assert.True(d.Truncated || d.ChangeCount > 0);

        SanitizedHtml big = HtmlSanitizer.Sanitize(new string('a', 100_000) + "<script>1</script>", new SanitizerOptions { MaxInputLength = 1000 });
        Assert.True(big.Truncated);
        Assert.True(big.Html.Length < 5000);

        SanitizedHtml many = HtmlSanitizer.Sanitize(string.Concat(Enumerable.Repeat("<b>x</b>", 5000)), new SanitizerOptions { MaxNodes = 100 });
        Assert.True(many.Truncated);

        Assert.NotNull(HtmlSanitizer.Sanitize(null).Html);
        Assert.NotNull(HtmlSanitizer.Sanitize("").Html);
        Assert.NotNull(HtmlSanitizer.Sanitize("\0\0<\0>\ud800").Html);
    }

    [Fact]
    public void OutputIsStableUnderReparse()
    {
        string[] inputs =
        [
            "<table><tr><td>a<p>b</table>",
            "<pre>\n\nx</pre>",
            "<b><i>x</b></i><p>y",
            "<ul><li>a<li>b</ul>",
            "<a href=\"#a\" id=a>x</a><div class=\"A  b\">y</div>",
            "<div style=\"color:red;color:blue !important\">x</div>",
        ];
        foreach (string i in inputs)
        {
            SanitizedHtml r = HtmlSanitizer.Sanitize(i);
            Assert.DoesNotContain(r.Changes, c => c.Detail.Contains("unstable", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DeeplyNestedMisnestedAnchorsSettle()
    {
        // The adoption-agency repair of an anchor opened inside an anchor, 24 or more blocks deep, leaves an anchor in an anchor in the tree.
        foreach (int depth in new[] { 3, 24, 100, 200 })
        {
            SanitizedHtml r = HtmlSanitizer.Sanitize("<a>" + string.Concat(Enumerable.Repeat("<div>", depth)) + "<a><");
            Assert.DoesNotContain(r.Changes, c => c.Detail.Contains("unstable", StringComparison.Ordinal));
            Assert.Contains("&lt;", r.Html, StringComparison.Ordinal);
        }
    }
}
