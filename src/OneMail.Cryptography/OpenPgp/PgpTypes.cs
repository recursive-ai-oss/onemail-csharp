using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneCrypto.Policy;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.OpenPgp;

/// <summary>The kind of OpenPGP protection one layer of a message carries.</summary>
public enum PgpLayerKind
{
    /// <summary><c>multipart/signed</c> with an <c>application/pgp-signature</c> part (RFC 3156 §5).</summary>
    SignedDetached,

    /// <summary><c>multipart/encrypted</c> with an <c>application/pgp-encrypted</c> control part (RFC 3156 §4). The decrypted message may carry signatures of its own (the combined method, RFC 3156 §6.2).</summary>
    Encrypted,

    /// <summary>A text body holding a cleartext-signed message (RFC 9580 §7).</summary>
    InlineCleartextSigned,

    /// <summary>A text body holding an armored OpenPGP message: encrypted, signed, or both (RFC 9580 §6).</summary>
    InlineMessage,
}

/// <summary>How one layer ended.</summary>
public enum PgpStatus
{
    /// <summary>A signed layer: every signature on it is valid for a key that was usable when it was made, and the policy accepts it.</summary>
    Valid,

    /// <summary>An encrypted layer was decrypted. Any signatures inside it are reported in <see cref="PgpLayer.Signers"/>; decryption alone is not verification.</summary>
    Decrypted,

    /// <summary>A signature does not match the signed bytes (or a signature on a decrypted layer does not match).</summary>
    SignatureInvalid,

    /// <summary>No supplied certificate carries the signing key, so nothing could be checked.</summary>
    SignerUnknown,

    /// <summary>The signing key was unbound, expired, revoked or unable to sign when the signature was made, or the signature itself had expired.</summary>
    KeyNotValid,

    /// <summary>The crypto policy rejects the hash or the key size of a signature.</summary>
    PolicyRejected,

    /// <summary>The signature uses an algorithm this build or platform cannot verify.</summary>
    Unsupported,

    /// <summary>The <c>micalg</c> parameter names a different hash than the signature uses (RFC 3156 §5). The signature itself is reported in <see cref="PgpLayer.Signers"/>.</summary>
    MicalgMismatch,

    /// <summary>The MIME structure breaks RFC 3156 (not exactly two parts, a missing or wrong control part or version).</summary>
    StructureInvalid,

    /// <summary>The OpenPGP data is malformed, or armor could not be found or decoded.</summary>
    Malformed,

    /// <summary>Decryption failed: no key matched, the key is locked, or the ciphertext did not authenticate. One status for every reason, as RFC 9580 §13 asks.</summary>
    DecryptionFailed,

    /// <summary>The message is encrypted and no key or password was supplied that applies.</summary>
    NoDecryptionKey,

    /// <summary>The message is encrypted without integrity protection and <see cref="PgpOpenOptions.AllowUnauthenticatedEncryption"/> is off.</summary>
    UnauthenticatedEncryption,

    /// <summary>A limit (output size, nesting, key-derivation cost) was exceeded.</summary>
    LimitExceeded,

    /// <summary>The signature layer carried no signature at all.</summary>
    NoSignature,
}

/// <summary>A repair, tolerance or warning raised while reading. Each is a typed defect; none of them hides a failure.</summary>
public enum PgpIssue
{
    /// <summary>The <c>protocol</c> parameter is missing or is not the one RFC 3156 names for this multipart.</summary>
    ProtocolParameterUnexpected,

    /// <summary>A <c>multipart/signed</c> without a <c>micalg</c> parameter (RFC 3156 §5 requires one). Tolerated.</summary>
    MicalgMissing,

    /// <summary>The signature part is not <c>application/pgp-signature</c>.</summary>
    SignaturePartTypeUnexpected,

    /// <summary>The signed part verified only after its line ends were made CRLF (a store or gateway changed them). RFC 3156 §5 signs the CRLF form.</summary>
    SignedPartLineEndingsNormalised,

    /// <summary>The signed part verified only after trailing white space was removed from its lines (RFC 3156 §3 asks senders to strip it).</summary>
    SignedPartTrailingWhitespaceStripped,

    /// <summary>A signature, key or cipher the policy flags (SHA-1, MD5, short RSA, CFB without AEAD …). Reading accepts these; the names are in the signer's <see cref="PgpSignerResult.WeakAlgorithms"/>.</summary>
    WeakAlgorithms,

    /// <summary>The nesting limit was reached before the innermost content.</summary>
    DepthLimitReached,

    /// <summary>The protected entity sits inside an unprotected wrapper (for example a <c>multipart/mixed</c> added by a list); the wrapper's other parts are not covered. See <see cref="PgpOutside.UnprotectedParts"/>.</summary>
    ProtectedPartInUnprotectedWrapper,

    /// <summary>The control part of a <c>multipart/encrypted</c> is missing, is not <c>application/pgp-encrypted</c>, or does not say <c>Version: 1</c>. Tolerated when the second part decodes.</summary>
    ControlPartUnexpected,

