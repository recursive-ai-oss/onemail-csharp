using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Policy;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Cryptography.Smime;

/// <summary>What one S/MIME wrapper around a message is.</summary>
public enum SmimeLayerKind
{
    /// <summary><c>multipart/signed</c> (RFC 8551 §3.5): the signed entity travels in the clear beside a detached CMS signature.</summary>
    SignedDetached,

    /// <summary><c>application/pkcs7-mime; smime-type=signed-data</c> (RFC 8551 §3.4.3.2): the content is inside the CMS.</summary>
    SignedOpaque,

    /// <summary><c>smime-type=enveloped-data</c> (RFC 8551 §3.3).</summary>
    Enveloped,

    /// <summary><c>smime-type=authEnveloped-data</c> (RFC 8551 §3.3, AES-GCM).</summary>
    AuthEnveloped,

    /// <summary><c>smime-type=compressed-data</c> (RFC 3274).</summary>
    Compressed,

    /// <summary><c>smime-type=certs-only</c>: a degenerate SignedData that carries certificates and no content (RFC 8551 §3.7).</summary>
    CertsOnly,
}

/// <summary>
/// How a layer or a signer ended. Not every value applies to every kind of layer; each member says where it is used.
/// A signer that is <see cref="Valid"/> is cryptographically sound for the certificate that came with it; whether that
/// certificate is trusted is a separate question, answered by <see cref="SmimeSignerResult.Chain"/> only when the caller asked.
/// </summary>
public enum SmimeStatus
{
    /// <summary>Signer: every signature check passed (and the chain check, when it was asked for). Signed layer: the same, and every signer's chain was built to a caller-supplied anchor (or <see cref="SmimeOpenOptions.AllowUntrustedSigner"/> is set).</summary>
    Valid,

    /// <summary>Signer: the signature value does not verify over the signed attributes.</summary>
    SignatureInvalid,

    /// <summary>Signer: the message-digest attribute does not match the covered bytes, so the content was changed after signing.</summary>
    ContentModified,

    /// <summary>Signer: no certificate in the message or the supplied collection matches the signer identifier.</summary>
    CertificateNotFound,

    /// <summary>Signer: the signature is sound but the certificate did not validate to a trust anchor. Signed layer: also when no chain was asked for and <see cref="SmimeOpenOptions.AllowUntrustedSigner"/> is off.</summary>
    UntrustedChain,

    /// <summary>Signer or decryption: the algorithm or key is refused by the crypto policy the caller supplied.</summary>
    PolicyRejected,

    /// <summary>Signer, layer: an algorithm, key type or form this library cannot process (for example a legacy cipher without the explicit flag).</summary>
    Unsupported,

    /// <summary>Layer: the CMS is damaged or truncated, or is not CMS at all.</summary>
    Malformed,

    /// <summary>Layer: the MIME structure is not what RFC 1847 / RFC 8551 require (for example a <c>multipart/signed</c> without exactly two parts).</summary>
    StructureInvalid,

    /// <summary>Encrypted layer: the content was decrypted.</summary>
    Decrypted,

    /// <summary>Encrypted layer: none of the supplied keys is addressed by a recipient info.</summary>
    NoMatchingRecipient,

    /// <summary>Encrypted layer: a recipient matched but decryption failed (wrong key, damaged message, failed authentication tag).</summary>
    DecryptionFailed,

    /// <summary>Encrypted layer: the caller supplied no decryption key, so nothing was tried.</summary>
    NoDecryptionKey,

    /// <summary>Compressed layer: the data was decompressed.</summary>
    Decompressed,

    /// <summary>Layer: the processing limits (nesting, size) stopped it.</summary>
    LimitExceeded,

    /// <summary>Layer: a degenerate SignedData that carries certificates and no content (<c>smime-type=certs-only</c>).</summary>
    CertificatesOnly,

    /// <summary>Signer: a rule other than signature, digest, certificate lookup, policy or chain failed (for example key usage or the signed attributes); see the signer's checks.</summary>
    RuleFailed,
}

