namespace RecursiveAI.OneMail.Calendar;

/// <summary>
/// What the content-line reader or a component parser repaired, guessed or refused. Parsers never throw on malformed
/// content: they keep going, keep every byte and report one of these with the byte offset. Kinds whose name starts with
/// <c>Ambiguous</c> mark places where real clients disagree and the library took one documented reading (see
/// <c>docs/ambiguities.md</c>, calendar section).
/// </summary>
public enum CalendarDefectKind
{
    /// <summary>No defect.</summary>
    None = 0,

    // ---- Lines ----

    /// <summary>A line ends with a bare line feed instead of CRLF (RFC 5545 §3.1). Reported once, at the first such line.</summary>
    BareLineFeed,

    /// <summary>A line ends with a bare carriage return. It is taken as a line end. Reported once, at the first such line.</summary>
    BareCarriageReturn,

    /// <summary>A folded continuation (a line starting with space or tab) has no line to continue. The line is read on its own.</summary>
    OrphanContinuation,

    /// <summary>A line has no colon, so it is not a content line. It is kept as a malformed line.</summary>
    MissingColon,

    /// <summary>A content line has an empty property name. It is kept as a malformed line.</summary>
    EmptyName,

    /// <summary>A property or parameter name contains a character outside letters, digits and hyphen (RFC 5545 §3.1). Kept as written.</summary>
    InvalidNameCharacter,

    /// <summary>Whitespace sits next to a name, an equals sign or a colon. It is ignored when reading.</summary>
    WhitespaceAroundDelimiter,

    /// <summary>A parameter value opens a double quote that is never closed on the line. The quote is read as an ordinary character.</summary>
    UnterminatedQuote,

    /// <summary>A parameter has no name before its equals sign. It is kept with an empty name.</summary>
    EmptyParameterName,

    /// <summary>A quoted-printable value (vCard 2.1) ended a physical line with a soft break; the lines were joined. Informational.</summary>
    QuotedPrintableSoftBreak,

    /// <summary>Base64 data continued on lines that are not indented (vCard 2.1); the lines were joined. Informational.</summary>
    Base64ContinuationWithoutFold,

    // ---- Text ----

    /// <summary>The file starts with a UTF-8 byte order mark. It is kept and written back. Informational.</summary>
    ByteOrderMark,

    /// <summary>
    /// A line is not valid UTF-8 and has no usable <c>CHARSET</c>. It is read as Windows-1252, the reading clients
    /// most often give such text (ambiguity).
    /// </summary>
    AmbiguousNonUtf8Text,

    /// <summary>A <c>CHARSET</c> parameter names a charset this build cannot decode. The bytes are read as UTF-8 (or Windows-1252 when invalid).</summary>
    UnknownCharset,

    /// <summary>
    /// A parameter value uses an RFC 6868 caret sequence (<c>^n</c>, <c>^^</c>, <c>^'</c>) and it was decoded. Older
    /// clients write a literal caret, so the decoded and literal readings differ (ambiguity).
    /// </summary>
    AmbiguousCaretEncoding,

    // ---- Structure ----

    /// <summary>A <c>BEGIN</c> line has no component name. It is kept as an ordinary property.</summary>
    BeginWithoutName,

    /// <summary>An <c>END</c> line matches no open component. It is kept as a malformed line.</summary>
    UnmatchedEnd,

    /// <summary>A component was never closed: its <c>END</c> is missing, or an enclosing component's <c>END</c> came first.</summary>
    MissingEnd,

    // ---- Resource limits ----

    /// <summary>A logical line is longer than <see cref="CalendarLimits.MaxLineBytes"/>. Its value is cut there; the source bytes are kept.</summary>
    LineTooLong,

    /// <summary>Components nest deeper than <see cref="CalendarLimits.MaxDepth"/>. The deeper <c>BEGIN</c>/<c>END</c> lines are kept as ordinary properties.</summary>
    DepthLimitExceeded,

    /// <summary>More components than <see cref="CalendarLimits.MaxComponents"/>. Further <c>BEGIN</c>/<c>END</c> lines are kept as ordinary properties.</summary>
    ComponentLimitExceeded,

    /// <summary>More properties than <see cref="CalendarLimits.MaxProperties"/>. The rest of the input is kept as one malformed block.</summary>
    PropertyLimitExceeded,

