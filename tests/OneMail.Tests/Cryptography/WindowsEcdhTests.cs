using System.Security.Cryptography;
using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.X509;
using RecursiveAI.OneMail.Cryptography.Smime;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>
/// ECDH-encrypted messages written by Windows' own CMS encoder (testdata/smime/outlook-ecdh), the encoder Outlook's S/MIME uses. Windows writes the
/// originator key's algorithm identifier and the AES key-wrap identifier with NULL parameters, where RFC 5753 and RFC 3565 leave them out.
/// </summary>
public class WindowsEcdhTests
{
    private static string Dir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "smime", "outlook-ecdh"));

    public static TheoryData<string> Messages => new(Directory.GetFiles(Dir(), "windows-*.eml").Select(Path.GetFileName).Order(StringComparer.Ordinal).Select(static f => f!).ToArray());

    [Theory]
    [MemberData(nameof(Messages))]
    public void WindowsEcdhMessagesDecrypt(string file)
    {
        string dir = Dir();
        Certificate cert = Certificate.FromPem(File.ReadAllText(Path.Combine(dir, "cert.pem")));
        using var dh = ECDiffieHellman.Create();
        dh.ImportFromPem(File.ReadAllText(Path.Combine(dir, "key.pem")));
        string expected = File.ReadAllText(Path.Combine(dir, "expected.txt"));
        MimeEntity message = MimeEntity.Parse(File.ReadAllBytes(Path.Combine(dir, file)));
        SmimeResult r = SmimeProcessor.Open(message, new SmimeOpenOptions { DecryptionKeys = [CmsDecryptionKey.FromECDiffieHellman(dh, cert)], AllowLegacyCiphers = true });
        Assert.Equal(SmimeStatus.Decrypted, r.Layers[0].Status);
        Assert.Equal(expected, r.InnerItem!.TextBody!.ReadText().Trim());
    }
}
