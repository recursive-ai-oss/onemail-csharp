namespace RecursiveAI.OneMail.Mime.Reading;

/// <summary>
/// What the MIME reader repaired, guessed or refused. The reader never throws on malformed content; it keeps going and
/// reports one of these with the byte offset. Kinds whose name starts with <c>Ambiguous</c> mark places where
/// real mail clients disagree: the reader took one documented reading (see <c>docs/mime-reading.md</c>) and says so.
/// </summary>
public enum MimeDefectKind
{
    /// <summary>No defect.</summary>
    None = 0,

    // ---- Header block ----

    /// <summary>
    /// The header block ended without the empty line that separates it from the body: end of input, or a line that is not
    /// a header field. The offending line starts the body.
    /// </summary>
    MissingHeaderBodySeparator,

    /// <summary>A folded line (starting with space or tab) came where no header field precedes it. It starts the body.</summary>
    HeaderContinuationWithoutHeader,

    /// <summary>A carriage return not followed by a line feed inside a header field (RFC 5322 §2.2). Left in place.</summary>
    BareCarriageReturnInHeader,

    /// <summary>A NUL byte inside a header field. Left in place.</summary>
    NulInHeader,

    /// <summary>A header field contains bytes above 0x7F that are valid UTF-8 (RFC 6532). Informational.</summary>
    Utf8Header,

    /// <summary>
    /// A header field contains bytes above 0x7F that are not valid UTF-8 and not an RFC 2047 encoded word's ASCII form.
    /// Clients guess a charset differently, so the bytes are kept raw and the decoder picks (ambiguity).
    /// </summary>
    AmbiguousEightBitHeader,

    /// <summary>A header field name is followed by whitespace before the colon (obsolete syntax, RFC 5322 §4.2). Informational.</summary>
    WhitespaceBeforeHeaderColon,

    /// <summary>Two <c>Content-Type</c> fields. The first one decides the structure (ambiguity).</summary>
    AmbiguousDuplicateContentType,

    /// <summary>Two <c>Content-Transfer-Encoding</c> fields. The first one decides (ambiguity).</summary>
    AmbiguousDuplicateTransferEncoding,

    // ---- Content type and structure ----

    /// <summary>The <c>Content-Type</c> value is not <c>type/subtype</c>. The entity is treated as <c>text/plain</c>.</summary>
    InvalidContentType,

    /// <summary>The <c>Content-Transfer-Encoding</c> value is not a known mechanism. The body is treated as identity-encoded.</summary>
    UnknownTransferEncoding,

    /// <summary>A multipart type with no <c>boundary</c> parameter. The entity is treated as a leaf.</summary>
    MultipartWithoutBoundary,

    /// <summary>A multipart type with an empty boundary. The entity is treated as a leaf.</summary>
    EmptyBoundary,

    /// <summary>A boundary longer than <see cref="MimeLimits.MaxBoundaryLength"/>. The entity is treated as a leaf.</summary>
    BoundaryTooLong,

    /// <summary>
    /// A multipart repeats the boundary of an enclosing multipart. The innermost multipart wins the match
    /// (ambiguity).
    /// </summary>
    AmbiguousDuplicateBoundary,

    /// <summary>
    /// A line starts with <c>--boundary</c> but is followed by something other than optional <c>--</c>, whitespace and the
    /// line end (RFC 2046 §5.1.1). It is body text, not a delimiter (ambiguity).
    /// </summary>
    AmbiguousBoundaryLikeLine,

    /// <summary>
    /// A multipart or message type carries a <c>base64</c> or <c>quoted-printable</c> transfer encoding, which RFC 2045 §6.4
    /// forbids. Its content is not parsed further and is kept as a leaf body (ambiguity).
    /// </summary>
    AmbiguousEncodedComposite,

    /// <summary>A multipart had no closing delimiter: the input ended, or an enclosing multipart's delimiter came first.</summary>
    MissingFinalBoundary,

    // ---- Reader structure, text, addresses, dates, identifiers ----

