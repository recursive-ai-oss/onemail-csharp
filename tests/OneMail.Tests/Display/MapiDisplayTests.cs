using System.Text;
using RecursiveAI.OneMail.Display;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Tests.Msg;
using RecursiveAI.OneMail.Sources;
using static RecursiveAI.OneMail.Tests.Msg.MsgFixtures;

namespace RecursiveAI.OneMail.Tests.Display;

public class MapiDisplayTests
{
    private static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAAAAACwAAAAAAQABAAACAkQBADs=");

    private static BuildObject Message()
    {
        BuildObject m = Simple("Quarterly <report>");
        m.Props.Add(new BuildProp(T(MapiPid.Html, MapiType.Binary), Encoding.UTF8.GetBytes(
            "<html><body><p>Hi <img src=\"cid:pic@x\"> <img src=\"img/b.gif\"> <img src=\"cid:none\"> <img src=\"http://r.example/t.gif\" width=1 height=1><script>alert(1)</script></p></body></html>")));
        m.Props.Add(new BuildProp(T(MapiPid.InternetCodepage, MapiType.Integer32), 65001));
        foreach ((string cid, string loc, string name) in new[] { ("pic@x", "", "pic.gif"), ("", "http://host.example/img/b.gif", "b.gif") })
        {
            var a = new BuildAttachment();
            a.Props.Add(new BuildProp(T(MapiPid.AttachMethod, MapiType.Integer32), 1));
            a.Props.Add(new BuildProp(T(MapiPid.AttachLongFilename, MapiType.UnicodeString), name));
            a.Props.Add(new BuildProp(T(MapiPid.AttachMimeTag, MapiType.UnicodeString), "image/gif"));
            a.Props.Add(new BuildProp(T(MapiPid.AttachDataBinary, MapiType.Binary), Gif));
            if (cid.Length > 0)
            {
                a.Props.Add(new BuildProp(T(MapiPid.AttachContentId, MapiType.UnicodeString), cid));
            }

            if (loc.Length > 0)
            {
                a.Props.Add(new BuildProp(T(0x3713, MapiType.UnicodeString), loc));
            }

            m.Attachments.Add(a);
        }

        return m;
    }

    [Fact]
    public void CidAndContentLocationResolveOnAnOutlookItem()
    {
        byte[] file = MsgTestBuilder.Build(Message());
        using MailContainer c = Mail.Open(new MemorySource(file));
        IMailItem item = c.Enumerate().Single();
        DisplayResult r = item.ToDisplayHtml();
        string gif = "data:image/gif;base64," + Convert.ToBase64String(Gif);
        Assert.Equal(2, r.Html.Split(gif, StringSplitOptions.None).Length - 1);
        Assert.Equal(["pic@x", "img/b.gif"], r.ResolvedAttachments);
        Assert.Equal(["none"], r.UnresolvedReferences);
        Assert.DoesNotContain("<script", r.Html);
        Assert.DoesNotContain("r.example\"", r.Html);
        Assert.Contains("Quarterly &lt;report&gt;", r.Html);
        Assert.True(Assert.Single(r.TrackingIndications).Reasons.HasFlag(TrackingReasons.TinySize));
        Assert.DoesNotContain("<ul>", r.Html);
    }
}
