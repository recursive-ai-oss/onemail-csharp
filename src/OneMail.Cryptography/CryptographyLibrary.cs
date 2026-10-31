namespace RecursiveAI.OneMail.Cryptography;

/// <summary>Identifies the OneMail.Cryptography package: DKIM, ARC, S/MIME and OpenPGP.</summary>
public static class CryptographyLibrary
{
    /// <summary>The package ID.</summary>
    public const string Name = "RecursiveAI.OneMail.Cryptography";

    /// <summary>Names this package and the core package it is built on.</summary>
    /// <returns>A one-line description.</returns>
    public static string Describe() => $"{Name} (on {OneMailLibrary.Name})";
}