    /// <summary>A Content-Type or Content-Disposition parameter has no <c>=</c>; it was dropped.</summary>
    InvalidParameter,

    /// <summary>A multipart whose body has no delimiter line at all. The whole body is its preamble; there are no parts.</summary>
    MissingStartBoundary,

    /// <summary>After the closing delimiter, a line looks like a delimiter of the same multipart. It stays epilogue text.</summary>
    EpilogueHasDelimiter,

    /// <summary>The message mixes LF and CRLF line ends in its headers and delimiters. Each line may end either way; reported once per message.</summary>
    MixedLineEndings,

    /// <summary>A header field contains a control character other than NUL, tab, CR and LF. Kept as written.</summary>
    ControlCharacterInHeader,

    /// <summary>A boundary parameter ends in spaces or tabs. The trimmed boundary is matched, which also matches the literal one because delimiters may carry trailing blanks.</summary>
    BoundaryHasTrailingSpace,

    /// <summary>Bytes that are not valid in the declared charset were replaced by U+FFFD.</summary>
    InvalidCharsetBytes,

    /// <summary>A charset label is unknown; the text was decoded as UTF-8 if valid, else windows-1252.</summary>
    AmbiguousUnknownCharset,

    /// <summary>An RFC 2047 encoded word is malformed and was left as literal text.</summary>
    MalformedEncodedWord,

    /// <summary>The base64 of an encoded word is damaged; what could be decoded was kept.</summary>
    InvalidEncodedWordBase64,

    /// <summary>The Q encoding of an encoded word has a bad escape.</summary>
    InvalidEncodedWordQuotedPrintable,

    /// <summary>An encoded word is longer than 75 characters; it was decoded anyway.</summary>
    EncodedWordTooLong,

    /// <summary>An encoded word inside a quoted string or parameter value, where RFC 2047 forbids it; decoded, as clients do.</summary>
    AmbiguousEncodedWordInQuotedString,

    /// <summary>Adjacent encoded words split a multibyte character; they were decoded as one byte stream.</summary>
    AmbiguousSplitMultibyte,

    /// <summary>A percent escape in an RFC 2231 value is malformed; kept literally.</summary>
    InvalidParameterEscape,

    /// <summary>An RFC 2231 extended value lacks the charset and language quote marks; the whole value was used.</summary>
    MalformedExtendedParameter,

    /// <summary>RFC 2231 continuation segments are not contiguous from 0; all present were joined in index order.</summary>
    AmbiguousParameterSegments,

    /// <summary>The same RFC 2231 segment number appears twice; both were used.</summary>
    DuplicateParameterSegment,

    /// <summary>A parameter is given both plainly and in RFC 2231 form; the RFC 2231 form won.</summary>
    AmbiguousParameterForms,

    /// <summary>The same parameter name appears twice; the first one wins.</summary>
    DuplicateParameter,

    /// <summary>A text-layer resource limit (parameters, segments) was reached; the excess was ignored.</summary>
    TextLimitExceeded,

    /// <summary>A comma inside an unquoted display name before an angle address; kept as part of the name.</summary>
    AmbiguousUnquotedCommaInPhrase,

    /// <summary>Words followed by a bare address; the words became the display name.</summary>
    MissingAngleBrackets,

    /// <summary>An obsolete source route (RFC 5322 section 4.4) was dropped.</summary>
    ObsoleteRoute,

    /// <summary>A mailbox has an empty address (&lt;&gt;).</summary>
    EmptyAddress,

    /// <summary>An address has no domain.</summary>
    MissingDomain,

    /// <summary>An angle address is not closed; it was closed at the end of the value.</summary>
    UnclosedAngle,

    /// <summary>A quoted string is not closed; it ends at the next angle bracket or the end of the value.</summary>
    UnterminatedQuote,

    /// <summary>Two angle addresses in one mailbox; both were kept as separate mailboxes.</summary>
    AmbiguousAdjacentAngleAddrs,

    /// <summary>Text that is not an address (after a semicolon, for example) was left unparsed.</summary>
    UnparsedAddressText,

