namespace RecursiveAI.OneMail.Cryptography.Dkim;

/// <summary>The signature algorithm of a DKIM signature (RFC 6376 §3.3, RFC 8463).</summary>
public enum DkimAlgorithm
{
    /// <summary><c>rsa-sha256</c>.</summary>
    RsaSha256 = 0,

    /// <summary><c>rsa-sha1</c>. Deprecated by RFC 8301: verified and flagged, never produced.</summary>
    RsaSha1 = 1,

    /// <summary><c>ed25519-sha256</c> (RFC 8463): Ed25519 over the SHA-256 digest of the canonicalised data.</summary>
    Ed25519Sha256 = 2,
}

/// <summary>Header or body canonicalisation (RFC 6376 §3.4).</summary>
public enum DkimCanonicalization
{
    /// <summary>Nothing is changed.</summary>
    Simple = 0,

    /// <summary>Header names lower-cased, folding removed and whitespace collapsed; body whitespace collapsed.</summary>
    Relaxed = 1,
}

/// <summary>The result category of a signature, named as in RFC 8601 §2.7.1.</summary>
public enum DkimStatus
{
    /// <summary>The signature verified.</summary>
    Pass = 0,

    /// <summary>The signature was well formed and usable but does not verify (tampering, expiry, a revoked or too-weak key).</summary>
    Fail = 1,

    /// <summary>The signature could not be processed: syntax errors, an unsupported version or algorithm.</summary>
    Neutral = 2,

    /// <summary>A temporary condition (DNS failure) prevented verification; try again later.</summary>
    TempError = 3,

    /// <summary>A permanent condition prevented verification: no key record, an unusable key record, a missing required tag.</summary>
    PermError = 4,
}

/// <summary>Why a signature got its status. New members are only ever added at the end.</summary>
public enum DkimReason
{
    /// <summary>Verified.</summary>
    None = 0,

    /// <summary>The tag list is malformed.</summary>
    MalformedSignature = 1,

    /// <summary>A tag appears more than once.</summary>
    DuplicateTag = 2,

    /// <summary>A required tag is missing.</summary>
    MissingTag = 3,

    /// <summary>The version is not 1.</summary>
    UnsupportedVersion = 4,

    /// <summary>The <c>a=</c> algorithm is not supported.</summary>
    UnsupportedAlgorithm = 5,

    /// <summary>The <c>c=</c> canonicalisation is not supported.</summary>
    UnsupportedCanonicalization = 6,

    /// <summary>The <c>q=</c> query method is not <c>dns/txt</c>.</summary>
    UnsupportedQueryMethod = 7,

    /// <summary>A tag value (<c>t=</c>, <c>x=</c>, <c>l=</c>, <c>b=</c>, <c>bh=</c>) is not valid.</summary>
    InvalidTagValue = 8,

    /// <summary>The <c>h=</c> list does not include From (RFC 6376 §3.5).</summary>
    FromNotSigned = 9,

    /// <summary>The <c>i=</c> domain is not <c>d=</c> or a subdomain of it.</summary>
    IdentityNotInDomain = 10,

    /// <summary>The key record says <c>t=s</c> and <c>i=</c> is a subdomain rather than <c>d=</c> itself.</summary>
    StrictIdentityMismatch = 11,

    /// <summary><c>d=</c> or <c>s=</c> is not a usable DNS name.</summary>
    InvalidDomain = 12,

    /// <summary>The DNS lookup of the key failed temporarily.</summary>
    DnsTemporaryFailure = 13,

    /// <summary>No key record exists.</summary>
    KeyNotFound = 14,

    /// <summary>The key record is malformed or unusable.</summary>
    KeyRecordInvalid = 15,

    /// <summary>The key record's <c>p=</c> is empty: the key was revoked.</summary>
    KeyRevoked = 16,

    /// <summary>The key is smaller than the verifier's minimum.</summary>
    KeyTooWeak = 17,

    /// <summary>The key record's type or hash list does not allow the signature's algorithm.</summary>
    KeyAlgorithmMismatch = 18,

    /// <summary>The body hash does not match.</summary>
    BodyHashMismatch = 19,

    /// <summary>The signature does not match the signed headers.</summary>
    SignatureMismatch = 20,

    /// <summary><c>l=</c> is larger than the canonicalised body.</summary>
    BodyLengthExceedsBody = 21,

    /// <summary><c>x=</c> is in the past.</summary>
    Expired = 22,

    /// <summary>The verifier's policy refuses <c>rsa-sha1</c>.</summary>
    Sha1Refused = 23,

    /// <summary>More signatures than the verifier's limit; this one was not examined.</summary>
    TooManySignatures = 24,

    /// <summary>A header named in <c>h=</c> is not allowed there (an ARC seal's <c>h=</c>, ARC headers in an ARC message signature).</summary>
    ForbiddenHeader = 25,

    /// <summary>The key record's <c>s=</c> excludes email.</summary>
    ServiceTypeMismatch = 26,

    /// <summary>The signature uses <c>l=</c> and content follows the signed part of the body, which the signature does not protect (RFC 6376 §8.2). Refused unless <see cref="DkimVerifierOptions.AllowBodyLengthLimit"/> is set.</summary>
    BodyLengthLimited = 27,

    /// <summary>The message holds more <c>From</c> header fields than the signature covers, so the one a reader shows may be unsigned (RFC 6376 §8.15). Refused unless <see cref="DkimVerifierOptions.AllowUnsignedFromInstances"/> is set.</summary>
    UnsignedFromHeader = 28,

    /// <summary>The header section holds more fields than <see cref="DkimVerifierOptions.MaxHeaderFields"/>; nothing was verified.</summary>
    TooManyHeaders = 29,
}

/// <summary>Things worth knowing about a signature even when it passes.</summary>
[Flags]
public enum DkimWarnings
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The signature uses rsa-sha1 (RFC 8301 deprecates it).</summary>
    WeakAlgorithm = 1,

    /// <summary>The RSA key is under 2048 bits.</summary>
    WeakKey = 2,

    /// <summary>The key record has <c>t=y</c>: the domain is testing DKIM and verifiers should not treat failures as errors.</summary>
    TestingKey = 4,

    /// <summary>The signature uses <c>l=</c>, so content may follow the signed part of the body.</summary>
    BodyLengthLimit = 8,

    /// <summary>The signature's <c>t=</c> is in the future.</summary>
    SignedInFuture = 16,

    /// <summary>The signature has no <c>x=</c> expiry.</summary>
    NoExpiry = 32,

    /// <summary>The message holds more instances of a header named in <c>h=</c> than the signature covers; see <see cref="DkimSignatureResult.UnsignedHeaderInstances"/>.</summary>
    UnsignedHeaderInstances = 64,
}

/// <summary>How a signature stands against the clock.</summary>
public enum DkimTimeValidity
{
    /// <summary>Neither <c>t=</c> nor <c>x=</c> is present, or both are within range.</summary>
    Valid = 0,

    /// <summary><c>t=</c> is in the future (beyond the allowed skew).</summary>
    NotYetValid = 1,

    /// <summary><c>x=</c> is in the past.</summary>
    Expired = 2,
}

/// <summary>A byte range of the message that a signature covers.</summary>
/// <param name="Offset">Start, in bytes from the start of the message passed in.</param>
/// <param name="Length">Length in bytes.</param>
public readonly record struct DkimByteRange(long Offset, long Length);
