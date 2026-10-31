using RecursiveAI.OneMail.Composition;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Fuzz;

// Replies and forwards of hostile messages. Byte 0: bits 0-1 pick reply, reply-all, inline forward or attachment forward; bit 2 makes the parent an
// Outlook item (the MIME message imported by MS-OXCMAIL, so the MAPI path runs); bit 3 asks for a conversation index. The rest is the parent message.
// The composer may not throw, and what it writes must parse, hold only the headers it is meant to write, and respect its limits.
internal static class CompositionTarget
{
    private static readonly string[] Allowed =
    [
        "From", "To", "Cc", "Subject", "Date", "Message-ID", "In-Reply-To", "References", "MIME-Version", "Thread-Topic", "Thread-Index",
        "Content-Type", "Content-Transfer-Encoding", "Content-Disposition", "Content-ID",
    ];

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte mode = data[0];
        IMailItem parent = MimeMailItem.Parse(data[1..].ToArray());
        if ((mode & 4) != 0)
        {
            parent = new MapiMailItem(new MimeToMapiConverter().Convert(((MimeMailItem)parent).Entity).Value, default);
        }

        var options = new CompositionOptions
        {
            TimeProvider = new Clock(),
            RandomBytes = b => b.Fill(0x33),
            MessageIdDomain = "fuzz.example",
            ForwardStyle = (mode & 3) == 3 ? ForwardStyle.Attachment : ForwardStyle.Inline,
            ConversationIndex = (mode & 8) != 0 ? ConversationIndexMode.Always : ConversationIndexMode.Auto,
            MaxRecipients = 20,
            MaxReferences = 8,
            MaxQuoteCharacters = 4096,
            MaxCarriedBytes = 1 << 20,
            Text = "x",
        };
        ComposedMessage c = (mode & 3) switch
        {
            0 => MessageComposer.Reply(parent, options),
            1 => MessageComposer.ReplyAll(parent, options),
            _ => MessageComposer.Forward(parent, options),
        };

        byte[] bytes = c.Message.ToArray();
        MimeEntity back = MimeEntity.Parse(bytes);
        if (c.To.Count + c.Cc.Count > 20)
        {
            throw new InvalidOperationException("recipient limit exceeded");
        }

        foreach (MimeHeader h in back.Headers)
        {
            if (!Allowed.Contains(h.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("unexpected header " + h.Name);
            }
        }

        if (back.GetReferences().Count > 8)
        {
            throw new InvalidOperationException("References limit exceeded");
        }

        string rest = c.Subject.StartsWith("Re: ", StringComparison.Ordinal) ? c.Subject[4..] : c.Subject.StartsWith("Fwd: ", StringComparison.Ordinal) ? c.Subject[5..] : throw new InvalidOperationException("no prefix");
        if (SubjectPrefixes.Strip(rest) != rest)
        {
            throw new InvalidOperationException("stacked prefix");
        }

        if (c.Mapi is not null)
        {
            _ = new MapiToMimeConverter().Convert(c.Mapi).Value.ToArray();
        }
    }
}
