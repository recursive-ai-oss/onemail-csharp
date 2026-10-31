using System.Text;
using RecursiveAI.OneMail.Display;

namespace RecursiveAI.OneMail.Tests.Display;

public class DisplayHtmlTests
{
    private const string Gif = "R0lGODlhAQABAAAAACwAAAAAAQABAAACAkQBADs=";

    private static IMailItem Msg(string headersAndBody) => Mail.Parse(Encoding.UTF8.GetBytes(headersAndBody.Replace("\n", "\r\n", StringComparison.Ordinal)));

    private const string RelatedMessage =
        "From: Ann <ann@example.com>\nTo: Bob <bob@example.com>\nSubject: Hi <b>there</b>\nDate: Mon, 14 May 2001 23:39:00 +0200\nMIME-Version: 1.0\n"
        + "Content-Type: multipart/related; boundary=\"b1\"\n\n"
        + "--b1\nContent-Type: text/html; charset=utf-8\n\n"
        + "<p>Logo: <img src=\"cid:logo@x\"> and <img src=\"images/pic.gif\"> and <img src=\"cid:missing\"> and <img src=\"http://img.example/a.gif\"></p>\n"
        + "--b1\nContent-Type: image/gif\nContent-ID: <logo@x>\nContent-Transfer-Encoding: base64\n\n" + Gif + "\n"
        + "--b1\nContent-Type: image/gif\nContent-Location: http://host.example/mail/images/pic.gif\nContent-Transfer-Encoding: base64\n\n" + Gif + "\n"
        + "--b1\nContent-Type: application/pdf\nContent-Disposition: attachment; filename=\"a<b>.pdf\"\nContent-Transfer-Encoding: base64\n\nJVBERi0=\n"
        + "--b1--\n";

    [Fact]
    public void CidAndContentLocationResolveToDataUris()
    {
        DisplayResult r = Msg(RelatedMessage).ToDisplayHtml();
        Assert.Contains("src=\"data:image/gif;base64," + Gif + "\"", r.Html);
        Assert.Equal(2, r.Html.Split("data:image/gif;base64,", StringSplitOptions.None).Length - 1);
        Assert.Equal(["logo@x", "images/pic.gif"], r.ResolvedAttachments);
        Assert.Equal(["missing"], r.UnresolvedReferences);
        Assert.DoesNotContain("img.example", r.Html);
        Assert.Contains(r.RemoteReferences, x => x.Url == "http://img.example/a.gif" && x.Outcome == RemoteContentOutcome.Blocked);
    }