    /// <summary>The armored block was surrounded by other text (a list footer, a quoted reply); only the block was read.</summary>
    InlineTextOutsideArmor,

    /// <summary>Every armor line carried the same quote prefix (<c>&gt; </c>); it was removed.</summary>
    InlineQuotePrefixRemoved,

    /// <summary>The text still held quoted-printable escapes that the content-transfer-encoding did not account for; they were decoded to read the block.</summary>
    InlineQuotedPrintableRepaired,

    /// <summary>The cleartext signature verified only after the text was converted from the part's declared charset to UTF-8.</summary>
    InlineCharsetReencoded,

    /// <summary>An encrypted layer carried no signature: the sender is not authenticated (RFC 9580 §13).</summary>
    EncryptedWithoutSignature,

    /// <summary>The data was encrypted without an integrity check and was opened only because the caller allowed it.</summary>
    UnauthenticatedEncryption,

    /// <summary>The signature covers the entity but not the message headers; <c>Subject</c>, <c>From</c> and <c>To</c> are unprotected unless <see cref="PgpOutside.ProtectedHeaders"/> lists them.</summary>
    HeadersNotProtected,

    /// <summary>The decrypted entity carries <c>protected-headers</c> (draft-autocrypt-lamps-protected-headers); its headers are authenticated only when the layer was signed or the encryption is authenticated.</summary>
    ProtectedHeadersPresent,
}

/// <summary>Everything about one signature on a layer: who, with which key, when, and why it was or was not accepted.</summary>
public sealed class PgpSignerResult
{
    internal PgpSignerResult(PgpSignatureResult report)
    {
        Report = report;
    }

    /// <summary>OneCrypto's report for the signature, including the signature packet.</summary>
    public PgpSignatureResult Report { get; }

    /// <summary>The verdict for this signature.</summary>
    public PgpSignatureStatus Status => Report.Status;

    /// <summary>True when the signature is valid, made with a key that was valid at <see cref="CreatedAt"/>, under a policy that accepts it.</summary>
    public bool IsValid => Report.IsValid;

    /// <summary>The certificate that carries the signing key; null when the key is unknown.</summary>
    public PgpCertificate? Certificate => Report.Signer?.Certificate;

    /// <summary>The fingerprint of the signing key (primary or subkey), or empty when the key is unknown.</summary>
    public byte[] KeyFingerprint => Report.Signer?.Fingerprint ?? [];

    /// <summary>The fingerprint of the certificate's primary key, or empty when the key is unknown.</summary>
    public byte[] CertificateFingerprint => Certificate?.Fingerprint ?? [];

    /// <summary>The user IDs of the signer's certificate (text of the User ID packets). The signature does not choose one; compare with <see cref="UserIdMatchesSender"/>.</summary>
    public IReadOnlyList<string> UserIds { get; internal set; } = [];

    /// <summary>The first user ID that names the message's sender address, or the certificate's first user ID when none does; null when the key is unknown.</summary>
    public string? UserId { get; internal set; }

    /// <summary>True when a user ID of the certificate names the address in the message's <c>From</c>. False when it does not or there is no <c>From</c> to compare. Only meaningful for a signature that is valid.</summary>
    public bool UserIdMatchesSender { get; internal set; }

    /// <summary>The signature creation time, or null when the signature carries none.</summary>
    public DateTimeOffset? CreatedAt => Report.CreatedAt;

    /// <summary>Whether the signing key was bound, unexpired, unrevoked and able to sign at <see cref="CreatedAt"/>. Null when the key is unknown or the signature has no creation time.</summary>
    public bool? KeyValidAtSigning { get; internal set; }

    /// <summary>The hash algorithm of the signature.</summary>
    public string HashAlgorithm { get; internal set; } = string.Empty;

    /// <summary>The policy's answer about the hash at the signature date, or null when it was not asked.</summary>
    public PolicyDecision? Policy => Report.Policy;

    /// <summary>Algorithms the policy flags as weak but still accepts for reading (for example SHA-1 before its reject date).</summary>
    public IReadOnlyList<string> WeakAlgorithms { get; internal set; } = [];

    /// <summary>Reasons, each naming the rule it comes from; empty for a clean valid signature.</summary>
    public IReadOnlyList<string> Reasons => Report.Reasons;
}

/// <summary>What a protected entity leaves unprotected.</summary>
public sealed class PgpOutside
{
    internal PgpOutside(IReadOnlyList<string> unprotectedHeaders, IReadOnlyList<string> unprotectedParts, IReadOnlyList<string> protectedHeaders)
    {
        UnprotectedHeaders = unprotectedHeaders;
        UnprotectedParts = unprotectedParts;
        ProtectedHeaders = protectedHeaders;
    }

    /// <summary>Names of the outermost message headers (<c>Subject</c>, <c>From</c> …). RFC 3156 protects only content headers; these are not covered unless repeated in <see cref="ProtectedHeaders"/>.</summary>
    public IReadOnlyList<string> UnprotectedHeaders { get; }

    /// <summary>Paths (<c>1</c>, <c>2.1</c> …) of parts that sit beside the protected entity inside a wrapper.</summary>
    public IReadOnlyList<string> UnprotectedParts { get; }

