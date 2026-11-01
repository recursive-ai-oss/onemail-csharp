using RecursiveAI.OneMail.Cli;

namespace RecursiveAI.OneMail.Tests.Cli;

public sealed class InspectCalendarTests
{
    private const string Ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\nBEGIN:VEVENT\r\nUID:1\r\nDTSTAMP:20260101T000000Z\r\nSUMMARY:x\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:2\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
    private const string Vcf = "BEGIN:VCARD\r\nVERSION:4.0\r\nFN:A B\r\nEND:VCARD\r\nBEGIN:VCARD\r\nVERSION:4.0\r\nFN:C D\r\nEND:VCARD\r\n";
    private const string JCal = "[\"vcalendar\",[[\"version\",{},\"text\",\"2.0\"]],[[\"vevent\",[[\"uid\",{},\"text\",\"1\"]],[]]]]";

    private static (int Rc, string Out) Run(string name, string content, params string[] options)
    {
        string dir = Path.Combine(Path.GetTempPath(), "onemail-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            StringWriter o = new();
            StringWriter e = new();
            int rc = Inspect.Run([.. options, path], o, e);
            Assert.Contains("0 not readable", e.ToString());
            return (rc, o.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void IcsIsSummarised()
    {
        (int rc, string o) = Run("a.ics", Ics);
        Assert.Equal(0, rc);
        Assert.Contains(": Ics", o);
        Assert.Contains("calendars: 1  components: VEVENT 1, VTODO 1  defects: 0", o);
    }

    [Fact]
    public void VCardIsSummarised()
    {
        (_, string o) = Run("a.vcf", Vcf);
        Assert.Contains(": VCard", o);
        Assert.Contains("cards: 2  defects: 0", o);
    }

    [Fact]
    public void JCalIsSummarisedAndJsonLineIsEmitted()
    {
        (_, string o) = Run("a.json", JCal);
        Assert.Contains(": JCal", o);
        Assert.Contains("VEVENT 1", o);
        (_, string j) = Run("a.json", JCal, "--json");
        Assert.Contains("\"type\":\"calendar\"", j);
        Assert.Contains("\"VEVENT\":1", j);
    }
}
