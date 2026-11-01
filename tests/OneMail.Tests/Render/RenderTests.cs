using System.Text;
using RecursiveAI.OneMail.Render;

namespace RecursiveAI.OneMail.Tests.Render;

public class RenderTests
{
    private static CancellationToken T => TestContext.Current.CancellationToken;

    private const string Gif = "R0lGODlhAQABAAAAACwAAAAAAQABAAACAkQBADs=";

    private static IMailItem Msg(string headersAndBody) => Mail.Parse(Encoding.UTF8.GetBytes(headersAndBody.Replace("\n", "\r\n", StringComparison.Ordinal)));

    private const string Related =
        "From: Ann <ann@example.com>\nTo: Bob <bob@example.com>\nSubject: Quarterly report\nDate: Mon, 14 May 2001 23:39:00 +0200\nMIME-Version: 1.0\n"
        + "Content-Type: multipart/related; boundary=\"b1\"\n\n"
        + "--b1\nContent-Type: text/html; charset=utf-8\n\n"
        + "<h1>Hello</h1><p>Logo: <img src=\"cid:logo@x\" width=40 height=40> remote: <img src=\"http://img.example/a.gif\" width=10 height=10></p>"
        + "<table border=1><tr><td>a</td><td>b</td></tr></table>\n"
        + "--b1\nContent-Type: image/gif\nContent-ID: <logo@x>\nContent-Transfer-Encoding: base64\n\n" + Gif + "\n"
        + "--b1--\n";

    private const string Plain = "From: a@example.com\nTo: b@example.com\nSubject: Plain\nContent-Type: text/plain; charset=utf-8\n\nline one\nline two\n";

    [Fact]
    public async Task PdfIsWrittenAndNothingIsFetched()
    {
        using var ms = new MemoryStream();
        var seen = new List<string>();
        RenderResult r = await Msg(Related).ToPdfAsync(ms, new RenderOptions { Resources = u => { seen.Add(u); return null; } }, T);
        Assert.True(r.PageCount >= 1);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(ms.ToArray(), 0, 4));
        Assert.Empty(seen);
        Assert.DoesNotContain(r.Defects, d => d.Kind == RenderDefectKind.DisplayTruncated);
    }

    [Fact]
    public async Task BlockedRemoteContentIsNeverAskedFor()
    {
        using var ms = new MemoryStream();
        RenderResult r = await Msg(Related).ToPdfAsync(ms, null, T);
        Assert.Empty(r.UnservedRequests);
        Assert.DoesNotContain("img.example", r.Display.Html);
    }

    [Fact]
    public async Task AllowedRemoteContentIsOnlyAskedOfTheCaller()
    {
        var o = new RenderOptions
        {
            Display = new RecursiveAI.OneMail.Display.DisplayOptions { Sanitizer = new RecursiveAI.OneMail.Display.SanitizerOptions { RemoteContent = RecursiveAI.OneMail.Display.RemoteContentAction.Allow } },
        };
        using var ms = new MemoryStream();
        RenderResult r = await Msg(Related).ToPdfAsync(ms, o, T);
        Assert.Equal(["http://img.example/a.gif"], r.UnservedRequests);
        Assert.Contains(r.Defects, d => d.Kind == RenderDefectKind.ResourceNotServed);
    }

    [Fact]
    public async Task PlainTextRendersToPng()
    {
        var pages = new List<MemoryStream>();
        RenderResult r = await Msg(Plain).ToPngAsync(n => { var s = new MemoryStream(); pages.Add(s); return s; }, null, T);
        Assert.Equal(1, r.PagesWritten);
        byte[] png = pages[0].ToArray();
        Assert.Equal(0x89, png[0]);
        Assert.Equal("PNG", Encoding.ASCII.GetString(png, 1, 3));
    }

    [Fact]
    public async Task LongMessageSplitsIntoPagesAndRangeSelectsThem()
    {
        var sb = new StringBuilder("From: a@example.com\nSubject: Long\nContent-Type: text/plain\n\n");
        for (int i = 0; i < 400; i++)
        {
            sb.Append("line ").Append(i).Append('\n');
        }

        IMailItem item = Msg(sb.ToString());
        var numbers = new List<int>();
        RenderResult all = await item.ToPngAsync(n => { numbers.Add(n); return Stream.Null; }, null, T);
        Assert.True(all.PageCount > 2, "pages: " + all.PageCount);
        Assert.Equal(all.PageCount, numbers.Count);

        numbers.Clear();
        RenderResult some = await item.ToPngAsync(n => { numbers.Add(n); return Stream.Null; }, new RenderOptions { FirstPage = 2, LastPage = 2 }, T);
        Assert.Equal([2], numbers);
        Assert.Equal(all.PageCount, some.PageCount);

        using var pdf = new MemoryStream();
        RenderResult p = await item.ToPdfAsync(pdf, null, T);
        Assert.True(p.PageCount > 2);
    }

    [Fact]
    public async Task PageLimitThrowsATypedException()
    {
        var sb = new StringBuilder("From: a@example.com\nSubject: Long\nContent-Type: text/plain\n\n");
        for (int i = 0; i < 400; i++)
        {
            sb.Append("line ").Append(i).Append('\n');
        }

        MailRenderException ex = await Assert.ThrowsAsync<MailRenderException>(() => Msg(sb.ToString()).ToPdfAsync(Stream.Null, new RenderOptions { MaxPdfPages = 1 }, T));
        Assert.Equal(RenderFailure.PageLimit, ex.Failure);
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Msg(Plain).ToPdfAsync(Stream.Null, null, cts.Token));
    }

    [Fact]
    public void DataUriDecoding()
    {
        Assert.Equal([1, 2, 3], MessageRenderer.DecodeDataUri("data:application/octet-stream;base64,AQID"));
        Assert.Equal("a b"u8.ToArray(), MessageRenderer.DecodeDataUri("data:text/plain,a%20b"));
        Assert.Null(MessageRenderer.DecodeDataUri("data:nope"));
    }
}
