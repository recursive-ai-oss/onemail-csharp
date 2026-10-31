using MimeKit;
using RecursiveAI.OneMail.Mime;
using OneEntity = RecursiveAI.OneMail.Mime.MimeEntity;
using OnePart = RecursiveAI.OneMail.Mime.MimePart;
using RecursiveAI.OneMail.Mime.Transfer;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>
/// The work of each row for each side, written out once so the benchmark classes and the peak-memory runner do exactly the same.
/// Both sides read from memory, never touch the file system, and return a number derived from the result so nothing is elided.
/// </summary>
internal static class Workloads
{
    // ---- row 1: parse ----

    // OneMail: OneEntity.Parse over the byte array (the tree keeps ranges into it; bodies are not copied).
    public static int ParseOneMail(byte[] data) => CountEntities(OneEntity.Parse(data));

    // MimeKit: MimeMessage.Load over a MemoryStream of the same bytes (its default, non-persistent parse; bodies are copied into content streams).
    public static int ParseMimeKit(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        MimeMessage m = MimeMessage.Load(ms);
        return m.Body is null ? 0 : 1;
    }

    // ---- row 2: parse + decoded headers ----

    // OneMail: Subject (RFC 2047 decoded), From and To (parsed address lists, display names decoded), Date, Message-ID.
    public static int HeadersOneMail(byte[] data)
    {
        OneEntity e = OneEntity.Parse(data);
        int n = e.Headers.GetFirst("Subject")?.Value.Length ?? 0;
        foreach (MimeAddress a in e.GetAddresses("From"))
        {
            n += a is MimeMailbox mb ? mb.Address.Length + (mb.Name?.Length ?? 0) : 1;
        }

        foreach (MimeAddress a in e.GetAddresses("To"))
        {
            n += a is MimeMailbox mb ? mb.Address.Length + (mb.Name?.Length ?? 0) : 1;
        }

        n += e.GetDate()?.GetHashCode() ?? 0;
        return n + (e.GetMessageId()?.Length ?? 0);
    }

    // MimeKit: the same five properties of MimeMessage.
    public static int HeadersMimeKit(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        MimeMessage m = MimeMessage.Load(ms);
        int n = m.Subject?.Length ?? 0;
        foreach (MailboxAddress a in m.From.Mailboxes)
        {
            n += a.Address.Length + (a.Name?.Length ?? 0);
        }

        foreach (MailboxAddress a in m.To.Mailboxes)
        {
            n += a.Address.Length + (a.Name?.Length ?? 0);
        }

        n += m.Date.GetHashCode();
        return n + (m.MessageId?.Length ?? 0);
    }

    // ---- row 3: parse + decode every body part ----

    // OneMail: every leaf part's decoded content as a byte[] (transfer encoding undone; no charset decoding).
    public static long BodiesOneMail(byte[] data)
    {
        long n = 0;
        Walk(OneEntity.Parse(data), ref n);
        return n;
    }

    private static void Walk(OneEntity e, ref long n)
    {
        switch (e)
        {
            case OnePart p:
                n += p.GetDecodedContent().Length;
                break;
            case MimeMultipart mp:
                foreach (OneEntity c in mp.Children)
                {
                    Walk(c, ref n);
                }

                break;
            case MimeMessagePart mm when mm.Message is not null:
                Walk(mm.Message, ref n);
                break;
        }
    }

    // MimeKit: every MimePart in BodyParts decoded into a fresh MemoryStream (transfer encoding undone; no charset decoding).
    public static long BodiesMimeKit(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        MimeMessage m = MimeMessage.Load(ms);
        long n = 0;
        foreach (MimeKit.MimeEntity e in m.BodyParts)
        {
            if (e is global::MimeKit.MimePart p && p.Content is not null)
            {
                using var o = new MemoryStream();
                p.Content.DecodeTo(o);
                n += o.Length;
            }
        }

        return n;
    }

    // ---- row 4: write ----

    // Unmodified: serialise an already parsed message to a stream that discards. OneMail copies the untouched source ranges;
    // MimeKit re-serialises its model (it does not promise the same bytes). Parsing is outside the timed region.
    public static OneEntity PrepareOneMail(byte[] data) => OneEntity.Parse(data);

    public static MimeMessage PrepareMimeKit(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        return MimeMessage.Load(ms);
    }

