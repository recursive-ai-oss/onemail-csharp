using System.Text;
using RecursiveAI.OneMail.Bodies;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;

namespace RecursiveAI.OneMail.Tests.Bodies;

public class BodySelectorTests
{
    private static IMailItem Eml(string s) => Mail.Parse(Encoding.UTF8.GetBytes(s.Replace("\n", "\r\n", StringComparison.Ordinal)));

    private const string Head = "From: a@example.com\nTo: b@example.com\nSubject: s\nMIME-Version: 1.0\n";

    private static string Alt(string first, string second) =>
        Head + "Content-Type: multipart/alternative; boundary=b\n\n--b\n" + first + "\n--b\n" + second + "\n--b--\n";

    private const string Plain = "Content-Type: text/plain; charset=utf-8\n\nplain body";
    private const string Html = "Content-Type: text/html; charset=utf-8\n\n<p>html <b>body</b></p>";

    [Fact]
    public void AlternativeWithBothPrefersHtml()
    {
        SelectedBody s = BodySelector.Select(Eml(Alt(Plain, Html)));
        Assert.Equal((BodyKind.Html, BodyReason.HtmlPreferred), (s.Kind, s.Reason));
        Assert.Empty(s.Issues);
        Assert.Contains("html", s.Body!.ReadText(), StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlBeforePlainStillChoosesHtmlAndSaysSo()
    {
        SelectedBody s = BodySelector.Select(Eml(Alt(Html, Plain)));
        Assert.Equal(BodyKind.Html, s.Kind);
        Assert.Contains(s.Issues, i => i.Kind == BodyIssueKind.AmbiguousAlternativeOrder);
    }

    [Fact]
    public void TextOnlyGetsADerivedHtmlAndHtmlOnlyGetsDerivedText()
    {
        IMailItem t = Eml(Head + Plain);
        Assert.Equal(BodyReason.TextOnly, BodySelector.Select(t).Reason);
        DerivedMailBody h = Assert.IsType<DerivedMailBody>(BodySelector.GetHtml(t));
        Assert.Equal(BodyOrigin.TextToHtml, h.Origin);
        Assert.Contains("<p>plain body</p>", h.ReadText(), StringComparison.Ordinal);

        IMailItem x = Eml(Head + Html);
        Assert.Equal(BodyReason.HtmlOnly, BodySelector.Select(x).Reason);
        DerivedMailBody d = Assert.IsType<DerivedMailBody>(BodySelector.GetText(x));
        Assert.Equal(BodyOrigin.HtmlToText, d.Origin);
        Assert.Equal("html body", d.ReadText());
    }

    [Fact]
    public void StoredBodiesAreReturnedAsTheyAre()
    {
        IMailItem m = Eml(Alt(Plain, Html));
        Assert.Equal(m.TextBody!.ReadText(), BodySelector.GetText(m)!.ReadText());
        Assert.Equal(m.HtmlBody!.ReadText(), BodySelector.GetHtml(m)!.ReadText());
        Assert.Equal(m.HtmlBody.ReadText(), m.BestBody!.ReadText());
    }

    [Fact]
    public void FlowedTextIsJoinedAndKeepsQuotes()
    {
        IMailItem m = Eml(Head + "Content-Type: text/plain; charset=utf-8; format=flowed\n\nHello \nworld\n> quoted \n> text\n");
        SelectedBody s = BodySelector.Select(m);
        Assert.Equal(BodyKind.Text, s.Kind);
        Assert.Equal("Hello world\n> quoted text", s.Body!.ReadText());
        Assert.Equal(BodyOrigin.FlowedDecoded, s.Body!.Origin);
        string html = BodySelector.GetHtml(m)!.ReadText();
        Assert.Contains("<blockquote type=\"cite\">\n<p>quoted text</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyHtmlFallsBackToText()
    {
        IMailItem m = Eml(Alt(Plain, "Content-Type: text/html; charset=utf-8\n\n<html><body> <div> </div> </body></html>"));
        SelectedBody s = BodySelector.Select(m);
        Assert.Equal((BodyKind.Text, BodyReason.EmptyHtml), (s.Kind, s.Reason));
        Assert.Contains(s.Issues, i => i.Kind == BodyIssueKind.AmbiguousEmptyHtml);
    }

    [Fact]
    public void ImageOnlyHtmlIsABody()
    {
        IMailItem m = Eml(Head + "Content-Type: text/html\n\n<img src=\"cid:x\">");
        Assert.Equal(BodyKind.Html, BodySelector.Select(m).Kind);
    }

    [Fact]
    public void NoBodyIsSaidSo()
    {
        IMailItem m = Eml(Head + "Content-Type: application/pdf\nContent-Disposition: attachment; filename=a.pdf\n\n%PDF");
        SelectedBody s = BodySelector.Select(m);
        Assert.Equal((BodyKind.None, BodyReason.NoBody), (s.Kind, s.Reason));
        Assert.Null(m.BestBody);
        Assert.Null(BodySelector.GetText(m));
        Assert.Null(BodySelector.GetHtml(m));
    }

    private static MapiProperty S(ushort id, string v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    private static MapiProperty Bin(ushort id, byte[] v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.Binary), v);

    private static MapiProperty I(ushort id, int v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.Integer32), BitConverter.GetBytes(v));

    private static MapiProperty B(ushort id, bool v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.Boolean), [(byte)(v ? 1 : 0), 0]);

    [Fact]
    public void OutlookRtfEncapsulatedHtmlIsTheBody()
    {
        var m = new MapiMessage();
        m.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress(RtfEncapsulator.EncapsulateHtml("<b>Bold ü</b>"))));
        SelectedBody s = BodySelector.Select(new MapiMailItem(m, default));
        Assert.Equal((BodyKind.Html, BodyReason.HtmlOnly), (s.Kind, s.Reason));
    }