    /// <summary>Names of the headers the protected content repeats inside the protection (a <c>protected-headers</c> entity or a signed <c>message/rfc822</c>).</summary>
    public IReadOnlyList<string> ProtectedHeaders { get; }
}

/// <summary>One layer of OpenPGP protection, outermost first.</summary>
public sealed class PgpLayer
{
    internal PgpLayer(PgpLayerKind kind)
    {
        Kind = kind;
    }

    /// <summary>What the layer is.</summary>
    public PgpLayerKind Kind { get; }

    /// <summary>How it ended.</summary>
    public PgpStatus Status { get; internal set; }

    /// <summary>True when the layer did what it promises: a signed layer is <see cref="PgpStatus.Valid"/>; an encrypted layer is decrypted and every signature inside it that exists is valid.</summary>
    public bool Succeeded { get; internal set; }

    /// <summary>The typed defects raised for this layer.</summary>
    public IReadOnlyList<PgpIssue> Issues { get; internal set; } = [];

    /// <summary>A sentence saying why a layer failed; null on success.</summary>
    public string? Detail { get; internal set; }

    /// <summary>The entity the layer was read from.</summary>
    public MimeEntity Entity { get; internal set; } = null!;

    /// <summary>Every signature found on the layer, in the order checked.</summary>
    public IReadOnlyList<PgpSignerResult> Signers { get; internal set; } = [];

    /// <summary>For a detached layer, the bytes the signature was checked against (exactly the first part as received, or its CRLF form when <see cref="PgpIssue.SignedPartLineEndingsNormalised"/>). Empty for other layers.</summary>
    public ReadOnlyMemory<byte> CoveredBytes { get; internal set; }

    /// <summary>For a detached layer read from a source, the offset of the signed part in that source; -1 when it was modified or built in memory.</summary>
    public long CoveredSourceOffset { get; internal set; } = -1;

    /// <summary>The length in bytes of the signed part at <see cref="CoveredSourceOffset"/>.</summary>
    public long CoveredSourceLength { get; internal set; }

    /// <summary>The <c>micalg</c> parameter as written (detached layers).</summary>
    public string? Micalg { get; internal set; }

    /// <summary>For an encrypted layer, what protected the plaintext against change; <see cref="PgpProtection.NotEncrypted"/> for signed layers.</summary>
    public PgpProtection Protection { get; internal set; }

    /// <summary>The entity that was signed (detached) or decrypted; null when the layer failed before it.</summary>
    public MimeEntity? Content { get; internal set; }
}

/// <summary>The outcome of <see cref="PgpMimeProcessor.Open(MimeEntity, PgpOpenOptions)"/>.</summary>
public sealed class PgpResult
{
    internal PgpResult(MimeEntity root)
    {
        Root = root;
        Content = root;
    }

    /// <summary>The entity that was examined.</summary>
    public MimeEntity Root { get; }

    /// <summary>The layers, outermost first. Empty when the message uses no OpenPGP.</summary>
    public IReadOnlyList<PgpLayer> Layers { get; internal set; } = [];

    /// <summary>The innermost entity reached (the signed or decrypted content); the root when nothing could be unwrapped.</summary>
    public MimeEntity Content { get; internal set; }

    /// <summary>The innermost content as a message in the shared view; null when nothing was unwrapped.</summary>
    public IMailItem? InnerItem { get; internal set; }

    /// <summary>What lies outside the protection; null when no layer was signed or encrypted.</summary>
    public PgpOutside? Outside { get; internal set; }

    /// <summary>Result-level defects (those that belong to no single layer).</summary>
    public IReadOnlyList<PgpIssue> Issues { get; internal set; } = [];

    /// <summary>True when OpenPGP protection was found.</summary>
    public bool IsPgp => Layers.Count > 0;

    /// <summary>True when there is at least one layer, every layer succeeded and the protected entity was not found inside an unprotected wrapper.</summary>
    public bool Succeeded => Layers.Count > 0 && Layers.All(static l => l.Succeeded) && !Issues.Contains(PgpIssue.ProtectedPartInUnprotectedWrapper);

    /// <summary>True when any layer was encrypted (decrypted or not).</summary>
    public bool WasEncrypted => Layers.Any(static l => l.Kind == PgpLayerKind.Encrypted || l.Protection != PgpProtection.NotEncrypted);

    /// <summary>All signatures of all layers.</summary>
    public IEnumerable<PgpSignerResult> Signers => Layers.SelectMany(static l => l.Signers);

    /// <summary>True when the message was authenticated: at least one signature is valid and every layer succeeded. An encrypted message with no signature is never authenticated.</summary>
    public bool IsAuthenticated => Succeeded && Signers.Any(static s => s.IsValid);

    /// <summary>True when the headers listed in <see cref="PgpOutside.ProtectedHeaders"/> were authenticated: the result is authenticated and the protected content repeats headers.</summary>
    public bool HeadersProtected => IsAuthenticated && Outside is { ProtectedHeaders.Count: > 0 };
}
