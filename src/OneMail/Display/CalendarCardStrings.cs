namespace RecursiveAI.OneMail.Display;

/// <summary>
/// Every word the calendar card shows, in English, in one place so a translation can replace this class later. Nothing here
/// depends on the current culture: dates are formatted by <see cref="CalendarCardFormat"/> with the invariant culture.
/// </summary>
internal static class CalendarCardStrings
{
    internal const string Invitation = "Invitation";
    internal const string UpdatedInvitation = "Updated invitation";
    internal const string Cancelled = "Cancelled";
    internal const string CounterProposal = "Counter-proposal";
    internal const string CounterDeclined = "Counter-proposal declined";
    internal const string Published = "Event";
    internal const string AddedInstance = "Added to the event";
    internal const string RefreshRequest = "Request for the latest version";
    internal const string UnknownMethod = "Calendar item";
    internal const string Accepted = "accepted";
    internal const string Declined = "declined";
    internal const string TentativelyAccepted = "tentatively accepted";
    internal const string RepliedTo = "replied to";
    internal const string Someone = "An attendee";

    internal const string StatusCancelled = "Cancelled";
    internal const string StatusTentative = "Tentative";
    internal const string StatusConfirmed = "Confirmed";

    internal const string When = "When";
    internal const string Where = "Where";
    internal const string Repeats = "Repeats";
    internal const string Next = "Occurrences";
    internal const string Organizer = "Organizer";
    internal const string Attendees = "Attendees";
    internal const string Join = "Join";
    internal const string Attachments = "Attachments";
    internal const string Description = "Description";
    internal const string NoSummary = "(no title)";

    internal const string AllDay = "All day";
    internal const string To = "to";
    internal const string OriginalTime = "Organizer's time";
    internal const string Unknown = "unknown";

    internal const string CustomRule = "Repeats (custom rule)";
    internal const string SelectedDates = "Repeats on selected dates";
    internal const string Except = "except";
    internal static string Changed(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture) + (n == 1 ? " changed occurrence" : " changed occurrences");
    internal const string ExtraDates = "extra dates";

    internal const string Required = "required";
    internal const string Optional = "optional";
    internal const string Chair = "chair";
    internal const string NonParticipant = "for information";
    internal const string Room = "room";
    internal const string Resource = "resource";
    internal const string Group = "group";

    internal const string NeedsAction = "no reply yet";
    internal const string StatusAccepted = "accepted";
    internal const string StatusDeclined = "declined";
    internal const string StatusTentativeReply = "tentative";
    internal const string StatusDelegated = "delegated";

    internal static string More(int n) => "and " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";
    internal const string MoreEvents = "{0} more events in this calendar object";
    internal const string Embedded = "embedded file";
    internal const string Ellipsis = "…";

    internal static readonly string[] Ordinals = ["", "first", "second", "third", "fourth", "fifth"];
    internal static readonly string[] OrdinalsFromEnd = ["", "last", "second to last", "third to last", "fourth to last", "fifth to last"];
    internal static readonly string[] Months = ["", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    internal static readonly string[] ShortMonths = ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    internal static readonly string[] Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    internal static readonly string[] ShortDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
}
