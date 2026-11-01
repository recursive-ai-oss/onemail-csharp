namespace RecursiveAI.OneMail.Mime;

public abstract partial class MimeEntity
{
    /// <summary>
    /// The leaf parts of this entity (and of the messages inside it) whose media type is <c>text/calendar</c> (or the unregistered <c>application/ics</c> some senders use for an attached .ics file): the iMIP invitation, reply or
    /// cancellation of RFC 6047, whether it is an attachment or an alternative of the body.
    /// </summary>
    /// <returns>The parts, in document order. Their content is untouched; <see cref="MimePart.OpenDecodedContent"/> gives the iCalendar bytes.</returns>
    public IEnumerable<MimePart> EnumerateCalendarParts()
    {
        switch (this)
        {
            case MimePart part:
                // Some senders label an attached .ics file application/ics, which is not registered but is common; it is read the same way (the content is iCalendar either way).
                if ((part.MediaTopLevelType == "text" && part.MediaSubType == "calendar") || (part.MediaTopLevelType == "application" && part.MediaSubType == "ics"))
                {
                    yield return part;
                }

                break;
            case MimeMultipart multipart:
                foreach (MimeEntity child in multipart.Children)
                {
                    foreach (MimePart found in child.EnumerateCalendarParts())
                    {
                        yield return found;
                    }
                }

                break;
            case MimeMessagePart { Message: { } inner }:
                foreach (MimePart found in inner.EnumerateCalendarParts())
                {
                    yield return found;
                }

                break;
        }
    }
}
