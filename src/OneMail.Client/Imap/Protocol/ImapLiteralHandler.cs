namespace RecursiveAI.OneMail.Client.Imap.Protocol;

/// <summary>Where a literal sits in a response, given to a literal handler.</summary>
/// <param name="MessageNumber">The message sequence number of the FETCH response.</param>
/// <param name="ItemName">The FETCH data item the literal belongs to, as the server wrote it (<c>BODY[]</c>, <c>BODY[HEADER.FIELDS (FROM)]</c>, <c>RFC822</c>).</param>
/// <param name="Length">The literal's size in bytes.</param>
public readonly record struct ImapLiteralContext(long MessageNumber, string ItemName, long Length);

/// <summary>
/// Receives the body of a FETCH literal as a stream, before the rest of the response is read. Return true when the stream was
/// consumed as needed (what is left is discarded); return false without reading to have the literal buffered instead.
/// </summary>
/// <param name="context">Where the literal is.</param>
/// <param name="literal">The literal's bytes, straight from the connection. Valid only during the call.</param>
/// <param name="cancellationToken">Cancels the read.</param>
/// <returns>True if handled.</returns>
public delegate ValueTask<bool> ImapLiteralHandler(ImapLiteralContext context, Stream literal, CancellationToken cancellationToken);

/// <summary>Synchronous form of <see cref="ImapLiteralHandler"/>.</summary>
/// <param name="context">Where the literal is.</param>
/// <param name="literal">The literal's bytes, straight from the connection. Valid only during the call.</param>
/// <returns>True if handled.</returns>
public delegate bool ImapLiteralHandlerSync(ImapLiteralContext context, Stream literal);