    [Fact]
    public void ImagesUsedByTheBodyLeaveTheAttachmentListButOthersStay()
    {
        DisplayResult r = Msg(RelatedMessage).ToDisplayHtml();
        Assert.Contains("a&lt;b&gt;.pdf", r.Html);
        Assert.Contains("application/pdf", r.Html);
        Assert.Equal(1, r.Html.Split("<li>", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void CallbackFormUsesTheCallersUrl()
    {
        DisplayResult r = Msg(RelatedMessage).ToDisplayHtml(new DisplayOptions
        {
            ReferenceForm = ContentReferenceForm.Callback,
            AttachmentUrl = a => "/att/" + Uri.EscapeDataString(a.ContentId ?? a.FileName ?? "x"),
        });
        Assert.Contains("src=\"/att/logo%40x\"", r.Html);
        Assert.DoesNotContain("data:image", r.Html);
    }

    [Fact]
    public void HeaderBlockEscapesEverything()
    {
        DisplayResult r = Msg(RelatedMessage).ToDisplayHtml();
        Assert.Contains("Hi &lt;b&gt;there&lt;/b&gt;", r.Html);
        Assert.Contains("Ann &lt;ann@example.com&gt;", r.Html);
        Assert.Contains("Mon, 14 May 2001 23:39:00 +0200", r.Html);
        DisplayResult none = Msg(RelatedMessage).ToDisplayHtml(new DisplayOptions { IncludeHeader = false, IncludeAttachmentList = false });
        Assert.DoesNotContain("class=\"omx-header\"", none.Html);
        Assert.DoesNotContain("class=\"omx-attachments\"", none.Html);
    }

    [Fact]
    public void ForgedImageTypeGetsNoDataUri()
    {
        string m = "Content-Type: multipart/related; boundary=\"b\"\nMIME-Version: 1.0\n\n--b\nContent-Type: text/html\n\n<img src=\"cid:a\">\n"
            + "--b\nContent-Type: image/png\nContent-ID: <a>\nContent-Transfer-Encoding: base64\n\nPHN2ZyBvbmxvYWQ9YWxlcnQoMSk+\n--b--\n";
        DisplayResult r = Msg(m).ToDisplayHtml();
        Assert.DoesNotContain("data:", r.Html);
        Assert.Equal(["a"], r.UnresolvedReferences);
    }

    [Fact]
    public void InlineSizeLimitLeavesTheReferenceUnresolved()
    {
        DisplayResult r = Msg(RelatedMessage).ToDisplayHtml(new DisplayOptions { MaxInlineImageBytes = 10 });
        Assert.DoesNotContain("data:image", r.Html);
        Assert.Contains("logo@x", r.UnresolvedReferences);
    }

    [Fact]
    public void PlainTextIsEscapedAndLinksAreConservative()
    {
        string m = "Content-Type: text/plain; charset=utf-8\nMIME-Version: 1.0\n\nSee <b>x</b> & https://example.com/a?b=1&c=2). Also (http://e.org/x_(y)) and javascript:alert(1) and www.example.com\n  indented\n";
        DisplayResult r = Msg(m).ToDisplayHtml(new DisplayOptions { IncludeHeader = false });
        Assert.True(r.UsedTextBody);
        Assert.Contains("See &lt;b&gt;x&lt;/b&gt; &amp; <a href=\"https://example.com/a?b=1&amp;c=2\" rel=\"noopener noreferrer\" target=\"_blank\">https://example.com/a?b=1&amp;c=2</a>).", r.Html);
        Assert.Contains("<a href=\"http://e.org/x_(y)\"", r.Html);
        Assert.Equal(2, r.Html.Split("<a href=", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("href=\"javascript", r.Html);
        Assert.Contains("<div>  indented</div>", r.Html);
        Assert.Equal(2, r.RemoteReferences.Count);
    }

    [Fact]
    public void FormatFlowedIsJoinedAndQuoted()
    {
        string m = "Content-Type: text/plain; charset=utf-8; format=flowed; delsp=yes\nMIME-Version: 1.0\n\n"
            + "Hello this is a long \nparagraph that was wrapped.\n> quoted line one \n> and two\n>> deeper\n\n-- \nSig\n";
        DisplayResult r = Msg(m).ToDisplayHtml(new DisplayOptions { IncludeHeader = false });
        Assert.Contains("<div>Hello this is a longparagraph that was wrapped.</div>", r.Html);
        Assert.Contains("<blockquote class=\"omx-q\"><div>quoted line oneand two</div><blockquote class=\"omx-q\"><div>deeper</div></blockquote></blockquote>", r.Html);
        Assert.Contains("<div>-- </div><div>Sig</div>", r.Html);
    }

    [Fact]
    public void FormatFlowedWithoutDelspKeepsTheSpace()
    {
        string m = "Content-Type: text/plain; format=flowed\nMIME-Version: 1.0\n\nfoo \nbar\n";
        Assert.Contains("<div>foo bar</div>", Msg(m).ToDisplayHtml(new DisplayOptions { IncludeHeader = false }).Html);
    }

    [Fact]
    public void HtmlBodyWinsUnlessTextIsPreferred()
    {
        string m = "MIME-Version: 1.0\nContent-Type: multipart/alternative; boundary=\"a\"\n\n--a\nContent-Type: text/plain\n\nplain body\n--a\nContent-Type: text/html\n\n<p>html body</p>\n--a--\n";
        Assert.Contains("html body", Msg(m).ToDisplayHtml().Html);
        DisplayResult t = Msg(m).ToDisplayHtml(new DisplayOptions { PreferText = true });
        Assert.Contains("plain body", t.Html);
        Assert.DoesNotContain("html body", t.Html);
    }

    [Fact]
    public void CalendarCardHookIsCalledAndNeverThrowsThrough()
    {
        string m = "Subject: s\n\nbody\n";
        Assert.Contains("<div class=\"omx-card\"><i>card</i></div>", Msg(m).ToDisplayHtml(new DisplayOptions { CalendarCard = _ => "<i>card</i>" }).Html);
        Assert.NotNull(Msg(m).ToDisplayHtml(new DisplayOptions { CalendarCard = _ => throw new InvalidOperationException() }).Html);
    }

    [Fact]
    public void MessageWithNoBodyStillRenders()
    {
        DisplayResult r = Msg("Subject: empty\n\n").ToDisplayHtml();
        Assert.Contains("empty", r.Html);
    }

    [Fact]
    public void HostileHtmlBodyThroughTheWholePath()
    {
        string m = "Content-Type: text/html\nMIME-Version: 1.0\n\n<script>alert(1)</script><img src=x onerror=alert(1)><a href=\"javascript:alert(1)\">x</a><style>@import url(http://leak.invalid/x)</style>ok\n";
        DisplayResult r = Msg(m).ToDisplayHtml();
        Assert.DoesNotContain("script", r.Html);
        Assert.DoesNotContain("onerror", r.Html);
        Assert.DoesNotContain("javascript", r.Html);
        Assert.DoesNotContain("leak.invalid\"", r.Html);
        Assert.Contains("ok", r.Html);
    }
}
