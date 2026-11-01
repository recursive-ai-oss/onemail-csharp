using RecursiveAI.OneMail.Client.Smtp;
using RecursiveAI.OneMail.Composition;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail;

/// <summary>
/// <c>Mail.SendAsync</c>: connect to a submission server, send, say QUIT. Each overload is one call over <see cref="SmtpClient"/>; to send
/// several messages over one connection, to keep the session or to use the other layers, use <see cref="SmtpClient"/> directly.
/// </summary>
public static class MailSending
{
    extension(Mail)
    {
        /// <summary>Sends a message built with <see cref="OutgoingMessage"/>.</summary>
        /// <param name="message">The message.</param>
        /// <param name="server">The server, security and credentials.</param>
        /// <param name="cancellationToken">Cancels connecting and sending.</param>
        /// <returns>What the server said, per recipient and for the data. Call <see cref="SmtpSendResult.ThrowIfNotDelivered"/> to turn a refusal into an exception.</returns>
        public static Task<SmtpSendResult> SendAsync(OutgoingMessage message, SmtpClientOptions server, CancellationToken cancellationToken = default) =>
            SendAsync(SmtpMessage.FromEntity((message ?? throw new ArgumentNullException(nameof(message))).ToEntity()), server, cancellationToken);

        /// <summary>Sends a MIME tree. The envelope comes from its headers.</summary>
        /// <param name="message">The message.</param>
        /// <param name="server">The server, security and credentials.</param>
        /// <param name="cancellationToken">Cancels connecting and sending.</param>
        /// <returns>The result.</returns>
        public static Task<SmtpSendResult> SendAsync(MimeEntity message, SmtpClientOptions server, CancellationToken cancellationToken = default) =>
            SendAsync(SmtpMessage.FromEntity(message), server, cancellationToken);

        /// <summary>Sends a message with an explicit envelope or content source (<see cref="SmtpMessage"/>).</summary>
        /// <param name="message">The message.</param>
        /// <param name="server">The server, security and credentials.</param>
        /// <param name="cancellationToken">Cancels connecting and sending.</param>
        /// <returns>The result.</returns>
        public static async Task<SmtpSendResult> SendAsync(SmtpMessage message, SmtpClientOptions server, CancellationToken cancellationToken = default)
        {
            await using SmtpClient client = await SmtpClient.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
            SmtpSendResult result = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.QuitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