    /// <summary>The input is longer than <see cref="CalendarLimits.MaxTotalBytes"/>. The rest is kept as one malformed block.</summary>
    TotalSizeLimitExceeded,
}

/// <summary>A repair, guess or refusal reported while reading content lines or building a component tree.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Offset">Absolute byte offset in the input where the defect was found.</param>
/// <param name="Length">Bytes involved, or zero when the defect is a position.</param>
/// <param name="Depth">Component nesting depth where it happened (the document itself is 0).</param>
public readonly record struct CalendarDefect(CalendarDefectKind Kind, long Offset, int Length, int Depth)
{
    /// <summary>True for the <c>Ambiguous*</c> kinds, where clients disagree and the library took a documented reading.</summary>
    public bool IsAmbiguity => Kind is CalendarDefectKind.AmbiguousNonUtf8Text or CalendarDefectKind.AmbiguousCaretEncoding;

    /// <summary>True for the kinds that report a resource limit being reached.</summary>
    public bool IsLimit => Kind >= CalendarDefectKind.LineTooLong;

    /// <summary>A one-line description of the kind, in plain words.</summary>
    public string Description => Describe(Kind);

    /// <summary>A one-line description of a defect kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The description.</returns>
    public static string Describe(CalendarDefectKind kind) => kind switch
    {
        CalendarDefectKind.BareLineFeed => "A line ends with a bare line feed.",
        CalendarDefectKind.BareCarriageReturn => "A line ends with a bare carriage return.",
        CalendarDefectKind.OrphanContinuation => "A folded line has no line to continue.",
        CalendarDefectKind.MissingColon => "A line has no colon.",
        CalendarDefectKind.EmptyName => "A content line has no name.",
        CalendarDefectKind.InvalidNameCharacter => "A name contains an unexpected character.",
        CalendarDefectKind.WhitespaceAroundDelimiter => "Whitespace next to a delimiter was ignored.",
        CalendarDefectKind.UnterminatedQuote => "A quoted parameter value is never closed.",
        CalendarDefectKind.EmptyParameterName => "A parameter has an empty name.",
        CalendarDefectKind.QuotedPrintableSoftBreak => "Quoted-printable lines were joined at a soft break.",
        CalendarDefectKind.Base64ContinuationWithoutFold => "Base64 lines without indentation were joined.",
        CalendarDefectKind.ByteOrderMark => "The file starts with a byte order mark.",
        CalendarDefectKind.AmbiguousNonUtf8Text => "A line is not valid UTF-8 and was read as Windows-1252.",
        CalendarDefectKind.UnknownCharset => "A CHARSET parameter names an unsupported charset.",
        CalendarDefectKind.AmbiguousCaretEncoding => "A parameter value uses RFC 6868 caret encoding.",
        CalendarDefectKind.BeginWithoutName => "A BEGIN line has no component name.",
        CalendarDefectKind.UnmatchedEnd => "An END line matches no open component.",
        CalendarDefectKind.MissingEnd => "A component is not closed.",
        CalendarDefectKind.LineTooLong => "A line is longer than the limit.",
        CalendarDefectKind.DepthLimitExceeded => "Components nest deeper than the limit.",
        CalendarDefectKind.ComponentLimitExceeded => "There are more components than the limit.",
        CalendarDefectKind.PropertyLimitExceeded => "There are more properties than the limit.",
        CalendarDefectKind.TotalSizeLimitExceeded => "The input is longer than the limit.",
        _ => "No defect.",
    };
}

/// <summary>
/// Resource limits for reading. They are on by default and protect against hostile input; a limit never loses bytes,
/// because whatever is cut from the parsed view stays in the untouched source and is written back as it was.
/// </summary>
public sealed record CalendarLimits
{
    /// <summary>The default limits.</summary>
    public static CalendarLimits Default { get; } = new();

    /// <summary>The longest logical line (after unfolding) whose value is parsed, in bytes. Default 16 MiB, enough for large inline photos.</summary>
    public int MaxLineBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>The deepest component nesting. Default 32 (real files nest 3 or 4 deep).</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>The most components in one document. Default 1,000,000.</summary>
    public int MaxComponents { get; init; } = 1_000_000;

    /// <summary>The most properties (content lines) in one document. Default 8,000,000.</summary>
    public int MaxProperties { get; init; } = 8_000_000;

    /// <summary>The most bytes parsed from one document. Default 512 MiB.</summary>
    public int MaxTotalBytes { get; init; } = 512 * 1024 * 1024;
}
