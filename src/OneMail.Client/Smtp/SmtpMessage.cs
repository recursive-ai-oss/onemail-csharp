using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Client.Smtp;

/// <summary>The delivery-status parameters of a message (RFC 3461).</summary>
public sealed class SmtpDsnOptions
{
    /// <summary>What a failure report returns: <c>FULL</c> or <c>HDRS</c>. Null: the server's choice.</summary>
    public string? Return { get; init; }

    /// <summary>An envelope id the report echoes. Null: none.</summary>
    public string? EnvelopeId { get; init; }

    /// <summary>When reports are wanted for each recipient: <c>SUCCESS</c>, <c>FAILURE</c>, <c>DELAY</c> or <c>NEVER</c>. Null: the server's default.</summary>
    public IReadOnlyList<string>? Notify { get; init; }
}

/// <summary>
/// A message to send with its envelope. Build it from a MIME tree, bytes or a stream. Unless given, the sender and recipients come from the
/// message's headers; the <c>Bcc</c> header is used for the recipients and never transmitted.
/// </summary>
public sealed class SmtpMessage
{
    internal MimeEntity? Entity { get; private init; }

    internal ReadOnlyMemory<byte> Bytes { get; private init; }

    internal Stream? Stream { get; private init; }

    internal long? StreamLength { get; private init; }

    private SmtpMessage() { }

    /// <summary>A message held as a MIME tree. It is written as it is (untouched parts byte for byte) and is not changed, except that a <c>Bcc</c> header is taken out while sending and put back.</summary>
    /// <param name="entity">The top-level entity.</param>
    /// <returns>The message.</returns>
    public static SmtpMessage FromEntity(MimeEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new SmtpMessage { Entity = entity };
    }

    /// <summary>A message held as bytes (a complete RFC 5322 message). The bytes are sent as they are, <c>Bcc</c> headers included: take them out first.</summary>
    /// <param name="bytes">The message.</param>
    /// <returns>The message.</returns>
    public static SmtpMessage FromBytes(ReadOnlyMemory<byte> bytes) => new() { Bytes = bytes };

    /// <summary>A message read from a stream, never held whole. The sender and recipients must be given.</summary>
    /// <param name="stream">The message; read once from its current position.</param>
    /// <param name="length">The number of bytes to read, when known (enables the SIZE check); null reads to the end. A seekable stream's remaining length is used when this is null.</param>
    /// <returns>The message.</returns>
    public static SmtpMessage FromStream(Stream stream, long? length = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SmtpMessage { Stream = stream, StreamLength = length ?? (stream.CanSeek ? stream.Length - stream.Position : null) };
    }

    /// <summary>The reverse-path. Null: the <c>Sender</c> header, else the first <c>From</c> address. Empty string: the null sender <c>&lt;&gt;</c> (for bounces).</summary>
    public string? Sender { get; set; }

    /// <summary>The forward-paths. Null: every address in <c>To</c>, <c>Cc</c> and <c>Bcc</c>, groups flattened, duplicates removed.</summary>
    public IReadOnlyList<string>? Recipients { get; set; }

    /// <summary>Delivery-status requests (needs the server's DSN extension), or null.</summary>
    public SmtpDsnOptions? Dsn { get; set; }

    /// <summary>Require that every hop uses TLS (REQUIRETLS, RFC 8689). Fails with <see cref="SmtpFeatureRequiredException"/> when the server does not offer it. Default false.</summary>
    public bool RequireTls { get; set; }

    /// <summary>When true, a rejected recipient stops the send before any data goes out (nothing is delivered). Default false: the message goes to the accepted recipients and the result lists the rejected ones.</summary>
    public bool RequireAllRecipients { get; set; }

    /// <summary>
    /// When the server lacks 8BITMIME and the message has 8-bit parts, re-encode them as quoted-printable or base64 (never when the message is signed).
    /// Default true. False refuses with <see cref="SmtpDowngradeRefusedException"/>.
    /// </summary>
    public bool AllowDowngrade { get; set; } = true;
}