    [Fact]
    public void OutlookNativeTextWinsOverItsHtml()
    {
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.Body, "typed text"));
        m.Properties.Add(Bin(MapiPid.Html, Encoding.UTF8.GetBytes("<p>typed text</p>")));
        Assert.Equal(BodyReason.HtmlPreferred, BodySelector.Select(new MapiMailItem(m, default)).Reason);
        m.Properties.Add(I(MapiPid.NativeBody, 1));
        SelectedBody s = BodySelector.Select(new MapiMailItem(m, default));
        Assert.Equal((BodyKind.Text, BodyReason.NativeText), (s.Kind, s.Reason));
    }

    [Fact]
    public void OutlookStaleTextYieldsToTheNewerRtf()
    {
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.Body, "old text"));
        m.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress(RtfEncapsulator.EncapsulateHtml("<p>new html</p>"))));
        m.Properties.Add(B(MapiPid.RtfInSync, false));
        SelectedBody s = BodySelector.Select(new MapiMailItem(m, default));
        Assert.Equal((BodyKind.Html, BodyReason.StaleText), (s.Kind, s.Reason));
        Assert.Contains("new html", s.Body!.ReadText(), StringComparison.Ordinal);
        Assert.Contains(s.Issues, i => i.Kind == BodyIssueKind.AmbiguousStaleText);
    }

    [Fact]
    public void RtfWrittenAsRtfGivesConvertedHtml()
    {
        // The item view makes HTML from RTF written as RTF with the native converter, so the selector sees HTML and says where it came from.
        var m = new MapiMessage();
        m.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress("{\\rtf1\\ansi Plain {\\b bold}}"u8)));
        SelectedBody s = BodySelector.Select(new MapiMailItem(m, default));
        Assert.Equal(BodyKind.Html, s.Kind);
        Assert.Equal(BodyReason.HtmlPreferred, s.Reason);
        Assert.Equal(BodyOrigin.RtfConverted, s.Body!.Origin);
        MailBody text = BodySelector.GetText(new MapiMailItem(m, default))!;
        Assert.Contains("Plain", text.ReadText(), StringComparison.Ordinal);
    }
}
