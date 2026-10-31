using RecursiveAI.OneMail.Mapi;

namespace RecursiveAI.OneMail.Calendaring;

/// <summary>PidLidTaskStatus (MS-OXOTASK 2.2.2.2.2).</summary>
public enum OutlookTaskStatus
{
    /// <summary>tsvNotStarted.</summary>
    NotStarted = 0,

    /// <summary>tsvInProgress.</summary>
    InProgress = 1,

    /// <summary>tsvComplete.</summary>
    Complete = 2,

    /// <summary>tsvWaiting (waiting on someone else).</summary>
    Waiting = 3,

    /// <summary>tsvDeferred.</summary>
    Deferred = 4,
}

/// <summary>PidLidTaskState (MS-OXOTASK 2.2.2.2.14): who holds the task in an assignment.</summary>
public enum OutlookTaskState
{
    /// <summary>The task is not assigned.</summary>
    NotAssigned = 1,

    /// <summary>The copy held by the assignee, who owns the task.</summary>
    AssigneeOwned = 2,

    /// <summary>The copy kept by the assigner.</summary>
    AssignerCopy = 3,

    /// <summary>The assignee declined.</summary>
    Declined = 4,
}

/// <summary>
/// Typed access to an Outlook task (<c>IPM.Task</c>, MS-OXOTASK). Dates of a task are calendar dates stored as midnight in a PtypTime
/// with no zone; they are returned as stored, with the kind <see cref="DateTimeKind.Unspecified"/>.
/// </summary>
public sealed class OutlookTask
{
    private readonly List<CalendaringDefect> _defects = [];
    private readonly CalendaringLimits _limits;
    private RecurrencePattern? _recur;
    private bool _recurRead;

    private OutlookTask(MapiMessage message, CalendaringLimits limits)
    {
        Message = message;
        _limits = limits;
    }

    /// <summary>Wraps a message when its class is <c>IPM.Task</c> or a subclass of it.</summary>
    /// <param name="message">A MAPI message.</param>
    /// <param name="limits">Bounds for the recurrence blob; default <see cref="CalendaringLimits.Default"/>.</param>
    /// <returns>The view, or null for any other class (<c>IPM.TaskRequest</c> is a message about a task, not a task).</returns>
    public static OutlookTask? TryCreate(MapiMessage message, CalendaringLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? c = message.MessageClass;
        bool isTask = c is not null && c.StartsWith("IPM.Task", StringComparison.OrdinalIgnoreCase) && (c.Length == 8 || c[8] == '.');
        return isTask ? new OutlookTask(message, limits ?? CalendaringLimits.Default) : null;
    }

    /// <summary>The message this view reads.</summary>
    public MapiMessage Message { get; }

    /// <summary>The repairs made while reading the recurrence.</summary>
    public IReadOnlyList<CalendaringDefect> Defects => _defects;

    private int? Int(MapiNamedProperty n) => Message.Properties.Get(n, MapiType.Integer32)?.AsInt32();

    private bool? Bool(MapiNamedProperty n) => Message.Properties.Get(n, MapiType.Boolean)?.AsBoolean();

    private DateTime? Date(MapiNamedProperty n) => Message.Properties.Get(n, MapiType.Time)?.AsTime() is { } t ? DateTime.SpecifyKind(t.UtcDateTime, DateTimeKind.Unspecified) : null;

    /// <summary>PidTagSubject.</summary>
    public string? Subject => Message.Subject;

    /// <summary>PidLidTaskStatus.</summary>
    public OutlookTaskStatus? Status => Int(OutlookNames.TaskStatus) is >= 0 and <= 4 and int v ? (OutlookTaskStatus)v : null;

    /// <summary>PidLidPercentComplete as a fraction from 0 to 1 (a value outside the range is clamped).</summary>
    public double? PercentComplete => Message.Properties.Get(OutlookNames.PercentComplete, MapiType.Floating64)?.AsDouble() is { } d && !double.IsNaN(d) ? Math.Clamp(d, 0, 1) : null;

    /// <summary>PidLidTaskComplete.</summary>
    public bool IsComplete => Bool(OutlookNames.TaskComplete) == true || Status == OutlookTaskStatus.Complete;

    /// <summary>PidLidTaskStartDate.</summary>
    public DateTime? StartDate => Date(OutlookNames.TaskStartDate);

    /// <summary>PidLidTaskDueDate.</summary>
    public DateTime? DueDate => Date(OutlookNames.TaskDueDate);

    /// <summary>PidLidTaskDateCompleted.</summary>
    public DateTime? DateCompleted => Date(OutlookNames.TaskDateCompleted);

    /// <summary>PidLidTaskActualEffort in minutes.</summary>
    public int? ActualEffortMinutes => Int(OutlookNames.TaskActualEffort);

    /// <summary>PidLidTaskEstimatedEffort in minutes.</summary>
    public int? EstimatedEffortMinutes => Int(OutlookNames.TaskEstimatedEffort);

    /// <summary>PidLidTaskState.</summary>
    public OutlookTaskState? State => Int(OutlookNames.TaskState) is >= 1 and <= 4 and int v ? (OutlookTaskState)v : null;

    /// <summary>PidLidTaskOwner: the name of the person who owns the task now.</summary>
    public string? Owner => Message.Properties.Get(OutlookNames.TaskOwner)?.AsString(Message.String8Encoding);

    /// <summary>PidLidTaskAssigner: the name of the person who assigned it.</summary>
    public string? Assigner => Message.Properties.Get(OutlookNames.TaskAssigner)?.AsString(Message.String8Encoding);

    /// <summary>PidLidTaskFRecurring.</summary>
    public bool IsRecurring => Bool(OutlookNames.TaskFRecurring) == true;

    /// <summary>PidLidReminderSet.</summary>
    public bool ReminderSet => Bool(OutlookNames.ReminderSet) == true;

    /// <summary>PidLidReminderTime, UTC.</summary>
    public DateTimeOffset? ReminderTime => Message.Properties.Get(OutlookNames.ReminderTime, MapiType.Time)?.AsTime();

    /// <summary>PidTagImportance: 0 low, 1 normal, 2 high.</summary>
    public int? Importance => Message.GetInt32(MapiPid.Importance);

    /// <summary>PidTagSensitivity.</summary>
    public int? Sensitivity => Message.GetInt32(MapiPid.Sensitivity);

    /// <summary>The task's recurrence (PidLidTaskRecurrence is a bare RecurrencePattern, MS-OXOTASK 2.2.2.2.15).</summary>
    /// <returns>The pattern, or null when absent or unreadable.</returns>
    public RecurrencePattern? Recurrence
    {
        get
        {
            if (!_recurRead)
            {
                _recurRead = true;
                if (Message.Properties.Get(OutlookNames.TaskRecurrence, MapiType.Binary) is { } p)
                {
                    if (!RecurrencePattern.TryParse(p.GetBytes().Span, out _, out _recur, _defects, _limits))
                    {
                        _recur = null;
                    }
                }
            }

            return _recur;
        }
    }
}
