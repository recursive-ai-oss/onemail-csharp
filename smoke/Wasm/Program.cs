using RecursiveAI.OneMail;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.WasmSmoke;

/// <summary>Runs the format core in the browser's .NET runtime: parse a message, read an mbox through the shared view, read
/// an ICS tree, then the Outlook formats (<see cref="OutlookChecks"/>). Prints one line per check and a final "wasm smoke: ok" or "wasm smoke: FAILED".</summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine(OneMailLibrary.Describe());

        IMailItem parsed = Mail.Parse("Subject: =?utf-8?B?Y2Fmw6k=?=\r\nFrom: A <a@example.com>\r\n\r\nhello\r\n"u8.ToArray());
        bool mimeOk = parsed.Subject == "caf\u00e9" && parsed.From[0].Address == "a@example.com"
            && parsed.TextBody!.ReadText().StartsWith("hello", StringComparison.Ordinal);
        Console.WriteLine("mime: " + (mimeOk ? "ok" : "FAILED"));

        byte[] mbox = "From a@b.c Mon Jan  1 00:00:00 2024\nSubject: x\n\n>From quoted\n\nFrom d@e.f Tue Jan  2 00:00:00 2024\nSubject: y\n\nz\n"u8.ToArray();
        using MailContainer container = Mail.Open(new MemorySource(mbox));
        int count = 0;
        foreach (IMailItem item in container.Enumerate())
        {
            count += item.Subject is "x" or "y" ? 1 : 0;
        }

        bool storesOk = container.Format == MailFormat.Mbox && count == 2;
        Console.WriteLine("stores: " + (storesOk ? "ok" : "FAILED"));

        byte[] ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:1\r\nSUMMARY:Meet\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"u8.ToArray();
        ContentDocument doc = ContentDocument.Parse(ics);
        bool calendarOk = doc.ToArray().AsSpan().SequenceEqual(ics);
        Console.WriteLine("calendar: " + (calendarOk ? "ok" : "FAILED"));

        bool outlookOk = OutlookChecks.RunAll();
        Console.WriteLine("outlook: " + (outlookOk ? "ok" : "FAILED"));

        var jcal = RecursiveAI.OneMail.Calendar.Forms.JsonForms.ToJson(doc);
        var xcal = RecursiveAI.OneMail.Calendar.Forms.XmlForms.ToXCal(doc);
        bool jxformsOk = RecursiveAI.OneMail.Calendar.Forms.JsonForms.FromJson(jcal.Bytes).Document.ToArray().AsSpan().SequenceEqual(ics)
            && RecursiveAI.OneMail.Calendar.Forms.XmlForms.FromXml(xcal.Bytes).Document.ToArray().AsSpan().SequenceEqual(ics);
        Console.WriteLine("jxforms: " + (jxformsOk ? "ok" : "FAILED"));

        bool ok = mimeOk && storesOk && calendarOk && outlookOk && jxformsOk;
        Console.WriteLine("wasm smoke: " + (ok ? "ok" : "FAILED"));
        return ok ? 0 : 1;
    }
}
