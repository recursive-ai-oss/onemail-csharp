using RecursiveAI.OneMail.Calendar;

namespace RecursiveAI.OneMail;

/// <summary>Identifies the OneMail core package: the email formats, the models and the one-line entry points.</summary>
public static class OneMailLibrary
{
    /// <summary>The package ID.</summary>
    public const string Name = "RecursiveAI.OneMail";

    /// <summary>Names this package and the packages it is built on.</summary>
    /// <returns>A one-line description such as <c>RecursiveAI.OneMail (on RecursiveAI.OneMail.Calendar)</c>.</returns>
    public static string Describe() => $"{Name} (on {CalendarLibrary.Name})";
}
