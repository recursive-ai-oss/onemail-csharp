namespace RecursiveAI.OneMail.Cryptography;

/// <summary>Identifies the OneMail.Cryptography package: DKIM and ARC signing and verification with a DNS TXT client.</summary>
public static class CryptographyLibrary
{
    /// <summary>The package ID.</summary>
    public const string Name = "RecursiveAI.OneMail.Cryptography";

    /// <summary>Names this package and the core package it is built on.</summary>
    /// <returns>A one-line description.</returns>
    public static string Describe() => $"{Name} (on {OneMailLibrary.Name})";
}
