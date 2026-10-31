using System.Linq;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Client;
using RecursiveAI.OneMail.Cryptography;
using RecursiveAI.OneMail.Render;

namespace RecursiveAI.OneMail.AotSmoke;

/// <summary>Touches every package under NativeAOT with trimming; the publish must produce no trim or AOT warning.</summary>
internal static class Program
{
    private static int Main()
    {
        string[] lines =
        [
            CalendarLibrary.Name,
            OneMailLibrary.Describe(),
            ClientLibrary.Describe(),
            CryptographyLibrary.Describe(),
            RenderLibrary.Describe(),
        ];
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }

        // Calendar: parse, read typed values, edit one line and write back, all under NativeAOT.
        ContentDocument cal = ContentDocument.Parse("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:a\\,b\r\nDTSTART;TZID=Europe/Paris:20260101T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        IcsComponent ev = IcsDocument.Calendars(cal).Single().Events.Single();
        ev.SetText("SUMMARY", "c;d");
        ContentDocument card = ContentDocument.Parse("BEGIN:VCARD\r\nVERSION:2.1\r\nN;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;Hans\r\nTEL;WORK:+1\r\nEND:VCARD\r\n");
        VCardView c = VCardDocument.Cards(card).Single();
        if (ev.Summary != "c;d" || ev.RecurrenceRules.Count != 1 || c.Name?.Given[0] != "Hans" || c.Phones[0].Types[0] != "WORK" || cal.ToArray().Length == 0)
        {
            return 2;
        }

        return OneMailLibrary.Describe() == "RecursiveAI.OneMail (on RecursiveAI.OneMail.Calendar)" ? 0 : 1;
    }
}
