using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneMail.Client.Sasl;

/// <summary>
/// NTLM version 2 (MS-NLMP), for Exchange-style servers that still offer it. The password hash uses our own <see cref="Md4"/>. Only the
/// NTLMv2 response is computed (never LM or NTLMv1), and no signing or sealing is negotiated, which is all SASL needs.
/// </summary>
/// <remarks>
/// NTLM is a legacy mechanism: <see cref="SaslMechanism.IsLegacy"/> is true, and it should only be used over TLS. The challenge
/// message from the server is parsed with every offset and length checked; a malformed message raises <see cref="SaslException"/>.
/// </remarks>
public sealed class SaslNtlm : SaslMechanism
{
    // MS-NLMP 2.2.2.5 NegotiateFlags
    private const uint NegotiateUnicode = 0x00000001;
    private const uint RequestTarget = 0x00000004;
    private const uint NegotiateNtlm = 0x00000200;
    private const uint NegotiateAlwaysSign = 0x00008000;
    private const uint NegotiateExtendedSessionSecurity = 0x00080000;
    private const uint NegotiateTargetInfo = 0x00800000;
    private const uint Negotiate128 = 0x20000000;
    private const uint Negotiate56 = 0x80000000;

    private static ReadOnlySpan<byte> Signature => "NTLMSSP\0"u8;

    private readonly string _user;
    private readonly string _domain;
    private readonly string _workstation;
    private readonly byte[] _password;
    private readonly byte[]? _fixedClientChallenge;
    private readonly long? _fixedTime;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="userName">The user name; <c>DOMAIN\user</c> sets the domain too.</param>
    /// <param name="password">The password.</param>
    /// <param name="domain">The domain, or null/empty for none.</param>
    /// <param name="workstation">The client's workstation name sent to the server, or null/empty.</param>
    /// <param name="clientChallenge">A fixed 8-byte client challenge, for test vectors only. Null: random.</param>
    /// <param name="fileTime">A fixed timestamp (100 ns ticks since 1601-01-01 UTC) used when the server sends none, for test vectors only. Null: now.</param>
    /// <exception cref="ArgumentException">The user name is empty, or the client challenge is not 8 bytes.</exception>
    public SaslNtlm(string userName, string password, string? domain = null, string? workstation = null, byte[]? clientChallenge = null, long? fileTime = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        int slash = userName.IndexOf('\\', StringComparison.Ordinal);
        if (slash > 0 && string.IsNullOrEmpty(domain))
        {
            domain = userName[..slash];
            userName = userName[(slash + 1)..];
        }

        if (userName.Length == 0 || (clientChallenge is not null && clientChallenge.Length != 8))
        {
            throw new ArgumentException("The user name must not be empty and a fixed client challenge must be 8 bytes.");
        }

        _user = userName;
        _domain = domain ?? "";
        _workstation = workstation ?? "";
        _password = Md4.Hash(Encoding.Unicode.GetBytes(password)); // MS-NLMP 3.3.1 NTOWFv1: MD4(UNICODE(Passwd))
        _fixedClientChallenge = clientChallenge;
        _fixedTime = fileTime;
    }

    /// <inheritdoc />
    public override string Name => "NTLM";

    /// <inheritdoc />
    public override bool RequiresEncryption => true;

    /// <inheritdoc />
    public override bool IsLegacy => true;