    /// <summary>A local part with leading, trailing or doubled dots (obsolete syntax); kept as written.</summary>
    ObsoleteLocalPart,

    /// <summary>An address list element is not valid and was skipped.</summary>
    InvalidAddressSyntax,

    /// <summary>The day of the week does not match the date; the weekday is ignored.</summary>
    WeekdayMismatch,

    /// <summary>A two-digit year; 00-68 mean 2000-2068 and 69-99 mean 1969-1999.</summary>
    AmbiguousTwoDigitYear,

    /// <summary>A three-digit year; 1900 was added (RFC 5322 section 4.3).</summary>
    ThreeDigitYear,

    /// <summary>A zone of -0000, a military letter or no zone: the instant is read as UTC and the zone is unknown.</summary>
    UnknownTimeZone,

    /// <summary>A zone offset out of range was read as unknown.</summary>
    InvalidTimeZone,

    /// <summary>Seconds of 60; the instant is clamped to :59.</summary>
    LeapSecond,

    /// <summary>Fractional seconds were dropped.</summary>
    FractionalSeconds,

    /// <summary>A date value is not a valid date; it reads as absent.</summary>
    InvalidDate,

    /// <summary>A message identifier list element is not a valid msg-id; it was skipped or taken as written.</summary>
    InvalidMessageId,

    // ---- Resource limits ----

    /// <summary>Nesting went deeper than <see cref="MimeLimits.MaxDepth"/>; the deeper content is kept as a leaf body.</summary>
    DepthLimitExceeded,

    /// <summary>More entities than <see cref="MimeLimits.MaxEntityCount"/>; the rest of the input is kept as body text.</summary>
    EntityLimitExceeded,

    /// <summary>A header block grew past <see cref="MimeLimits.MaxHeaderBlockBytes"/> or <see cref="MimeLimits.MaxHeadersPerEntity"/>; the rest is body.</summary>
    HeaderLimitExceeded,

    /// <summary>One header field is longer than <see cref="MimeLimits.MaxHeaderFieldBytes"/>; the rest is body.</summary>
    HeaderFieldLimitExceeded,

    /// <summary>The input is longer than <see cref="MimeLimits.MaxTotalBytes"/>; reading stopped there.</summary>
    TotalSizeLimitExceeded,
}

