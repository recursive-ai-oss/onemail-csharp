using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Tests.Mime;

/// <summary>
/// Dates in the common written form are read by a short path. An extra empty comment at the end keeps a value off that path without changing what
/// the general path makes of it, so the two must agree on every value, damaged ones included.
/// </summary>
public class DateParserFastPathTests
{
    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Xyz", "mon", "FRI"];
    private static readonly string[] Months = ["Jan", "feb", "MAR", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec", "Foo", "Janu"];
    private static readonly string[] Zones = ["+0000", "-0000", "-0700", "+0530", "+1400", "+1401", "-1400", "+2359", "+2400", "+0060", "-0600", "+5", "PST", "GMT"];
    private static readonly string[] Comments = ["", " (PDT)", " (a b c)", " (x(y))", " ()", " (", " )"];

    private static (bool Ok, MimeDate Date, List<MimeDefectKind> Defects) Parse(string text)
    {
        var defects = new List<MimeDefect>();
        bool ok = DateParser.TryParse(Encoding.Latin1.GetBytes(text), out MimeDate d, defects, 10, 1);
        return (ok, d, defects.Select(x => x.Kind).ToList());
    }

    [Fact]
    public void CanonicalPathAgreesWithTheGeneralPath()
    {
        var rnd = new Random(11);
        string Pick(string[] a) => a[rnd.Next(a.Length)];
        int fast = 0;
        for (int i = 0; i < 20000; i++)
        {
            string weekday = rnd.Next(4) == 0 ? string.Empty : Pick(Days) + ", ";
            string day = rnd.Next(3) == 0 ? rnd.Next(0, 34).ToString("D2") : rnd.Next(0, 34).ToString();
            string year = rnd.Next(10) == 0 ? rnd.Next(0, 100).ToString("D2") : (1990 + rnd.Next(0, 60)).ToString();
            string time = $"{rnd.Next(0, 26):D2}:{rnd.Next(0, 62):D2}:{rnd.Next(0, 62):D2}";
            string text = $"{weekday}{day} {Pick(Months)} {year} {time} {Pick(Zones)}{Pick(Comments)}";
            string lead = rnd.Next(3) == 0 ? "" : rnd.Next(2) == 0 ? " " : " \t";
            var a = Parse(lead + text + (rnd.Next(4) == 0 ? " " : ""));
            var b = Parse(lead + text + " ()");
            Assert.Equal(b.Ok, a.Ok);
            Assert.Equal(b.Date, a.Date);
            Assert.Equal(b.Defects, a.Defects);
            if (a.Ok && a.Defects.Count == 0)
            {
                fast++;
            }
        }

        Assert.True(fast > 500, "the generator should produce plenty of clean dates; got " + fast);
    }
}
