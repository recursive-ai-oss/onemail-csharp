namespace RecursiveAI.OneMail.Mime;

public abstract partial class MimeEntity
{
    /// <summary>
    /// The leaf parts of this entity (and of the messages inside it) whose media type is <c>text/calendar</c>: the iMIP invitation, reply or
    /// cancellation of RFC 6047, whether it is an attachment or an alternative of the body.
    /// </summary>
    /// <returns>The parts, in document order. Their content is untouched; <see cref="MimePart.OpenDecodedContent"/> gives the iCalendar bytes.</returns>
    public IEnumerable<MimePart> EnumerateCalendarParts()
    {
        switch (this)
        {
            case MimePart part:
                if (part.MediaTopLevelType == "text" && part.MediaSubType == "calendar")
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
