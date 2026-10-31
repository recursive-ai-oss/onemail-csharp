using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.VCard;

namespace RecursiveAI.OneMail.Tests.Calendar;

public class ValueAndViewTests
{
    private static ContentDocument Doc(string s) => ContentDocument.Parse(Encoding.UTF8.GetBytes(s));

    [Theory]
    [InlineData("20260131", IcsTimeKind.Date, "20260131")]
    [InlineData("20260131T090000", IcsTimeKind.Floating, "20260131T090000")]
    [InlineData("20260131T090000Z", IcsTimeKind.Utc, "20260131T090000Z")]
    [InlineData("20161231T235960Z", IcsTimeKind.Utc, "20161231T235959Z")]
    public void ParsesDatesAndTimes(string text, IcsTimeKind kind, string back)
    {
        Assert.True(IcsTime.TryParse(text, null, out IcsTime t));
        Assert.Equal(kind, t.Kind);
        Assert.Equal(back, t.ToString());
    }

    [Fact]
    public void ZonedTimeCarriesTheTzidWithoutResolvingIt()
    {
        Assert.True(IcsTime.TryParse("20260131T090000", "Europe/Berlin", out IcsTime t));
        Assert.Equal(IcsTimeKind.Zoned, t.Kind);
        Assert.Equal("Europe/Berlin", t.TzId);
        Assert.True(IcsTime.TryParse("20260131T090000Z", "Europe/Berlin", out IcsTime u));
        Assert.Equal(IcsTimeKind.Utc, u.Kind);
    }

    [Theory]
    [InlineData("20261301")]
    [InlineData("20260230")]
    [InlineData("2026013")]
    [InlineData("20260131T250000")]
    [InlineData("20260131T0900")]
    [InlineData("")]
    public void RejectsBadDates(string text) => Assert.False(IcsTime.TryParse(text, null, out _));

