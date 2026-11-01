using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneHtml;
using RecursiveAI.OneHtml.Dom;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Tests.Display;

/// <summary>
/// Vectors from the sanitiser review (testdata/sanitizer/review.txt, one finding or attack category each) and the hostile-callback cases.
/// The output is parsed again and held to the allowlists of elements, attributes and URL schemes, whatever the input or the caller's callbacks said.
/// </summary>
public class SanitizerReviewTests
{
    private const string Host = "leak.invalid:8080";

    private static string DataRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "sanitizer"));

    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "a", "abbr", "acronym", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "br", "caption", "center", "cite", "code",
        "col", "colgroup", "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font", "footer", "h1", "h2", "h3",
        "h4", "h5", "h6", "header", "hr", "i", "img", "ins", "kbd", "li", "main", "mark", "nav", "ol", "p", "picture", "pre", "q", "rp", "rt",
        "ruby", "s", "samp", "section", "small", "source", "span", "strike", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot",
        "th", "thead", "time", "tr", "tt", "u", "ul", "var", "wbr", "style", "html", "head", "body",
    };

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "class", "id", "style", "title", "alt", "lang", "dir", "align", "valign", "width", "height", "border", "cellpadding", "cellspacing", "colspan",
        "rowspan", "hspace", "vspace", "size", "span", "start", "value", "bgcolor", "color", "bordercolor", "face", "nowrap", "noshade", "reversed",
        "open", "scope", "headers", "abbr", "summary", "rules", "frame", "clear", "char", "datetime", "type", "href", "rel", "target", "src", "srcset",
        "sizes", "referrerpolicy", "background",
    };

    public static IEnumerable<object[]> VectorData()
    {
        string name = "";
        StringBuilder body = new();
        bool have = false;
        foreach (string line in File.ReadAllLines(Path.Combine(DataRoot(), "review.txt")))
        {
            if (line == "====")
            {
                if (have)
                {
                    yield return [name, body.ToString().Replace("{HOST}", Host, StringComparison.Ordinal)];
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
    }

    /// <summary>The output holds only allowlisted elements and attributes and no script-bearing URL; style text has no raw markup.</summary>
    internal static void AssertAllowlisted(string output, string name)
    {
        Document doc = Document.Parse(output);
        foreach (Node n in doc.Root.Descendants)
        {
            Assert.False(n.Kind == NodeKind.Comment, name + ": comment in output");
            if (!n.IsElement)
            {
                continue;
            }

            Assert.Equal(ElementNamespace.Html, n.Namespace);
            Assert.True(Elements.Contains(n.LocalName), $"{name}: element {n.LocalName}");
            foreach (Attr a in n.Attributes)
            {
                string an = a.LocalName;
                Assert.True(Attributes.Contains(an), $"{name}: attribute {an} on {n.LocalName}");
                string v = a.Value.ToString();
                string compact = new string(v.Where(c => c > ' ').ToArray()).ToLowerInvariant();
                if (an is "href" or "src" or "background")
                {
                    Assert.DoesNotMatch("^(javascript|vbscript|livescript|data:(?!image/)|blob|file|view-source|filesystem):", compact);
                }

                foreach (char c in v)
                {
                    Assert.False(c < ' ' && c is not ('\t' or '\n' or '\f'), $"{name}: control character in {an}");
                }
            }

            if (n.LocalName == "style")
            {
                Assert.DoesNotContain("<", n.TextContent ?? "");
            }
        }
    }

    [Fact]
    public void ReviewCorpusIsLargeEnough() => Assert.True(VectorData().Count() >= 130);

    [Theory]
    [MemberData(nameof(VectorData))]
    public void EveryReviewVectorIsNeutralised(string name, string html)
    {
        SanitizedHtml block = HtmlSanitizer.Sanitize(html);
        AssertAllowlisted(block.Html, name);
        Assert.DoesNotContain(block.Changes, c => c.Detail == "unstable output replaced by text");
        Assert.DoesNotContain(block.RemoteReferences.Where(x => x.Kind != RemoteContentKind.Link), x => x.Outcome == RemoteContentOutcome.Allowed);

        SanitizedHtml allow = HtmlSanitizer.Sanitize(html, new SanitizerOptions { RemoteContent = RemoteContentAction.Allow });
        AssertAllowlisted(allow.Html, name + " (allow)");

        // Under "allow" a remote URL may only appear in the output if the report says the policy let it through.
        HashSet<string> allowed = allow.RemoteReferences.Where(x => x.Outcome == RemoteContentOutcome.Allowed).Select(x => x.Url).ToHashSet(StringComparer.Ordinal);
        foreach (string url in RemoteUrlsIn(allow.Html))
        {
            Assert.True(allowed.Contains(url), $"{name}: {url} is in the output but was not reported as allowed");
        }

        // Under "block" no remote URL may appear except links.
        foreach (string url in RemoteUrlsIn(block.Html, includeLinks: false))
        {
            Assert.Fail($"{name}: {url} is in the output under block");
        }
    }

    // Every http(s) URL in a src, background, srcset (and, when asked, href) attribute and in a url("...") of a style element or attribute.
    // CSS strings that are not the argument of url() are data (a font name can say anything) and are skipped.
    private static List<string> RemoteUrlsIn(string output, bool includeLinks = true)
    {
        Document doc = Document.Parse(output);
        List<string> urls = [];
        foreach (Node n in doc.Root.Descendants)
        {
            if (!n.IsElement)
            {
                continue;
            }

            foreach (Attr a in n.Attributes)
            {
                string v = a.Value.ToString();
                switch (a.LocalName)
                {
                    case "src" or "background":
                        if (v.StartsWith("http", StringComparison.Ordinal))
                        {
                            urls.Add(v);
                        }

                        break;
                    case "srcset":
                        urls.AddRange(v.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimEnd(',')).Where(t => t.StartsWith("http", StringComparison.Ordinal)));
                        break;
                    case "href" when includeLinks:
                        if (v.StartsWith("http", StringComparison.Ordinal))
                        {
                            urls.Add(v);
                        }

                        break;
                    case "style":
                        urls.AddRange(CssUrls(v));
                        break;
                }
            }

            if (n.LocalName == "style")
            {
                urls.AddRange(CssUrls(n.TextContent ?? ""));
            }
        }

        return urls;

        static IEnumerable<string> CssUrls(string css)
        {
            // One left-to-right pass: a string that is the argument of url() is a URL, any other string is skipped whole.
            const string pattern = "url\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"|\"(?:[^\"\\\\\n]|\\\\.)*\"|'(?:[^'\\\\\n]|\\\\.)*'|url\\(\\s*([^\"'\\s)]+)";
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(css, pattern))
            {
                string u = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : "";
                if (u.StartsWith("http", StringComparison.Ordinal))
                {
                    yield return u;
                }
            }
        }
    }

    private static readonly string[] HostileUrls =
    [
        "javascript:alert(1)", " JaVaScRiPt:alert(1)", "java\tscript:alert(1)", "data:text/html,<script>alert(1)</script>", "data:image/svg+xml,<svg onload=alert(1)>",
        "vbscript:x", "blob:http://a/b", "file:///etc/passwd", "livescript:x", "view-source:http://h", "http://h.test/\"><script>alert(1)</script>",
        "http://h.test/' onerror='alert(1)", "https://h.test/a b", "http://h.test/a,http://evil.test/b 1x", "//h.test/\u2028x", "http://h.test/\u0001x",
        "x\" onerror=\"alert(1)", "</style><script>alert(1)</script>", "http://h.test/</style><img src=x onerror=alert(1)>", "\\\\h.test\\x",
        "http://h.test/\\3c script\\3e", "http://h.test/);}</style><script>alert(1)</script>", "http://h.test/\")}p{background:url(http://evil.test/x)",
        "", " ", "\0", new string('a', 9000), "http://" + new string('a', 9000), "data:image/png;base64,AAAA\" onerror=\"alert(1)", "#" + new string('x', 600),
    ];

    private const string OneOfEach =
        "<img src=\"http://o.test/1\" srcset=\"http://o.test/2 1x\"><table background=\"http://o.test/3\"><tr><td style=\"background:url(http://o.test/4)\">x</td></tr></table>"
        + "<style>@font-face{font-family:f;src:url(http://o.test/5)}p{font-family:f;background:url(http://o.test/8)}</style><p>p</p><a href=\"http://o.test/6\">l</a>"
        + "<picture><source srcset=\"http://o.test/9 2x\"><img src=\"http://o.test/10\"></picture>";

    [Fact]
    public void HostileRewriteTargetsNeverReachTheOutputUnchecked()
    {
        foreach (string hostile in HostileUrls)
        {
            foreach (bool anyUrl in new[] { true, false })
            {
                SanitizedHtml r = HtmlSanitizer.Sanitize("<body background=\"http://o.test/7\">" + OneOfEach, new SanitizerOptions
                {
                    Policy = _ => RemoteContentDecision.Rewrite(hostile),
                    AllowRewriteToAnyUrl = anyUrl,
                });
                AssertAllowlisted(r.Html, $"rewrite to {hostile.Length} chars starting {hostile[..Math.Min(hostile.Length, 20)]} (any={anyUrl})");
                Assert.DoesNotContain(r.Changes, c => c.Detail == "unstable output replaced by text");
            }
        }
    }

    [Fact]
    public void HostileResolverResultsNeverReachTheOutputUnchecked()
    {
        string html = "<img src=\"cid:a\"><img src=\"rel.png\"><img srcset=\"cid:b 1x\"><div style=\"background:url(cid:c)\">d</div><style>p{background:url(rel2.png)}</style><p>x</p>";
        foreach (string hostile in HostileUrls)
        {
            SanitizedHtml r = HtmlSanitizer.Sanitize(html, new SanitizerOptions { ResolveReference = _ => hostile });
            AssertAllowlisted(r.Html, "resolver " + hostile[..Math.Min(hostile.Length, 20)]);
            Assert.DoesNotContain(r.Changes, c => c.Detail == "unstable output replaced by text");
        }
    }

    [Fact]
    public void ThrowingAndNullCallbacksAreSafe()
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize(OneOfEach, new SanitizerOptions
        {
            Policy = _ => throw new InvalidOperationException("no"),
            ResolveReference = _ => throw new InvalidOperationException("no"),
        });
        AssertAllowlisted(r.Html, "throwing callbacks");
        Assert.DoesNotContain("o.test", r.Html.Replace("href=\"http://o.test/6\"", "", StringComparison.Ordinal));
        SanitizedHtml n = HtmlSanitizer.Sanitize(OneOfEach, new SanitizerOptions { Policy = _ => new RemoteContentDecision(RemoteContentAction.Rewrite, null) });
        AssertAllowlisted(n.Html, "rewrite to null");
    }

    // Finding R1 (hardening): control characters and non-characters were written through. An ESC starts a character-set switch when a page is
    // mislabelled ISO-2022-JP, so none is ever written; tab, line feed and form feed stay.
    [Fact]
    public void ControlCharactersAreNotWritten()
    {
        string html = "<p title=\"a&#x1b;(Jb&#x7f;c&#xffff;d&#x1;e\tf\">g&#x1b;$Bh&#xfffe;j&#x8;k\tl\nm</p>";
        SanitizedHtml r = HtmlSanitizer.Sanitize(html);
        Assert.Contains("title=\"a(Jbcde\tf\"", r.Html);
        Assert.Contains(">g$Bhjk\tl\nm</p>", r.Html);
        foreach (char c in r.Html)
        {
            Assert.False(c < ' ' && c is not ('\t' or '\n' or '\f'));
        }

        // The plain-text path and the header block go through the same escape.
        DisplayResult d = Mail.Parse(Encoding.UTF8.GetBytes("Subject: s\u001b(J\r\nContent-Type: text/plain\r\n\r\nline\u001b$B one\r\n")).ToDisplayHtml();
        Assert.DoesNotContain('\u001b', d.Html);
    }

    // Plain text: links only for http(s), everything else escaped; hostile text cannot open markup.
    [Fact]
    public void PlainTextBodyIsEscapedAndLinksAreHttpOnly()
    {
        string body = "<script>alert(1)</script> javascript:alert(1) http://o.test/a\"onmouseover=\"alert(1) https://o.test/b<img src=x onerror=alert(1)> "
            + "HTTP://O.test/c?x=<b> http://\u0001evil.test/ http:// https://";
        DisplayResult d = Mail.Parse(Encoding.UTF8.GetBytes("Subject: s\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" + body + "\r\n")).ToDisplayHtml();
        AssertAllowlisted(d.Html, "plain text");
        Assert.DoesNotContain("<script", d.Html);
        Assert.DoesNotContain("<img", d.Html);
    }

    // Charset and transfer-encoding tricks reach the sanitiser only as decoded text, so what the decoder makes of them is what is checked.
    public static IEnumerable<object[]> EncodedBodies() =>
    [
        ["utf7", "Content-Type: text/html; charset=utf-7\r\n\r\n+ADw-script+AD4-alert(1)+ADw-/script+AD4-+ADw-img src=x onerror=alert(2)+AD4-"],
        ["utf7-attr", "Content-Type: text/html; charset=utf-7\r\n\r\n+ADw-a href=+ACI-javascript:alert(1)+ACI-+AD4-x+ADw-/a+AD4-"],
        ["iso2022jp", "Content-Type: text/html; charset=iso-2022-jp\r\n\r\n<p title=\"\u001b$B\u001b(J\" onclick=\"alert(1)\">x</p>\u001b(J<img src=x onerror=alert(1)>"],
        ["iso2022jp-yen", "Content-Type: text/html; charset=iso-2022-jp\r\n\r\n\u001b(J<a href=\"\\\" onclick=alert(1)//\">x</a>"],
        ["qp-split-scheme", "Content-Type: text/html\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n<a href=3D\"java=\r\nscript:alert(1)\">x</a><img src=3Dx onerror=3Dalert(1)>"],
        ["qp-encoded-lt", "Content-Type: text/html\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n=3Cscript=3Ealert(1)=3C/script=3E"],
        ["base64", "Content-Type: text/html\r\nContent-Transfer-Encoding: base64\r\n\r\nPHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0PjxpbWcgc3JjPXggb25lcnJvcj1hbGVydCgyKT4="],
        ["bom-utf16-label-utf8", "Content-Type: text/html; charset=utf-8\r\n\r\n\ufeff<img src=x onerror=alert(1)>\ufeff<script>alert(2)</script>"],
        ["bogus-charset", "Content-Type: text/html; charset=x-no-such\r\n\r\n<script>alert(1)</script><img src=x onerror=alert(2)>"],
        ["nul-bytes", "Content-Type: text/html\r\n\r\n<scr\0ipt>alert(1)</scr\0ipt><img src=x on\0error=alert(2)>"],
        ["html-in-text-plain", "Content-Type: text/plain\r\n\r\n<script>alert(1)</script><img src=x onerror=alert(2)>"],
        ["meta-charset-inside", "Content-Type: text/html; charset=utf-8\r\n\r\n<meta charset=utf-7>+ADw-script+AD4-alert(1)+ADw-/script+AD4-"],
    ];

    [Theory]
    [MemberData(nameof(EncodedBodies))]
    public void EncodedBodiesAreNeutralised(string name, string headersAndBody)
    {
        byte[] raw = Encoding.Latin1.GetBytes("Subject: " + name + "\r\nMIME-Version: 1.0\r\n" + (name.Contains("bom", StringComparison.Ordinal) ? Utf8(headersAndBody) : headersAndBody));
        DisplayResult d = Mail.Parse(raw).ToDisplayHtml();
        AssertAllowlisted(d.Html, name);
        Assert.DoesNotContain("<script", d.Html);
        Assert.DoesNotContain("<img", d.Html.Replace("<img referrerpolicy=\"no-referrer\">", "", StringComparison.Ordinal));

        static string Utf8(string s) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(s));
    }

    // Finding R4 (exploitable, availability): trimming the brackets off a link rescanned the whole address for each one, so one line of
    // "http://x/" followed by a few hundred thousand ")" took minutes. It is linear now, and a run of address characters beyond the link limit is skipped.
    [Fact]
    public void LongRunsOfClosingBracketsInPlainTextCostLinearTime()
    {
        string body = "http://x.test/a" + new string(')', 400_000) + " and http://y.test/b" + new string(']', 400_000) + " " + new string('h', 300_000);
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        DisplayResult d = Mail.Parse(Encoding.UTF8.GetBytes("Subject: s\r\nContent-Type: text/plain\r\n\r\n" + body + "\r\n")).ToDisplayHtml();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        AssertAllowlisted(d.Html, "bracket run");
    }

    // Finding R5 (hardening, resource): one line of ">" signs in format=flowed text wrote a block quote per sign. Depth stops at 32.
    [Fact]
    public void QuoteDepthInFlowedTextIsCapped()
    {
        string body = new string('>', 200_000) + " deep\r\n>> two\r\nplain\r\n";
        DisplayResult d = Mail.Parse(Encoding.UTF8.GetBytes("Subject: s\r\nContent-Type: text/plain; format=flowed\r\n\r\n" + body)).ToDisplayHtml();
        Assert.True(d.Html.Length < 20_000, $"output is {d.Html.Length} chars");
        Assert.Equal(32, d.Html.Split("<blockquote", StringSplitOptions.None).Length - 1);
        Assert.Contains("deep", d.Html);
        AssertAllowlisted(d.Html, "quote depth");
    }

    // Finding R6 (hardening, resource): each cid or location reference scanned every part, reading each part's properties each time.
    // A message with thousands of parts and tens of thousands of references took minutes. Ids are looked up in a dictionary and the location
    // scan has a budget.
    [Fact]
    public void ManyPartsAndManyReferencesResolveInBoundedTime()
    {
        StringBuilder m = new();
        m.Append("Subject: s\r\nMIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=\"b\"\r\n\r\n--b\r\nContent-Type: text/html\r\n\r\n");
        for (int i = 0; i < 60_000; i++)
        {
            m.Append("<img src=\"cid:none").Append(i).Append("\"><img src=\"rel").Append(i).Append(".png\">");
        }

        m.Append("<img src=\"cid:p4999\">\r\n");
        for (int i = 0; i < 5000; i++)
        {
            m.Append("--b\r\nContent-Type: image/gif\r\nContent-ID: <p").Append(i).Append(">\r\nContent-Location: http://l.test/x").Append(i)
                .Append("\r\nContent-Transfer-Encoding: base64\r\n\r\nR0lGODlhAQABAAAAACwAAAAAAQABAAACAkQBADs=\r\n");
        }

        m.Append("--b--\r\n");
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        DisplayResult d = Mail.Parse(Encoding.UTF8.GetBytes(m.ToString())).ToDisplayHtml();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        Assert.Contains("p4999", d.ResolvedAttachments);
    }

    // Finding R8 (hardening): "0url(x)" is a dimension and a parenthesis to the spec, but reads as a function name to a tokeniser that differs.
    // A parenthesis directly after a number, dimension, hash, identifier or string now removes the declaration. (Found by the display fuzz target.)
    [Fact]
    public void ParenthesisGluedToAValueTokenIsRemoved()
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize("<style>@font-face{font-family:w;src:0url(javascript2ale,AAAA)}p{width:1url(a);height:2px}</style><p style=\"margin:1px(2);padding:3px\">x</p>");
        Assert.DoesNotContain("0url(", r.Html);
        Assert.DoesNotContain("1url(", r.Html);
        Assert.DoesNotContain("1px(", r.Html);
        Assert.Contains("height:2px", r.Html);
        Assert.Contains("padding:3px", r.Html);
        SanitizedHtml ok = HtmlSanitizer.Sanitize("<p style=\"width:calc(2*(3px + 1px));height:calc((1px))\">x</p>");
        Assert.Contains("width:calc(2*(3px + 1px))", ok.Html);
    }

    // Finding R2 (note): the calendar card hook's HTML is written as given, so it is the hook's job to escape; the sanitiser's guarantees stop at
    // the body. This pins the behaviour so a change to it is deliberate.
    [Fact]
    public void CalendarCardHtmlIsWrittenAsGiven()
    {
        DisplayResult d = Mail.Parse(Encoding.UTF8.GetBytes("Subject: s\r\nContent-Type: text/plain\r\n\r\nx\r\n"))
            .ToDisplayHtml(new DisplayOptions { CalendarCard = _ => "<b>card</b>" });
        Assert.Contains("<div class=\"omx-card\"><b>card</b></div>", d.Html);
    }

    // Finding R3 (note): a newline right after <pre> is dropped by the parser, so one is written twice. A browser whose innerHTML does not yet
    // write the extra newline reads the copy as one newline shorter; only whitespace changes.
    [Fact]
    public void PreWithLeadingNewlineKeepsItsTextThroughOurOwnReparse()
    {
        SanitizedHtml r = HtmlSanitizer.Sanitize("<pre>\n\nx</pre><pre><script>s</script>\ny</pre>");
        Assert.Contains("<pre>\n\nx</pre>", r.Html);
        Assert.Contains("<pre>\n\ny</pre>", r.Html);
    }
}
