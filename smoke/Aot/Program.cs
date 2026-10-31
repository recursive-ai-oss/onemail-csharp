using System.Linq;
using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
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

        // Structured headers: addresses, dates, identifiers and RFC 2231 parameters, and the writer's header encoding.
        byte[] structured = "From: =?utf-8?q?Caf=C3=A9?= <a@example.com>\r\nTo: Doe, John <j@example.org>, g: x@example.net;\r\nDate: Mon, 14 May 2001 23:39:00 +0200\r\nMessage-ID: <id@example.com>\r\nContent-Disposition: attachment; filename*=utf-8''caf%C3%A9.txt\r\n\r\nx"u8.ToArray();
        MimeEntity sm = MimeEntity.Parse(structured);
        var mailboxes = AddressParser.Flatten(sm.GetAddresses("From")).Concat(AddressParser.Flatten(sm.GetAddresses("To"))).ToList();
        var written = new MimeHeader("Subject", "caf\u00e9 \u65e5");
        bool structuredOk = mailboxes.Count == 3 && mailboxes[0].Name == "Caf\u00e9" && mailboxes[1].Name == "Doe, John"
            && sm.GetDate()!.Value.OffsetMinutes == 120 && sm.GetMessageId() == "id@example.com" && sm.FileName == "caf\u00e9.txt"
            && written.Value == "caf\u00e9 \u65e5" && System.Text.Ascii.IsValid(written.Raw.Span)
            && MimeHeaderValue.Parameter("filename", "caf\u00e9.txt").Contains("filename*=", StringComparison.Ordinal);
        Console.WriteLine("structured: " + (structuredOk ? "ok" : "FAILED"));

        return textOk && mimeOk && structuredOk && OneMailLibrary.Describe() == "RecursiveAI.OneMail (on RecursiveAI.OneMail.Calendar)" ? 0 : 1;
    }
}