/// <summary>How the signer's certificate relates to the message's <c>From</c> address (RFC 8551 §3.2.2, RFC 8550 §3).</summary>
public enum SmimeSenderMatch
{
    /// <summary>The message has no <c>From</c> (or <c>Sender</c>) address to compare.</summary>
    NoSenderAddress,

    /// <summary>The certificate carries no e-mail address (no rfc822Name and no emailAddress in the subject).</summary>
    NoCertificateAddress,

    /// <summary>Every address of <c>From</c> (or of <c>Sender</c> when there is no <c>From</c>) is an address of the certificate, ignoring case. A header that also names someone else is a <see cref="Mismatch"/>.</summary>
    Match,

    /// <summary>Both sides have addresses and none is shared.</summary>
    Mismatch,

    /// <summary>The signer's certificate was not found, so there is nothing to compare.</summary>
    NoCertificate,
}

/// <summary>Whether the signer's certificate chain was evaluated, and how it ended.</summary>
public enum SmimeChainStatus
{
    /// <summary>The caller passed no trust options, so no chain was built. This is the default: nothing is trusted implicitly.</summary>
    NotChecked,

    /// <summary>The certificate validates to a supplied trust anchor.</summary>
    Passed,

    /// <summary>Valid, but something deserves attention (for example revocation status stayed unknown).</summary>
    Warning,

    /// <summary>The certificate does not validate.</summary>
    Failed,

    /// <summary>The validator could not decide (for example required revocation data is missing).</summary>
    Undecided,
}

/// <summary>A non-fatal observation about an S/MIME structure or its handling. Stable names; new members are appended.</summary>
public enum SmimeIssue
{
    /// <summary>The signed part contained bare LF or CR; verification succeeded only after canonicalising line ends to CRLF (RFC 8551 §3.1.1).</summary>
    SignedPartLineEndingsNormalised,

    /// <summary>The <c>micalg</c> parameter is missing.</summary>
    MicalgMissing,

    /// <summary>The <c>micalg</c> parameter does not name the digests the CMS signers used; it is advisory and was ignored.</summary>
    MicalgMismatch,

    /// <summary>The <c>protocol</c> parameter is missing or not <c>application/pkcs7-signature</c> / <c>application/x-pkcs7-signature</c>; the second part was read as a signature anyway.</summary>
    ProtocolParameterUnexpected,

    /// <summary>The <c>smime-type</c> parameter is missing; the layer kind was taken from the CMS content type.</summary>
    SmimeTypeMissing,

    /// <summary>The <c>smime-type</c> parameter disagrees with the CMS content type; the CMS content type won.</summary>
    SmimeTypeMismatch,

    /// <summary>The type was <c>application/octet-stream</c> with a <c>.p7m</c>/<c>.p7s</c> name and was read as S/MIME (MS-OXOSMIME §2.1.3.2.1.1).</summary>
    RecognisedByFileName,

    /// <summary>The detached signature part's second media type is not a recognised signature type.</summary>
    SignaturePartTypeUnexpected,

    /// <summary>The content-encryption cipher has no integrity protection of its own (CBC EnvelopedData): changes to the plaintext that leave the padding intact are not detected (RFC 8551 §2.7).</summary>
    UnauthenticatedEncryption,

    /// <summary>The content was encrypted with a legacy cipher that the caller allowed explicitly.</summary>
    LegacyCipherAllowed,

    /// <summary>The unwrapped content could not be read as a MIME entity; it was wrapped as a single <c>application/octet-stream</c> part.</summary>
    InnerContentNotMime,

    /// <summary>Layers nested deeper than <see cref="SmimeOpenOptions.MaxDepth"/> were not opened.</summary>
    DepthLimitReached,

    /// <summary>A signer's certificate was found only in <see cref="SmimeOpenOptions.Certificates"/>, not in the message.</summary>
    CertificateFromCaller,

    /// <summary>The message holds signed parts in a wrapper that is itself not signed; see <see cref="SmimeResult.Outside"/>.</summary>
    SignedPartInUnsignedWrapper,
}

