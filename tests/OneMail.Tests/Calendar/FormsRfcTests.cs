using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Forms;

namespace RecursiveAI.OneMail.Tests.Calendar;

/// <summary>
/// The examples printed in RFC 7265 (jCal), RFC 7095 (jCard), RFC 6321 (xCal) and RFC 6351 (xCard) convert both ways. The
/// fixtures are the RFCs' own text, with the RFCs' internal inconsistencies corrected as listed in
/// testdata/calendar/forms/rfc/ERRATA.txt.
/// </summary>
public class FormsRfcTests
{
    private static string Dir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "calendar", "forms", "rfc"));

    private static string Read(string name) => File.ReadAllText(Path.Combine(Dir(), name));

    // JSON equality that ignores the order of object keys.
    internal static string CanonJson(string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        return Canon(d.RootElement);
    }

    private static string Canon(JsonElement e)
    {
        StringBuilder sb = new();
        Walk(e, sb);
        return sb.ToString();
    }

    private static void Walk(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                foreach (JsonProperty p in e.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    sb.Append(JsonSerializer.Serialize(p.Name, JsonSerializerOptions.Default)).Append(':');
                    Walk(p.Value, sb);
                    sb.Append(',');
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                // A properties array (every item is a property array [name, {params}, ...]) is unordered.
                bool props = e.GetArrayLength() > 0 && e.EnumerateArray().All(static i => i.ValueKind == JsonValueKind.Array && i.GetArrayLength() > 1 && i[1].ValueKind == JsonValueKind.Object);
                IEnumerable<string> items = e.EnumerateArray().Select(static i => Canon(i));
                foreach (string i in props ? items.Order(StringComparer.Ordinal) : items)
                {
                    sb.Append(i).Append(',');
                }

                sb.Append(']');
                break;
            default:
                sb.Append(e.GetRawText());
                break;
        }
    }

    // XML equality that ignores indentation between elements and comments.
    internal static string CanonXml(string xml)
    {
        XDocument d = XDocument.Parse(xml, LoadOptions.None);
        StringBuilder sb = new();
        WalkX(d.Root!, sb);
        return sb.ToString();
    }

    private static void WalkX(XElement e, StringBuilder sb)
    {
        sb.Append('<').Append(e.Name.ToString());
        foreach (XAttribute a in e.Attributes().Where(static a => !a.IsNamespaceDeclaration).OrderBy(static a => a.Name.ToString(), StringComparer.Ordinal))
        {
            sb.Append(' ').Append(a.Name.ToString()).Append("=\"").Append(a.Value).Append('"');
        }

        sb.Append('>');
        if (e.HasElements)
        {
            // The order of the properties of a component is not part of xCal (RFC 6321 Appendix A).
            IEnumerable<string> kids = e.Elements().Select(static c =>
            {
                StringBuilder k = new();
                WalkX(c, k);
                return k.ToString();
            });
            foreach (string k in e.Name.LocalName == "properties" ? kids.Order(StringComparer.Ordinal) : kids)
            {
                sb.Append(k);
            }
        }
        else
        {
            sb.Append(e.Value);
        }

        sb.Append("</").Append(e.Name.ToString()).Append('>');
    }

    // The tree as sorted lines: name, parameters (sorted, values joined), value. Order of properties and parameters does not
    // matter to the forms.
    internal static List<string> Lines(ContentDocument doc)
    {
        List<string> o = [];
        Collect(doc.Root, string.Empty, o);
        return o;
    }

    private static void Collect(Component c, string path, List<string> o)
    {
        List<string> mine = [];
        foreach (ComponentItem i in c.Items)
        {
            if (i is ContentProperty { Kind: RecursiveAI.OneMail.Calendar.ContentLines.ContentLineKind.Property } p)
            {
                string ps = string.Join(';', p.Parameters.Where(static x => !x.IsNamed("VALUE")).Select(static x => x.Name.ToUpperInvariant() + "=" + string.Join(',', x.Values)).Order(StringComparer.Ordinal));
                mine.Add(path + "|" + (p.Group is null ? string.Empty : p.Group.ToUpperInvariant() + ".") + p.Name.ToUpperInvariant() + "|" + ps + "|" + p.Value);
            }
        }

        mine.Sort(StringComparer.Ordinal);
        o.AddRange(mine);
        foreach (Component child in c.Components)
        {
            Collect(child, path + "/" + child.Name.ToUpperInvariant(), o);
        }
    }

    public static IEnumerable<object[]> JsonPairs() =>
    [
        ["rfc7265-b1.ics", "rfc7265-b1.json"],
        ["rfc7265-b2.ics", "rfc7265-b2.json"],
    ];

    public static IEnumerable<object[]> XmlPairs() =>
    [
        ["rfc6321-b1.ics", "rfc6321-b1.xml"],
        ["rfc6321-b2.ics", "rfc6321-b2.xml"],
    ];

    [Theory]
    [MemberData(nameof(JsonPairs))]
    public void JCalExamplesConvertBothWays(string ics, string json)
    {
        ContentDocument doc = ContentDocument.Parse(Read(ics));
        FormWriteResult w = JsonForms.ToJson(doc);
        Assert.Equal(CanonJson(Read(json)), CanonJson(w.Text));

        FormReadResult r = JsonForms.FromJson(Read(json));
        Assert.Empty(r.Defects);
        Assert.Equal(Lines(doc), Lines(r.Document));
    }

    [Theory]
    [MemberData(nameof(XmlPairs))]
    public void XCalExamplesConvertBothWays(string ics, string xml)
    {
        ContentDocument doc = ContentDocument.Parse(Read(ics));
        FormWriteResult w = XmlForms.ToXCal(doc);
        Assert.Equal(CanonXml(Read(xml)), CanonXml(w.Text));

        FormReadResult r = XmlForms.FromXml(Read(xml));
        Assert.Empty(r.Defects);
        Assert.Equal(Lines(doc), Lines(r.Document));
    }

    [Fact]
    public void JCardExampleConvertsBothWays()
    {
        // RFC 7095 Appendix B.1 differs from its own tables in two places, both documented in docs/calendar.md:
        // ANNIVERSARY 20090808T1430-0500 is printed with seconds (2009-08-08T14:30:00-05:00) although §3.5.5 keeps
        // reduced accuracy, and TZ:-0500 is printed as utc-offset although the vCard has no VALUE parameter.
        ContentDocument doc = ContentDocument.Parse(Read("rfc7095-b1.vcf"));
        string expected = Read("rfc7095-b1.json").Replace("2009-08-08T14:30:00-05:00", "2009-08-08T14:30-05:00", StringComparison.Ordinal);
        FormWriteResult w = JsonForms.ToJson(doc);
        Assert.Equal(CanonJson(expected), CanonJson(w.Text));

        FormReadResult r = JsonForms.FromJson(expected);
        Assert.Empty(r.Defects);
        Assert.Equal(Lines(doc), Lines(r.Document));

        // The RFC's own spelling is read too (seconds are kept, so the text differs only there).
        FormReadResult rfc = JsonForms.FromJson(Read("rfc7095-b1.json"));
        Assert.Empty(rfc.Defects);
        Assert.Contains(Lines(rfc.Document), static l => l.Contains("ANNIVERSARY||20090808T143000-0500", StringComparison.Ordinal));
    }

    [Fact]
    public void XCardExampleConvertsBothWays()
    {
        ContentDocument doc = ContentDocument.Parse(Read("rfc6351-s4.vcf"));
        FormWriteResult w = XmlForms.ToXCard(doc);
        Assert.Equal(CanonXml(Read("rfc6351-s4.xml")), CanonXml(w.Text));
        Assert.DoesNotContain(w.Losses, static l => l.Kind is FormLossKind.ValueNotRepresentable or FormLossKind.LineNotCarried);

        FormReadResult r = XmlForms.FromXml(Read("rfc6351-s4.xml"));
        Assert.Empty(r.Defects);
        Assert.Equal(Lines(doc), Lines(r.Document));
    }

    [Fact]
    public void XCardForeignElementBecomesXmlPropertyAndBack()
    {
        // RFC 6351 §6: the last example. The element from another namespace is wrapped in an XML property.
        string xml = """
            <?xml version="1.0"?>
            <vcards xmlns="urn:ietf:params:xml:ns:vcard-4.0">
              <vcard>
                <fn><text>J. Doe</text></fn>
                <n><surname>Doe</surname><given>J.</given><additional/><prefix/><suffix/></n>
                <x-file><parameters><mediatype><text>image/jpeg</text></mediatype></parameters><unknown>alien.jpg</unknown></x-file>
                <a xmlns="http://www.w3.org/1999/xhtml" href="http://www.example.com">My web page!</a>
              </vcard>
            </vcards>
            """;
        FormReadResult r = XmlForms.FromXml(xml);
        Assert.Empty(r.Defects);
        List<string> lines = Lines(r.Document);
        Assert.Contains("/VCARD|FN||J. Doe", lines);
        Assert.Contains("/VCARD|N||Doe;J.;;;", lines);
        Assert.Contains("/VCARD|X-FILE|MEDIATYPE=image/jpeg|alien.jpg", lines);
        Assert.Contains("/VCARD|XML||<a xmlns=\"http://www.w3.org/1999/xhtml\" href=\"http://www.example.com\">My web page!</a>", lines);

        FormWriteResult back = XmlForms.ToXCard(r.Document);
        Assert.Contains("<a xmlns=\"http://www.w3.org/1999/xhtml\" href=\"http://www.example.com\">My web page!</a>", back.Text, StringComparison.Ordinal);
        Assert.Contains("<x-file>", back.Text, StringComparison.Ordinal);
        Assert.Contains("<unknown>alien.jpg</unknown>", back.Text, StringComparison.Ordinal);
    }

    // The small examples in the body of the RFCs: (text, JSON property array).
    public static IEnumerable<object[]> PropertyExamples() =>
    [
        // RFC 7265 §3.4, §3.4.1
        ["SUMMARY:Meeting with Fred", """["summary", {}, "text", "Meeting with Fred"]"""],
        ["CATEGORIES:Meetings,Work", """["categories", {}, "text", "Meetings", "Work"]"""],
        ["GEO:37.386013;-122.082932", """["geo", {}, "float", [37.386013, -122.082932]]"""],
        ["REQUEST-STATUS:2.0;Success", """["request-status", {}, "text", ["2.0", "Success"]]"""],
        ["REQUEST-STATUS:3.7;Invalid calendar user;ATTENDEE:mailto:jsmith@example.org", """["request-status", {}, "text", ["3.7", "Invalid calendar user", "ATTENDEE:mailto:jsmith@example.org"]]"""],
        // §3.5
        ["ATTENDEE;PARTSTAT=ACCEPTED;RSVP=TRUE;ROLE=REQ-PARTICIPANT:mailto:jsmith@example.org",
            """["attendee", {"partstat":"ACCEPTED","rsvp":"TRUE","role":"REQ-PARTICIPANT"}, "cal-address", "mailto:jsmith@example.org"]"""],
        ["ATTENDEE;DELEGATED-TO=\"mailto:jdoe@example.org\",\"mailto:jqpublic@example.org\":mailto:jsmith@example.org",
            """["attendee", {"delegated-to":["mailto:jdoe@example.org","mailto:jqpublic@example.org"]}, "cal-address", "mailto:jsmith@example.org"]"""],
        // §3.6
        ["ATTACH;VALUE=BINARY;ENCODING=BASE64:SGVsbG8gV29ybGQh", """["attach", {"encoding":"BASE64"}, "binary", "SGVsbG8gV29ybGQh"]"""],
        ["X-NON-SMOKING;VALUE=BOOLEAN:TRUE", """["x-non-smoking", {}, "boolean", true]"""],
        ["DTSTART;VALUE=DATE:20110517", """["dtstart", {}, "date", "2011-05-17"]"""],
        ["DTSTART:20121017T120000", """["dtstart", {}, "date-time", "2012-10-17T12:00:00"]"""],
        ["DTSTAMP:20121017T120000Z", """["dtstamp", {}, "date-time", "2012-10-17T12:00:00Z"]"""],
        ["DTEND;TZID=Europe/Berlin:20111017T130000", """["dtend", {"tzid":"Europe/Berlin"}, "date-time", "2011-10-17T13:00:00"]"""],
        ["DURATION:P1D", """["duration", {}, "duration", "P1D"]"""],
        ["X-GRADE;VALUE=FLOAT:1.3", """["x-grade", {}, "float", 1.3]"""],
        ["PERCENT-COMPLETE:42", """["percent-complete", {}, "integer", 42]"""],
        ["FREEBUSY;FBTYPE=FREE:19970308T160000Z/P1D", """["freebusy", {"fbtype":"FREE"}, "period", ["1997-03-08T16:00:00Z", "P1D"]]"""],
        ["RRULE:FREQ=YEARLY;COUNT=5;BYDAY=-1SU,2MO;BYMONTH=10", """["rrule", {}, "recur", {"freq":"YEARLY","count":5,"byday":["-1SU","2MO"],"bymonth":10}]"""],
        ["RRULE:FREQ=MONTHLY;INTERVAL=2;BYMONTHDAY=1,15,-1;UNTIL=20131001", """["rrule", {}, "recur", {"freq":"MONTHLY","interval":2,"bymonthday":[1,15,-1],"until":"2013-10-01"}]"""],
        ["COMMENT:hello\\, world", """["comment", {}, "text", "hello, world"]"""],
        ["X-TIME-LOCAL;VALUE=TIME:123000", """["x-time-local", {}, "time", "12:30:00"]"""],
        ["X-TIME-UTC;VALUE=TIME:123000Z", """["x-time-utc", {}, "time", "12:30:00Z"]"""],
        ["TZURL:http://example.org/tz/Europe-Berlin.ics", """["tzurl", {}, "uri", "http://example.org/tz/Europe-Berlin.ics"]"""],
        ["TZOFFSETFROM:-0500", """["tzoffsetfrom", {}, "utc-offset", "-05:00"]"""],
        ["TZOFFSETTO:+1245", """["tzoffsetto", {}, "utc-offset", "+12:45"]"""],
        // §5.3
        ["X-COMPLAINT-DEADLINE:20110512T120000Z", """["x-complaint-deadline", {}, "unknown", "20110512T120000Z"]"""],
        ["X-COFFEE-DATA:Stenophylla;Guinea\\,Africa", """["x-coffee-data", {}, "unknown", "Stenophylla;Guinea\\,Africa"]"""],
        ["DTSTART;X-SLACK=30.3;VALUE=DATE:20110512", """["dtstart", {"x-slack":"30.3"}, "date", "2011-05-12"]"""],
    ];

    [Theory]
    [MemberData(nameof(PropertyExamples))]
    public void JCalPropertyExamples(string line, string expectedProperty)
    {
        ContentDocument doc = ContentDocument.Parse("BEGIN:VCALENDAR\r\n" + line + "\r\nEND:VCALENDAR\r\n");
        FormWriteResult w = JsonForms.ToJson(doc);
        string expected = "[\"vcalendar\",[" + expectedProperty + "],[]]";
        Assert.Equal(CanonJson(expected), CanonJson(w.Text));

        // and back to a line that means the same (VALUE parameters equal to the default may be added or dropped)
        FormReadResult r = JsonForms.FromJson(expected);
        Assert.Empty(r.Defects);
        FormWriteResult again = JsonForms.ToJson(r.Document);
        Assert.Equal(CanonJson(expected), CanonJson(again.Text));
    }

    public static IEnumerable<object[]> CardExamples() =>
    [
        // RFC 7095 §3.3.1
        ["CONTACT.FN:Mr. John Q. Public\\, Esq.", """["fn", {"group":"CONTACT"}, "text", "Mr. John Q. Public, Esq."]"""],
        ["ADR:;;123 Main Street;Any Town;CA;91921-1234;U.S.A.", """["adr", {}, "text", ["", "", "123 Main Street", "Any Town", "CA", "91921-1234", "U.S.A."]]"""],
        ["ADR:;;My Street,Left Side,Second Shack;Hometown;PA;18252;U.S.A.", """["adr", {}, "text", ["", "", ["My Street", "Left Side", "Second Shack"], "Hometown", "PA", "18252", "U.S.A."]]"""],
        ["GENDER:M", """["gender", {}, "text", "M"]"""],
        ["GENDER:F;grrrl", """["gender", {}, "text", ["F", "grrrl"]]"""],
        ["CATEGORIES:computers,cameras", """["categories", {}, "text", "computers", "cameras"]"""],
        ["ROLE;LANGUAGE=tr:roca", """["role", {"language":"tr"}, "text", "roca"]"""],
        ["N;SORT-AS=\"Harten,Rene\":van der Harten;Rene;J.;Sir;R.D.O.N.", """["n", {"sort-as":["Harten","Rene"]}, "text", ["van der Harten", "Rene", "J.", "Sir", "R.D.O.N."]]"""],
        // §3.5
        ["KIND:group", """["kind", {}, "text", "group"]"""],
        ["SOURCE:ldap://ldap.example.com/cn=babs%20jensen", """["source", {}, "uri", "ldap://ldap.example.com/cn=babs%20jensen"]"""],
        ["BDAY:19850412", """["bday", {}, "date-and-or-time", "1985-04-12"]"""],
        ["BDAY;VALUE=date:1985-04", """["bday", {}, "date", "1985-04"]"""],
        ["BDAY;VALUE=date:--0412", """["bday", {}, "date", "--04-12"]"""],
        ["BDAY;VALUE=date:---12", """["bday", {}, "date", "---12"]"""],
        ["BDAY:---22T1400", """["bday", {}, "date-and-or-time", "---22T14:00"]"""],
        ["BDAY:T1230", """["bday", {}, "date-and-or-time", "T12:30"]"""],
        ["BDAY:20130214T123000", """["bday", {}, "date-and-or-time", "2013-02-14T12:30:00"]"""],
        ["ANNIVERSARY;VALUE=date-time:19850412T232050+0400", """["anniversary", {}, "date-time", "1985-04-12T23:20:50+04:00"]"""],
        ["ANNIVERSARY;VALUE=date-time:19850412T232050+04", """["anniversary", {}, "date-time", "1985-04-12T23:20:50+04"]"""],
        ["ANNIVERSARY;VALUE=date-time:--0412T2320", """["anniversary", {}, "date-time", "--04-12T23:20"]"""],
        ["ANNIVERSARY;VALUE=date-time:---12T2320", """["anniversary", {}, "date-time", "---12T23:20"]"""],
        ["X-TIME;VALUE=time:232050", """["x-time", {}, "time", "23:20:50"]"""],
        ["X-TIME;VALUE=time:-2050", """["x-time", {}, "time", "-20:50"]"""],
        ["X-TIME;VALUE=time:--50", """["x-time", {}, "time", "--50"]"""],
        ["X-TIME;VALUE=time:123000-0800", """["x-time", {}, "time", "12:30:00-08:00"]"""],
        ["REV:20130214T123000Z", """["rev", {}, "timestamp", "2013-02-14T12:30:00Z"]"""],
        ["REV:20130214T123000-05", """["rev", {}, "timestamp", "2013-02-14T12:30:00-05"]"""],
        ["X-NON-SMOKING;VALUE=boolean:TRUE", """["x-non-smoking", {}, "boolean", true]"""],
        ["X-KARMA-POINTS;VALUE=integer:42", """["x-karma-points", {}, "integer", 42]"""],
        ["X-GRADE;VALUE=float:1.3", """["x-grade", {}, "float", 1.3]"""],
        ["LANG:de", """["lang", {}, "language-tag", "de"]"""],
        // §5.3
        ["X-COMPLAINT-URI:mailto:abuse@example.org", """["x-complaint-uri", {}, "unknown", "mailto:abuse@example.org"]"""],
        ["X-COFFEE-DATA:Stenophylla;Guinea\\,Africa", """["x-coffee-data", {}, "unknown", "Stenophylla;Guinea\\,Africa"]"""],
        ["GENDER;X-PROBABILITY=0.8:M", """["gender", {"x-probability":"0.8"}, "text", "M"]"""],
        ["ADR;LABEL=\"123 Maple Ave\\nSuite 901\\nVancouver BC\\nA1B 2C9\\nCanada\":;;;;;;", """["adr", {"label":"123 Maple Ave\nSuite 901\nVancouver BC\nA1B 2C9\nCanada"}, "text", ["","","","","","",""]]"""],
    ];

    [Theory]
    [MemberData(nameof(CardExamples))]
    public void JCardPropertyExamples(string line, string expectedProperty)
    {
        ContentDocument doc = ContentDocument.Parse("BEGIN:VCARD\r\nVERSION:4.0\r\n" + line + "\r\nEND:VCARD\r\n");
        FormWriteResult w = JsonForms.ToJson(doc);
        string expected = "[\"vcard\",[[\"version\",{},\"text\",\"4.0\"]," + expectedProperty + "]]";
        Assert.Equal(CanonJson(expected), CanonJson(w.Text));

        FormReadResult r = JsonForms.FromJson(expected);
        Assert.Empty(r.Defects);
        Assert.Equal(Lines(doc), Lines(r.Document));
    }

    [Fact]
    public void XCalSectionExamples()
    {
        string cal(string props) => "<icalendar xmlns=\"urn:ietf:params:xml:ns:icalendar-2.0\"><vcalendar><properties>" + props + "</properties></vcalendar></icalendar>";
        (string Line, string Xml)[] cases =
        [
            ("GEO:37.386013;-122.082932", "<geo><latitude>37.386013</latitude><longitude>-122.082932</longitude></geo>"),
            ("REQUEST-STATUS:2.0;Success", "<request-status><code>2.0</code><description>Success</description></request-status>"),
            ("ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:a@example.com", "<attendee><parameters><partstat><text>NEEDS-ACTION</text></partstat></parameters><cal-address>mailto:a@example.com</cal-address></attendee>"),
            ("X-PROPERTY:20110512T120000Z", "<x-property><unknown>20110512T120000Z</unknown></x-property>"),
            ("DTSTART;X-PARAM=PT30M:20110512T130000Z", "<dtstart><parameters><x-param><unknown>PT30M</unknown></x-param></parameters><date-time>2011-05-12T13:00:00Z</date-time></dtstart>"),
            ("RRULE:FREQ=YEARLY;COUNT=5;BYDAY=-1SU;BYMONTH=10", "<rrule><recur><freq>YEARLY</freq><count>5</count><byday>-1SU</byday><bymonth>10</bymonth></recur></rrule>"),
            ("CATEGORIES:a,b", "<categories><text>a</text><text>b</text></categories>"),
            ("RDATE;VALUE=PERIOD:20110517T120000/PT1H", "<rdate><period><start>2011-05-17T12:00:00</start><duration>PT1H</duration></period></rdate>"),
            ("ATTACH;VALUE=BINARY;ENCODING=BASE64:SGVsbG8gV29ybGQh", "<attach><parameters><encoding><text>BASE64</text></encoding></parameters><binary>SGVsbG8gV29ybGQh</binary></attach>"),
            ("TZOFFSETFROM:-0500", "<tzoffsetfrom><utc-offset>-05:00</utc-offset></tzoffsetfrom>"),
        ];
        foreach ((string line, string xml) in cases)
        {
            ContentDocument doc = ContentDocument.Parse("BEGIN:VCALENDAR\r\n" + line + "\r\nEND:VCALENDAR\r\n");
            FormWriteResult w = XmlForms.ToXCal(doc);
            Assert.Equal(CanonXml(cal(xml)), CanonXml(w.Text));
            FormReadResult r = XmlForms.FromXml(cal(xml));
            Assert.Empty(r.Defects);
            Assert.Equal(Lines(doc), Lines(r.Document));
        }
    }
}
