using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.TimeZones;
using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Calendaring;

/// <summary>What an item of the calendar family is (MS-OXOCAL 2.2.2 to 2.2.10).</summary>
public enum OutlookAppointmentKind
{
    /// <summary>An appointment or meeting in a calendar folder (<c>IPM.Appointment</c>).</summary>
    Appointment = 0,

    /// <summary>A meeting request or update (<c>IPM.Schedule.Meeting.Request</c>).</summary>
    MeetingRequest,

    /// <summary>An acceptance (<c>IPM.Schedule.Meeting.Resp.Pos</c>).</summary>
    MeetingResponseAccepted,

    /// <summary>A tentative acceptance (<c>.Resp.Tent</c>).</summary>
    MeetingResponseTentative,

    /// <summary>A decline (<c>.Resp.Neg</c>).</summary>
    MeetingResponseDeclined,

    /// <summary>A cancellation (<c>IPM.Schedule.Meeting.Canceled</c>).</summary>
    MeetingCancellation,

    /// <summary>A forward notification (<c>IPM.Schedule.Meeting.Notification.Forward</c>).</summary>
    MeetingForwardNotification,

    /// <summary>The message embedded in an exception attachment of a recurring series (<c>IPM.OLE.CLASS.{00061055-0000-0000-C000-000000000046}</c>).</summary>
    Exception,
}

/// <summary>PidLidBusyStatus values (MS-OXOCAL 2.2.1.2).</summary>
public enum OutlookBusyStatus
{
    /// <summary>olFree.</summary>
    Free = 0,

    /// <summary>olTentative.</summary>
    Tentative = 1,

    /// <summary>olBusy.</summary>
    Busy = 2,

    /// <summary>olOutOfOffice.</summary>
    OutOfOffice = 3,

    /// <summary>olWorkingElsewhere.</summary>
    WorkingElsewhere = 4,
}

/// <summary>Response values (MS-OXOCAL 2.2.1.11): PidLidResponseStatus and PidTagRecipientTrackStatus.</summary>
public enum OutlookResponse
{
    /// <summary>respNone.</summary>
    None = 0,

    /// <summary>respOrganized.</summary>
    Organized = 1,

    /// <summary>respTentative.</summary>
    Tentative = 2,

    /// <summary>respAccepted.</summary>
    Accepted = 3,

    /// <summary>respDeclined.</summary>
    Declined = 4,

    /// <summary>respNotResponded.</summary>
    NotResponded = 5,
}

/// <summary>What an attendee is to the meeting, from PidTagRecipientType (MS-OXOCAL 2.2.4.10.7).</summary>
public enum OutlookAttendeeRole
{
    /// <summary>Recipient type To.</summary>
    Required = 0,

    /// <summary>Recipient type Cc.</summary>
    Optional,

    /// <summary>Recipient type Bcc: a resource such as a room.</summary>
    Resource,
}

/// <summary>One row of a meeting's recipient table.</summary>
/// <param name="Name">PidTagDisplayName.</param>
/// <param name="Address">The SMTP address: PidTagSmtpAddress, else PidTagEmailAddress when the address type is SMTP; null otherwise.</param>
/// <param name="Role">From PidTagRecipientType.</param>
/// <param name="Response">PidTagRecipientTrackStatus (<see cref="OutlookResponse.None"/> when unset).</param>
/// <param name="ResponseTime">PidTagRecipientTrackStatusTime, UTC.</param>
/// <param name="IsOrganizer">The recipientOrganizer flag (0x2) of PidTagRecipientFlags.</param>
/// <param name="IsDeleted">The exceptional-deleted flag (0x20): the row exists but counts as absent.</param>
public sealed record OutlookAttendee(string? Name, string? Address, OutlookAttendeeRole Role, OutlookResponse Response, DateTimeOffset? ResponseTime, bool IsOrganizer, bool IsDeleted);

/// <summary>A time zone of an Outlook item, ready for the <c>.Calendar</c> layer to convert with.</summary>
public sealed class OutlookTimeZone
{
    internal OutlookTimeZone(string id, TimeZoneDefinition? definition, TzRule rule, IReadOnlyList<TzRule> rules)
    {
        Id = id;
        Definition = definition;
        Rule = rule;
        VTimeZone = TimeZoneBuilder.Build(id, rules);
        Component cal = new("VCALENDAR");
        cal.Add(VTimeZone);
        Zone = new TimeZoneResolver(new IcsComponent(cal)).Resolve(id) ?? throw new InvalidOperationException("The generated VTIMEZONE could not be read back.");
    }

