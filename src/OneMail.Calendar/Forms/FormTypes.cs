using System.Text;

namespace RecursiveAI.OneMail.Calendar.Forms;

/// <summary>What was wrong with a jCal, jCard, xCal or xCard document. Readers never throw on content: they repair or skip and report one of these.</summary>
public enum FormDefectKind
{
    /// <summary>No defect.</summary>
    None = 0,

    /// <summary>The input is not well-formed JSON or XML. Nothing after the error is read.</summary>
    NotWellFormed,

    /// <summary>The root is not a jCal/jCard component (or an array of them), <c>icalendar</c> or <c>vcards</c>. Nothing is read.</summary>
    UnrecognizedRoot,

    /// <summary>A component, property or parameter has the wrong JSON shape (for example a property array shorter than four elements). It is skipped.</summary>
    MalformedStructure,

    /// <summary>A value does not have the shape its type calls for (a number where a date should be). Its text is taken as it stands.</summary>
    ValueShape,

    /// <summary>A parameter value is not a string or an array of strings. Its text is taken as it stands.</summary>
    ParameterValueNotString,

    /// <summary>An object repeats a key. Both are kept for parameters; the later wins for recurrence parts.</summary>
    DuplicateKey,

    /// <summary>A property element has no value element (xCal written without the type element). Its text is the value, as the default type.</summary>
    MissingValueElement,

    /// <summary>A property element holds value elements of different types. The first type is used and the others are read as text.</summary>
    MixedValueTypes,

    /// <summary>An element or attribute from another namespace sits where it should not. XML elements under properties become an <c>XML</c> property; others are ignored.</summary>
    ForeignContent,

    /// <summary>A DOCTYPE was found. DTDs are never processed; the document is refused.</summary>
    DtdProhibited,

    /// <summary>The input is longer than <see cref="FormLimits.MaxInputBytes"/>. Nothing is read.</summary>
    InputTooLarge,

    /// <summary>Nesting is deeper than <see cref="FormLimits.MaxDepth"/>. Nothing after the point is read.</summary>
    DepthLimitExceeded,

    /// <summary>More than <see cref="FormLimits.MaxNodes"/> JSON values or XML elements. Nothing after the point is read.</summary>
    NodeLimitExceeded,

    /// <summary>A string is longer than <see cref="FormLimits.MaxStringChars"/>. It is replaced by an empty string.</summary>
    StringTooLong,

    /// <summary>A value contained a line break that cannot be written on a content line; it was written as the <c>\n</c> escape.</summary>
    LineBreakEscaped,
}

/// <summary>
/// One defect found while reading JSON or XML.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Offset">Byte offset of the JSON token, or -1 for XML.</param>
/// <param name="Line">Line of the XML node (1-based), or 0 for JSON.</param>
/// <param name="Column">Column of the XML node (1-based), or 0 for JSON.</param>
/// <param name="Path">The component, property or element names leading to it, for example <c>vcalendar/vevent/dtstart</c>.</param>
public readonly record struct FormDefect(FormDefectKind Kind, long Offset, int Line, int Column, string Path)
{
    /// <summary>True for the kinds that report a resource limit being reached.</summary>
    public bool IsLimit => Kind is FormDefectKind.InputTooLarge or FormDefectKind.DepthLimitExceeded or FormDefectKind.NodeLimitExceeded or FormDefectKind.StringTooLong;
}

/// <summary>What a conversion to JSON or XML could not carry. The forms preserve the data, not the text it came from; each kind says which part of the text is gone.</summary>
public enum FormLossKind
{
    /// <summary>No loss.</summary>
    None = 0,

    /// <summary>Line folding. Counted once per document (RFC 7265 §3.1, RFC 7095 §3.1); writing folds again at 75 octets.</summary>
    Folding,

    /// <summary>A component, property or parameter name was not upper-case. The forms use lower case and the way back writes upper case (RFC 7265 §3.3, §3.4).</summary>
    NameCase,

    /// <summary>The order of properties against nested components. The forms list properties first and components after.</summary>
    ItemOrder,

    /// <summary>A blank line, a malformed line, an unmatched <c>END</c> or a stray line. The forms have nowhere to put it.</summary>
    LineNotCarried,

    /// <summary>A <c>VALUE</c> parameter equal to the property's default type. The type is carried by the form itself (RFC 7265 §3.5.1).</summary>
    RedundantValueParameter,

    /// <summary>A parameter was given twice; the values were merged into one.</summary>
    DuplicateParameterMerged,

    /// <summary>A <c>CHARSET</c> parameter (vCard 2.1). The value was decoded; JSON and XML are always Unicode.</summary>
    CharsetRemoved,

    /// <summary>A quoted-printable or base64 transfer encoding on a value that is not binary. The value was decoded and the parameter removed (RFC 7265 §3.1).</summary>
    TransferEncodingRemoved,

