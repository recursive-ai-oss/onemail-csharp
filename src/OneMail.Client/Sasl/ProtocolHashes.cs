using System.Security.Cryptography;

namespace RecursiveAI.OneMail.Client.Sasl;

/// <summary>
/// The weak primitives that SASL mechanisms are defined on. CRAM-MD5 (RFC 2195) and NTLMv2 (MS-NLMP) are built from HMAC-MD5 and
/// SCRAM-SHA-1 (RFC 5802) from SHA-1; a client that speaks the mechanism has no choice. They are used only as the protocols
/// define them (as keyed MACs, never to sign anything), and every use sits behind a mechanism that is flagged legacy or that
/// the caller selected. Kept in one place so the analyser suppression is one reviewed spot.
/// </summary>
internal static class ProtocolHashes
{
#pragma warning disable CA5350, CA5351 // Required by RFC 2195, RFC 5802 and MS-NLMP; see the type comment.
    public static byte[] HmacMd5(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) => HMACMD5.HashData(key, data);

    public static byte[] Sha1(ReadOnlySpan<byte> data) => SHA1.HashData(data);

    public static byte[] HmacSha1(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) => HMACSHA1.HashData(key, data);
#pragma warning restore CA5350, CA5351
}