/// <summary>What of a message sits outside a signature (RFC 8551 §3.1: only the signed entity is protected).</summary>
public sealed class SmimeOutside
{
    internal SmimeOutside(IReadOnlyList<string> headers, IReadOnlyList<string> parts, IReadOnlyList<string> protectedHeaders)
    {
        UnsignedHeaders = headers;
        UnsignedParts = parts;
        ProtectedHeaders = protectedHeaders;
    }

    /// <summary>
    /// Names of the header fields of the outermost entity (<c>From</c>, <c>To</c>, <c>Subject</c>, <c>Date</c> …) other than the
    /// <c>Content-*</c> and <c>MIME-Version</c> ones. A signature over <c>multipart/signed</c> covers none of them, so a display must not
    /// present them as signed. Names a signed <c>message/rfc822</c> entity protects are in <see cref="ProtectedHeaders"/> as well.
    /// </summary>
    public IReadOnlyList<string> UnsignedHeaders { get; }

    /// <summary>
    /// Entity paths (<c>1</c>, <c>2.1</c> … counted from 1 under the root) of parts that sit beside the signed entity inside a wrapper
    /// (for example a <c>multipart/mixed</c> that adds an unsigned attachment next to a signed part). Empty when the root is the signed entity.
    /// </summary>
    public IReadOnlyList<string> UnsignedParts { get; }

    /// <summary>
    /// Header field names the signed entity itself carries when it is a <c>message/rfc822</c> (header protection, RFC 8551 §3.1 and the
    /// "protected headers" practice): these are covered by the signature.
    /// </summary>
    public IReadOnlyList<string> ProtectedHeaders { get; }
}

/// <summary>The outcome for one signer of a signed layer.</summary>
public sealed class SmimeSignerResult
{
    internal SmimeSignerResult(SignerReport report)
    {
        Report = report;
    }

    /// <summary>OneCrypto's per-rule report for this signer: every check with its status, reason and reference, countersignatures, policy and chain details.</summary>
    public SignerReport Report { get; }

    /// <summary>The summary status; see <see cref="SmimeStatus"/>.</summary>
    public SmimeStatus Status { get; internal set; }

    /// <summary>True when <see cref="Status"/> is <see cref="SmimeStatus.Valid"/>. This says the signature is sound for the certificate that came with it, not that the certificate belongs to who it names: see <see cref="IsTrusted"/>.</summary>
    public bool IsValid => Status == SmimeStatus.Valid;

    /// <summary>
    /// True when the signature is sound and the certificate chain was built to a trust anchor the caller supplied (<see cref="Chain"/> is
    /// <see cref="SmimeChainStatus.Passed"/> or <see cref="SmimeChainStatus.Warning"/>). False when no trust options were given, however good the signature:
    /// anyone can sign with a certificate they made themselves and put any address in it. Compare <see cref="SenderMatch"/> only for a trusted signer.
    /// </summary>
    public bool IsTrusted => IsValid && Chain is SmimeChainStatus.Passed or SmimeChainStatus.Warning;

    /// <summary>The signer's certificate, or null when none matched the identifier.</summary>
    public Certificate? Certificate => Report.Certificate;

    /// <summary>The digest algorithm OID of the SignerInfo.</summary>
    public string DigestAlgorithm { get; internal set; } = string.Empty;

    /// <summary>The signature algorithm OID of the SignerInfo.</summary>
    public string SignatureAlgorithm { get; internal set; } = string.Empty;

    /// <summary>The claimed signing time (never used as a validation time), or null.</summary>
    public DateTimeOffset? SigningTime => Report.SigningTime;

    /// <summary>E-mail addresses in the certificate: rfc822Name entries of the subject alternative name, then the subject's emailAddress.</summary>
    public IReadOnlyList<string> CertificateAddresses { get; internal set; } = [];

    /// <summary>Whether the certificate matches the message's sender address (RFC 8550 §3, compared ignoring case).</summary>
    public SmimeSenderMatch SenderMatch { get; internal set; }

    /// <summary>How the chain check ended; <see cref="SmimeChainStatus.NotChecked"/> unless trust options were supplied.</summary>
    public SmimeChainStatus Chain { get; internal set; }

