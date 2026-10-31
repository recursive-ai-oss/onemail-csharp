using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Calendaring;

/// <summary>The named properties of MS-OXOCAL, MS-OXOTASK and MS-OXORMDR that the calendar and task views read (names and LIDs from MS-OXPROPS).</summary>
public static class OutlookNames
{
    private static MapiNamedProperty Appt(uint lid) => new(MapiPropertySets.Appointment, lid);

    private static MapiNamedProperty Mtg(uint lid) => new(MapiPropertySets.Meeting, lid);

    private static MapiNamedProperty Cmn(uint lid) => new(MapiPropertySets.Common, lid);

    private static MapiNamedProperty Tsk(uint lid) => new(MapiPropertySets.Task, lid);

    /// <summary>PidLidNetShowUrl: the URL launched to join the online meeting (MS-OXOCAL 2.2.1.56.8; ICS <c>X-MS-OLK-NETSHOWURL</c>).</summary>
    public static readonly MapiNamedProperty NetShowUrl = Appt(0x8248);

    /// <summary>PidLidAppointmentSequence.</summary>
    public static readonly MapiNamedProperty AppointmentSequence = Appt(0x8201);

    /// <summary>PidLidAppointmentSequenceTime.</summary>
    public static readonly MapiNamedProperty AppointmentSequenceTime = Appt(0x8202);

    /// <summary>PidLidChangeHighlight.</summary>
    public static readonly MapiNamedProperty ChangeHighlight = Appt(0x8204);

    /// <summary>PidLidBusyStatus.</summary>
    public static readonly MapiNamedProperty BusyStatus = Appt(0x8205);

    /// <summary>PidLidAppointmentAuxiliaryFlags.</summary>
    public static readonly MapiNamedProperty AppointmentAuxiliaryFlags = Appt(0x8207);

    /// <summary>PidLidLocation.</summary>
    public static readonly MapiNamedProperty Location = Appt(0x8208);

    /// <summary>PidLidAppointmentStartWhole.</summary>
    public static readonly MapiNamedProperty StartWhole = Appt(0x820D);

    /// <summary>PidLidAppointmentEndWhole.</summary>
    public static readonly MapiNamedProperty EndWhole = Appt(0x820E);

    /// <summary>PidLidAppointmentDuration.</summary>
    public static readonly MapiNamedProperty Duration = Appt(0x8213);

    /// <summary>PidLidAppointmentSubType (true for an all-day event).</summary>
    public static readonly MapiNamedProperty SubType = Appt(0x8215);

    /// <summary>PidLidAppointmentRecur.</summary>
    public static readonly MapiNamedProperty AppointmentRecur = Appt(0x8216);

    /// <summary>PidLidAppointmentStateFlags.</summary>
    public static readonly MapiNamedProperty StateFlags = Appt(0x8217);

    /// <summary>PidLidResponseStatus.</summary>
    public static readonly MapiNamedProperty ResponseStatus = Appt(0x8218);

    /// <summary>PidLidAppointmentReplyTime.</summary>
    public static readonly MapiNamedProperty ReplyTime = Appt(0x8220);

    /// <summary>PidLidRecurring.</summary>
    public static readonly MapiNamedProperty Recurring = Appt(0x8223);

    /// <summary>PidLidIntendedBusyStatus.</summary>
    public static readonly MapiNamedProperty IntendedBusyStatus = Appt(0x8224);

    /// <summary>PidLidExceptionReplaceTime.</summary>
    public static readonly MapiNamedProperty ExceptionReplaceTime = Appt(0x8228);

    /// <summary>PidLidTimeZoneStruct.</summary>
    public static readonly MapiNamedProperty TimeZoneStruct = Appt(0x8233);

    /// <summary>PidLidTimeZoneDescription.</summary>
    public static readonly MapiNamedProperty TimeZoneDescription = Appt(0x8234);

    /// <summary>PidLidClipStart.</summary>
    public static readonly MapiNamedProperty ClipStart = Appt(0x8235);

    /// <summary>PidLidClipEnd.</summary>
    public static readonly MapiNamedProperty ClipEnd = Appt(0x8236);

    /// <summary>PidLidAllAttendeesString (LID 0x8238 in MS-OXPROPS).</summary>
    public static readonly MapiNamedProperty AllAttendeesString = Appt(0x8238);

    /// <summary>PidLidToAttendeesString.</summary>
    public static readonly MapiNamedProperty ToAttendeesString = Appt(0x823B);

    /// <summary>PidLidCcAttendeesString.</summary>
    public static readonly MapiNamedProperty CcAttendeesString = Appt(0x823C);

    /// <summary>PidLidAppointmentProposedStartWhole.</summary>
    public static readonly MapiNamedProperty ProposedStartWhole = Appt(0x8250);

    /// <summary>PidLidAppointmentProposedEndWhole.</summary>
    public static readonly MapiNamedProperty ProposedEndWhole = Appt(0x8251);

    /// <summary>PidLidAppointmentTimeZoneDefinitionStartDisplay.</summary>
    public static readonly MapiNamedProperty TimeZoneDefinitionStartDisplay = Appt(0x825E);

    /// <summary>PidLidAppointmentTimeZoneDefinitionEndDisplay.</summary>
    public static readonly MapiNamedProperty TimeZoneDefinitionEndDisplay = Appt(0x825F);

    /// <summary>PidLidAppointmentTimeZoneDefinitionRecur.</summary>
    public static readonly MapiNamedProperty TimeZoneDefinitionRecur = Appt(0x8260);

    /// <summary>PidLidWhere.</summary>
    public static readonly MapiNamedProperty Where = Mtg(0x0002);

    /// <summary>PidLidGlobalObjectId.</summary>
    public static readonly MapiNamedProperty GlobalObjectId = Mtg(0x0003);

