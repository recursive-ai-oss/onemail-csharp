using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;

namespace RecursiveAI.OneMail.Tests.Calendar;

public class DocumentTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private const string Ics =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//x//y//EN\r\nBEGIN:VEVENT\r\nUID:1@example.com\r\nSUMMARY:Lunch\\, with team\r\nDTSTART;TZID=Europe/Berlin:20260131T120000\r\nBEGIN:VALARM\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    public static IEnumerable<object[]> MalformedInputs() =>
    [
        [""],
        ["\r\n"],
        ["no colon at all"],
        ["BEGIN:VCALENDAR"],
        ["BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nEND:VCALENDAR\r\n"],
        ["END:VEVENT\r\nBEGIN:VCALENDAR\r\nEND:VCALENDAR"],
        ["BEGIN:\r\nX:1\r\n"],
        ["BEGIN:VCARD\nVERSION:3.0\nFN:A\nEND:VCARD\n"],
        ["BEGIN:VCARD\rVERSION:3.0\rFN:A\rEND:VCARD\r"],
        ["\r\n\r\nBEGIN:VCARD\r\nFN:A\r\nEND:VCARD\r\n\r\ntrailing junk\r\n"],
        ["BEGIN:VCARD\r\nFN:A\r\n b\r\n\tc\r\nEND:VCARD"],
        ["BEGIN:VCARD\r\nVERSION:2.1\r\nNOTE;QUOTED-PRINTABLE:a=\r\nb=\r\n\r\nEND:VCARD\r\n"],
        ["BEGIN:VCARD\r\nPHOTO;ENCODING=BASE64:\r\nQUJD\r\nREVG\r\n\r\nEND:VCARD\r\n"],
        ["BEGIN:VCARD\r\nX;CN=\"unterminated:v\r\nEND:VCARD\r\n"],
        ["A\r\n:v\r\n;;;:x\r\n"],
        ["BEGIN:VEVENT\nX:1\r\nY:2\rZ:3\r\nEND:VEVENT"],
        [" leading\r\nA:1"],
    ];

    [Theory]
    [MemberData(nameof(MalformedInputs))]
    public void UntouchedDocumentWritesBackByteIdentical(string text)
    {
        byte[] data = B(text);
        ContentDocument d = ContentDocument.Parse(data);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void BinaryGarbageRoundTripsToo()
    {
        Random rnd = new(42);
        for (int i = 0; i < 200; i++)
        {
            byte[] data = new byte[rnd.Next(0, 400)];
            rnd.NextBytes(data);
            // Seed some structure so the parser has components to nest.
            if (i % 2 == 0)
            {
                data = [.. B("BEGIN:A\r\nBEGIN:B\r\n"), .. data, .. B("\r\nEND:A\r\n")];
            }

            Assert.Equal(data, ContentDocument.Parse(data).ToArray());
        }
    }

    [Fact]
    public void ParsesTheComponentTree()
    {
        ContentDocument d = ContentDocument.Parse(B(Ics));
        Component cal = Assert.Single(d.Components);
        Assert.Equal("VCALENDAR", cal.Name);
        Component ev = Assert.Single(cal.Components);
        Assert.Equal("VEVENT", ev.Name);
        Assert.Equal("Lunch, with team", ev.FindProperty("summary")!.GetText());
        Assert.Equal("Europe/Berlin", ev.FindProperty("DTSTART")!.GetParameter("TZID"));
        Assert.Equal("VALARM", Assert.Single(ev.Components).Name);
        Assert.Empty(d.Defects);
    }

    [Fact]
    public void EditingOnePropertyRewritesOnlyThatLine()
    {
        ContentDocument d = ContentDocument.Parse(B(Ics));
        ContentProperty summary = d.Components.Single().Components.Single().FindProperty("SUMMARY")!;
        summary.Value = "Dinner";
        string expected = Ics.Replace("SUMMARY:Lunch\\, with team", "SUMMARY:Dinner", StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.UTF8.GetString(d.ToArray()));
    }

    [Fact]
    public void EditedLineKeepsTheDocumentsLineEnd()
    {
        ContentDocument d = ContentDocument.Parse(B(Ics.Replace("\r\n", "\n", StringComparison.Ordinal)));
        d.Components.Single().FindProperty("VERSION")!.Value = "2.0 ";
        Assert.DoesNotContain('\r', Encoding.UTF8.GetString(d.ToArray()));
    }

    [Fact]
    public void ModifiedLongLineIsFoldedAt75OctetsWithoutSplittingUtf8()
    {
        ContentDocument d = ContentDocument.Parse(B("BEGIN:X\r\nA:1\r\nEND:X\r\n"));
        d.Components.Single().FindProperty("A")!.Value = new string('\u00e9', 100);
        byte[] bytes = d.ToArray();
        string text = Encoding.UTF8.GetString(bytes);
        string[] physical = text.Split("\r\n");
        Assert.All(physical, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.DoesNotContain('\ufffd', text);
        ContentDocument again = ContentDocument.Parse(bytes);
        Assert.Equal(new string('\u00e9', 100), again.Components.Single().FindProperty("A")!.Value);
    }

    [Fact]
    public void AddedPropertyAfterUnterminatedLastLineGetsALineEnd()
    {
        ContentDocument d = ContentDocument.Parse(B("BEGIN:X\r\nA:1\r\nEND:X"));
        d.Components.Single().Add(new ContentProperty("B", "2"));
        Assert.Equal("BEGIN:X\r\nA:1\r\nB:2\r\nEND:X", Encoding.UTF8.GetString(d.ToArray()));
    }

    [Fact]
    public void ParameterEditsMarkThePropertyModifiedAndQuoteWhenNeeded()
    {
        ContentDocument d = ContentDocument.Parse(B("BEGIN:X\r\nATTENDEE:mailto:a@b\r\nEND:X\r\n"));
        ContentProperty p = d.Components.Single().FindProperty("ATTENDEE")!;
        Assert.False(p.IsModified);
        p.Parameters.Add(new ContentParameter("CN", "Smith, \"Bob\"\nJr"));
        Assert.True(p.IsModified);
        string text = Encoding.UTF8.GetString(d.ToArray());
        Assert.Contains("ATTENDEE;CN=\"Smith, ^'Bob^'^nJr\":mailto:a@b\r\n", text, StringComparison.Ordinal);
        ContentProperty back = ContentDocument.Parse(d.ToArray()).Components.Single().FindProperty("ATTENDEE")!;
        Assert.Equal("Smith, \"Bob\"\nJr", back.GetParameter("CN"));
    }

    [Fact]
    public void CaretDecodingIsReportedAsAmbiguous()
    {
        ContentDocument d = ContentDocument.Parse(B("BEGIN:X\r\nA;CN=x^'y^^z^n:1\r\nEND:X\r\n"));
        Assert.Equal("x\"y^z\n", d.Components.Single().FindProperty("A")!.GetParameter("CN"));
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.AmbiguousCaretEncoding && x.IsAmbiguity);
    }

    [Fact]
    public void MultiValuedAndBareParametersAreParsed()
    {
        ContentDocument d = ContentDocument.Parse(B("BEGIN:VCARD\r\nTEL;WORK;VOICE;TYPE=pref,cell:+1\r\nEND:VCARD\r\n"));
        ContentProperty p = d.Components.Single().FindProperty("TEL")!;
        Assert.Equal(["WORK", "VOICE", "TYPE"], p.Parameters.Select(x => x.Name));
        Assert.True(p.Parameters[0].IsBare);
        Assert.Equal(["pref", "cell"], p.Parameters[2].Values);
    }

    [Fact]
    public void EmptyParameterNameIsReported()
    {
        ContentDocument d = ContentDocument.Parse(B("BEGIN:X\r\nA;=v:1\r\nEND:X\r\n"));
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.EmptyParameterName);
    }

    [Fact]
    public void MissingEndIsReportedAndNotInvented()
    {
        byte[] data = B("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:1\r\nEND:VCALENDAR\r\n");
        ContentDocument d = ContentDocument.Parse(data);
        Component ev = d.Components.Single().Components.Single();
        Assert.False(ev.IsClosed);
        Assert.Single(d.Defects, x => x.Kind == CalendarDefectKind.MissingEnd);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void UnmatchedEndIsKeptAsAMalformedLine()
    {
        byte[] data = B("END:VEVENT\r\nBEGIN:X\r\nEND:X\r\n");
        ContentDocument d = ContentDocument.Parse(data);
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.UnmatchedEnd);
        Assert.Equal(ContentLineKind.Malformed, ((ContentProperty)d.Root.Items[0]).Kind);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void BeginWithoutNameIsKeptAsAProperty()
    {
        ContentDocument d = ContentDocument.Parse(B("BEGIN:\r\n"));
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.BeginWithoutName);
        Assert.Single(d.Root.Properties);
    }

    [Fact]
    public void DepthLimitKeepsDeeperBeginAndEndAsProperties()
    {
        string text = string.Concat(Enumerable.Range(0, 6).Select(i => $"BEGIN:C{i}\r\n")) + string.Concat(Enumerable.Range(0, 6).Reverse().Select(i => $"END:C{i}\r\n"));
        byte[] data = B(text);
        ContentDocument d = ContentDocument.Parse(data, new ContentDocumentOptions { Limits = new CalendarLimits { MaxDepth = 3 } });
        Assert.Equal(3, d.Defects.Count(x => x.Kind == CalendarDefectKind.DepthLimitExceeded));
        Component c = d.Components.Single();
        int depth = 1;
        while (c.Components.FirstOrDefault() is { } n) { c = n; depth++; }
        Assert.Equal(3, depth);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void ComponentLimitKeepsTheRestAsProperties()
    {
        byte[] data = B(string.Concat(Enumerable.Repeat("BEGIN:X\r\nEND:X\r\n", 10)));
        ContentDocument d = ContentDocument.Parse(data, new ContentDocumentOptions { Limits = new CalendarLimits { MaxComponents = 4 } });
        Assert.Equal(4, d.Components.Count());
        Assert.Equal(6, d.Defects.Count(x => x.Kind == CalendarDefectKind.ComponentLimitExceeded));
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void PropertyLimitKeepsTheRestAsOneBlock()
    {
        byte[] data = B("BEGIN:X\r\nA:1\r\nB:2\r\nC:3\r\nD:4\r\nEND:X\r\n");
        ContentDocument d = ContentDocument.Parse(data, new ContentDocumentOptions { Limits = new CalendarLimits { MaxProperties = 3 } });
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.PropertyLimitExceeded && x.IsLimit);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void TotalSizeLimitKeepsTheRestAsOneBlock()
    {
        byte[] data = B("BEGIN:X\r\nA:1\r\nB:2\r\nEND:X\r\n");
        ContentDocument d = ContentDocument.Parse(data, new ContentDocumentOptions { Limits = new CalendarLimits { MaxTotalBytes = 20 } });
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.TotalSizeLimitExceeded);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void LineLimitCutsTheValueButKeepsTheBytes()
    {
        byte[] data = B("BEGIN:X\r\nA:" + new string('x', 5000) + "\r\nEND:X\r\n");
        ContentDocument d = ContentDocument.Parse(data, new ContentDocumentOptions { Limits = new CalendarLimits { MaxLineBytes = 100 } });
        Assert.Equal(98, d.Components.Single().FindProperty("A")!.Value.Length);
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.LineTooLong);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void ByteOrderMarkSurvivesTheRoundTrip()
    {
        byte[] data = [0xEF, 0xBB, 0xBF, .. B("BEGIN:X\r\nEND:X\r\n")];
        ContentDocument d = ContentDocument.Parse(data);
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.ByteOrderMark);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void NonUtf8TextIsReadAsWindows1252AndFlagged()
    {
        byte[] data = [.. B("BEGIN:X\r\nA:caf"), 0xE9, .. B("\r\nEND:X\r\n")];
        ContentDocument d = ContentDocument.Parse(data);
        Assert.Equal("caf\u00e9", d.Components.Single().FindProperty("A")!.Value);
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.AmbiguousNonUtf8Text && x.IsAmbiguity);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void DeclaredCharsetDecodesTheBytesAndIsKeptOnWrite()
    {
        byte[] data = [.. B("BEGIN:VCARD\r\nFN;CHARSET=ISO-8859-1:J"), 0xFC, .. B("rgen\r\nEND:VCARD\r\n")];
        ContentDocument d = ContentDocument.Parse(data);
        ContentProperty fn = d.Components.Single().FindProperty("FN")!;
        Assert.Equal("J\u00fcrgen", fn.Value);
        Assert.Empty(d.Defects);
        fn.Value = "J\u00fcrgen B";
        Assert.Equal([.. B("BEGIN:VCARD\r\nFN;CHARSET=ISO-8859-1:J"), 0xFC, .. B("rgen B\r\nEND:VCARD\r\n")], d.ToArray());
    }

    [Fact]
    public void UnknownCharsetIsReported()
    {
        byte[] data = [.. B("BEGIN:VCARD\r\nFN;CHARSET=NO-SUCH-SET:J"), 0xC3, 0xBC, .. B("\r\nEND:VCARD\r\n")];
        ContentDocument d = ContentDocument.Parse(data);
        Assert.Equal("J\u00fc", d.Components.Single().FindProperty("FN")!.Value);
        Assert.Contains(d.Defects, x => x.Kind == CalendarDefectKind.UnknownCharset);
    }

    [Theory]
    [InlineData("UTF-7")]
    [InlineData("utf-7")]
    [InlineData("65000")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\u00e9\u00e9")]
    public void CharsetsTheRuntimeRefusesAreUnknownNotFatal(string charset)
    {
        // Found by fuzzing: a CHARSET naming UTF-7 made Encoding.GetEncoding throw NotSupportedException.
        byte[] data = [.. B("BEGIN:VCARD\r\nFN;CHARSET=" + charset + ":J"), 0xC3, 0xBC, .. B("\r\nEND:VCARD\r\n")];
        ContentDocument d = ContentDocument.Parse(data);
        Assert.Equal("J\u00fc", d.Components.Single().FindProperty("FN")!.Value);
        Assert.Equal(data, d.ToArray());
    }

    [Fact]
    public void QuotedPrintableValueDecodesWithItsCharset()
    {
        ContentDocument d = ContentDocument.Parse(B("BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;Hans\r\nEND:VCARD\r\n"));
        Assert.Equal("M\u00fcller;Hans", d.Components.Single().FindProperty("N")!.GetDecodedValue());
    }

    [Fact]
    public void WriterRejectsLineBreaksInValues()
    {
        ContentLineWriter w = new(new MemoryStream());
        Assert.Throws<ArgumentException>(() => w.WriteLine(null, "A", null, "x\ny"));
        Assert.Throws<ArgumentException>(() => w.WriteLine(null, "A:B", null, "x"));
    }

    [Fact]
    public void EditingAPropertyWithAnOddGroupOrNameStillWrites()
    {
        // Found by fuzzing: a dotted group such as "A.B" (from "A.B.C:x") made the writer throw after an edit.
        byte[] data = B("BEGIN:X\r\nA.B.C:x\r\nEND:X\r\n");
        ContentDocument d = ContentDocument.Parse(data);
        ContentProperty p = d.Components.Single().FindProperty("C")!;
        Assert.Equal("A.B", p.Group);
        p.Value = "y";
        Assert.Equal("BEGIN:X\r\nA.B.C:y\r\nEND:X\r\n", Encoding.UTF8.GetString(d.ToArray()));
    }

    [Fact]
    public void PropertyNamesAndGroupsWithDelimitersAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new ContentProperty("A:B", "x"));
        Assert.Throws<ArgumentException>(() => new ContentProperty("", "x"));
        Assert.Throws<ArgumentException>(() => new ContentProperty("A", "x", "g;h"));
        ContentProperty p = new("A", "x");
        Assert.Throws<ArgumentException>(() => p.Name = "B\n");
        Assert.Throws<ArgumentException>(() => p.Group = "");
    }

    [Fact]
    public void BlankAndMalformedLinesCannotBeEdited()
    {
        ContentDocument d = ContentDocument.Parse(B("junk\r\n"));
        ContentProperty p = (ContentProperty)d.Root.Items[0];
        Assert.Throws<InvalidOperationException>(() => p.Value = "x");
    }

    [Fact]
    public async Task ParseAsyncReadsAStream()
    {
        using MemoryStream ms = new(B(Ics));
        ContentDocument d = await ContentDocument.ParseAsync(ms, null, TestContext.Current.CancellationToken);
        Assert.Single(d.Components);
    }
}
