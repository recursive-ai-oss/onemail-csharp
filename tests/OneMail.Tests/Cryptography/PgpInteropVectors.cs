using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneMail.Cryptography.OpenPgp;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>
/// The interop vector set: messages our writer produced and messages other OpenPGP programs produced, one directory per key
/// (testdata/openpgp/vectors/&lt;origin&gt;/&lt;name&gt;), each with the key pair, the text that was sent and the PGP/MIME messages.
/// tools/openpgp/interop.sh regenerates the set and runs the other programs over ours; this class reads it back with no outside tool.
/// </summary>
public class PgpInteropVectors
{
    private const string BodyText = "Interop test caf\u00e9.\r\nA line that is quite long enough to need some wrapping when it is encoded, as long lines are.   \r\nFrom the other side.\r\n";

    private static string VectorRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "openpgp", "vectors"));

    private static readonly (string Name, PgpKeyProfile Profile)[] OurProfiles =
    [
        ("v4-ed25519-cv25519", PgpKeyProfile.Rfc4880Curve25519),
        ("v4-native-ed25519-x25519", PgpKeyProfile.Rfc9580V4),
        ("v6-ed25519-x25519", PgpKeyProfile.Rfc9580),
        ("v4-rsa3072", PgpKeyProfile.Rfc4880Rsa),
    ];

    /// <summary>Writes our half of the set when ONEMAIL_PGP_VECTORS_OUT names a directory; otherwise does nothing. Run by tools/openpgp/interop.sh.</summary>
    [Fact]
    public void WriteOurVectorsWhenAsked()
    {
        string? outDir = Environment.GetEnvironmentVariable("ONEMAIL_PGP_VECTORS_OUT");
        if (string.IsNullOrEmpty(outDir))
        {
            return;
        }

        foreach ((string name, PgpKeyProfile profile) in OurProfiles)
        {
            string dir = Path.Combine(outDir, "ours", name);
            Directory.CreateDirectory(dir);
            string email = "vector-" + name + "@example.org";
            PgpCertificate key = PgpKeyGenerator.Generate(new PgpKeyGenerationOptions { Now = PgpFixtures.Now.AddDays(-30), UserIds = ["Vector " + name + " <" + email + ">"], Profile = profile });
            File.WriteAllBytes(Path.Combine(dir, "key.sec.asc"), key.Export(armor: true));
            File.WriteAllBytes(Path.Combine(dir, "key.pub.asc"), key.ExportPublic(armor: true));
            File.WriteAllText(Path.Combine(dir, "expected.txt"), BodyText, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "info.txt"), "OneMail PGP/MIME writer\n");

            MimeEntity Content() => MimeEntity.Parse(Encoding.UTF8.GetBytes("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n" + BodyText));
            MimeEntity Wrap(MimeEntity e)
            {
                e.Headers.Insert(0, new MimeHeader("From", "Vector <" + email + ">"));
                e.Headers.Insert(1, new MimeHeader("To", email));
                e.Headers.Insert(2, new MimeHeader("Subject", "Interop vector"));
                e.Headers.Insert(3, new MimeHeader("MIME-Version", "1.0"));
                return e;
            }

            PgpSecretKey Signer() => PgpSecretKey.Unlock(key.FindSigningKey(PgpFixtures.Now)!);
            File.WriteAllBytes(Path.Combine(dir, "signed.eml"), Wrap(PgpMimeSigner.SignDetached(Content(), new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [Signer()] })).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "encrypted.eml"), Wrap(PgpMimeEncryptor.Encrypt(Content(), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [key.ToPublicCertificate()] })).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "signed-encrypted.eml"), Wrap(PgpMimeEncryptor.Encrypt(Content(), new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [key.ToPublicCertificate()], Signers = [Signer()] })).ToArray());
            // Sign-then-encrypt: a multipart/signed inside the encrypted message.
            MimeMultipart inner = PgpMimeSigner.SignDetached(Content(), new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [Signer()] });
            File.WriteAllBytes(Path.Combine(dir, "sign-then-encrypt.eml"), Wrap(PgpMimeEncryptor.Encrypt(inner, new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [key.ToPublicCertificate()] })).ToArray());
        }
    }

    [Fact]
    public void EveryCommittedVectorOpens()
    {
        string root = VectorRoot();
        Assert.True(Directory.Exists(root), "the vector set is missing: " + root);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Where(static d => File.Exists(Path.Combine(d, "key.sec.asc"))))
        {
            PgpCertificate key = PgpCertificate.Parse(File.ReadAllBytes(Path.Combine(dir, "key.sec.asc")));
            string expected = File.ReadAllText(Path.Combine(dir, "expected.txt"), Encoding.UTF8);
            string origin = Path.GetFileName(Path.GetDirectoryName(dir)!);
            foreach (string eml in Directory.GetFiles(dir, "*.eml"))
            {
                string kind = Path.GetFileNameWithoutExtension(eml);
                // The other programs stamped their keys and signatures with the day they ran, so read at a fixed later time.
                var options = new PgpOpenOptions { Now = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), Certificates = [key.ToPublicCertificate()], SecretKeys = [key] };
                PgpResult r = PgpMimeProcessor.Open(File.ReadAllBytes(eml), options);

                bool wantsSignature = kind != "encrypted";
                Assert.True(r.Succeeded, dir + "/" + kind + ": " + string.Join("; ", r.Layers.Select(static l => l.Status + " " + l.Detail)));
                Assert.Equal(wantsSignature, r.IsAuthenticated);
                MimePart leaf = Assert.IsType<MimePart>(r.Content);
                string actual = Encoding.UTF8.GetString(leaf.GetDecodedContent());
                Assert.Equal(expected.TrimEnd(), actual.TrimEnd());
                if (kind == "sign-then-encrypt")
                {
                    Assert.Equal(2, r.Layers.Count);
                }

                string keyType = Path.GetFileName(dir);
                string tool = Path.GetFileName(Path.GetDirectoryName(dir)!) + ":" + File.ReadAllLines(Path.Combine(dir, "info.txt"))[0];
                _ = origin;
                string label = tool + " / " + keyType;
                counts[label] = counts.GetValueOrDefault(label) + 1;
            }
        }

        Assert.NotEmpty(counts);
        File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "pgp-vector-counts.txt"), counts.Select(static kv => kv.Value + "\t" + kv.Key));
    }
}
