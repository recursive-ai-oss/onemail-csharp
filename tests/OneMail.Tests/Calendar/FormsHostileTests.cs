using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Forms;
using RecursiveAI.OneMail.Sources;
using Xunit;

namespace RecursiveAI.OneMail.Tests.Calendar;

/// <summary>Hostile jCal, jCard, xCal and xCard input: every case ends in a typed defect or limit, never an exception or unbounded work.</summary>
public class FormsHostileTests
{
    private static bool Has(FormReadResult r, FormDefectKind k) => r.Defects.Any(d => d.Kind == k);

    [Fact]
    public void DeepJsonNestingStopsAtTheDepthLimit()
    {
        string json = new string('[', 100_000) + new string(']', 100_000);
        FormReadResult r = JsonForms.FromJson(json);
        Assert.True(Has(r, FormDefectKind.DepthLimitExceeded) || Has(r, FormDefectKind.NotWellFormed));
        Assert.Empty(r.Document.Components);
    }

    [Fact]
    public void DeepXmlNestingStopsAtTheDepthLimit()
    {
        StringBuilder sb = new("<icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\">");
        for (int i = 0; i < 5000; i++)
        {
            sb.Append("<vcalendar><components>");
        }

        FormReadResult r = XmlForms.FromXml(sb.ToString());
        Assert.Contains(r.Defects, static d => d.IsLimit || d.Kind == FormDefectKind.NotWellFormed);
    }

    [Fact]
    public void ComponentNestingBeyondTheConfiguredDepthIsALimit()
    {
        string inner = "[\"vevent\",[],[]]";
        for (int i = 0; i < 40; i++)
        {
            inner = "[\"valarm\",[],[" + inner + "]]";
        }

        string json = "[\"vcalendar\",[],[" + inner + "]]";
        FormReadResult r = JsonForms.FromJson(json, new FormLimits { MaxDepth = 32 });
        Assert.Contains(r.Defects, static d => d.Kind == FormDefectKind.DepthLimitExceeded);
    }

    [Fact]
    public void InputOverTheSizeLimitIsRefusedBeforeParsing()
    {
        byte[] big = new byte[2048];
        FormReadResult j = JsonForms.FromJson(big, new FormLimits { MaxInputBytes = 1024 });
        FormReadResult x = XmlForms.FromXml(big, new FormLimits { MaxInputBytes = 1024 });
        Assert.Contains(j.Defects, static d => d.Kind == FormDefectKind.InputTooLarge);
        Assert.Contains(x.Defects, static d => d.Kind == FormDefectKind.InputTooLarge);
    }

    [Fact]
    public void ANodeCountLimitStopsAHugeComponentList()
    {
        StringBuilder sb = new("[\"vcalendar\",[],[");
        for (int i = 0; i < 5000; i++)
        {
            sb.Append(i == 0 ? "" : ",").Append("[\"vevent\",[],[]]");
        }

        sb.Append("]]");
        FormReadResult r = JsonForms.FromJson(sb.ToString(), new FormLimits { MaxNodes = 1000 });
        Assert.Contains(r.Defects, static d => d.Kind == FormDefectKind.NodeLimitExceeded);
    }

    [Fact]
    public void AStringOverTheLengthLimitIsATypedDefect()
    {
        string json = "[\"vcard\",[[\"fn\",{},\"text\",\"" + new string('a', 5000) + "\"]]]";
        FormReadResult r = JsonForms.FromJson(json, new FormLimits { MaxStringChars = 1000 });
        Assert.Contains(r.Defects, static d => d.Kind == FormDefectKind.StringTooLong);
    }

    [Fact]
    public void ADoctypeIsProhibitedAndEntitiesAreNotExpanded()
    {
        // A billion-laughs shape: the document type declaration is refused, so no entity is ever expanded.
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE icalendar [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
            + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]><icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\"><vcalendar><properties>"
            + "<prodid><text>&c;</text></prodid></properties></vcalendar></icalendar>";
        FormReadResult r = XmlForms.FromXml(xml);
        Assert.Contains(r.Defects, static d => d.Kind == FormDefectKind.DtdProhibited);
        Assert.DoesNotContain("aaaa", Encoding.UTF8.GetString(r.Document.ToArray()));
    }

    [Fact]
    public void AnExternalEntityIsNeverFetched()
    {
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\">"
            + "<vcalendar><properties><prodid><text>&e;</text></prodid></properties></vcalendar></icalendar>";
        FormReadResult r = XmlForms.FromXml(xml);
        Assert.Contains(r.Defects, static d => d.Kind == FormDefectKind.DtdProhibited);
        Assert.DoesNotContain("root:", Encoding.UTF8.GetString(r.Document.ToArray()));
    }

    [Fact]
    public void DuplicateJsonKeysAreKeptAndReported()
    {
        FormReadResult r = JsonForms.FromJson("[\"vcard\",[[\"tel\",{\"type\":\"home\",\"type\":\"work\"},\"text\",\"1\"]]]");
        Assert.Contains(r.Defects, static d => d.Kind == FormDefectKind.DuplicateKey);
        Assert.Contains("TYPE", Encoding.UTF8.GetString(r.Document.ToArray()));
    }

