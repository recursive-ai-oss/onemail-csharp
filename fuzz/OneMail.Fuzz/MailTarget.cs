using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Fuzz;

// Fuzz target for the sniffer and Mail.Open: random bytes go through format detection and, when a reader exists, through
// every accessor of the shared view. Only UnsupportedMailFormatException (the typed answer for a format that is unknown or has no
// reader yet) may escape. An mbox that sniffs as one is enumerated to the end, so the store readers are fuzzed through the
// view as well; attachments are measured, which decodes them.
internal static class MailTarget
{
    private const int MaxItems = 200;

    internal static void Run(ReadOnlySpan<byte> data)
    {
        byte[] file = data.ToArray();
        using MemorySource src = new(file);
        MailFormat sniffed = MailFormatSniffer.Sniff(src);
        try
        {
            using MailContainer c = Mail.Open(src);
            if (c.Format != sniffed)
            {
                throw new InvalidOperationException("Open and Sniff disagree");
            }

            int n = 0;
            foreach (IMailItem item in c.Enumerate())
            {
                Touch(item, 0);
                if (++n >= MaxItems)
                {
                    break;
                }
            }
        }
        catch (UnsupportedMailFormatException e)
        {
            if (e.Format != sniffed)
            {
                throw new InvalidOperationException("the exception names another format than Sniff");
            }
        }

        Touch(Mail.Parse(file), 0);
    }

    private static void Touch(IMailItem item, int depth)
    {
        _ = item.Kind;
        _ = item.Subject;
        _ = item.Sender;
        _ = item.From;
        _ = item.ToRecipients;
        _ = item.Cc;
        _ = item.Bcc;
        _ = item.ReplyTo;
        _ = item.Sent;
        foreach (MailDateSource s in Enum.GetValues<MailDateSource>())
        {
            _ = item.TryGetDate(s, out _);
        }

        _ = item.MessageId;
        _ = item.InReplyTo;
        _ = item.References;
        _ = item.ConversationIndex;
        _ = item.ConversationTopic;
        _ = item.TextBody?.ReadText();
        _ = item.HtmlBody?.ReadText();
        _ = item.RtfBody;
        _ = item.Provenance;
        int d = 0;
        foreach (MailDefect x in item.Defects)
        {
            if (x.Code.Length == 0 || ++d > 100000)
            {
                break;
            }
        }

        int a = 0;
        foreach (IMailAttachment att in item.Attachments)
        {
            _ = att.FileName;
            _ = att.ContentType;
            _ = att.ContentId;
            _ = att.IsInline;
            _ = att.Size;
            if (depth < 3 && att.Embedded is { } inner)
            {
                Touch(inner, depth + 1);
            }

            if (++a >= 50)
            {
                break;
            }
        }
    }
}
