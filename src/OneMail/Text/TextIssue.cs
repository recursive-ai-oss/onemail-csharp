namespace RecursiveAI.OneMail.Text;

/// <summary>
/// What a text decoder repaired or guessed. The decoders never throw on malformed input: they produce the best text
/// they can and report each repair as a <see cref="TextIssue"/> so the caller can turn it into a defect.
/// Kinds whose name starts with <c>Ambiguous</c> are the documented single resolution of a known ambiguity
/// (see <c>docs/text-encodings.md</c>); the others are plain damage.
/// </summary>
public enum TextIssueKind
{
    /// <summary>Bytes that are not valid in the charset were replaced by U+FFFD.</summary>
    InvalidBytes,

    /// <summary>
    /// The charset label is not known. The bytes were decoded as UTF-8 when they are valid UTF-8 and as windows-1252
    /// otherwise.
    /// </summary>
    AmbiguousUnknownCharset,

    /// <summary>
    /// Undeclared 8-bit bytes (outside any encoded word or declared charset) were decoded as UTF-8 when valid, and with
    /// the fallback encoding (windows-1252 by default) otherwise.
    /// </summary>
    AmbiguousRaw8Bit,

    /// <summary>An encoded word (<c>=?charset?B?...?=</c>) is malformed and was left as literal text.</summary>
    MalformedEncodedWord,

    /// <summary>The base64 of an encoded word had invalid characters, bad padding or a dangling partial group; what could be decoded was kept.</summary>
    InvalidBase64,

    /// <summary>The Q encoding of an encoded word had a bad <c>=XX</c> escape; the <c>=</c> was kept literally.</summary>
    InvalidQuotedPrintable,

    /// <summary>An encoded word is longer than the 75 characters RFC 2047 allows. It was decoded anyway.</summary>
    EncodedWordTooLong,

    /// <summary>
    /// An encoded word appeared inside a quoted string or a parameter value, where RFC 2047 forbids it. It was decoded,
    /// as mail clients do.
    /// </summary>
    AmbiguousEncodedWordInQuotedString,

    /// <summary>
    /// Adjacent encoded words with the same charset were decoded as one byte stream, because a multibyte character was
    /// split between them (RFC 2047 forbids the split; clients cope with it).
    /// </summary>
    AmbiguousSplitMultibyte,

    /// <summary>A percent escape in an RFC 2231 extended value was not <c>%XX</c>; it was kept literally.</summary>
    InvalidPercentEscape,

    /// <summary>An RFC 2231 extended value did not have the form <c>charset'language'value</c>; the whole value was used.</summary>
    MalformedExtendedValue,

    /// <summary>
    /// RFC 2231 continuation segments are not contiguous from 0. Every segment present was concatenated in numeric
    /// order.
    /// </summary>
    AmbiguousParameterSegments,

    /// <summary>The same RFC 2231 segment number appeared twice. Both copies were used, in source order.</summary>
    DuplicateParameterSegment,

    /// <summary>
    /// A parameter is given both as <c>name=value</c> and in RFC 2231 form (<c>name*=</c> or <c>name*0=</c>). The
    /// RFC 2231 form was used.
    /// </summary>
    AmbiguousParameterForms,

    /// <summary>The same plain parameter name appeared twice. The first was used.</summary>
    DuplicateParameter,

    /// <summary>A resource limit (segments per parameter, parameters per collector) was reached; the excess was ignored.</summary>
    LimitExceeded,

    /// <summary>A UTF-7 sequence was malformed (bad base64, unpaired surrogate, non-zero padding bits, 8-bit byte).</summary>
    InvalidUtf7,

    /// <summary>An IMAP modified UTF-7 sequence was malformed.</summary>
    InvalidImapUtf7,

    /// <summary>An ISO-2022 / HZ escape or byte sequence was malformed and replaced.</summary>
    InvalidIso2022,

    /// <summary>
    /// An ISO-2022-JP text used a character set outside the declared variant's repertoire (for example JIS X 0212 in
    /// plain ISO-2022-JP). It was decoded anyway, as clients do.
    /// </summary>
    AmbiguousIso2022Repertoire,

    /// <summary>The text uses a character set this library cannot decode (JIS X 0213 plane 2, ISO-2022-CN); U+FFFD was written.</summary>
    UnsupportedCharacterSet,

    /// <summary>A character cannot be represented in the target charset and was replaced by <c>?</c> (or encoded as UTF-8 by the caller's choice).</summary>
    Unencodable,
}

/// <summary>One repair or guess a decoder made.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Offset">Byte offset in the span given to the decoder where the problem starts. For parameter issues
/// it is the <c>sourceOffset</c> passed to <see cref="ParameterCollector.Add"/>.</param>
/// <param name="Length">Number of input bytes involved (0 when unknown).</param>
public readonly record struct TextIssue(TextIssueKind Kind, int Offset, int Length)
{
    /// <summary>True for the documented resolutions of known ambiguities (the kinds named <c>Ambiguous*</c>).</summary>
    public bool IsAmbiguity => Kind is TextIssueKind.AmbiguousUnknownCharset or TextIssueKind.AmbiguousRaw8Bit
        or TextIssueKind.AmbiguousEncodedWordInQuotedString or TextIssueKind.AmbiguousSplitMultibyte
        or TextIssueKind.AmbiguousParameterSegments or TextIssueKind.AmbiguousParameterForms
        or TextIssueKind.AmbiguousIso2022Repertoire;
}
