namespace RecursiveAI.OneMail.Render;

/// <summary>Identifies the OneMail.Render package: messages to PDF and images.</summary>
public static class RenderLibrary
{
    /// <summary>The package ID.</summary>
    public const string Name = "RecursiveAI.OneMail.Render";

    /// <summary>Names this package and the core package it is built on.</summary>
    /// <returns>A one-line description.</returns>
    public static string Describe() => $"{Name} (on {OneMailLibrary.Name})";
}
