namespace RecursiveAI.OneMail.Client;

/// <summary>Identifies the OneMail.Client package: SMTP, IMAP, POP3 and SASL.</summary>
public static class ClientLibrary
{
    /// <summary>The package ID.</summary>
    public const string Name = "RecursiveAI.OneMail.Client";

    /// <summary>Names this package and the core package it is built on.</summary>
    /// <returns>A one-line description.</returns>
    public static string Describe() => $"{Name} (on {OneMailLibrary.Name})";
}