    [Theory]
    [InlineData("PT15M", false, 0, 0, 0, 15, 0, "PT15M")]
    [InlineData("-P1DT2H", true, 0, 1, 2, 0, 0, "-P1DT2H")]
    [InlineData("P2W", false, 2, 0, 0, 0, 0, "P2W")]
    [InlineData("PT0S", false, 0, 0, 0, 0, 0, "PT0S")]
    [InlineData("+PT1H30M10S", false, 0, 0, 1, 30, 10, "PT1H30M10S")]
    public void ParsesDurations(string text, bool neg, int w, int d, int h, int m, int s, string back)
    {
        Assert.True(IcsDuration.TryParse(text, out IcsDuration v));
        Assert.Equal(new IcsDuration(neg, w, d, h, m, s), v);
        Assert.Equal(back, v.ToString());
    }

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("15M")]
    [InlineData("P1H")]
    [InlineData("PT1")]
    [InlineData("PT1D")]
    public void RejectsBadDurations(string text) => Assert.False(IcsDuration.TryParse(text, out _));

    [Fact]
    public void ParsesPeriodsAndOffsets()
    {
        Assert.True(IcsPeriod.TryParse("19970101T180000Z/19970102T070000Z", null, out IcsPeriod p));
        Assert.NotNull(p.End);
        Assert.True(IcsPeriod.TryParse("19970101T180000Z/PT5H30M", null, out IcsPeriod q));
        Assert.Equal(new IcsDuration(false, 0, 0, 5, 30, 0), q.Duration);
        Assert.True(IcsUtcOffset.TryParse("-0500", out IcsUtcOffset o));
        Assert.Equal(TimeSpan.FromHours(-5), o.ToTimeSpan());
        Assert.True(IcsUtcOffset.TryParse("+053015", out IcsUtcOffset o2));
        Assert.Equal("+053015", o2.ToString());
        Assert.False(IcsUtcOffset.TryParse("0500", out _));
    }

    [Fact]
    public void ParsesRecurrenceRules()
    {
        Assert.True(RecurrenceRule.TryParse("FREQ=MONTHLY;BYDAY=-1SU,2MO;UNTIL=20261231T000000Z;INTERVAL=2;WKST=SU;BYSETPOS=1;X-FOO=bar", null, out RecurrenceRule r));
        Assert.Equal(RecurrenceFrequency.Monthly, r.Frequency);
        Assert.Equal(2, r.Interval);
        Assert.Equal([new WeekdayNumber(-1, DayOfWeek.Sunday), new WeekdayNumber(2, DayOfWeek.Monday)], r.ByDay);
        Assert.Equal(IcsTimeKind.Utc, r.Until!.Value.Kind);
        Assert.Equal(DayOfWeek.Sunday, r.WeekStart);
        Assert.Equal("X-FOO", Assert.Single(r.OtherParts).Key);
        Assert.True(RecurrenceRule.TryParse(r.ToString(), null, out RecurrenceRule again));
        Assert.Equal(r.ToString(), again.ToString());
        Assert.False(RecurrenceRule.TryParse("BYDAY=MO", null, out _));
        Assert.False(RecurrenceRule.TryParse("FREQ=FORTNIGHTLY", null, out _));
        Assert.False(RecurrenceRule.TryParse("FREQ=DAILY;COUNT=x", null, out _));
        Assert.False(RecurrenceRule.TryParse("FREQ=DAILY;FREQ=WEEKLY", null, out _));
    }

    [Fact]
    public void TextEscapesRoundTrip()
    {
        Assert.Equal("a,b;c\\d\ne", ContentValues.Unescape("a\\,b\\;c\\\\d\\ne"));
        Assert.Equal("a\\qb\\", ContentValues.Unescape("a\\qb\\"));
        Assert.Equal("a\\,b\\;c\\\\d\\ne", ContentValues.Escape("a,b;c\\d\r\ne"));
        Assert.Equal(["a", "b\\,c", "d"], ContentValues.SplitUnescaped("a,b\\,c,d", ','));
        Assert.Equal("a,b", ContentValues.Unescape("a,b", TextEscapeStyle.VCard21));
    }

    [Fact]
    public void QuotedPrintableAndBase64()
    {
        Assert.Equal("M\u00fcller =x", Encoding.UTF8.GetString(ContentValues.DecodeQuotedPrintable("M=C3=BCller =x")));
        Assert.Equal("=C3=A9", ContentValues.EncodeQuotedPrintable(Encoding.UTF8.GetBytes("\u00e9")));
        Assert.True(ContentValues.TryDecodeBase64("QUJD\r\n REVG", out byte[] b));
        Assert.Equal("ABCDEF", Encoding.UTF8.GetString(b));
        Assert.True(ContentValues.TryDecodeBase64("QUI", out byte[] c));
        Assert.Equal("AB", Encoding.UTF8.GetString(c));
        Assert.False(ContentValues.TryDecodeBase64("@@@@", out _));
    }

    private const string Event = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//t//t//EN
        METHOD:REQUEST
        BEGIN:VEVENT
        UID:abc
        SUMMARY:Planning\, round one
        DTSTART;TZID=America/New_York:20260105T090000
        DTEND;VALUE=DATE:20260106
        RRULE:FREQ=WEEKLY;COUNT=4;BYDAY=MO
        EXDATE;TZID=America/New_York:20260112T090000,20260119T090000
        CATEGORIES:work,deep\,focus
        ORGANIZER;CN=Ann:mailto:ann@example.com
        ATTENDEE;PARTSTAT=ACCEPTED;RSVP=TRUE;CN=Bob:mailto:bob@example.com
        PRIORITY:3
        GEO:37.386013;-122.082932
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER;RELATED=END:-PT5M
        END:VALARM
        END:VEVENT
        END:VCALENDAR

        """;

    [Fact]
    public void IcsViewExposesTypedProperties()
    {
        IcsComponent cal = IcsDocument.Calendars(Doc(Event.ReplaceLineEndings("\r\n"))).Single();
        Assert.Equal(IcsMethod.Request, cal.Method);
        Assert.Equal("2.0", cal.Version);
        IcsComponent ev = cal.Events.Single();
        Assert.Equal("Planning, round one", ev.Summary);
        Assert.Equal(new IcsTime(new DateTime(2026, 1, 5, 9, 0, 0), IcsTimeKind.Zoned, "America/New_York"), ev.Start);
        Assert.True(ev.End!.Value.IsDate);
        Assert.Equal(RecurrenceFrequency.Weekly, ev.RecurrenceRules.Single().Frequency);
        Assert.Equal(2, ev.ExceptionDates.Count);
        Assert.Equal(["work", "deep,focus"], ev.Categories);
        Assert.Equal("ann@example.com", ev.Organizer!.Email);
        Assert.Equal("ACCEPTED", ev.Attendees.Single().ParticipationStatus);
        Assert.True(ev.Attendees.Single().Rsvp);
        Assert.Equal(3, ev.Priority);
        Assert.Equal(37.386013, ev.Geo!.Value.Latitude);
        IcsTrigger trig = ev.Alarms.Single().Trigger!.Value;
        Assert.True(trig.RelatedToEnd);
        Assert.Equal(TimeSpan.FromMinutes(-5), trig.Offset!.Value.ToTimeSpan());
    }

    [Fact]
    public void IcsEditsChangeOnlyTheTouchedLines()
    {
        string text = Event.ReplaceLineEndings("\r\n");
        ContentDocument d = Doc(text);
        IcsComponent ev = IcsDocument.Calendars(d).Single().Events.Single();
        ev.SetText("SUMMARY", "A; B");
        ev.SetTime("DTEND", new IcsTime(new DateTime(2026, 1, 5, 17, 0, 0, DateTimeKind.Utc), IcsTimeKind.Utc));
        string expected = text.Replace("SUMMARY:Planning\\, round one", "SUMMARY:A\\; B", StringComparison.Ordinal)
            .Replace("DTEND;VALUE=DATE:20260106", "DTEND:20260105T170000Z", StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.UTF8.GetString(d.ToArray()));
    }

    [Fact]
    public void VTimeZoneObservancesAreParsedAsData()
    {
        ContentDocument d = Doc("BEGIN:VCALENDAR\r\nBEGIN:VTIMEZONE\r\nTZID:X\r\nBEGIN:STANDARD\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nDTSTART:19671029T020000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nEND:VCALENDAR\r\n");
        IcsComponent tz = IcsDocument.Calendars(d).Single().TimeZones.Single();
        IcsComponent std = tz.Observances.Single();
        Assert.Equal("X", tz.TimeZoneId);
        Assert.Equal(TimeSpan.FromHours(-5), std.OffsetTo!.Value.ToTimeSpan());
        Assert.Equal(11, std.RecurrenceRules.Single().ByMonth.Single());
    }

    [Fact]
    public void VCard21WithBareTypesAndQuotedPrintable()
    {
        ContentDocument d = Doc("BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;Hans\r\nFN:Hans M\r\nTEL;WORK;VOICE;PREF:+49 30 1\r\nTEL;CELL:+49 170 1\r\nEMAIL;INTERNET:h@example.com\r\nADR;HOME:;;1 Main\\; St;Berlin;;10115;DE\r\nNOTE;ENCODING=QUOTED-PRINTABLE:line1=0D=0Aline2=\r\n continued\r\nPHOTO;ENCODING=BASE64;JPEG:\r\nQUJD\r\nREVG\r\n\r\nEND:VCARD\r\n");
        VCardView c = VCardDocument.Cards(d).Single();
        Assert.Equal(VCardVersion.V21, c.Version);
        Assert.Equal("M\u00fcller", c.Name!.Family.Single());
        Assert.Equal("Hans", c.Name.Given.Single());
        Assert.Equal(["WORK", "VOICE", "PREF"], c.Phones[0].Types);
        Assert.Equal(1, c.Phones[0].Preference);
        Assert.Equal(["INTERNET"], c.Emails.Single().Types);
        Assert.Equal("1 Main; St", c.Addresses.Single().Street.Single());
        Assert.Equal("DE", c.Addresses.Single().Country.Single());
        Assert.Equal("line1\r\nline2 continued", c.Note);
        VCardMedia photo = c.Photos.Single();
        Assert.Equal("ABCDEF", Encoding.ASCII.GetString(photo.Data!));
        Assert.Equal("image/jpeg", photo.MediaType);
    }

    [Fact]
    public void VCard30AndGroups()
    {
        ContentDocument d = Doc("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ann\r\nN:Lee;Ann;;Dr.;PhD,MD\r\nORG:Acme\\, Inc.;R&D\r\nitem1.EMAIL;TYPE=INTERNET,HOME:a@example.com\r\nitem1.X-ABLabel:Home\r\nCATEGORIES:a,b\r\nBDAY:1996-10-22\r\nPHOTO;ENCODING=b;TYPE=PNG:QUJD\r\nEND:VCARD\r\n");
        VCardView c = VCardDocument.Cards(d).Single();
        Assert.Equal(VCardVersion.V30, c.Version);
        Assert.Equal(["PhD", "MD"], c.Name!.Suffixes);
        Assert.Equal(["Acme, Inc.", "R&D"], c.Organization);
        Assert.Equal(["item1"], c.Groups);
        Assert.Equal(2, c.GetGroup("ITEM1").Count());
        Assert.Equal(["INTERNET", "HOME"], c.Emails.Single().Types);
        Assert.Equal(["a", "b"], c.Categories);
        Assert.Equal(new VCardDate(1996, 10, 22, null, null, null, null), c.Birthday);
        Assert.Equal("image/png", c.Photos.Single().MediaType);
    }

    [Fact]
    public void VCard40KindMembersAndDataUriPhoto()
    {
        ContentDocument d = Doc("BEGIN:VCARD\r\nVERSION:4.0\r\nKIND:group\r\nFN:Team\r\nMEMBER:urn:uuid:1\r\nMEMBER:mailto:x@example.com\r\nTEL;TYPE=\"work,voice\";PREF=1:tel:+1-555\r\nPHOTO:data:image/jpeg;base64,QUJD\r\nBDAY:--0412\r\nEND:VCARD\r\n");
        VCardView c = VCardDocument.Cards(d).Single();
        Assert.Equal(VCardVersion.V40, c.Version);
        Assert.Equal("group", c.Kind);
        Assert.Equal(2, c.Members.Count);
        Assert.Equal(["work", "voice"], c.Phones.Single().Types);
        Assert.Equal(1, c.Phones.Single().Preference);
        Assert.Equal("ABC", Encoding.ASCII.GetString(c.Photos.Single().Data!));
        Assert.Equal("image/jpeg", c.Photos.Single().MediaType);
        Assert.Equal(new VCardDate(null, 4, 12, null, null, null, null), c.Birthday);
    }

    [Theory]
    [InlineData("19961022T140000Z", 1996, 10, 22, 14, 0, 0, "Z")]
    [InlineData("1996-10-22T14:00:00-05:00", 1996, 10, 22, 14, 0, 0, "-05:00")]
    [InlineData("---22", null, null, 22, null, null, null, null)]
    [InlineData("1996", 1996, null, null, null, null, null, null)]
    public void ParsesVCardDates(string text, int? y, int? mo, int? d, int? h, int? mi, int? s, string? off) =>
        Assert.Equal(new VCardDate(y, mo, d, h, mi, s, off), VCardDate.TryParse(text));

    [Fact]
    public void FreeTextBirthdayIsNotADate() => Assert.Null(VCardDate.TryParse("circa 1800"));
}