    /// <inheritdoc />
    public override byte[]? GetInitialResponse()
    {
        // MS-NLMP 2.2.1.1 NEGOTIATE_MESSAGE; the domain and workstation fields are left empty (flags 0x1000/0x2000 unset).
        byte[] m = new byte[32];
        Signature.CopyTo(m);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(12), NegotiateUnicode | RequestTarget | NegotiateNtlm | NegotiateAlwaysSign | NegotiateExtendedSessionSecurity | Negotiate128 | Negotiate56 | NegotiateTargetInfo);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(20), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(28), 32);
        return m;
    }

    /// <inheritdoc />
    /// <exception cref="SaslException">The challenge is not a well-formed NTLM CHALLENGE_MESSAGE.</exception>
    public override byte[] Respond(ReadOnlySpan<byte> challenge)
    {
        NtlmChallenge c = ParseChallenge(challenge);
        return Authenticate(c);
    }

    /// <summary>The fields of a CHALLENGE_MESSAGE (MS-NLMP 2.2.1.2) that the client uses.</summary>
    internal readonly record struct NtlmChallenge(uint Flags, byte[] ServerChallenge, byte[] TargetInfo);

    internal static NtlmChallenge ParseChallenge(ReadOnlySpan<byte> m)
    {
        if (m.Length < 32 || !m[..8].SequenceEqual(Signature) || BinaryPrimitives.ReadUInt32LittleEndian(m[8..]) != 2)
        {
            throw new SaslException("NTLM: the server's message is not a CHALLENGE_MESSAGE.");
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(m[20..]);
        if ((flags & NegotiateUnicode) == 0)
        {
            throw new SaslException("NTLM: the server chose OEM strings; only Unicode is supported.");
        }

        byte[] serverChallenge = m.Slice(24, 8).ToArray();
        byte[] targetInfo = [];
        // TargetInfoFields (offset 40) exist only in messages long enough to hold them and when the server sent target info.
        if ((flags & NegotiateTargetInfo) != 0)
        {
            if (m.Length < 48)
            {
                throw new SaslException("NTLM: the challenge announces target information but is too short for it.");
            }

            int len = BinaryPrimitives.ReadUInt16LittleEndian(m[40..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(m[44..]);
            if (offset > m.Length || len > m.Length - offset)
            {
                throw new SaslException("NTLM: the target information lies outside the message.");
            }

            targetInfo = m.Slice((int)offset, len).ToArray();
        }

        return new NtlmChallenge(flags, serverChallenge, targetInfo);
    }

    // MS-NLMP 2.2.2.1 AV_PAIR: id (2), length (2), value; MsvAvEOL (0) ends the list, MsvAvTimestamp (7) carries a FILETIME.
    internal static bool TryFindTimestamp(ReadOnlySpan<byte> targetInfo, out long fileTime)
    {
        fileTime = 0;
        int p = 0;
        while (p + 4 <= targetInfo.Length)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(targetInfo[p..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(targetInfo[(p + 2)..]);
            p += 4;
            if (id == 0 || len > targetInfo.Length - p)
            {
                return false;
            }

            if (id == 7 && len == 8)
            {
                fileTime = BinaryPrimitives.ReadInt64LittleEndian(targetInfo[p..]);
                return true;
            }

            p += len;
        }

        return false;
    }

    private byte[] Authenticate(NtlmChallenge c)
    {
        byte[] clientChallenge = _fixedClientChallenge ?? RandomNumberGenerator.GetBytes(8);
        bool serverTime = TryFindTimestamp(c.TargetInfo, out long time);
        if (!serverTime)
        {
            time = _fixedTime ?? DateTime.UtcNow.ToFileTimeUtc();
        }

        // MS-NLMP 3.3.2 NTOWFv2: HMAC_MD5(NTOWFv1, UNICODE(Uppercase(User) + UserDom))
        byte[] responseKey = ProtocolHashes.HmacMd5(_password, Encoding.Unicode.GetBytes(_user.ToUpperInvariant() + _domain));

        // MS-NLMP 2.2.2.7 NTLMv2_CLIENT_CHALLENGE: version 1.1, reserved, time, client challenge, the server's target info, padding.
        byte[] temp = new byte[28 + c.TargetInfo.Length + 4];
        temp[0] = 1;
        temp[1] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(temp.AsSpan(8), time);
        clientChallenge.CopyTo(temp, 16);
        c.TargetInfo.CopyTo(temp, 28);

        byte[] proofInput = [.. c.ServerChallenge, .. temp];
        byte[] ntProof = ProtocolHashes.HmacMd5(responseKey, proofInput);
        byte[] ntResponse = [.. ntProof, .. temp];

        // MS-NLMP 3.1.5.1.2: when the server sent a timestamp the LM response is 24 zero bytes; otherwise LMv2.
        byte[] lmResponse = serverTime
            ? new byte[24]
            : [.. ProtocolHashes.HmacMd5(responseKey, [.. c.ServerChallenge, .. clientChallenge]), .. clientChallenge];

        // The flags we answer with: what we asked for and the server agreed to, without key exchange, signing or sealing.
        uint flags = c.Flags & (NegotiateUnicode | RequestTarget | NegotiateNtlm | NegotiateAlwaysSign | NegotiateExtendedSessionSecurity | NegotiateTargetInfo | Negotiate128 | Negotiate56);

        byte[] domain = Encoding.Unicode.GetBytes(_domain);
        byte[] user = Encoding.Unicode.GetBytes(_user);
        byte[] workstation = Encoding.Unicode.GetBytes(_workstation);

        // MS-NLMP 2.2.1.3 AUTHENTICATE_MESSAGE, no version and no MIC: the payload starts at 64. Order: domain, user, workstation, LM, NT.
        int offset = 64;
        byte[] m = new byte[offset + domain.Length + user.Length + workstation.Length + lmResponse.Length + ntResponse.Length];
        Signature.CopyTo(m);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), 3);
        void Field(int at, byte[] data)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(at), (ushort)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(at + 2), (ushort)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(at + 4), (uint)offset);
            data.CopyTo(m, offset);
            offset += data.Length;
        }

        Field(28, domain);
        Field(36, user);
        Field(44, workstation);
        Field(12, lmResponse);
        Field(20, ntResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(52), 0); // EncryptedRandomSessionKeyFields: empty
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(56), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(60), flags);
        return m;
    }
}