    /// <summary>The TZID used in iCalendar output: the Windows key name when the item has one, else its description, else a name made from the offset.</summary>
    public string Id { get; }

    /// <summary>The TZDEFINITION the zone came from, or null when only a PidLidTimeZoneStruct existed.</summary>
    public TimeZoneDefinition? Definition { get; }

    /// <summary>The rule in force (the effective rule of the definition, or the struct's single rule).</summary>
    public TzRule Rule { get; }

    /// <summary>The VTIMEZONE describing the zone.</summary>
    public Component VTimeZone { get; }

    /// <summary>The zone as the <c>.Calendar</c> layer reads it.</summary>
    public IcsTimeZone Zone { get; }
}

/// <summary>A modified instance of a recurring series: what the series' blob says and the exception message that holds the rest.</summary>
/// <param name="Info">The ExceptionInfo/ExtendedException fields.</param>
/// <param name="Attachment">The exception attachment (an embedded message), or null when the blob is all there is.</param>
/// <param name="Message">The embedded exception message, or null.</param>
public sealed record OutlookModifiedInstance(ExceptionInfo Info, MapiAttachment? Attachment, MapiMessage? Message)
{
    /// <summary>The instance's original start (local time of the series' zone).</summary>
    public DateTime OriginalStart => OutlookMinutes.ToDateTime(Info.OriginalStartTime);

    /// <summary>The instance's new start (local).</summary>
    public DateTime Start => OutlookMinutes.ToDateTime(Info.StartDateTime);

    /// <summary>The instance's new end (local).</summary>
    public DateTime End => OutlookMinutes.ToDateTime(Info.EndDateTime);
}

/// <summary>
/// Typed access to an Outlook appointment, meeting request, response, cancellation, forward notification or exception message
/// (MS-OXOCAL). It reads the item's property bag and never changes it; nothing here throws on malformed content, and what was repaired is in <see cref="Defects"/>.
/// </summary>
public sealed class OutlookAppointment
{
    private readonly List<CalendaringDefect> _defects = [];
    private readonly CalendaringLimits _limits;
    private AppointmentRecurrencePattern? _recur;
    private bool _recurRead;
    private OutlookTimeZone? _zone;
    private bool _zoneRead;
    private IReadOnlyList<OutlookAttendee>? _attendees;
    private IReadOnlyList<OutlookModifiedInstance>? _exceptions;

    private OutlookAppointment(MapiMessage message, OutlookAppointmentKind kind, CalendaringLimits limits)
    {
        Message = message;
        Kind = kind;
        _limits = limits;
    }

    /// <summary>The message class prefix of an exception message (MS-OXOCAL 2.2.10.2.1).</summary>
    public const string ExceptionMessageClass = "IPM.OLE.CLASS.{00061055-0000-0000-C000-000000000046}";

    /// <summary>Wraps a message if its class belongs to the calendar family.</summary>
    /// <param name="message">A MAPI message from an MSG, a PST/OST, TNEF or the MIME conversion.</param>
    /// <param name="limits">Bounds for the blobs; default <see cref="CalendaringLimits.Default"/>.</param>
    /// <returns>The view, or null when the class is not <c>IPM.Appointment</c>, <c>IPM.Schedule.Meeting.*</c> or an exception message.</returns>
    public static OutlookAppointment? TryCreate(MapiMessage message, CalendaringLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        OutlookAppointmentKind? k = KindOf(message.MessageClass);
        return k is { } kind ? new OutlookAppointment(message, kind, limits ?? CalendaringLimits.Default) : null;
    }

