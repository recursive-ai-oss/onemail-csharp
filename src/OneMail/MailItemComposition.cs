using RecursiveAI.OneMail.Composition;

namespace RecursiveAI.OneMail;

/// <summary>Replies and forwards as one call on any item. Each is a call to <see cref="MessageComposer"/>; the settings are <see cref="CompositionOptions"/>.</summary>
public static class MailItemComposition
{
    /// <summary>Creates a reply to the sender of the item.</summary>
    /// <param name="item">The item to reply to.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The reply.</returns>
    public static ComposedMessage CreateReply(this IMailItem item, CompositionOptions? options = null) => MessageComposer.Reply(item, options);

    /// <summary>Creates a reply to the sender and the other recipients of the item.</summary>
    /// <param name="item">The item to reply to.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The reply.</returns>
    public static ComposedMessage CreateReplyAll(this IMailItem item, CompositionOptions? options = null) => MessageComposer.ReplyAll(item, options);

    /// <summary>Creates a forward of the item (inline or as an attachment, per the options).</summary>
    /// <param name="item">The item to forward.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The forward.</returns>
    public static ComposedMessage CreateForward(this IMailItem item, CompositionOptions? options = null) => MessageComposer.Forward(item, options);
}
