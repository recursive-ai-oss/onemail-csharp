using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tnef;

/// <summary>
/// Decides whether the TNEF part of a MIME message is used as the message's own content (the part is unpacked) or stays an
/// ordinary attachment. The conversion to MAPI and the shared view both ask here, so they always agree.
/// </summary>
internal static class TnefInMime
{
    /// <summary>Why a TNEF part is not unpacked (the part stays an attachment, MS-OXCMAIL 2.2.3.8 step 4), or a note on one that is.</summary>
    internal enum Refusal
    {
        /// <summary>The part is unpacked.</summary>
        None,

        /// <summary>The part does not hold a TNEF stream, or the stream is damaged beyond MS-OXTNEF (signature, version, truncation, checksum, limits).</summary>
        NotConforming,

        /// <summary>
        /// The stream ends with CR LF outside any attribute. MS-OXCMAIL 2.2.3.8 step 3 says a reader SHOULD ignore such a stream; every reader
        /// observed unpacks it, so it is used and this value is returned with <c>true</c> to let callers report the ambiguity (OX20).
        /// </summary>
        TrailingCrLf,

        /// <summary>The TNEF correlation key differs from the message's <c>X-MS-TNEF-Correlator</c> field (2.2.3.8 step 4).</summary>
        CorrelationMismatch,
    }

    // 2.1.3.2.26 names the field X-MS-TNEF-Correlator; 2.2.3.8 spells it X-MS-Tnef-Correlation. Field names are case-insensitive, the two
    // spellings are different names, so both are read.
    private static readonly string[] CorrelatorFields = ["X-MS-TNEF-Correlator", "X-MS-Tnef-Correlation"];

    // The defects that make a stream non-conforming to MS-OXTNEF: what a conforming reader rejects (2.3.3.1 for the version) or cannot read.
    private static bool IsFatal(TnefDefectKind kind) =>
        kind is TnefDefectKind.NotTnef or TnefDefectKind.UnexpectedVersion or TnefDefectKind.AttributeTruncated or TnefDefectKind.ChecksumMismatch
            or TnefDefectKind.LimitReached;

    /// <summary>Reads a candidate part of <paramref name="message"/> and applies the rules of MS-OXCMAIL 2.2.3.8.</summary>
    /// <param name="part">A leaf part.</param>
    /// <param name="message">The message (top level or embedded) whose header holds the part; its correlator field is compared.</param>
    /// <param name="options">TNEF reader limits, or null.</param>
    /// <param name="tnef">The message when <paramref name="refusal"/> is None; also set for a correlation mismatch or trailing CR LF, for diagnostics.</param>
    /// <param name="refusal">Why the part is not unpacked.</param>
    /// <returns>True when the part is a TNEF stream that is used.</returns>
    public static bool TryRead(MimePart part, MimeEntity message, TnefReaderOptions? options, out TnefMessage? tnef, out Refusal refusal)
    {
        tnef = null;
        refusal = Refusal.None;
        if (!part.IsTnef())
        {
            refusal = Refusal.NotConforming;
            return false;
        }

        byte[] data = part.GetDecodedContent();
        TnefMessage t = TnefMessage.Read(data, options);
        foreach (TnefDefect d in t.TnefDefects)
        {
            if (IsFatal(d.Kind))
            {
                refusal = Refusal.NotConforming;
                return false;
            }
        }

        tnef = t;
        bool crlf = t.Document.TrailingLength == 2 && data.Length >= 2 && data[^2] == 0x0D && data[^1] == 0x0A;

        if (CorrelationMismatch(t, message))
        {
            refusal = Refusal.CorrelationMismatch;
            return false;
        }

        if (crlf)
        {
            refusal = Refusal.TrailingCrLf;
        }

        return true;
    }

    // The key is a binary property and the field is text; the spec says only that they "match" (2.2.3.8 step 4). Equal as ASCII text or as
    // hexadecimal digits counts as a match. Either one missing cannot be compared and counts as a match (Outlook-made mail often has no field).
    private static bool CorrelationMismatch(TnefMessage t, MimeEntity message)
    {
        string? field = null;
        foreach (string name in CorrelatorFields)
        {
            if (message.Headers.GetFirst(name) is { } h)
            {
                field = h.Value.Trim();
                break;
            }
        }

        if (field is null || t.Properties.Get(MapiPid.TnefCorrelationKey, MapiType.Binary) is not { } key)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = key.GetBytes().Span;
        while (bytes.Length > 0 && bytes[^1] is 0 or (byte)' ')
        {
            bytes = bytes[..^1];
        }

        if (Encoding.ASCII.GetString(bytes).Equals(field, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !Convert.ToHexString(bytes).Equals(field, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The loss-report code and wording for a refusal.</summary>
    public static (string Code, string Reason) Describe(Refusal refusal) => refusal switch
    {
        Refusal.TrailingCrLf => ("TnefTrailingCrLfIgnored", "The stream ends with CR LF outside any attribute; MS-OXCMAIL 2.2.3.8 says to ignore such a stream, but every reader checked unpacks it, so it is used (AmbiguousTnefTrailingCrLf)."),
        Refusal.CorrelationMismatch => ("TnefNotUsed", "The TNEF correlation key does not match the X-MS-TNEF-Correlator field, so MS-OXCMAIL 2.2.3.8 has the message read as pure MIME; the part stays an application/octet-stream attachment."),
        _ => ("TnefNotUsed", "The stream does not conform to MS-OXTNEF, so MS-OXCMAIL 2.2.3.8 has the message read as pure MIME; the part stays an application/octet-stream attachment."),
    };
}