    /// <summary>The calendar-family kind a message class stands for.</summary>
    /// <param name="messageClass">PidTagMessageClass.</param>
    /// <returns>The kind, or null for any other class. Matching is case-insensitive and on whole dot-separated segments.</returns>
    public static OutlookAppointmentKind? KindOf(string? messageClass)
    {
        if (messageClass is null)
        {
            return null;
        }

        if (Is(messageClass, "IPM.Appointment"))
        {
            return OutlookAppointmentKind.Appointment;
        }

        if (Is(messageClass, "IPM.Schedule.Meeting.Request"))
        {
            return OutlookAppointmentKind.MeetingRequest;
        }

        if (Is(messageClass, "IPM.Schedule.Meeting.Resp.Pos"))
        {
            return OutlookAppointmentKind.MeetingResponseAccepted;
        }

        if (Is(messageClass, "IPM.Schedule.Meeting.Resp.Tent"))
        {
            return OutlookAppointmentKind.MeetingResponseTentative;
        }

        if (Is(messageClass, "IPM.Schedule.Meeting.Resp.Neg"))
        {
            return OutlookAppointmentKind.MeetingResponseDeclined;
        }

        if (Is(messageClass, "IPM.Schedule.Meeting.Canceled"))
        {
            return OutlookAppointmentKind.MeetingCancellation;
        }

        if (Is(messageClass, "IPM.Schedule.Meeting.Notification.Forward"))
        {
            return OutlookAppointmentKind.MeetingForwardNotification;
        }

        return string.Equals(messageClass, ExceptionMessageClass, StringComparison.OrdinalIgnoreCase) ? OutlookAppointmentKind.Exception : null;
    }

    private static bool Is(string cls, string prefix) =>
        cls.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (cls.Length == prefix.Length || cls[prefix.Length] == '.');

    /// <summary>The message this view reads.</summary>
    public MapiMessage Message { get; }

    /// <summary>The kind of item.</summary>
    public OutlookAppointmentKind Kind { get; }

    /// <summary>True for every kind except a plain appointment and an exception message.</summary>
    public bool IsMeetingMessage => Kind is not (OutlookAppointmentKind.Appointment or OutlookAppointmentKind.Exception);

    /// <summary>True when the asfMeeting bit of PidLidAppointmentStateFlags is set or the item is a meeting message: other people take part.</summary>
    public bool IsMeeting => IsMeetingMessage || (StateFlags & 1) != 0;

    /// <summary>The repairs made while reading, accumulated as properties are used (read the properties first, then this).</summary>
    public IReadOnlyList<CalendaringDefect> Defects => _defects;

    private int? Int(MapiNamedProperty n) => Message.Properties.Get(n, MapiType.Integer32)?.AsInt32();

    private bool? Bool(MapiNamedProperty n) => Message.Properties.Get(n, MapiType.Boolean)?.AsBoolean();

    private DateTimeOffset? Time(MapiNamedProperty n) => Message.Properties.Get(n, MapiType.Time)?.AsTime();

    private string? Str(MapiNamedProperty n) => Message.Properties.Get(n)?.AsString(Message.String8Encoding);

    private byte[]? Bin(MapiNamedProperty n) => Message.Properties.Get(n, MapiType.Binary)?.GetBytes().ToArray();

    /// <summary>PidTagSubject.</summary>
    public string? Subject => Message.Subject;

    /// <summary>PidLidLocation, else PidLidWhere (meeting messages).</summary>
    public string? Location => Str(OutlookNames.Location) ?? Str(OutlookNames.Where);

    /// <summary>PidLidAppointmentStartWhole as a UTC instant (see <see cref="StartLocal"/> for how it reads on a clock).</summary>
    public DateTimeOffset? Start => Time(OutlookNames.StartWhole);

    /// <summary>PidLidAppointmentEndWhole as a UTC instant.</summary>
    public DateTimeOffset? End => Time(OutlookNames.EndWhole);

    /// <summary>PidLidAppointmentSubType: the user marked the event all-day.</summary>
    public bool IsAllDay => Bool(OutlookNames.SubType) == true;

    /// <summary>PidLidBusyStatus.</summary>
    public OutlookBusyStatus? BusyStatus => Int(OutlookNames.BusyStatus) is >= 0 and <= 4 and int v ? (OutlookBusyStatus)v : null;

    /// <summary>PidLidIntendedBusyStatus (meeting requests).</summary>
    public OutlookBusyStatus? IntendedBusyStatus => Int(OutlookNames.IntendedBusyStatus) is >= 0 and <= 4 and int v ? (OutlookBusyStatus)v : null;

