using System.Text;

namespace RecursiveAI.OneMail.Client.Sasl;

/// <summary>
/// One SASL mechanism (RFC 4422) as the client side of a challenge/response exchange. The protocol layers carry the bytes
/// (base64 on the wire for IMAP, SMTP and POP3); the mechanism only turns challenges into responses.
/// </summary>
/// <remarks>
/// A mechanism object holds the exchange state, so use a new one (or <see cref="Reset"/>) for each authentication attempt.
/// </remarks>
public abstract class SaslMechanism
{
    /// <summary>The registered mechanism name, upper case (<c>PLAIN</c>).</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Whether the mechanism sends something equivalent to the password in the clear. When true the raw connections refuse to run it
    /// over a connection that is not encrypted unless <see cref="Protocol.ProtocolConnectionOptions.AllowInsecureAuthentication"/> is set.
    /// Default false.
    /// </summary>
    public virtual bool RequiresEncryption => false;

    /// <summary>
    /// Whether the mechanism is kept only for old servers: its exchange is weak by today's standards (a replayable or offline-guessable
    /// proof). Selection prefers every other mechanism, and the mechanism is never chosen over TLS-protected PLAIN-style alternatives
    /// unless nothing else is offered. Default false.
    /// </summary>
    public virtual bool IsLegacy => false;

    /// <summary>
    /// Whether the client speaks first. When true the response from <see cref="GetInitialResponse"/> can ride on the command
    /// (IMAP SASL-IR, RFC 4959; SMTP and POP3 initial response). Default true.
    /// </summary>
    public virtual bool ClientFirst => true;

    /// <summary>Produces the first message of a client-first mechanism. Called once per exchange.</summary>
    /// <returns>The initial response; empty array is a valid empty response. Null for server-first mechanisms.</returns>
    public abstract byte[]? GetInitialResponse();

    /// <summary>Answers one server challenge.</summary>
    /// <param name="challenge">The decoded challenge (empty when the server sent none).</param>
    /// <returns>The response to send.</returns>
    public abstract byte[] Respond(ReadOnlySpan<byte> challenge);

    /// <summary>Forgets the exchange state so the object can authenticate again.</summary>
    public virtual void Reset() { }
}

/// <summary>PLAIN (RFC 4616): the authorization id, the authentication id and the password in one message. Needs an encrypted connection.</summary>
public sealed class SaslPlain : SaslMechanism
{
    private readonly byte[] _message;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="authenticationId">The user name (authcid).</param>
    /// <param name="password">The password.</param>
    /// <param name="authorizationId">The identity to act as (authzid), or null for the same as <paramref name="authenticationId"/>.</param>
    /// <exception cref="ArgumentException">A value contains a NUL, or the user name is empty.</exception>
    public SaslPlain(string authenticationId, string password, string? authorizationId = null)
    {
        ArgumentNullException.ThrowIfNull(authenticationId);
        ArgumentNullException.ThrowIfNull(password);
        if (authenticationId.Length == 0 || authenticationId.Contains('\0', StringComparison.Ordinal) || password.Contains('\0', StringComparison.Ordinal)
            || (authorizationId?.Contains('\0', StringComparison.Ordinal) ?? false))
        {
            throw new ArgumentException("PLAIN values must be non-empty (user name) and free of NUL.");
        }

        // RFC 4616 §2: message = [authzid] NUL authcid NUL passwd
        _message = Encoding.UTF8.GetBytes((authorizationId ?? "") + "\0" + authenticationId + "\0" + password);
    }

    /// <inheritdoc />
    public override string Name => "PLAIN";

    /// <inheritdoc />
    public override bool RequiresEncryption => true;

    /// <inheritdoc />
    public override byte[]? GetInitialResponse() => _message;

    /// <inheritdoc />
    public override byte[] Respond(ReadOnlySpan<byte> challenge) => _message;
}
