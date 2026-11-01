using System.Text;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Mapi;
using static RecursiveAI.OneMail.Tests.Calendaring.CalendarFixtures;

namespace RecursiveAI.OneMail.Tests.Calendaring;

public class CalendarViewTests
{
    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s.Replace("\n", "\r\n", StringComparison.Ordinal));

    [Fact]
    public void AppointmentAndTaskItemsKeepTheirKindAndOpenTheirTypedViews()
    {
        MapiMessage appt = Series(new RecurrencePattern(), 600, 630, UtcRule, "UTC", "Standup");
        var view = new MapiMailItem(appt, default);
        Assert.Equal(MailItemKind.Appointment, view.Kind);
        Assert.Equal("Standup", view.Subject);
        Assert.NotNull(view.Appointment);
        Assert.Null(view.TaskItem);

        var note = new MapiMailItem(Series(new RecurrencePattern(), 600, 630, UtcRule, "UTC", "n", "IPM.Note"), default);
        Assert.Equal(MailItemKind.Message, note.Kind);
        Assert.Null(note.Appointment);
    }

    // RFC 6047: an iMIP message carries the invitation as a text/calendar alternative or attachment; the view must let a caller reach it.
    [Fact]
    public void ATextCalendarPartInMimeIsReachableThroughTheAttachments()
    {
        IMailItem m = Mail.Parse(B(
            "From: a@example.com\nTo: b@example.com\nSubject: Invitation\nMIME-Version: 1.0\nContent-Type: multipart/mixed; boundary=m\n\n" +
            "--m\nContent-Type: text/plain\n\nSee attached.\n" +
            "--m\nContent-Type: text/calendar; method=REQUEST; charset=utf-8\nContent-Disposition: attachment; filename=invite.ics\n\n" +
            "BEGIN:VCALENDAR\nVERSION:2.0\nMETHOD:REQUEST\nBEGIN:VEVENT\nUID:x@example.com\nDTSTAMP:20260101T000000Z\nDTSTART:20260102T100000Z\nSUMMARY:Sync\nEND:VEVENT\nEND:VCALENDAR\n" +
            "--m--\n"));
        IMailAttachment cal = Assert.Single(m.Attachments, a => a.ContentType.StartsWith("text/calendar", StringComparison.OrdinalIgnoreCase));
        using Stream s = cal.Open();
        IcsComponent doc = IcsDocument.Calendars(ContentDocument.Parse(new BinaryReader(s).ReadBytes(100000))).First();
        Assert.Equal("Sync", doc.Events.First().Component.FindProperty("SUMMARY")!.Value);
    }

    [Fact]
    public void ATextCalendarAlternativeIsListedAsACalendarPart()
    {
        var m = (MimeMailItem)Mail.Parse(B(
            "Subject: Invitation\nMIME-Version: 1.0\nContent-Type: multipart/alternative; boundary=a\n\n" +
            "--a\nContent-Type: text/plain\n\nSee attached.\n" +
            "--a\nContent-Type: text/calendar; method=REQUEST\n\nBEGIN:VCALENDAR\nEND:VCALENDAR\n--a--\n"));
        Assert.Single(m.CalendarParts);
    }

    [Fact]
    public void AnIcsAttachmentTypedApplicationIcsIsACalendarPartAndGivesACard()
    {
        var m = (MimeMailItem)Mail.Parse(B(
            "Subject: Event\nMIME-Version: 1.0\nContent-Type: multipart/mixed; boundary=a\n\n" +
            "--a\nContent-Type: text/plain\n\nSee attached.\n" +
            "--a\nContent-Type: application/ics; name=event.ics\nContent-Disposition: attachment; filename=event.ics\n\n" +
            "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//x//y//EN\nBEGIN:VEVENT\nUID:1@example.org\nDTSTAMP:20260101T000000Z\nDTSTART:20260105T100000Z\nDTEND:20260105T110000Z\nSUMMARY:Planning\nEND:VEVENT\nEND:VCALENDAR\n" +
            "--a--\n"));
        Assert.Single(m.CalendarParts);
        Assert.Equal("Planning", Assert.Single(RecursiveAI.OneMail.Display.CalendarCard.FromItem(m)).Summary);
    }
}