    /// <summary>PidLidResponseStatus: the user's own response, or respOrganized on the organizer's copy.</summary>
    public OutlookResponse? ResponseStatus => Int(OutlookNames.ResponseStatus) is >= 0 and <= 5 and int v ? (OutlookResponse)v : null;

    /// <summary>PidTagSensitivity: 0 normal, 1 personal, 2 private, 3 confidential.</summary>
    public int? Sensitivity => Message.GetInt32(MapiPid.Sensitivity);

    /// <summary>PidTagImportance: 0 low, 1 normal, 2 high.</summary>
    public int? Importance => Message.GetInt32(MapiPid.Importance);

    /// <summary>PidLidAppointmentStateFlags: asfMeeting 0x1, asfReceived 0x2, asfCanceled 0x4.</summary>
    public int StateFlags => Int(OutlookNames.StateFlags) ?? 0;

    /// <summary>The asfCanceled bit.</summary>
    public bool IsCanceled => (StateFlags & 4) != 0 || Kind == OutlookAppointmentKind.MeetingCancellation;

    /// <summary>PidLidAppointmentSequence.</summary>
    public int? Sequence => Int(OutlookNames.AppointmentSequence);

    /// <summary>PidLidMeetingType on a meeting request: mtgRequest 0x1, mtgFull 0x10000, mtgInfo 0x20000, mtgOutOfDate 0x80000, mtgDelegatorCopy 0x100000.</summary>
    public int? MeetingType => Int(OutlookNames.MeetingType);

    /// <summary>True for a meeting request whose PidLidMeetingType says it updates an earlier one.</summary>
    public bool IsMeetingUpdate => Kind == OutlookAppointmentKind.MeetingRequest && MeetingType is { } t && (t & 0x30000) != 0;

    /// <summary>PidLidReminderSet.</summary>
    public bool ReminderSet => Bool(OutlookNames.ReminderSet) == true;

    /// <summary>PidLidReminderDelta in minutes (0x5AE980E1 means "the default, 15" in MS-OXCICAL and is returned as is).</summary>
    public int? ReminderMinutes => Int(OutlookNames.ReminderDelta);

    /// <summary>PidLidGlobalObjectId (MS-OXOCAL 2.2.1.27).</summary>
    public byte[]? GlobalObjectId => Bin(OutlookNames.GlobalObjectId);

    /// <summary>PidLidCleanGlobalObjectId (2.2.1.28): the same with the instance date zeroed.</summary>
    public byte[]? CleanGlobalObjectId => Bin(OutlookNames.CleanGlobalObjectId);

    /// <summary>PidLidRecurring: this item is a recurring series.</summary>
    public bool IsRecurring => Bool(OutlookNames.Recurring) == true || Message.Properties.Get(OutlookNames.AppointmentRecur, MapiType.Binary) is not null && Bool(OutlookNames.Recurring) != false;

    /// <summary>The organizer: PidTagSentRepresenting* of the item, else PidTagSender*, else the recipient flagged as organizer.</summary>
    public (string? Name, string? Address)? Organizer
    {
        get
        {
            string? name = Message.GetString(MapiPid.SentRepresentingName);
            string? addr = Smtp(MapiPid.SentRepresentingAddressType, MapiPid.SentRepresentingEmailAddress, MapiPid.SentRepresentingSmtpAddress);
            if (name is null && addr is null)
            {
                name = Message.GetString(MapiPid.SenderName);
                addr = Smtp(MapiPid.SenderAddressType, MapiPid.SenderEmailAddress, MapiPid.SenderSmtpAddress);
            }

            if (name is null && addr is null && Attendees.FirstOrDefault(a => a.IsOrganizer) is { } o)
            {
                return (o.Name, o.Address);
            }

            return name is null && addr is null ? null : (name, addr);
        }
    }

    private string? Smtp(ushort typeId, ushort addressId, ushort smtpId)
    {
        if (Message.GetString(smtpId) is { Length: > 0 } s)
        {
            return s;
        }

        return string.Equals(Message.GetString(typeId), "SMTP", StringComparison.OrdinalIgnoreCase) ? Message.GetString(addressId) : null;
    }