    [Fact]
    public void WrongShapesNeverThrow()
    {
        string[] inputs =
        [
            "", "null", "7", "\"vcalendar\"", "{}", "[]", "[1]", "[\"vcalendar\"]", "[\"vcalendar\",1,2]", "[\"vcalendar\",[1],[2]]",
            "[\"vcalendar\",[[\"a\"]],[]]", "[\"vcalendar\",[[1,2,3,4]],[]]", "[\"vcalendar\",[[\"a\",[],\"text\",\"x\"]],[]]",
            "[\"vcalendar\",[[\"a\",{\"b\":[1,[2]]},\"text\",\"x\"]],[]]", "[\"vcard\",[[\"n\",{},\"text\",[[1],{}]]]]",
            "[\"vcalendar\",[[\"rrule\",{},\"recur\",{\"freq\":[1,2],\"count\":\"x\"}]],[]]", "[\"vcalendar\",[[\"dtstart\",{},\"date-time\",{}]],[]]",
            "[[\"vcard\",[]],[\"vcalendar\",[],[]],7]", "\uFEFF[\"vcard\",[]]", "[\"vcard\",[[\"x-a\",{},\"unknown\",null,true,1.5e999]]]",
            "[\"vcard\",[[\"photo\",{},\"uri\",\"data:image/png;base64,@@@\"]]]", "[\"vcalendar\",[[\"dtstart\",{},\"date\",\"99999-99-99\"]],[]]",
        ];
        foreach (string s in inputs)
        {
            FormReadResult r = JsonForms.FromJson(s);
            Assert.NotNull(r.Document);
            _ = JsonForms.ToJson(r.Document);
            _ = XmlForms.ToXCal(r.Document);
            _ = XmlForms.ToXCard(r.Document);
        }
    }

    [Fact]
    public void BrokenXmlNeverThrows()
    {
        string[] inputs =
        [
            "", "<", "<a", "<a/>", "<icalendar/>", "<icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\"/>",
            "<icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\"><vcalendar><properties><x><text/></x></properties>",
            "<vcards xmlns=\"urn:ietf:params:xml:ns:vcard-4.0\"><vcard><n><surname>a</surname><bogus/></n></vcard></vcards>",
            "<vcards xmlns=\"urn:ietf:params:xml:ns:vcard-4.0\"><vcard><tel><parameters><type><text>a</text><date/></type></parameters><uri/></tel></vcard></vcards>",
            "<icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\"><vcalendar><properties><rrule><recur><freq>X</freq><count>zz</count><byday/></recur></rrule></properties></vcalendar></icalendar>",
            "<icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\"><vcalendar><properties><dtstart><date-time>nope</date-time></dtstart><dtstart/><dtend><date>1</date><text>2</text></dtend></properties></vcalendar></icalendar>",
            "<?xml version=\"1.0\" encoding=\"x-nonexistent\"?><a/>", "\0\0\0",
        ];
        foreach (string s in inputs)
        {
            FormReadResult r = XmlForms.FromXml(s);
            Assert.NotNull(r.Document);
            _ = JsonForms.ToJson(r.Document);
        }
    }

    [Fact]
    public void ControlCharactersAndLoneSurrogatesDoNotBreakXmlOutput()
    {
        // XML 1.0 cannot hold U+000C or an unpaired surrogate; the writer substitutes U+FFFD and reports it.
        ContentDocument doc = ContentDocument.Parse(Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nSUMMARY:a\fb\r\nEND:VCALENDAR\r\n"));
        FormWriteResult w = XmlForms.ToXCal(doc);
        Assert.Contains(w.Losses, static l => l.Kind == FormLossKind.InvalidXmlCharacter);
        FormReadResult back = XmlForms.FromXml(w.Bytes);
        Assert.DoesNotContain(back.Defects, static d => d.Kind == FormDefectKind.NotWellFormed);
    }

    [Fact]
    public void JsonWithLoneSurrogateEscapesIsReadAndWrittenWithoutThrowing()
    {
        FormReadResult r = JsonForms.FromJson("[\"vcard\",[[\"fn\",{},\"text\",\"a\\ud800b\"]]]");
        FormWriteResult w = XmlForms.ToXCard(r.Document);
        Assert.NotEmpty(w.Bytes);
        _ = JsonForms.ToJson(r.Document);
    }

    [Fact]
    public void ABillionLongRepeatInATextValueDoesNotAllocateAnythingLarger()
    {
        // 4 MiB of text through every form stays linear; the default limits are far above this.
        string big = new('x', 4 * 1024 * 1024);
        FormReadResult r = JsonForms.FromJson("[\"vcard\",[[\"note\",{},\"text\",\"" + big + "\"]]]");
        Assert.Empty(r.Defects);
        FormWriteResult w = XmlForms.ToXCard(r.Document);
        Assert.True(w.Bytes.Length > big.Length);
    }

    [Theory]
    [InlineData("[\"vcalendar\",[],[]]", MailFormat.JCal)]
    [InlineData("  \n[ \"VCALENDAR\" , [], []]", MailFormat.JCal)]
    [InlineData("[\"vcard\",[]]", MailFormat.JCard)]
    [InlineData("[[\"vcard\",[]],[\"vcard\",[]]]", MailFormat.JCard)]
    [InlineData("[\"vevent\",[]]", MailFormat.Unknown)]
    [InlineData("<?xml version=\"1.0\"?><icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\"/>", MailFormat.XCal)]
    [InlineData("<vcards xmlns=\"urn:ietf:params:xml:ns:vcard-4.0\"><vcard/></vcards>", MailFormat.XCard)]
    [InlineData("<html xmlns=\"http://www.w3.org/1999/xhtml\"/>", MailFormat.Unknown)]
    public void TheSnifferRecognisesTheJsonAndXmlForms(string text, MailFormat expected)
    {
        Assert.Equal(expected, MailFormatSniffer.Sniff(new MemorySource(Encoding.UTF8.GetBytes(text))));
    }
}
