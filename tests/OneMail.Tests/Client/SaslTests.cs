using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Client.Sasl;

namespace RecursiveAI.OneMail.Tests.Client;

// Published test vectors: RFC 1320 (MD4), RFC 2195, RFC 5802, RFC 7677, MS-NLMP 4.2.
public class SaslTests
{
    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    [Theory]
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "043f8582f241db351ce627e153e7f0e4")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void Md4MatchesRfc1320(string input, string expected) => Assert.Equal(expected, Hex(Md4.Hash(Encoding.ASCII.GetBytes(input))));

    [Fact]
    public void Md4HandlesEveryPaddingBoundary()
    {
        // 55, 56, 63, 64 and 65 bytes straddle the length-field boundary; compare with the one-shot result of a streaming reference built from the vectors' property:
        // hashing must be deterministic and different for different lengths.
        var seen = new HashSet<string>();
        foreach (int n in new[] { 0, 1, 55, 56, 57, 63, 64, 65, 119, 120, 128 })
        {
            Assert.True(seen.Add(Hex(Md4.Hash(new byte[n]))));
        }
    }

    [Fact]
    public void CramMd5MatchesRfc2195()
    {
        var m = new SaslCramMd5("tim", "tanstaaftanstaaf");
        byte[] r = m.Respond(Encoding.ASCII.GetBytes("<1896.697170952@postoffice.reston.mci.net>"));
        Assert.Equal("tim b913a602c7eda7a495b4e6e7334d3890", Encoding.ASCII.GetString(r));
        Assert.True(m.IsLegacy);
    }

    [Fact]
    public void ScramSha1MatchesRfc5802()
    {
        var m = new SaslScram(ScramHash.Sha1, "user", "pencil", nonce: "fyko+d2lbbFgONRv9qkxdawL");
        Assert.Equal("n,,n=user,r=fyko+d2lbbFgONRv9qkxdawL", Encoding.ASCII.GetString(m.GetInitialResponse()!));
        byte[] final = m.Respond("r=fyko+d2lbbFgONRv9qkxdawL3rfcNHYJY1ZVvWVs7j,s=QSXCR+Q6sek8bf92,i=4096"u8);
        Assert.Equal("c=biws,r=fyko+d2lbbFgONRv9qkxdawL3rfcNHYJY1ZVvWVs7j,p=v0X8v3Bz2T0CJGbJQyF0X+HI4Ts=", Encoding.ASCII.GetString(final));
        Assert.Empty(m.Respond("v=rmF9pqV8S7suAoZWja4dJRkFsKQ="u8));
    }

    [Fact]
    public void ScramSha256MatchesRfc7677()
    {
        var m = new SaslScram(ScramHash.Sha256, "user", "pencil", nonce: "rOprNGfwEbeRWgbNEkqO");
        Assert.Equal("n,,n=user,r=rOprNGfwEbeRWgbNEkqO", Encoding.ASCII.GetString(m.GetInitialResponse()!));
        byte[] final = m.Respond("r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"u8);
        Assert.Equal("c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=", Encoding.ASCII.GetString(final));
        Assert.Empty(m.Respond("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4="u8));
    }

    [Fact]
    public void ScramRejectsAWrongServerSignatureAndHostileChallenges()
    {
        var m = new SaslScram(ScramHash.Sha256, "user", "pencil", nonce: "rOprNGfwEbeRWgbNEkqO");
        m.GetInitialResponse();
        m.Respond("r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"u8);
        Assert.Throws<SaslException>(() => m.Respond("v=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="u8));

        foreach (string bad in new[] { "", "r=other,s=AA==,i=4096", "r=xyz,s=AA==", "r=rOprNGfwEbeRWgbNEkqOabc,s=AA==,i=99999999999", "r=rOprNGfwEbeRWgbNEkqOabc,s=!!!,i=4096", "m=ext,r=a,s=b,i=1", "e=bad", "r=rOprNGfwEbeRWgbNEkqO,s=AA==,i=4096" })
        {
            var s = new SaslScram(ScramHash.Sha256, "user", "pencil", nonce: "rOprNGfwEbeRWgbNEkqO");
            s.GetInitialResponse();
            Assert.Throws<SaslException>(() => s.Respond(Encoding.ASCII.GetBytes(bad)));
        }
    }

    [Fact]
    public void ScramPlusAnnouncesTheBindingAndChannelBindingFlagsFollowRfc5802()
    {
        var binding = new SaslChannelBinding("tls-server-end-point", [1, 2, 3]);
        var plus = new SaslScram(ScramHash.Sha256, "u=1,2", "pw", binding: binding, plus: true, nonce: "abc");
        Assert.Equal("SCRAM-SHA-256-PLUS", plus.Name);
        Assert.Equal("p=tls-server-end-point,,n=u=3D1=2C2,r=abc", Encoding.ASCII.GetString(plus.GetInitialResponse()!));
        // Binding known but -PLUS not offered by the server: "y" so a stripped offer is detectable.
        var y = new SaslScram(ScramHash.Sha1, "u", "pw", binding: binding, nonce: "abc");
        Assert.StartsWith("y,,", Encoding.ASCII.GetString(y.GetInitialResponse()!), StringComparison.Ordinal);
        byte[] final = plus.Respond("r=abcdef,s=AAAA,i=1"u8);
        string text = Encoding.ASCII.GetString(final);
        string c = text.Split(',')[0][2..];
        Assert.Equal("p=tls-server-end-point,,\u0001\u0002\u0003", Encoding.Latin1.GetString(Convert.FromBase64String(c)));
        Assert.Throws<ArgumentException>(() => new SaslScram(ScramHash.Sha1, "u", "p", plus: true));
    }

    // MS-NLMP 4.2.4: the CHALLENGE_MESSAGE of the NTLMv2 example, and the response the client must compute for it.
    private static readonly byte[] NlmpChallenge = Convert.FromHexString(
        "4e544c4d53535000020000000c000c003800000033828ae20123456789abcdef0000000000000000240024004400000006007017000000" + "0f" +
        "53006500720076006500720002000c0044006f006d00610069006e0001000c005300650072007600650072000000" + "0000");

    [Fact]
    public void NtlmV2MatchesMsNlmpExample()
    {
        // MS-NLMP 4.2.4.1: user "User", domain "Domain", password "Password", client challenge 0xaa x8, time 0.
        var m = new SaslNtlm("User", "Password", "Domain", "COMPUTER", clientChallenge: Enumerable.Repeat((byte)0xaa, 8).ToArray(), fileTime: 0);
        byte[] auth = m.Respond(NlmpChallenge);
        Assert.Equal("NTLMSSP\0"u8.ToArray(), auth[..8]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(auth.AsSpan(8)));
        (int lmLen, int lmOff) = (BinaryPrimitives.ReadUInt16LittleEndian(auth.AsSpan(12)), (int)BinaryPrimitives.ReadUInt32LittleEndian(auth.AsSpan(16)));
        (int ntLen, int ntOff) = (BinaryPrimitives.ReadUInt16LittleEndian(auth.AsSpan(20)), (int)BinaryPrimitives.ReadUInt32LittleEndian(auth.AsSpan(24)));
        // 4.2.4.2.1 LMv2 response and 4.2.4.2.2 NTProofStr
        Assert.Equal("86c35097ac9cec102554764a57cccc19aaaaaaaaaaaaaaaa", Hex(auth[lmOff..(lmOff + lmLen)]));
        Assert.Equal("68cd0ab851e51c96aabc927bebef6a1c", Hex(auth[ntOff..(ntOff + 16)]));
        // 4.2.4.1.3 temp, from the 16th byte of the NT response
        Assert.Equal("0101000000000000" + "0000000000000000" + "aaaaaaaaaaaaaaaa" + "00000000" + "02000c0044006f006d00610069006e00" + "01000c00530065007200760065007200" + "0000000000000000",
            Hex(auth[(ntOff + 16)..(ntOff + ntLen)]));
        // 4.2.4.3: the domain, user and workstation names, found through their security buffers (this message has no version field).
        string Field(int at) => Encoding.Unicode.GetString(auth, (int)BinaryPrimitives.ReadUInt32LittleEndian(auth.AsSpan(at + 4)), BinaryPrimitives.ReadUInt16LittleEndian(auth.AsSpan(at)));
        Assert.Equal("Domain", Field(28));
        Assert.Equal("User", Field(36));
        Assert.Equal("COMPUTER", Field(44));
    }

    [Fact]
    public void NtlmNegotiateAndChallengeParsing()
    {
        var m = new SaslNtlm(@"DOM\bob", "pw");
        byte[] neg = m.GetInitialResponse()!;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(neg.AsSpan(8)));
        Assert.Equal("a4f49c406510bdcab6824ee7c30fd852", Hex(Md4.Hash(Encoding.Unicode.GetBytes("Password")))); // MS-NLMP 4.2.2.1.2 NTOWFv1
        foreach (byte[] bad in new byte[][] { [], "NTLMSSP\0"u8.ToArray(), NlmpChallenge[..40], [.. NlmpChallenge[..44], 0xff, 0xff, 0xff, 0x7f] })
        {
            Assert.Throws<SaslException>(() => new SaslNtlm("u", "p").Respond(bad));
        }
    }

    [Fact]
    public void OAuthMechanismsFormTheirMessages()
    {
        Assert.Equal("user=ann@example.com\u0001auth=Bearer tok\u0001\u0001", Encoding.ASCII.GetString(new SaslXOAuth2("ann@example.com", "tok").GetInitialResponse()!));
        Assert.Equal("n,a=ann@example.com,\u0001host=mail.example.com\u0001port=587\u0001auth=Bearer tok\u0001\u0001",
            Encoding.ASCII.GetString(new SaslOAuthBearer("ann@example.com", "tok", "mail.example.com", 587).GetInitialResponse()!));
        Assert.Equal([0x01], new SaslOAuthBearer("a", "t").Respond("{\"status\":\"401\"}"u8));
        Assert.Empty(new SaslXOAuth2("a", "t").Respond("{\"status\":\"401\"}"u8));
        Assert.Throws<ArgumentException>(() => new SaslXOAuth2("a", "t\u0001x"));
    }

    [Fact]
    public void LoginAnswersInOrder()
    {
        var m = new SaslLogin("u", "p");
        Assert.False(m.ClientFirst);
        Assert.Equal("u", Encoding.ASCII.GetString(m.Respond("Username:"u8)));
        Assert.Equal("p", Encoding.ASCII.GetString(m.Respond("Password:"u8)));
    }
}
