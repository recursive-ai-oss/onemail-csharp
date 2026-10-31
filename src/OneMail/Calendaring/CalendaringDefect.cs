namespace RecursiveAI.OneMail.Calendaring;

/// <summary>What a reader of Outlook calendar and task structures repaired or could not use. Every kind carries the byte offset in the structure where it was found.</summary>
public enum CalendaringDefectKind
{
    /// <summary>The structure ends before a field it needs; what was read before that point is kept.</summary>
    Truncated = 0,

    /// <summary>A reader or writer version field holds a value MS-OXOCAL does not define; the structure is read as if it were the defined one.</summary>
    UnexpectedVersion,

    /// <summary>A count or length field is larger than the bytes that remain, or larger than the limit; the list is cut to what fits.</summary>
    CountOutOfRange,

    /// <summary>A field holds a value outside the defined set (frequency, pattern type, end type, weekday mask...).</summary>
    ValueOutOfRange,

    /// <summary>Bytes remain after the structure's last field; they are ignored.</summary>
    TrailingBytes,

    /// <summary>PidLidTimeZoneStruct and the effective time zone rule of PidLidAppointmentTimeZoneDefinitionRecur disagree (MS-OXOCAL 2.2.1.41); the struct is used.</summary>
    InconsistentTimeZone,

    /// <summary>The recurrence uses a calendar other than Gregorian (Hijri, Hebrew...); it cannot be expanded and is reported, not guessed.</summary>
    UnsupportedCalendar,

    /// <summary>The recurrence's stated end (EndDate or OccurrenceCount) does not agree with its pattern; the count wins, else the date.</summary>
    EndMismatch,

    /// <summary>A modified instance has no matching exception attachment; the ExceptionInfo fields alone describe it.</summary>
    ExceptionWithoutAttachment,

    /// <summary>An exception attachment matches no ExceptionInfo; it is kept as an attachment and not applied.</summary>
    AttachmentWithoutException,

    /// <summary>The start of the first instance is not a day the pattern allows; the first allowed day is used (the way the sources agree: MS-OXOCAL 2.2.1.44.1.2).</summary>
    AmbiguousFirstInstance,

    /// <summary>A monthly day of 29 or 30 falls on the last day of shorter months in Outlook but is skipped in iCalendar; the iCalendar form is written and the difference reported.</summary>
    AmbiguousShortMonth,

    /// <summary>The item holds no usable start or end time.</summary>
    MissingTime,
}

/// <summary>One repair or unusable value found while reading an Outlook calendar or task structure.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Offset">Byte offset in the structure, or -1 when the defect concerns the item as a whole.</param>
/// <param name="Detail">A short plain-text explanation naming the field.</param>
public readonly record struct CalendaringDefect(CalendaringDefectKind Kind, int Offset, string Detail)
{
    /// <summary>True for the <c>Ambiguous*</c> kinds: the input is readable, but clients disagree on its meaning and one documented resolution was applied.</summary>
    public bool IsAmbiguity => Kind is CalendaringDefectKind.AmbiguousFirstInstance or CalendaringDefectKind.AmbiguousShortMonth;
}

/// <summary>Limits on what the structure readers accept, so a hostile blob cannot make a reader allocate without bound.</summary>
public sealed record CalendaringLimits
{
    /// <summary>The defaults: 1,024 time zone rules (the maximum MS-OXOCAL 2.2.1.41 allows), 100,000 deleted or modified instances, 4,096 exceptions.</summary>
    public static CalendaringLimits Default { get; } = new();

    /// <summary>Most TZRule structures read from one definition. Default 1,024.</summary>
    public int MaxTimeZoneRules { get; init; } = 1024;

    /// <summary>Most deleted or modified instance dates read from one pattern. Default 100,000.</summary>
    public int MaxInstanceDates { get; init; } = 100_000;

    /// <summary>Most exceptions read from one appointment pattern. Default 4,096.</summary>
    public int MaxExceptions { get; init; } = 4096;
}