    /// <summary>The recipient table as attendees (MS-OXOCAL 2.2.4.10). Rows flagged as exceptionally deleted are included with <see cref="OutlookAttendee.IsDeleted"/> set.</summary>
    public IReadOnlyList<OutlookAttendee> Attendees => _attendees ??= Message.Recipients.Select(ToAttendee).ToList();

    private static OutlookAttendee ToAttendee(MapiRecipient r)
    {
        string? address = r.SmtpAddress is { Length: > 0 } s ? s : string.Equals(r.AddressType, "SMTP", StringComparison.OrdinalIgnoreCase) ? r.EmailAddress : null;
        int flags = r.GetInt32(MapiPid.RecipientFlags) ?? 0;
        int status = r.GetInt32(MapiPid.RecipientTrackStatus) ?? 0;
        OutlookAttendeeRole role = r.RecipientTypeValue switch { 2 => OutlookAttendeeRole.Optional, 3 => OutlookAttendeeRole.Resource, _ => OutlookAttendeeRole.Required };
        return new OutlookAttendee(r.DisplayName, address, role, status is >= 0 and <= 5 ? (OutlookResponse)status : OutlookResponse.None, r.GetTime(MapiPid.RecipientTrackStatusTime), (flags & 0x2) != 0, (flags & 0x20) != 0);
    }

    /// <summary>The recurrence blob (PidLidAppointmentRecur), parsed once.</summary>
    /// <returns>The pattern, or null when the item has none or it could not be read at all.</returns>
    public AppointmentRecurrencePattern? Recurrence
    {
        get
        {
            if (!_recurRead)
            {
                _recurRead = true;
                if (Message.Properties.Get(OutlookNames.AppointmentRecur, MapiType.Binary) is { } p)
                {
                    if (!AppointmentRecurrencePattern.TryParse(p.GetBytes().Span, out _recur, _defects, _limits))
                    {
                        _recur = null;
                    }
                }
            }

            return _recur;
        }
    }

    /// <summary>
    /// The time zone the item's times belong to. A recurring series uses PidLidTimeZoneStruct (MS-OXOCAL 2.2.1.41: the struct wins when the
    /// recurrence definition disagrees); a single item uses the start-display definition; with neither, null and the times are UTC.
    /// </summary>
    public OutlookTimeZone? TimeZone
    {
        get
        {
            if (!_zoneRead)
            {
                _zoneRead = true;
                _zone = BuildZone();
            }

            return _zone;
        }
    }

    private TimeZoneDefinition? ReadDefinition(MapiNamedProperty n)
    {
        if (Message.Properties.Get(n, MapiType.Binary) is { } p && TimeZoneDefinition.TryParse(p.GetBytes().Span, out TimeZoneDefinition? d, _defects, _limits))
        {
            return d;
        }

        return null;
    }

    private OutlookTimeZone? BuildZone()
    {
        TzRule? structRule = null;
        if (Message.Properties.Get(OutlookNames.TimeZoneStruct, MapiType.Binary) is { } sp)
        {
            if (!TimeZoneStruct.TryParse(sp.GetBytes().Span, out structRule, _defects))
            {
                structRule = null;
            }
        }

        TimeZoneDefinition? recur = ReadDefinition(OutlookNames.TimeZoneDefinitionRecur);
        TimeZoneDefinition? display = ReadDefinition(OutlookNames.TimeZoneDefinitionStartDisplay);
        bool recurring = Recurrence is not null;
        string? description = Str(OutlookNames.TimeZoneDescription);
        if (recurring && structRule is not null)
        {
            if (recur?.Effective is { } er && !er.SameAs(structRule))
            {
                _defects.Add(new CalendaringDefect(CalendaringDefectKind.InconsistentTimeZone, -1, "PidLidAppointmentTimeZoneDefinitionRecur and PidLidTimeZoneStruct disagree; the struct is used"));
                recur = null;
            }

            string id = recur?.KeyName is { Length: > 0 } k ? k : NameFor(description, structRule);
            return new OutlookTimeZone(id, recur, structRule, [structRule]);
        }

        TimeZoneDefinition? def = display ?? recur;
        if (def?.Effective is { } eff && def.Rules.Count > 0)
        {
            return new OutlookTimeZone(def.KeyName.Length > 0 ? def.KeyName : NameFor(description, eff), def, eff, def.Rules);
        }

        return structRule is not null ? new OutlookTimeZone(NameFor(description, structRule), null, structRule, [structRule]) : null;
    }