/// <summary>A repair, guess or refusal reported by the MIME reader, with where it happened.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Offset">Absolute byte offset in the input where the defect was found.</param>
/// <param name="Length">Bytes involved, or zero when the defect is a position (a missing delimiter).</param>
/// <param name="Depth">Nesting depth of the entity it occurred in (the message itself is 0).</param>
public readonly record struct MimeDefect(MimeDefectKind Kind, long Offset, int Length, int Depth)
{
    /// <summary>True for the <c>Ambiguous*</c> kinds, where clients disagree and the library took a documented reading.</summary>
    public bool IsAmbiguity => Kind is MimeDefectKind.AmbiguousEightBitHeader
        or MimeDefectKind.AmbiguousDuplicateContentType
        or MimeDefectKind.AmbiguousDuplicateTransferEncoding
        or MimeDefectKind.AmbiguousDuplicateBoundary
        or MimeDefectKind.AmbiguousBoundaryLikeLine
        or MimeDefectKind.AmbiguousEncodedComposite
        or MimeDefectKind.AmbiguousUnknownCharset
        or MimeDefectKind.AmbiguousEncodedWordInQuotedString
        or MimeDefectKind.AmbiguousSplitMultibyte
        or MimeDefectKind.AmbiguousParameterSegments
        or MimeDefectKind.AmbiguousParameterForms
        or MimeDefectKind.AmbiguousUnquotedCommaInPhrase
        or MimeDefectKind.AmbiguousAdjacentAngleAddrs
        or MimeDefectKind.AmbiguousTwoDigitYear;

    /// <summary>True for the kinds that report a resource limit being reached.</summary>
    public bool IsLimit => Kind >= MimeDefectKind.DepthLimitExceeded;

    /// <summary>A one-line description of the kind, in plain words.</summary>
    public string Description => Describe(Kind);

    /// <summary>A one-line description of a defect kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The description.</returns>
    public static string Describe(MimeDefectKind kind) => kind switch
    {
        MimeDefectKind.MissingHeaderBodySeparator => "The header block has no empty line before the body.",
        MimeDefectKind.HeaderContinuationWithoutHeader => "A folded line has no header field to continue.",
        MimeDefectKind.BareCarriageReturnInHeader => "A carriage return in a header is not followed by a line feed.",
        MimeDefectKind.NulInHeader => "A header contains a NUL byte.",
        MimeDefectKind.Utf8Header => "A header contains UTF-8 text.",
        MimeDefectKind.AmbiguousEightBitHeader => "A header contains 8-bit bytes that are not UTF-8.",
        MimeDefectKind.WhitespaceBeforeHeaderColon => "A header name is followed by whitespace before the colon.",
        MimeDefectKind.AmbiguousDuplicateContentType => "There is more than one Content-Type; the first one is used.",
        MimeDefectKind.AmbiguousDuplicateTransferEncoding => "There is more than one Content-Transfer-Encoding; the first one is used.",
        MimeDefectKind.InvalidContentType => "The Content-Type value is not type/subtype.",
        MimeDefectKind.UnknownTransferEncoding => "The Content-Transfer-Encoding is not recognised.",
        MimeDefectKind.MultipartWithoutBoundary => "A multipart entity has no boundary parameter.",
        MimeDefectKind.EmptyBoundary => "A multipart entity has an empty boundary.",
        MimeDefectKind.BoundaryTooLong => "A multipart boundary is longer than the limit.",
        MimeDefectKind.AmbiguousDuplicateBoundary => "A multipart reuses the boundary of an enclosing multipart.",
        MimeDefectKind.AmbiguousBoundaryLikeLine => "A line starts like a boundary delimiter but is not one.",
        MimeDefectKind.AmbiguousEncodedComposite => "A multipart or message entity has a base64 or quoted-printable encoding.",
        MimeDefectKind.MissingFinalBoundary => "A multipart entity has no closing delimiter.",
        MimeDefectKind.DepthLimitExceeded => "Nesting is deeper than the limit.",
        MimeDefectKind.EntityLimitExceeded => "There are more entities than the limit.",
        MimeDefectKind.HeaderLimitExceeded => "A header block is larger than the limit.",
        MimeDefectKind.HeaderFieldLimitExceeded => "A header field is longer than the limit.",
        MimeDefectKind.TotalSizeLimitExceeded => "The input is larger than the limit.",
        MimeDefectKind.MissingStartBoundary => "A multipart entity has no delimiter line.",
        MimeDefectKind.EpilogueHasDelimiter => "The epilogue contains a delimiter-looking line.",
        MimeDefectKind.MixedLineEndings => "The message mixes LF and CRLF line ends.",
        MimeDefectKind.ControlCharacterInHeader => "A header contains a control character.",
        MimeDefectKind.BoundaryHasTrailingSpace => "A boundary ends in whitespace.",
        MimeDefectKind.InvalidCharsetBytes => "Text has bytes that are invalid in its charset.",
        MimeDefectKind.AmbiguousUnknownCharset => "A charset label is not known.",
        MimeDefectKind.MalformedEncodedWord => "An encoded word is malformed.",
        MimeDefectKind.InvalidEncodedWordBase64 => "An encoded word has damaged base64.",
        MimeDefectKind.InvalidEncodedWordQuotedPrintable => "An encoded word has a bad Q escape.",
        MimeDefectKind.EncodedWordTooLong => "An encoded word is longer than 75 characters.",
        MimeDefectKind.AmbiguousEncodedWordInQuotedString => "An encoded word appears inside a quoted string.",
        MimeDefectKind.AmbiguousSplitMultibyte => "Encoded words split a multibyte character.",
        MimeDefectKind.InvalidParameterEscape => "A parameter has a malformed percent escape.",
        MimeDefectKind.MalformedExtendedParameter => "An extended parameter value is malformed.",
        MimeDefectKind.AmbiguousParameterSegments => "Parameter continuations are not contiguous.",
        MimeDefectKind.DuplicateParameterSegment => "A parameter continuation index repeats.",
        MimeDefectKind.AmbiguousParameterForms => "A parameter is given in two forms.",
        MimeDefectKind.DuplicateParameter => "A parameter name is repeated.",
        MimeDefectKind.TextLimitExceeded => "A parameter limit was reached.",
        MimeDefectKind.AmbiguousUnquotedCommaInPhrase => "An unquoted display name contains a comma.",
        MimeDefectKind.MissingAngleBrackets => "A display name is followed by an address without angle brackets.",
        MimeDefectKind.ObsoleteRoute => "An address has an obsolete source route.",
        MimeDefectKind.EmptyAddress => "A mailbox has an empty address.",
        MimeDefectKind.MissingDomain => "An address has no domain.",
        MimeDefectKind.UnclosedAngle => "An angle bracket is not closed.",
        MimeDefectKind.UnterminatedQuote => "A quoted string is not closed.",
        MimeDefectKind.AmbiguousAdjacentAngleAddrs => "Two angle addresses follow each other.",
        MimeDefectKind.UnparsedAddressText => "Part of an address list could not be parsed.",
        MimeDefectKind.ObsoleteLocalPart => "A local part has misplaced dots.",
        MimeDefectKind.InvalidAddressSyntax => "An address is not valid.",
        MimeDefectKind.WeekdayMismatch => "The weekday does not match the date.",
        MimeDefectKind.AmbiguousTwoDigitYear => "A date has a two-digit year.",
        MimeDefectKind.ThreeDigitYear => "A date has a three-digit year.",
        MimeDefectKind.UnknownTimeZone => "A date has an unknown time zone.",
        MimeDefectKind.InvalidTimeZone => "A date has an invalid time zone.",
        MimeDefectKind.LeapSecond => "A date has a leap second.",
        MimeDefectKind.FractionalSeconds => "A date has fractional seconds.",
        MimeDefectKind.InvalidDate => "A date is not valid.",
        MimeDefectKind.InvalidMessageId => "A message identifier is not valid.",
        MimeDefectKind.InvalidParameter => "A parameter has no value.",
        _ => "No defect.",
    };
}

