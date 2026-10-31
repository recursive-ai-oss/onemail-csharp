using System.Linq;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.VCard;
using RecursiveAI.OneMail.Client;
using RecursiveAI.OneMail.Cryptography;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Render;
using RecursiveAI.OneMail.Text;

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

        // Text layer: encoded words, charsets (OneHtml tables, ISO-2022, UTF-7), RFC 2231 parameters.
        var collector = new ParameterCollector();
        collector.Add("filename*0*"u8, "utf-8''caf%C3"u8);
        collector.Add("filename*1*"u8, "%A9.txt"u8);
        bool textOk =
            Rfc2047.DecodeText("=?utf-8?B?5pel5pys6Kqe?= =?iso-8859-1?Q?caf=E9?=") == "日本語café"
            && MailEncoding.ForLabel("shift_jis")!.GetString([0x93, 0xFA, 0x96, 0x7B]) == "日本"
            && MailEncoding.ForLabel("iso-2022-jp")!.GetString([0x1B, 0x24, 0x42, 0x46, 0x7C, 0x1B, 0x28, 0x42]) == "日"
            && MailEncoding.ForCodePage(50221) is not null
            && ImapUtf7.Decode("&ZeVnLIqe-"u8) == "日本語"
            && Utf7.Decode("+ZeVnLIqe-"u8) == "日本語"
            && collector.Resolve()[0].Value == "café.txt"
            && Rfc2047.DecodeText(Rfc2047.Encode("café 日")) == "café 日";
        Console.WriteLine("text: " + (textOk ? "ok" : "FAILED"));

        // The MIME layer under NativeAOT: parse a small multipart, read a decoded body, write it back unchanged.
        byte[] message = "Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Transfer-Encoding: base64\r\n\r\nSGk=\r\n--b--\r\n"u8.ToArray();
        var root = (MimeMultipart)MimeEntity.Parse(message);
        var part = (MimePart)root.Children[0];
        bool mimeOk = System.Text.Encoding.ASCII.GetString(part.GetDecodedContent()) == "Hi" && root.ToArray().AsSpan().SequenceEqual(message);
        Console.WriteLine(mimeOk ? "mime ok" : "mime FAILED");

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

        return textOk && mimeOk && OneMailLibrary.Describe() == "RecursiveAI.OneMail (on RecursiveAI.OneMail.Calendar)" ? 0 : 1;
    }
}