    private static string NameFor(string? description, TzRule rule)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description.Trim();
        }

        TimeSpan o = rule.StandardOffset;
        return "UTC" + (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ":" + Math.Abs(o.Minutes).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The start as local wall-clock time. The clock is the item's <see cref="TimeZone"/> (UTC when there is none); an all-day event of a non-meeting is floating (MS-OXOCAL 3.1.5.5.1).</summary>
    public DateTime? StartLocal => Local(Start);

    /// <summary>The end as local wall-clock time; see <see cref="StartLocal"/>.</summary>
    public DateTime? EndLocal => Local(End);

    private DateTime? Local(DateTimeOffset? utc) => utc is { } u ? (TimeZone?.Zone.ToLocal(u.UtcDateTime) ?? DateTime.SpecifyKind(u.UtcDateTime, DateTimeKind.Unspecified)) : null;

    /// <summary>The modified instances of a recurring series with their exception attachments, in the order of the blob.</summary>
    /// <returns>Empty for a single item.</returns>
    public IReadOnlyList<OutlookModifiedInstance> Exceptions => _exceptions ??= BuildExceptions();

    private List<OutlookModifiedInstance> BuildExceptions()
    {
        List<OutlookModifiedInstance> list = [];
        AppointmentRecurrencePattern? r = Recurrence;
        if (r is null)
        {
            return list;
        }

        List<(MapiAttachment A, MapiMessage M)> candidates = [];
        foreach (MapiAttachment a in Message.Attachments)
        {
            if (a.AttachMethod == MapiAttachMethod.EmbeddedMessage && a.EmbeddedMessage is { } m)
            {
                candidates.Add((a, m));
            }
        }

        List<(MapiAttachment A, MapiMessage M)> unused = [.. candidates];
        foreach (ExceptionInfo info in r.Exceptions)
        {
            (MapiAttachment A, MapiMessage M)? hit = null;
            foreach ((MapiAttachment A, MapiMessage M) c in unused)
            {
                // PidTagExceptionStartTime is the new start as local time stored in a PtypTime (MS-OXOCAL 2.2.10.1.4).
                DateTimeOffset? s = c.A.Properties.Get(OutlookNames.PidTagExceptionStartTime, MapiType.Time)?.AsTime();
                if (s is { } sv && OutlookMinutes.FromDateTime(sv.UtcDateTime) == info.StartDateTime)
                {
                    hit = c;
                    break;
                }
            }

            if (hit is null && unused.Count > 0 && candidates.Count == r.Exceptions.Count)
            {
                hit = unused[0];
            }

            if (hit is { } h)
            {
                unused.Remove(h);
                list.Add(new OutlookModifiedInstance(info, h.A, h.M));
            }
            else
            {
                _defects.Add(new CalendaringDefect(CalendaringDefectKind.ExceptionWithoutAttachment, -1, $"no exception attachment for the instance at minute {info.OriginalStartTime}"));
                list.Add(new OutlookModifiedInstance(info, null, null));
            }
        }

        foreach ((MapiAttachment A, MapiMessage M) c in unused)
        {
            if (OutlookAppointment.KindOf(c.M.MessageClass) == OutlookAppointmentKind.Exception)
            {
                _defects.Add(new CalendaringDefect(CalendaringDefectKind.AttachmentWithoutException, -1, "an exception attachment matches no ExceptionInfo"));
            }
        }

        return list;
    }

    /// <summary>The days the series skips: DeletedInstanceDates that are not modified instances.</summary>
    /// <returns>Midnight of each deleted day, in the series' local time.</returns>
    public IReadOnlyList<DateTime> DeletedInstances
    {
        get
        {
            AppointmentRecurrencePattern? r = Recurrence;
            if (r is null)
            {
                return [];
            }

            HashSet<uint> moved = [.. r.Exceptions.Select(e => (uint)(e.OriginalStartTime - (e.OriginalStartTime % 1440)))];
            return r.Pattern.DeletedInstanceDates.Where(d => !moved.Contains(d - (d % 1440))).Select(OutlookMinutes.ToDateTime).ToList();
        }
    }
}