    /// <summary>A bare vCard 2.1 parameter such as <c>HOME</c> was written as <c>TYPE=HOME</c>.</summary>
    BareParameterAsType,

    /// <summary>Inline vCard 2.1/3.0 binary (PHOTO, LOGO, SOUND, KEY) was written as a <c>data:</c> URI, because jCard and xCard have no binary type.</summary>
    InlineBinaryAsDataUri,

    /// <summary>The value does not parse as its declared type. It was carried as an <c>unknown</c> value and the <c>VALUE</c> parameter was dropped (RFC 7265 §5.1).</summary>
    ValueNotRepresentable,

    /// <summary>A structured value had fewer components than the property defines; the missing ones were written as empty strings.</summary>
    StructuredPadded,

    /// <summary>A structured value had more components than xCard can name; the extra ones were dropped.</summary>
    ExtraComponentsDropped,

    /// <summary>A vCard that is not version 4.0 was written as xCard, which has no VERSION property. Reading it back gives version 4.0.</summary>
    VersionNotCarried,

    /// <summary>A property group on iCalendar data. xCal and jCal have no group construct for iCalendar.</summary>
    GroupNotCarried,

    /// <summary>A top-level component the form cannot hold (a VCARD in xCal, a VCALENDAR in xCard). It was left out.</summary>
    ComponentNotCarried,

    /// <summary>A character that XML cannot hold was replaced by U+FFFD.</summary>
    InvalidXmlCharacter,

    /// <summary>A value was changed to a different but equivalent version-specific form (vCard 3.0 GEO as a geo URI in xCard).</summary>
    VersionSpecificForm,

    /// <summary>The value type was chosen by a documented reading of data that does not follow the RFC: base64 without VALUE=BINARY is binary, a DATE-TIME property holding only a date is a date.</summary>
    AmbiguousValueType,
}

/// <summary>One thing a conversion could not carry, with where it was.</summary>
/// <param name="Kind">What was lost.</param>
/// <param name="Path">The component path and property name, for example <c>vcard/tel</c>; empty for document-wide kinds.</param>
/// <param name="Count">How many times it happened at this path (document-wide kinds are counted once for the whole document).</param>
public readonly record struct FormLoss(FormLossKind Kind, string Path, int Count)
{
    /// <inheritdoc />
    public override string ToString() => Kind + (Path.Length == 0 ? string.Empty : " at " + Path) + (Count > 1 ? " x" + Count : string.Empty);
}

/// <summary>Resource limits for reading JSON and XML forms. They are on by default.</summary>
public sealed record FormLimits
{
    /// <summary>The default limits.</summary>
    public static FormLimits Default { get; } = new();

    /// <summary>Longest input in bytes. Default 256 MiB.</summary>
    public int MaxInputBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>
    /// Deepest nesting of JSON arrays/objects or XML elements. Default 128: the content-line parser allows 32 nested components,
    /// and each takes up to three levels in either form, so everything the library writes it reads back.
    /// </summary>
    public int MaxDepth { get; init; } = 128;

    /// <summary>Most JSON values or XML elements in a document. Default 16,000,000.</summary>
    public int MaxNodes { get; init; } = 16_000_000;

    /// <summary>Longest string, in UTF-16 characters. Default 16,777,216 (enough for large inline photos).</summary>
    public int MaxStringChars { get; init; } = 16 * 1024 * 1024;
}

/// <summary>The result of reading a JSON or XML form: the lossless tree, and what was wrong.</summary>
public sealed class FormReadResult
{
    internal FormReadResult(ContentDocument document, List<FormDefect> defects)
    {
        Document = document;
        Defects = defects;
    }

    /// <summary>The components read, as a content-line document that writes out as iCalendar or vCard text. Lines are written from their parts; the document never has source bytes.</summary>
    public ContentDocument Document { get; }

    /// <summary>What was repaired or skipped, in input order.</summary>
    public IReadOnlyList<FormDefect> Defects { get; }
}

/// <summary>The result of writing a JSON or XML form: the bytes, and what could not be carried.</summary>
public sealed class FormWriteResult
{
    internal FormWriteResult(byte[] bytes, List<FormLoss> losses)
    {
        Bytes = bytes;
        Losses = losses;
    }

    /// <summary>The UTF-8 bytes of the JSON or XML document.</summary>
    public byte[] Bytes { get; }

    /// <summary>The document as a string.</summary>
    public string Text => Encoding.UTF8.GetString(Bytes);

    /// <summary>Everything the conversion could not carry, aggregated per kind and path. Empty means nothing but folding and name case was lost... and those are listed too, so empty means lossless.</summary>
    public IReadOnlyList<FormLoss> Losses { get; }
}
