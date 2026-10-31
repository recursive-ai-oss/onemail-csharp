namespace RecursiveAI.OneMail.Mime.Transfer;

/// <summary>A content transfer encoding (RFC 2045 §6): how a body's bytes are encoded for transport.</summary>
public enum ContentTransferEncoding
{
    /// <summary>7bit: ASCII lines. Also what an absent header means (RFC 2045 §6.1).</summary>
    SevenBit = 0,

    /// <summary>8bit: lines of bytes with the high bit allowed.</summary>
    EightBit,

    /// <summary>binary: arbitrary bytes.</summary>
    Binary,

    /// <summary>base64 (RFC 2045 §6.8).</summary>
    Base64,

    /// <summary>quoted-printable (RFC 2045 §6.7).</summary>
    QuotedPrintable,

    /// <summary>A value that is none of the above. Decoders treat the body as identity-encoded.</summary>
    Unknown,
}