/// <summary>
/// Resource limits for reading MIME. They are on by default so hostile input ends in a defect, not in memory exhaustion
/// or a stack overflow. Every limit is in bytes or counts; reaching one raises the matching
/// <see cref="MimeDefectKind"/> and keeps the rest of the input as body text, so nothing is lost.
/// </summary>
public sealed class MimeLimits
{
    /// <summary>The defaults.</summary>
    public static MimeLimits Default { get; } = new();

    /// <summary>Deepest nesting of multipart and message entities (the message is depth 0). Default 64.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Most entities (parts and embedded messages, counting the root) in one message. Default 10,000.</summary>
    public int MaxEntityCount { get; init; } = 10_000;

    /// <summary>Largest header block of one entity, in bytes. Default 1 MiB.</summary>
    public int MaxHeaderBlockBytes { get; init; } = 1 << 20;

    /// <summary>Largest single header field including its folded lines, in bytes. Default 256 KiB.</summary>
    public int MaxHeaderFieldBytes { get; init; } = 256 * 1024;

    /// <summary>Most header fields in one entity. Default 10,000.</summary>
    public int MaxHeadersPerEntity { get; init; } = 10_000;

    /// <summary>Longest multipart boundary accepted, in bytes (RFC 2046 says 70). Default 255.</summary>
    public int MaxBoundaryLength { get; init; } = 255;

    /// <summary>Most input bytes read; the rest is ignored. Default 2 GiB minus one.</summary>
    public long MaxTotalBytes { get; init; } = int.MaxValue;
}