    /// <summary>
    /// Weak or deprecated algorithms in play, as stable tags: <c>digest:sha-1</c>, <c>digest:md5</c>, <c>key:weak</c>, plus the rule ids of
    /// any flagged crypto-policy decisions. Empty when nothing is flagged. Reading accepts these; new signatures refuse them.
    /// </summary>
    public IReadOnlyList<string> WeakAlgorithms { get; internal set; } = [];

    /// <summary>The OIDs of the algorithms the signer advertised in its SMIMECapabilities attribute, in order. Empty when the attribute is absent.</summary>
    public IReadOnlyList<string> Capabilities { get; internal set; } = [];

    /// <summary>True when the signer included an SMIMEEncryptionKeyPreference attribute (RFC 8551 §2.5.2).</summary>
    public bool HasEncryptionKeyPreference { get; internal set; }

    /// <summary>The OneCrypto rule checks, shortcut for <c>Report.Checks</c>.</summary>
    public IReadOnlyList<CmsCheck> Checks => Report.Checks;
}

/// <summary>One S/MIME wrapper that was found and processed.</summary>
public abstract class SmimeLayer
{
    internal SmimeLayer(SmimeLayerKind kind) => Kind = kind;

    /// <summary>What the wrapper is.</summary>
    public SmimeLayerKind Kind { get; }

    /// <summary>How processing the layer ended; see <see cref="SmimeStatus"/>.</summary>
    public SmimeStatus Status { get; internal set; }

    /// <summary>True when the layer did its job: signatures valid, or content decrypted or decompressed.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Non-fatal observations.</summary>
    public IReadOnlyList<SmimeIssue> Issues { get; internal set; } = [];

    /// <summary>A short explanation when <see cref="Succeeded"/> is false; null otherwise.</summary>
    public string? Detail { get; internal set; }

    /// <summary>The entity this layer was read from (the root for the outermost layer, the unwrapped content for inner ones).</summary>
    public MimeEntity Entity { get; internal set; } = null!;
}

/// <summary>A signed layer, detached or opaque.</summary>
public sealed class SmimeSignedLayer : SmimeLayer
{
    internal SmimeSignedLayer(SmimeLayerKind kind) : base(kind)
    {
    }

    /// <summary>One result per SignerInfo, in message order. Empty when the CMS could not be read.</summary>
    public IReadOnlyList<SmimeSignerResult> Signers { get; internal set; } = [];

    /// <summary>OneCrypto's whole report (message-level checks plus the signers), or null when the CMS could not be read.</summary>
    public SignedDataReport? Report { get; internal set; }

    /// <summary>
    /// The exact bytes the signature was checked over: for a detached signature the signed part as stored (header block, blank line and body);
    /// for an opaque one the encapsulated content. When <see cref="SmimeIssue.SignedPartLineEndingsNormalised"/> is among the issues these are
    /// the canonicalised bytes, not the stored ones.
    /// </summary>
    public ReadOnlyMemory<byte> CoveredBytes { get; internal set; }

    /// <summary>For a detached signature, the offset of the covered part in the source the entity was read from; -1 when unknown or opaque.</summary>
    public long CoveredSourceOffset { get; internal set; } = -1;

    /// <summary>For a detached signature, the length in the source of the covered part as stored; 0 when unknown or opaque.</summary>
    public long CoveredSourceLength { get; internal set; }

    /// <summary>The <c>micalg</c> parameter as written, or null.</summary>
    public string? Micalg { get; internal set; }

    /// <summary>The signed entity (the first part of a <c>multipart/signed</c>, or the parsed encapsulated content); null when it could not be read.</summary>
    public MimeEntity? SignedEntity { get; internal set; }

    /// <summary>The certificates carried in the CMS.</summary>
    public IReadOnlyList<Certificate> Certificates { get; internal set; } = [];

    /// <inheritdoc/>
    public override bool Succeeded => Status == SmimeStatus.Valid;

    /// <summary>True when the layer succeeded and every signer is trusted (<see cref="SmimeSignerResult.IsTrusted"/>); with <see cref="SmimeOpenOptions.AllowUntrustedSigner"/> a layer can succeed without this.</summary>
    public bool IsTrusted => Succeeded && Signers.Count > 0 && Signers.All(static s => s.IsTrusted);
}