    public static long WriteOneMail(OneEntity e)
    {
        e.WriteTo(Stream.Null);
        return 0;
    }

    public static long WriteMimeKit(MimeMessage m)
    {
        m.WriteTo(Stream.Null);
        return 0;
    }

    // Built: headers, a text part and a 1 MB binary attachment, built and written in one go. Boundaries are fixed on our side;
    // MimeKit generates its own.
    public static long BuildWriteOneMail(byte[] attachment)
    {
        var root = new MimeMultipart("mixed", "bench-boundary");
        root.Headers.Add("From", "Alice Example <alice@example.org>");
        root.Headers.Add("To", "Bob <bob@example.net>");
        root.Headers.Add("Subject", "Quarterly figures");
        root.Headers.Add("Date", "Fri, 30 Oct 2026 10:00:00 +0000");
        root.Headers.Add("Message-ID", "<gen-2@example.org>");
        var text = new OnePart("text/plain; charset=utf-8");
        text.SetDecodedContent("See the attachment.\r\n"u8, RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.QuotedPrintable);
        root.Add(text);
        var att = new OnePart("application/octet-stream");
        att.SetDecodedContent(attachment, RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.Base64);
        root.Add(att);
        root.WriteTo(Stream.Null);
        return 0;
    }

    public static long BuildWriteMimeKit(byte[] attachment)
    {
        var body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "See the attachment.\r\n" },
            new global::MimeKit.MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(attachment, writable: false)),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };
        var m = new MimeMessage { Subject = "Quarterly figures", Body = body, Date = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero), MessageId = "gen-2@example.org" };
        m.From.Add(new MailboxAddress("Alice Example", "alice@example.org"));
        m.To.Add(new MailboxAddress("Bob", "bob@example.net"));
        m.WriteTo(Stream.Null);
        return 0;
    }

    // ---- row 5: mbox ----

    // Both sides share one minimal splitter: a line starting with "From " after a blank line, or at the start, opens a message
    // (the rule used for the differential corpus). Each message's range is then parsed whole: OneMail with MimeEntity.Parse over
    // the range, MimeKit with MimeMessage.Load over a MemoryStream of the range. The row therefore measures parse per message
    // over a split, not a mailbox reader. (MimeKit's own mbox-mode parser is not used: on this file it splits at every
    // "From " line, including ones in message bodies, and throws on the fragments.)
    private static List<(int Start, int Length)> Split(byte[] mbox)
    {
        var ranges = new List<(int, int)>(3000);
        int start = -1;
        int pos = 0;
        bool prevBlank = true;
        while (pos < mbox.Length)
        {
            int eol = mbox.AsSpan(pos).IndexOf((byte)'\n');
            int next = eol < 0 ? mbox.Length : pos + eol + 1;
            int lineLen = (eol < 0 ? mbox.Length - pos : eol);
            bool blank = lineLen == 0 || (lineLen == 1 && mbox[pos] == (byte)'\r');
            if (prevBlank && lineLen >= 5 && mbox.AsSpan(pos, 5).SequenceEqual("From "u8))
            {
                if (start >= 0)
                {
                    ranges.Add((start, pos - start));
                }

                start = next;
            }

            prevBlank = blank;
            pos = next;
        }

        if (start >= 0)
        {
            ranges.Add((start, mbox.Length - start));
        }

        return ranges;
    }

    public static int MboxOneMail(byte[] mbox)
    {
        int count = 0;
        foreach ((int start, int length) in Split(mbox))
        {
            count += OneEntity.Parse(new ReadOnlyMemory<byte>(mbox, start, length)).Headers.Count;
        }

        return count;
    }

    public static int MboxMimeKit(byte[] mbox)
    {
        int count = 0;
        foreach ((int start, int length) in Split(mbox))
        {
            using var ms = new MemoryStream(mbox, start, length, writable: false);
            count += MimeMessage.Load(ms).Headers.Count;
        }

        return count;
    }

    private static int CountEntities(OneEntity e)
    {
        int n = 1;
        if (e is MimeMultipart mp)
        {
            foreach (OneEntity c in mp.Children)
            {
                n += CountEntities(c);
            }
        }
        else if (e is MimeMessagePart mm && mm.Message is not null)
        {
            n += CountEntities(mm.Message);
        }

        return n;
    }
}