    /// <summary>PidLidIsRecurring.</summary>
    public static readonly MapiNamedProperty IsRecurring = Mtg(0x0005);

    /// <summary>PidLidIsException.</summary>
    public static readonly MapiNamedProperty IsException = Mtg(0x000A);

    /// <summary>PidLidCleanGlobalObjectId.</summary>
    public static readonly MapiNamedProperty CleanGlobalObjectId = Mtg(0x0023);

    /// <summary>PidLidMeetingType.</summary>
    public static readonly MapiNamedProperty MeetingType = Mtg(0x0026);

    /// <summary>PidLidOldLocation.</summary>
    public static readonly MapiNamedProperty OldLocation = Mtg(0x0028);

    /// <summary>PidLidReminderDelta.</summary>
    public static readonly MapiNamedProperty ReminderDelta = Cmn(0x8501);

    /// <summary>PidLidReminderTime.</summary>
    public static readonly MapiNamedProperty ReminderTime = Cmn(0x8502);

    /// <summary>PidLidReminderSet.</summary>
    public static readonly MapiNamedProperty ReminderSet = Cmn(0x8503);

    /// <summary>PidLidPrivate.</summary>
    public static readonly MapiNamedProperty Private = Cmn(0x8506);

    /// <summary>PidLidReminderSignalTime.</summary>
    public static readonly MapiNamedProperty ReminderSignalTime = Cmn(0x8560);

    /// <summary>PidLidCommonStart.</summary>
    public static readonly MapiNamedProperty CommonStart = Cmn(0x8516);

    /// <summary>PidLidCommonEnd.</summary>
    public static readonly MapiNamedProperty CommonEnd = Cmn(0x8517);

    /// <summary>PidLidTaskMode.</summary>
    public static readonly MapiNamedProperty TaskMode = Cmn(0x8518);

    /// <summary>PidLidNonSendableTo.</summary>
    public static readonly MapiNamedProperty NonSendableTo = Cmn(0x8536);

    /// <summary>PidLidNonSendableCc.</summary>
    public static readonly MapiNamedProperty NonSendableCc = Cmn(0x8537);

    /// <summary>PidLidNonSendableBcc.</summary>
    public static readonly MapiNamedProperty NonSendableBcc = Cmn(0x8538);

    /// <summary>PidLidTaskGlobalId.</summary>
    public static readonly MapiNamedProperty TaskGlobalId = Cmn(0x8519);

    /// <summary>PidLidTaskStatus.</summary>
    public static readonly MapiNamedProperty TaskStatus = Tsk(0x8101);

    /// <summary>PidLidPercentComplete (a double from 0 to 1).</summary>
    public static readonly MapiNamedProperty PercentComplete = Tsk(0x8102);

    /// <summary>PidLidTaskStartDate.</summary>
    public static readonly MapiNamedProperty TaskStartDate = Tsk(0x8104);

    /// <summary>PidLidTaskDueDate.</summary>
    public static readonly MapiNamedProperty TaskDueDate = Tsk(0x8105);

    /// <summary>PidLidTaskResetReminder.</summary>
    public static readonly MapiNamedProperty TaskResetReminder = Tsk(0x8107);

    /// <summary>PidLidTaskAccepted.</summary>
    public static readonly MapiNamedProperty TaskAccepted = Tsk(0x8108);

    /// <summary>PidLidTaskDeadOccurrence.</summary>
    public static readonly MapiNamedProperty TaskDeadOccurrence = Tsk(0x8109);

    /// <summary>PidLidTaskDateCompleted.</summary>
    public static readonly MapiNamedProperty TaskDateCompleted = Tsk(0x810F);

    /// <summary>PidLidTaskActualEffort (minutes).</summary>
    public static readonly MapiNamedProperty TaskActualEffort = Tsk(0x8110);

    /// <summary>PidLidTaskEstimatedEffort (minutes).</summary>
    public static readonly MapiNamedProperty TaskEstimatedEffort = Tsk(0x8111);

    /// <summary>PidLidTaskState.</summary>
    public static readonly MapiNamedProperty TaskState = Tsk(0x8113);

    /// <summary>PidLidTaskRecurrence.</summary>
    public static readonly MapiNamedProperty TaskRecurrence = Tsk(0x8116);

    /// <summary>PidLidTaskLastUpdate.</summary>
    public static readonly MapiNamedProperty TaskLastUpdate = Tsk(0x8115);

    /// <summary>PidLidTaskComplete.</summary>
    public static readonly MapiNamedProperty TaskComplete = Tsk(0x811C);

    /// <summary>PidLidTaskOwner.</summary>
    public static readonly MapiNamedProperty TaskOwner = Tsk(0x811F);

    /// <summary>PidLidTaskAssigner.</summary>
    public static readonly MapiNamedProperty TaskAssigner = Tsk(0x8121);

    /// <summary>PidLidTaskFRecurring.</summary>
    public static readonly MapiNamedProperty TaskFRecurring = Tsk(0x8126);

    /// <summary>PidLidTaskOwnership.</summary>
    public static readonly MapiNamedProperty TaskOwnership = Tsk(0x8129);

    /// <summary>PidLidTaskAcceptanceState.</summary>
    public static readonly MapiNamedProperty TaskAcceptanceState = Tsk(0x812A);

    // Tagged properties that are not in the shared table yet (MS-OXPROPS ids).
    internal const ushort PidTagExceptionReplaceTime = 0x7FF9;
    internal const ushort PidTagExceptionStartTime = 0x7FFB;
    internal const ushort PidTagExceptionEndTime = 0x7FFC;
    internal const ushort PidTagResponseRequested = 0x0063;
}