/// <summary>An enveloped, authEnveloped or compressed layer.</summary>
public sealed class SmimeContentLayer : SmimeLayer
{
    internal SmimeContentLayer(SmimeLayerKind kind) : base(kind)
    {
    }

    /// <summary>The content-encryption (or compression) algorithm OID; empty when the CMS could not be read.</summary>
    public string ContentAlgorithm { get; internal set; } = string.Empty;

    /// <summary>True for AES-GCM or CCM authenticated encryption; false for CBC and for compression.</summary>
    public bool IsAuthenticated { get; internal set; }

    /// <summary>The number of recipient infos in the message.</summary>
    public int RecipientCount { get; internal set; }

    /// <summary>The certificate of the key that opened the message, when the caller's key carried one.</summary>
    public Certificate? RecipientCertificate { get; internal set; }

    /// <summary>The crypto-policy decision report for the cipher when a policy was supplied; null otherwise.</summary>
    public PolicyReport? Policy { get; internal set; }

    /// <inheritdoc/>
    public override bool Succeeded => Status is SmimeStatus.Decrypted or SmimeStatus.Decompressed;
}

/// <summary>
/// What <see cref="SmimeProcessor"/> found in a message: the S/MIME layers outermost first and the content they wrap.
/// Never a bare bool: look at <see cref="Layers"/> for each layer's status and signers.
/// </summary>
public sealed class SmimeResult
{
    internal SmimeResult(MimeEntity root) => Root = root;

    /// <summary>The entity that was examined.</summary>
    public MimeEntity Root { get; }

    /// <summary>The S/MIME layers found, outermost first. Empty when the message is not S/MIME.</summary>
    public IReadOnlyList<SmimeLayer> Layers { get; internal set; } = [];

    /// <summary>
    /// The innermost content that could be reached: the unwrapped entity, or the entity at which processing stopped (an encrypted layer
    /// nobody could open leaves its own entity here). For a message that is not S/MIME, the root.
    /// </summary>
    public MimeEntity Content { get; internal set; } = null!;

    /// <summary>
    /// The inner message in the shared view (subject, addresses, bodies, attachments) when the content reached is a message; this is the
    /// stored message for a detached signature and the decrypted one for an encrypted layer. Null when nothing was unwrapped.
    /// </summary>
    public IMailItem? InnerItem { get; internal set; }

    /// <summary>What of the message the signatures leave unprotected; null when no layer is signed.</summary>
    public SmimeOutside? Outside { get; internal set; }

    /// <summary>Observations that belong to no single layer.</summary>
    public IReadOnlyList<SmimeIssue> Issues { get; internal set; } = [];

    /// <summary>True when at least one layer was found.</summary>
    public bool IsSmime => Layers.Count > 0;

    /// <summary>True when every layer succeeded (and at least one exists): each signature is sound for its certificate and each wrapper was opened. False when the signed entity was found inside an unsigned wrapper (<see cref="SmimeIssue.SignedPartInUnsignedWrapper"/>), since the rest of the message is not signed. Trust is separate; see <see cref="IsTrusted"/>.</summary>
    public bool Succeeded => Layers.Count > 0 && Layers.All(static l => l.Succeeded) && !Issues.Contains(SmimeIssue.SignedPartInUnsignedWrapper);

    /// <summary>
    /// True when <see cref="Succeeded"/>, at least one layer is signed, and every signed layer is trusted (<see cref="SmimeSignedLayer.IsTrusted"/>), which needs
    /// <see cref="SmimeOpenOptions.Chain"/>. This is the property to act on; <see cref="Succeeded"/> alone also holds for a message signed with a certificate
    /// nobody vouched for.
    /// </summary>
    public bool IsTrusted => Succeeded && Layers.OfType<SmimeSignedLayer>().Any() && Layers.OfType<SmimeSignedLayer>().All(static l => l.IsTrusted);

    /// <summary>All signers of all signed layers, outermost first.</summary>
    public IEnumerable<SmimeSignerResult> Signers => Layers.OfType<SmimeSignedLayer>().SelectMany(static l => l.Signers);
}
