namespace RecursiveAI.OneMail.Rtf;

/// <summary>
/// What the RTF code repaired, guessed or refused. Nothing in this namespace throws on malformed input: the readers
/// produce the best result they can and describe each repair as an <see cref="RtfDefect"/>. Kinds whose name starts
/// with <c>Ambiguous</c> are the one documented resolution of a known ambiguity (see <c>docs/rtf.md</c>); the others
/// are plain damage or a limit that was hit.
/// </summary>
public enum RtfDefectKind
{
    /// <summary>The input is shorter than the 16-byte compressed-RTF header.</summary>
    HeaderTruncated,

    /// <summary>The compression type is neither <c>LZFu</c> nor <c>MELA</c>. Nothing was decompressed.</summary>
    UnknownCompressionType,

    /// <summary>
    /// The header's size field disagrees with the number of bytes present. When fewer bytes are present the data is
    /// treated as cut off; when more are present the extra bytes are ignored.
    /// </summary>
    CompressedSizeMismatch,

    /// <summary>The compressed data ended before the end marker (a dictionary reference to the write position).</summary>
    MissingEndMarker,

    /// <summary>The checksum in the header does not match the compressed contents. The output is still returned.</summary>
    CrcMismatch,

    /// <summary>The number of bytes produced differs from the raw size in the header.</summary>
    RawSizeMismatch,

    /// <summary>A dictionary reference points at a position that has not been written yet; zero bytes were used.</summary>
    ReferenceBeyondDictionary,

    /// <summary>The output reached the configured size limit; the output is cut short.</summary>
    OutputLimitExceeded,

    /// <summary>The header claims more output than the configured ratio allows for this input; nothing was decompressed.</summary>
    ExpansionRatioExceeded,

    /// <summary>The input does not start with <c>{\rtf</c>.</summary>
    NotRtf,

    /// <summary>A control word parameter does not fit in 32 bits; it was clamped.</summary>
    ParameterOverflow,

    /// <summary><c>\'</c> was not followed by two hexadecimal digits; the <c>\'</c> was dropped.</summary>
    InvalidHexEscape,

    /// <summary>A <c>\bin</c> length runs past the end of the input, or is negative; the data was clamped.</summary>
    BinaryDataTruncated,

    /// <summary>The input ended inside a control word or escape.</summary>
    TruncatedToken,

    /// <summary>A <c>}</c> without a matching <c>{</c>.</summary>
    UnbalancedGroupEnd,

    /// <summary>The input ended with groups still open.</summary>
    UnclosedGroups,

    /// <summary>The group nesting limit was reached; reading stopped there.</summary>
    NestingLimitExceeded,

    /// <summary>The document carries no encapsulated HTML or plain text.</summary>
    NotEncapsulated,

    /// <summary>
    /// A code page has no decoder (the symbol code page, or one the platform lacks). The default code page's
    /// decoder was used instead.
    /// </summary>
    AmbiguousUnknownCodePage,

    /// <summary>
    /// A Unicode escape (<c>\uN</c>) produced half of a surrogate pair without its other half; U+FFFD replaced it.
    /// </summary>
    LoneSurrogate,

    /// <summary>
    /// <c>\htmlrtf</c> appeared in a document that is not marked <c>\fromhtml1</c>. It was ignored, because the
    /// suppression only has a meaning for encapsulated HTML.
    /// </summary>
    AmbiguousHtmlRtfInPlainText,

    /// <summary>The number of defects reached the cap; later ones are not recorded.</summary>
    TooManyDefects,
}

/// <summary>One repair or limit hit, with where in the input it happened.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Offset">Byte offset in the input given to the reader that found it.</param>
/// <param name="Message">A short human-readable description; not part of the contract.</param>
public readonly record struct RtfDefect(RtfDefectKind Kind, long Offset, string Message);

/// <summary>Collects defects up to a cap, so hostile input cannot make the list grow without bound.</summary>
internal sealed class RtfDefectList
{
    private const int Cap = 256;
    private readonly List<RtfDefect> _items = [];
    private bool _capped;

    public IReadOnlyList<RtfDefect> Items => _items;

    public void Add(RtfDefectKind kind, long offset, string message)
    {
        if (_items.Count < Cap)
        {
            _items.Add(new RtfDefect(kind, offset, message));
        }
        else if (!_capped)
        {
            _capped = true;
            _items.Add(new RtfDefect(RtfDefectKind.TooManyDefects, offset, "further defects are not recorded"));
        }
    }
}
