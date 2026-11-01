using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Issuance;
using RecursiveAI.OneCrypto.Keys;
using RecursiveAI.OneCrypto.Primitives;
using RecursiveAI.OneCrypto.Validation;
using RecursiveAI.OneCrypto.X509;
using RecursiveAI.OneMail.Cryptography.Smime;
using MimeEntity = RecursiveAI.OneMail.Mime.MimeEntity;
using MkRecipient = MimeKit.Cryptography.CmsRecipient;
using MimePart = RecursiveAI.OneMail.Mime.MimePart;
using OneMime = RecursiveAI.OneMail.Mime;

namespace OneMail.Tools.SmimeInterop;

// Two-way S/MIME interop. Every cell makes a message with one implementation and has another open it. OpenSSL (`openssl cms`) and
// MimeKit run as released binaries/packages, black box. Usage: SmimeInterop <work dir> [cells.tsv]
internal static class Program
{
    private sealed record Id(string Name, string Email, Certificate Cert, ISigningKey Key, string CrtPath, string KeyPath, string Pfx, Func<CmsDecryptionKey>? Decrypt);

    private sealed record Row(string Cell, string Direction, bool Ok, string Detail);

    private static readonly List<Row> Rows = [];
    private static string work = "";
    private static string openssl = "openssl";
    private static string rootPem = "";
    private static Certificate root = null!;
    private static DateTimeOffset now;
    private const string Marker = "interop-marker-7f3a";

    private static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "scan")
        {
            return Scan(args.Skip(1));
        }

        if (args.Length > 1 && args[0] == "rfc9216")
        {
            return Rfc9216(args[1]);
        }

        work = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(work);
        openssl = Environment.GetEnvironmentVariable("ONEMAIL_OPENSSL") ?? "openssl";
        now = DateTimeOffset.UtcNow;
        Console.WriteLine("openssl: " + Run(openssl, ["version"]).Out.Trim());
        var notBefore = now.AddDays(-30);
        using var caKey = RSA.Create(3072);
        CertificateAuthority ca = CertificateAuthority.CreateRoot(DistinguishedName.FromString("CN=Interop Root"), X509Signer.FromRsa(caKey, HashAlgorithmName.SHA256), notBefore, TimeSpan.FromDays(3650));
        root = ca.Certificate;
        rootPem = Path.Combine(work, "root.pem");
        File.WriteAllText(rootPem, root.ToPem());

        Id alice = MakeRsa(ca, "alice", notBefore);
        Id bob = MakeRsa(ca, "bob", notBefore);
        Id carol = MakeEc(ca, "carol", notBefore);
        Id eve = MakeEd(ca, "eve", notBefore);

        OursToOpenSsl(alice, bob, carol, eve);
        OpenSslToOurs(alice, bob, carol, eve);
        MimeKitCells(alice, bob, carol);

        File.WriteAllLines(Path.Combine(work, "cells.tsv"), Rows.Select(static r => $"{r.Cell}\t{r.Direction}\t{(r.Ok ? "pass" : "FAIL")}\t{r.Detail}"));
        foreach (IGrouping<string, Row> g in Rows.GroupBy(static r => r.Direction))
        {
            Console.WriteLine($"{g.Key}: {g.Count(static r => r.Ok)}/{g.Count()} pass");
        }

        foreach (Row r in Rows.Where(static r => !r.Ok))
        {
            Console.WriteLine($"FAIL {r.Direction} {r.Cell}: {r.Detail}");
        }

        return Rows.Any(static r => !r.Ok && !r.Detail.StartsWith("oracle cannot:", StringComparison.Ordinal)) ? 1 : 0;
    }

    // ---- corpus scan: S/MIME items in stores and messages ----

    private static int Scan(IEnumerable<string> roots)
    {
        int files = 0, items = 0, mapi = 0, smimeClass = 0, mimeSmime = 0, errors = 0;
        var statuses = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            IEnumerable<string> paths = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : [root];
            foreach (string path in paths)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".msg" or ".oft" or ".pst" or ".ost" or ".eml" or ".mbox" or ".emlx" or ".dbx"))
                {
                    continue;
                }

                files++;
                try
                {
                    using RecursiveAI.OneMail.MailContainer c = RecursiveAI.OneMail.Mail.Open(path);
                    foreach (RecursiveAI.OneMail.IMailItem item in c.Enumerate())
                    {
                        items++;
                        if (item is RecursiveAI.OneMail.MapiMailItem mi)
                        {
                            mapi++;
                            string? cls = mi.Message.MessageClass;
                            if (SmimeOutlook.IsSmimeClass(cls))
                            {
                                smimeClass++;
                                classes[cls!] = classes.GetValueOrDefault(cls!) + 1;
                                SmimeResult r = SmimeOutlook.Open(mi.Message);
                                string key = r.Layers.Count == 0 ? "no-layers" : string.Join("+", r.Layers.Select(static l => l.Kind + "=" + l.Status));
                                statuses[key] = statuses.GetValueOrDefault(key) + 1;
                            }
                        }
                        else if (item is RecursiveAI.OneMail.MimeMailItem mm && SmimeProcessor.Open(mm.Entity).IsSmime)
                        {
                            mimeSmime++;
                        }
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    errors++;
                    Console.WriteLine("skip " + path + ": " + e.GetType().Name);
                }
            }
        }

        Console.WriteLine($"files {files}, items {items}, outlook items {mapi}, S/MIME-class outlook items {smimeClass}, MIME S/MIME messages {mimeSmime}, unreadable {errors}");
        foreach (KeyValuePair<string, int> kv in classes)
        {
            Console.WriteLine("class " + kv.Key + ": " + kv.Value);
        }

        foreach (KeyValuePair<string, int> kv in statuses)
        {
            Console.WriteLine("result " + kv.Key + ": " + kv.Value);
        }

        return 0;
    }

    // ---- published example keys (RFC 9216) ----

    // The PEM block that follows the heading of section `number` in the RFC text, with page furniture removed.
    private static string Pem(string[] lines, string number)
    {
        int h = Array.FindIndex(lines, l => l.StartsWith(number + ".  ", StringComparison.Ordinal));
        if (h < 0)
        {
            throw new InvalidDataException("section " + number + " not found");
        }

        int b = Array.FindIndex(lines, h, l => l.Contains("-----BEGIN ", StringComparison.Ordinal));
        var sb = new StringBuilder();
        for (int i = b; ; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("-----", StringComparison.Ordinal) || (t.Length > 0 && t.All(static c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=') && !t.StartsWith("RFC", StringComparison.Ordinal)))
            {
                sb.Append(t).Append('\n');
            }

            if (t.StartsWith("-----END ", StringComparison.Ordinal))
            {
                return sb.ToString();
            }
        }
    }

    private static int Rfc9216(string rfcPath)
    {
        work = Path.GetFullPath(rfcPath + ".work");
        Directory.CreateDirectory(work);
        now = DateTimeOffset.UtcNow;
        string[] lines = File.ReadAllLines(rfcPath);
        const string D = "rfc9216";
        Certificate rsaCa = Certificate.FromPem(Pem(lines, "3.1"));
        Certificate edCa = Certificate.FromPem(Pem(lines, "6.1"));
        var rsaTrust = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(rsaCa)], ReferenceTime = now };
        var edTrust = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(edCa)], ReferenceTime = now };

        // Alice and Bob: RSA. Carlos and Dana: Ed25519 signing, X25519 encryption.
        Certificate aliceSign = Certificate.FromPem(Pem(lines, "4.1")), aliceEnc = Certificate.FromPem(Pem(lines, "4.3"));
        Certificate bobSign = Certificate.FromPem(Pem(lines, "5.1")), bobEnc = Certificate.FromPem(Pem(lines, "5.3"));
        Certificate carlosSign = Certificate.FromPem(Pem(lines, "7.1")), carlosEnc = Certificate.FromPem(Pem(lines, "7.3"));
        Certificate danaSign = Certificate.FromPem(Pem(lines, "8.1")), danaEnc = Certificate.FromPem(Pem(lines, "8.3"));
        static RSA RsaKey(string pem)
        {
            var r = RSA.Create();
            r.ImportFromPem(pem);
            return r;
        }

        static byte[] RawKey(string pem)
        {
            // PrivateKeyInfo for Ed25519/X25519 (RFC 8410 §7): the last 32 bytes are the key.
            byte[] der = Convert.FromBase64String(string.Concat(pem.Split('\n').Where(static l => !l.StartsWith("-----", StringComparison.Ordinal))));
            return der[^32..];
        }

        RSA aliceSignKey = RsaKey(Pem(lines, "4.2")), aliceDecKey = RsaKey(Pem(lines, "4.4")), bobSignKey = RsaKey(Pem(lines, "5.2")), bobDecKey = RsaKey(Pem(lines, "5.4"));
        ISigningKey carlosKey = PrivateKey.FromEd25519(Ed25519PrivateKey.FromSeed(RawKey(Pem(lines, "7.2"))));
        ISigningKey danaKey = PrivateKey.FromEd25519(Ed25519PrivateKey.FromSeed(RawKey(Pem(lines, "8.2"))));
        byte[] carlosX = RawKey(Pem(lines, "7.4")), danaX = RawKey(Pem(lines, "8.4"));

        SmimeOpenOptions Open(ValidationOptions trust, params CmsDecryptionKey[] keys) => new() { DecryptionKeys = keys, Chain = trust };

        foreach (bool opaque in new[] { false, true })
        {
            string kind = opaque ? "opaque" : "detached";
            foreach ((string who, Certificate cert, ISigningKey key, ValidationOptions trust, Certificate ca) in new[]
            {
                ("alice", aliceSign, PrivateKey.FromRsa(aliceSignKey), rsaTrust, rsaCa),
                ("bob", bobSign, PrivateKey.FromRsa(bobSignKey), rsaTrust, rsaCa),
                ("carlos", carlosSign, carlosKey, edTrust, edCa),
                ("dana", danaSign, danaKey, edTrust, edCa),
            })
            {
                string cell = $"sign/{kind}/{who}";
                Cell(cell, D, () =>
                {
                    string from = cert.SubjectAlternativeName!.Names.First(static n => n.Kind == GeneralNameKind.Rfc822Name).Value!;
                    var o = new SmimeSignOptions { Signers = [new CmsSignerSpec { Certificate = cert, Key = key, DigestAlgorithm = key.PublicKey.KeyAlgorithm == KeyAlgorithm.Ed25519 ? "2.16.840.1.101.3.4.2.3" : null }], Certificates = [ca] };
                    MimeEntity m = opaque ? SmimeSigner.SignOpaque(Content(cell), o) : SmimeSigner.SignDetached(Content(cell), o);
                    string path = Msg(cell, m, from);
                    SmimeResult r = SmimeProcessor.Open(File.ReadAllBytes(path), Open(trust));
                    (bool ok, string detail) = OursOk(r);
                    if (ok && r.Signers.Single().SenderMatch != SmimeSenderMatch.Match)
                    {
                        return (false, "sender " + r.Signers.Single().SenderMatch);
                    }

                    return (ok, detail);
                });
            }
        }

        foreach ((string who, Certificate enc, Func<CmsDecryptionKey> key) in new (string, Certificate, Func<CmsDecryptionKey>)[]
        {
            ("alice", aliceEnc, () => CmsDecryptionKey.FromRsa(aliceDecKey, aliceEnc)),
            ("bob", bobEnc, () => CmsDecryptionKey.FromRsa(bobDecKey, bobEnc)),
            ("carlos", carlosEnc, () => CmsDecryptionKey.FromX25519(carlosX, carlosEnc)),
            ("dana", danaEnc, () => CmsDecryptionKey.FromX25519(danaX, danaEnc)),
        })
        {
            foreach (SmimeEncryptionKind kind in new[] { SmimeEncryptionKind.Enveloped, SmimeEncryptionKind.AuthEnveloped })
            {
                string cell = $"encrypt/{kind}/{who}";
                Cell(cell, D, () =>
                {
                    string path = Msg(cell, SmimeEncryptor.Encrypt(Content(cell), new SmimeEncryptOptions { Recipients = [enc], Kind = kind }), "alice@example.test");
                    SmimeResult r = SmimeProcessor.Open(File.ReadAllBytes(path), new SmimeOpenOptions { DecryptionKeys = [key()] });
                    return r.Succeeded && r.InnerItem!.TextBody!.ReadText().Contains(Marker, StringComparison.Ordinal) ? (true, "") : (false, string.Join(",", r.Layers.Select(static l => l.Status + " " + l.Detail)));
                });
            }
        }

        foreach (Row r in Rows)
        {
            Console.WriteLine($"{(r.Ok ? "pass" : "FAIL")} {r.Direction} {r.Cell} {r.Detail}");
        }

        Console.WriteLine($"rfc9216: {Rows.Count(static r => r.Ok)}/{Rows.Count} pass");
        return Rows.All(static r => r.Ok) ? 0 : 1;
    }

    // ---- identities ----

    private static CertificateIssueOptions Opts(string email, DateTimeOffset notBefore, CertificateProfile profile) =>
        new() { Profile = profile, NotBefore = notBefore, SubjectAlternativeNames = [GeneralName.Email(email)] };

    private static Id MakeRsa(CertificateAuthority ca, string n, DateTimeOffset nb)
    {
        string email = n + "@example.test";
        var rsa = RSA.Create(2048);
        Certificate c = ca.Issue(PublicKeyInfo.FromAsymmetricAlgorithm(rsa), DistinguishedName.FromString("CN=" + n + ",E=" + email), Opts(email, nb, CertificateProfile.SMime));
        return Save(n, email, c, PrivateKey.FromRsa(rsa), rsa.ExportPkcs8PrivateKeyPem(), () => CmsDecryptionKey.FromRsa(rsa, c), c.ToX509Certificate2().CopyWithPrivateKey(rsa));
    }

    private static readonly CertificateProfile EcProfile = new()
    {
        Name = "smime-ec",
        KeyUsages = KeyUsages.DigitalSignature | KeyUsages.NonRepudiation | KeyUsages.KeyAgreement,
        ExtendedKeyUsages = [KnownOids.EmailProtection],
        SubjectAlternativeNameRequired = true,
        AllowedSubjectAlternativeNames = [GeneralNameKind.Rfc822Name],
        MaxValidity = TimeSpan.FromDays(825),
    };

    private static Id MakeEc(CertificateAuthority ca, string n, DateTimeOffset nb)
    {
        string email = n + "@example.test";
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Certificate c = ca.Issue(PublicKeyInfo.FromAsymmetricAlgorithm(ec), DistinguishedName.FromString("CN=" + n + ",E=" + email), Opts(email, nb, EcProfile));
        ECDiffieHellman dh = ECDiffieHellman.Create(ec.ExportParameters(true));
        return Save(n, email, c, PrivateKey.FromECDsa(ec), ec.ExportPkcs8PrivateKeyPem(), () => CmsDecryptionKey.FromECDiffieHellman(dh, c), c.ToX509Certificate2().CopyWithPrivateKey(ec));
    }

    private static Id MakeEd(CertificateAuthority ca, string n, DateTimeOffset nb)
    {
        string email = n + "@example.test";
        var k = Ed25519PrivateKey.Generate();
        ISigningKey sk = PrivateKey.FromEd25519(k);
        var profile = new CertificateProfile
        {
            Name = "smime-ed",
            KeyUsages = KeyUsages.DigitalSignature,
            ExtendedKeyUsages = [KnownOids.EmailProtection],
            SubjectAlternativeNameRequired = true,
            AllowedSubjectAlternativeNames = [GeneralNameKind.Rfc822Name],
            MaxValidity = TimeSpan.FromDays(825),
        };
        Certificate c = ca.Issue(sk.PublicKey, DistinguishedName.FromString("CN=" + n + ",E=" + email), Opts(email, nb, profile));
        byte[] seed = new byte[32];
        k.ExportSeed(seed);
        // RFC 8410 §7: PrivateKeyInfo with the Ed25519 OID and the seed in an OCTET STRING inside an OCTET STRING.
        byte[] pkcs8 = Convert.FromHexString("302e020100300506032b657004220420").Concat(seed).ToArray();
        string pem = "-----BEGIN PRIVATE KEY-----\n" + Convert.ToBase64String(pkcs8) + "\n-----END PRIVATE KEY-----\n";
        return Save(n, email, c, sk, pem, null, null);
    }

    private static Id Save(string n, string email, Certificate c, ISigningKey key, string keyPem, Func<CmsDecryptionKey>? dec, X509Certificate2? withKey)
    {
        string crt = Path.Combine(work, n + ".crt"), kp = Path.Combine(work, n + ".key"), pfx = Path.Combine(work, n + ".pfx");
        File.WriteAllText(crt, c.ToPem());
        File.WriteAllText(kp, keyPem);
        if (withKey is not null)
        {
            File.WriteAllBytes(pfx, withKey.Export(X509ContentType.Pkcs12, "pw"));
        }

        return new Id(n, email, c, key, crt, kp, pfx, dec);
    }

    // ---- helpers ----

    private static (int Exit, string Out, string Err) Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        Task<string> o = p.StandardOutput.ReadToEndAsync();
        Task<string> e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60000))
        {
            p.Kill(true);
        }

        return (p.ExitCode, o.Result, e.Result);
    }

    private static void Cell(string name, string direction, Func<(bool Ok, string Detail)> body)
    {
        try
        {
            (bool ok, string detail) = body();
            Rows.Add(new Row(name, direction, ok, detail));
        }
        catch (Exception e)
        {
            Rows.Add(new Row(name, direction, false, "exception " + e.GetType().Name + ": " + e.Message.Split('\n')[0]));
        }
    }

    private static MimePart Content(string cell)
    {
        var p = new MimePart("text/plain; charset=utf-8");
        p.SetDecodedContent(Encoding.UTF8.GetBytes(Marker + " " + cell + "\r\ncaf\u00e9 line two\r\n"), OneMime.Transfer.ContentTransferEncoding.QuotedPrintable);
        return p;
    }

    private static string Msg(string cell, MimeEntity e, string from)
    {
        e.Headers.Add("MIME-Version", "1.0");
        e.Headers.Add("From", from);
        e.Headers.Add("To", "bob@example.test");
        e.Headers.Add("Subject", cell);
        string path = Path.Combine(work, cell.Replace('/', '_') + ".eml");
        File.WriteAllBytes(path, e.ToArray());
        return path;
    }

    private static SmimeOpenOptions Trusting(params CmsDecryptionKey[] keys) => Trusting(false, keys);

    private static SmimeOpenOptions Trusting(bool allowLegacy, params CmsDecryptionKey[] keys) => new()
    {
        AllowLegacyCiphers = allowLegacy,
        DecryptionKeys = keys,
        Chain = new ValidationOptions { TrustAnchors = [TrustAnchor.FromCertificate(root)], ReferenceTime = now },
    };

    private static (bool, string) OursOk(SmimeResult r, string? expectText = Marker)
    {
        if (!r.Succeeded)
        {
            return (false, "layers: " + string.Join(", ", r.Layers.Select(static l => l.Kind + "=" + l.Status + (l.Detail is null ? "" : " (" + l.Detail + ")"))));
        }

        foreach (SmimeSignerResult s in r.Signers)
        {
            if (s.Chain != SmimeChainStatus.Passed)
            {
                return (false, "chain " + s.Chain);
            }
        }

        string text = r.InnerItem?.TextBody?.ReadText() ?? "";
        return text.Contains(expectText ?? "", StringComparison.Ordinal) ? (true, "") : (false, "content missing: " + text.Length + " chars");
    }

    // ---- ours -> openssl ----

    private static void OursToOpenSsl(Id alice, Id bob, Id carol, Id eve)
    {
        const string D = "ours->openssl";
        var kinds = new (string Name, Id Who, SignatureSpec? Spec, string? Digest)[]
        {
            ("rsa-pkcs1", alice, null, null),
            ("rsa-pss", alice, SignatureSpec.RsaPss(HashAlgorithmName.SHA256), null),
            ("ecdsa-p256", carol, null, null),
            ("ed25519", eve, SignatureSpec.Ed25519(), "2.16.840.1.101.3.4.2.3"),
        };
        foreach ((string name, Id who, SignatureSpec? spec, string? digest) in kinds)
        {
            foreach (bool opaque in new[] { false, true })
            {
                string cell = $"sign/{(opaque ? "opaque" : "detached")}/{name}";
                Cell(cell, D, () =>
                {
                    var o = new SmimeSignOptions { Signers = [new CmsSignerSpec { Certificate = who.Cert, Key = who.Key, SignatureSpec = spec, DigestAlgorithm = digest }], Certificates = [root] };
                    MimeEntity m = opaque ? SmimeSigner.SignOpaque(Content(cell), o) : SmimeSigner.SignDetached(Content(cell), o);
                    string path = Msg(cell, m, who.Email);
                    string outp = Path.Combine(work, "out-" + cell.Replace('/', '_') + ".txt");
                    var r = Run(openssl, ["cms", "-verify", "-in", path, "-CAfile", rootPem, "-out", outp]);
                    bool ok = r.Exit == 0 && File.ReadAllText(outp).Contains(Marker, StringComparison.Ordinal);
                    // OpenSSL 3.0 refuses an Ed25519 CMS signer that names a digest; later releases accept it. Not ours to fix.
                    if (!ok && r.Err.Contains("eddsa_digest_signverify_init:invalid digest", StringComparison.Ordinal))
                    {
                        return (false, "oracle cannot: " + r.Err.Trim().Split('\n')[0]);
                    }

                    return (ok, ok ? "" : $"exit {r.Exit}: {r.Err.Trim()}");
                });
            }
        }

        var encs = new (string Name, Id To, SmimeEncryptionKind Kind, string? Cipher, KeyTransportScheme? Scheme, bool Legacy)[]
        {
            ("enveloped/cbc256/rsa-oaep-sha256", bob, SmimeEncryptionKind.Enveloped, null, KeyTransportScheme.OaepSha256, false),
            ("enveloped/cbc128/rsa-oaep-sha1", bob, SmimeEncryptionKind.Enveloped, CmsOids128Cbc, KeyTransportScheme.OaepSha1, false),
            ("enveloped/cbc256/rsa-pkcs1v15", bob, SmimeEncryptionKind.Enveloped, null, KeyTransportScheme.Pkcs1V15, true),
            ("enveloped/cbc256/ecdh-p256", carol, SmimeEncryptionKind.Enveloped, null, null, false),
            ("authenveloped/gcm256/rsa-oaep-sha256", bob, SmimeEncryptionKind.AuthEnveloped, null, KeyTransportScheme.OaepSha256, false),
            ("authenveloped/gcm256/ecdh-p256", carol, SmimeEncryptionKind.AuthEnveloped, null, null, false),
        };
        foreach ((string name, Id to, SmimeEncryptionKind kind, string? cipher, KeyTransportScheme? scheme, bool legacy) in encs)
        {
            string cell = "encrypt/" + name;
            Cell(cell, D, () =>
            {
                var o = new SmimeEncryptOptions { Recipients = [to.Cert], Kind = kind, ContentEncryption = cipher, KeyTransport = scheme ?? KeyTransportScheme.OaepSha256, AllowLegacy = legacy };
                string path = Msg(cell, SmimeEncryptor.Encrypt(Content(cell), o), "alice@example.test");
                string outp = Path.Combine(work, "out-" + cell.Replace('/', '_') + ".txt");
                var r = Run(openssl, ["cms", "-decrypt", "-in", path, "-recip", to.CrtPath, "-inkey", to.KeyPath, "-out", outp]);
                bool ok = r.Exit == 0 && File.ReadAllText(outp).Contains(Marker, StringComparison.Ordinal);
                return (ok, ok ? "" : $"exit {r.Exit}: {r.Err.Trim()}");
            });
        }

        foreach (bool opaque in new[] { false, true })
        {
            string cell = "nested/sign-then-encrypt/" + (opaque ? "opaque" : "detached");
            Cell(cell, D, () =>
            {
                var so = new SmimeSignOptions { Signers = [new CmsSignerSpec { Certificate = alice.Cert, Key = alice.Key }], Certificates = [root] };
                MimeEntity signed = opaque ? SmimeSigner.SignOpaque(Content(cell), so) : SmimeSigner.SignDetached(Content(cell), so);
                string path = Msg(cell, SmimeEncryptor.Encrypt(signed, new SmimeEncryptOptions { Recipients = [bob.Cert] }), alice.Email);
                string o1 = Path.Combine(work, "n1-" + cell.Replace('/', '_') + ".txt");
                var r1 = Run(openssl, ["cms", "-decrypt", "-in", path, "-recip", bob.CrtPath, "-inkey", bob.KeyPath, "-out", o1]);
                if (r1.Exit != 0)
                {
                    return (false, "decrypt: " + r1.Err.Trim());
                }

                string o2 = Path.Combine(work, "n2-" + cell.Replace('/', '_') + ".txt");
                var r2 = Run(openssl, ["cms", "-verify", "-in", o1, "-CAfile", rootPem, "-out", o2]);
                bool ok = r2.Exit == 0 && File.ReadAllText(o2).Contains(Marker, StringComparison.Ordinal);
                return (ok, ok ? "" : "verify: " + r2.Err.Trim());
            });
        }
    }

    private const string CmsOids128Cbc = "2.16.840.1.101.3.4.1.2";

    // ---- openssl -> ours ----

    private static string PlainFile(string cell)
    {
        string path = Path.Combine(work, "in-" + cell.Replace('/', '_') + ".txt");
        File.WriteAllText(path, "Content-Type: text/plain; charset=utf-8\r\n\r\n" + Marker + " " + cell + "\r\nsecond line\r\n");
        return path;
    }

    private static void OursOpens(string cell, string msg, SmimeOpenOptions o)
    {
        Cell(cell, "openssl->ours", () => OursOk(SmimeProcessor.Open(File.ReadAllBytes(msg), o)));
    }

    private static void OpenSslToOurs(Id alice, Id bob, Id carol, Id eve)
    {
        var kinds = new (string Name, Id Who, string[] Extra)[]
        {
            ("rsa-pkcs1", alice, ["-md", "sha256"]),
            ("rsa-pss", alice, ["-md", "sha256", "-keyopt", "rsa_padding_mode:pss"]),
            ("ecdsa-p256", carol, ["-md", "sha256"]),
            ("ed25519", eve, ["-md", "sha512"]),
        };
        foreach ((string name, Id who, string[] extra) in kinds)
        {
            foreach (bool opaque in new[] { false, true })
            {
                string cell = $"sign/{(opaque ? "opaque" : "detached")}/{name}";
                string msg = Path.Combine(work, "ossl-" + cell.Replace('/', '_') + ".eml");
                var args = new List<string> { "cms", "-sign", "-in", PlainFile(cell), "-signer", who.CrtPath, "-inkey", who.KeyPath, "-certfile", rootPem, "-from", who.Email, "-to", "bob@example.test", "-subject", cell, "-out", msg };
                args.AddRange(extra);
                if (opaque)
                {
                    args.Add("-nodetach");
                }

                var r = Run(openssl, args);
                if (r.Exit != 0)
                {
                    Rows.Add(new Row(cell, "openssl->ours", false, "oracle cannot: " + r.Err.Trim().Split('\n')[0]));
                    continue;
                }

                OursOpens(cell, msg, Trusting());
            }
        }

        var encs = new (string Name, Id To, string[] Extra)[]
        {
            ("enveloped/cbc256/rsa-pkcs1v15", bob, ["-aes-256-cbc"]),
            ("enveloped/cbc256/rsa-oaep-sha1", bob, ["-aes-256-cbc", "-keyopt", "rsa_padding_mode:oaep"]),
            ("enveloped/cbc128/rsa-oaep-sha256", bob, ["-aes-128-cbc", "-keyopt", "rsa_padding_mode:oaep", "-keyopt", "rsa_oaep_md:sha256"]),
            ("enveloped/cbc256/ecdh-p256", carol, ["-aes-256-cbc", "-keyopt", "ecdh_kdf_md:sha256"]),
            ("authenveloped/gcm256/rsa-oaep-sha256", bob, ["-aes-256-gcm", "-keyopt", "rsa_padding_mode:oaep", "-keyopt", "rsa_oaep_md:sha256"]),
            ("authenveloped/gcm256/ecdh-p256", carol, ["-aes-256-gcm", "-keyopt", "ecdh_kdf_md:sha256"]),
        };
        foreach ((string name, Id to, string[] extra) in encs)
        {
            string cell = "encrypt/" + name;
            string msg = Path.Combine(work, "ossl-" + cell.Replace('/', '_') + ".eml");
            var args = new List<string> { "cms", "-encrypt", "-in", PlainFile(cell), "-from", "alice@example.test", "-to", to.Email, "-subject", cell, "-out", msg };
            // `-keyopt` applies to the `-recip` before it, so the recipient comes first.
            args.InsertRange(args.Count, extra.Where(static e => e.StartsWith("-aes", StringComparison.Ordinal)));
            args.AddRange(["-recip", to.CrtPath]);
            args.AddRange(extra.Where(static e => !e.StartsWith("-aes", StringComparison.Ordinal)));
            var r = Run(openssl, args);
            if (r.Exit != 0)
            {
                Rows.Add(new Row(cell, "openssl->ours", false, "oracle cannot: " + r.Err.Trim().Split('\n')[0]));
                continue;
            }

            OursOpens(cell, msg, Trusting(to.Decrypt!()));
        }

        foreach (bool opaque in new[] { false, true })
        {
            string cell = "nested/sign-then-encrypt/" + (opaque ? "opaque" : "detached");
            string signed = Path.Combine(work, "ossl-n1-" + cell.Replace('/', '_') + ".eml");
            string msg = Path.Combine(work, "ossl-n2-" + cell.Replace('/', '_') + ".eml");
            var s = new List<string> { "cms", "-sign", "-in", PlainFile(cell), "-signer", alice.CrtPath, "-inkey", alice.KeyPath, "-certfile", rootPem, "-md", "sha256", "-out", signed };
            if (opaque)
            {
                s.Add("-nodetach");
            }

            var r1 = Run(openssl, s);
            var r2 = r1.Exit == 0
                ? Run(openssl, ["cms", "-encrypt", "-in", signed, "-from", alice.Email, "-to", bob.Email, "-subject", cell, "-aes-256-cbc", "-recip", bob.CrtPath, "-keyopt", "rsa_padding_mode:oaep", "-out", msg])
                : r1;
            if (r2.Exit != 0)
            {
                Rows.Add(new Row(cell, "openssl->ours", false, "oracle cannot: " + r2.Err.Trim().Split('\n')[0]));
                continue;
            }

            OursOpens(cell, msg, Trusting(bob.Decrypt!()));
        }
    }

    // ---- MimeKit ----

    private static void MimeKitCells(Id alice, Id bob, Id carol)
    {
        const string To = "ours->mimekit", From = "mimekit->ours";
        using var ctx = new TemporarySecureMimeContext();
        ctx.Import(alice.Pfx, "pw");
        ctx.Import(bob.Pfx, "pw");
        ctx.Import(carol.Pfx, "pw");

        MimeKit.MimePart Part(string cell)
        {
            var p = new MimeKit.TextPart("plain") { Text = Marker + " " + cell + "\ncaf\u00e9 line two\n" };
            return p;
        }

        // ours -> MimeKit
        foreach (bool opaque in new[] { false, true })
        {
            foreach (string pad in new[] { "pkcs1", "pss", "ecdsa" })
            {
                string cell = $"sign/{(opaque ? "opaque" : "detached")}/{(pad == "ecdsa" ? "ecdsa-p256" : "rsa-" + pad)}";
                Id who = pad == "ecdsa" ? carol : alice;
                Cell(cell, To, () =>
                {
                    SignatureSpec? spec = pad == "pss" ? SignatureSpec.RsaPss(HashAlgorithmName.SHA256) : null;
                    var o = new SmimeSignOptions { Signers = [new CmsSignerSpec { Certificate = who.Cert, Key = who.Key, SignatureSpec = spec }], Certificates = [root] };
                    MimeEntity m = opaque ? SmimeSigner.SignOpaque(Content(cell), o) : SmimeSigner.SignDetached(Content(cell), o);
                    string path = Msg(cell, m, who.Email);
                    var msg = MimeMessage.Load(path);
                    DigitalSignatureCollection sigs;
                    string text;
                    if (msg.Body is MultipartSigned ms)
                    {
                        sigs = ms.Verify(ctx);
                        text = ((MimeKit.TextPart)ms[0]).Text;
                    }
                    else
                    {
                        sigs = ((ApplicationPkcs7Mime)msg.Body!).Verify(ctx, out MimeKit.MimeEntity? content);
                        text = ((MimeKit.TextPart)content).Text;
                    }

                    bool ok = sigs.Count == 1 && sigs.All(static s => s.Verify(true)) && text.Contains(Marker, StringComparison.Ordinal);
                    return (ok, ok ? "" : "signatures " + sigs.Count);
                });
            }
        }

        foreach (string pad in new[] { "oaep-sha256", "pkcs1v15" })
        {
            string cell = "encrypt/enveloped/cbc256/rsa-" + pad;
            Cell(cell, To, () =>
            {
                var o = new SmimeEncryptOptions { Recipients = [bob.Cert], KeyTransport = pad == "pkcs1v15" ? KeyTransportScheme.Pkcs1V15 : KeyTransportScheme.OaepSha256, AllowLegacy = pad == "pkcs1v15" };
                string path = Msg(cell, SmimeEncryptor.Encrypt(Content(cell), o), alice.Email);
                var msg = MimeMessage.Load(path);
                MimeKit.MimeEntity? dec = ((ApplicationPkcs7Mime)msg.Body!).Decrypt(ctx);
                bool ok = dec is MimeKit.TextPart t && t.Text.Contains(Marker, StringComparison.Ordinal);
                return (ok, ok ? "" : "decrypted " + dec.GetType().Name);
            });
        }

        // MimeKit -> ours
        foreach (bool opaque in new[] { false, true })
        {
            foreach (string pad in new[] { "pkcs1", "pss", "ecdsa" })
            {
                string cell = $"sign/{(opaque ? "opaque" : "detached")}/{(pad == "ecdsa" ? "ecdsa-p256" : "rsa-" + pad)}";
                Id who = pad == "ecdsa" ? carol : alice;
                Cell(cell, From, () =>
                {
                    var signer = new CmsSigner(who.Pfx, "pw") { DigestAlgorithm = DigestAlgorithm.Sha256, RsaSignaturePadding = pad == "pss" ? RsaSignaturePadding.Pss : RsaSignaturePadding.Pkcs1 };
                    MimeKit.MimeEntity body = opaque ? ApplicationPkcs7Mime.Sign(ctx, signer, Part(cell)) : MultipartSigned.Create(ctx, signer, Part(cell));
                    string path = Path.Combine(work, "mk-" + cell.Replace('/', '_') + ".eml");
                    var message = new MimeMessage { Subject = cell, Body = body };
                    message.From.Add(new MailboxAddress(who.Name, who.Email));
                    message.To.Add(new MailboxAddress("bob", bob.Email));
                    message.WriteTo(path);
                    return OursOk(SmimeProcessor.Open(File.ReadAllBytes(path), Trusting()));
                });
            }
        }

        foreach (string pad in new[] { "oaep-sha256", "pkcs1v15" })
        {
            string cell = "encrypt/enveloped/cbc256/rsa-" + pad;
            Cell(cell, From, () =>
            {
                var rcpt = new MkRecipient(X509CertificateLoader.LoadPkcs12FromFile(bob.Pfx, "pw")) { RsaEncryptionPadding = pad == "pkcs1v15" ? RsaEncryptionPadding.Pkcs1 : RsaEncryptionPadding.OaepSha256, EncryptionAlgorithms = [EncryptionAlgorithm.Aes256] };
                MimeKit.MimeEntity body = ApplicationPkcs7Mime.Encrypt(ctx, new CmsRecipientCollection { rcpt }, Part(cell));
                string path = Path.Combine(work, "mk-" + cell.Replace('/', '_') + ".eml");
                var message = new MimeMessage { Subject = cell, Body = body };
                message.From.Add(new MailboxAddress("alice", alice.Email));
                message.To.Add(new MailboxAddress("bob", bob.Email));
                message.WriteTo(path);
                return OursOk(SmimeProcessor.Open(File.ReadAllBytes(path), Trusting(bob.Decrypt!())));
            });
        }

        {
            // MimeKit's default content cipher is Triple DES: refused by default, read with the explicit flag.
            foreach (bool allow in new[] { false, true })
            {
                string cell = "encrypt/enveloped/3des/default-" + (allow ? "allowed" : "refused");
                Cell(cell, From, () =>
                {
                    var rcpt = new MkRecipient(X509CertificateLoader.LoadPkcs12FromFile(bob.Pfx, "pw")) { RsaEncryptionPadding = RsaEncryptionPadding.OaepSha256, EncryptionAlgorithms = [EncryptionAlgorithm.TripleDes] };
                    MimeKit.MimeEntity body = ApplicationPkcs7Mime.Encrypt(ctx, new CmsRecipientCollection { rcpt }, Part(cell));
                    string path = Path.Combine(work, "mk-" + cell.Replace('/', '_') + ".eml");
                    var message = new MimeMessage { Subject = cell, Body = body };
                    message.From.Add(new MailboxAddress("alice", alice.Email));
                    message.To.Add(new MailboxAddress("bob", bob.Email));
                    message.WriteTo(path);
                    SmimeResult r = SmimeProcessor.Open(File.ReadAllBytes(path), Trusting(allow, bob.Decrypt!()));
                    return allow ? OursOk(r) : (r.Layers.Count == 1 && r.Layers[0].Status == SmimeStatus.Unsupported, r.Layers.Count == 1 ? r.Layers[0].Status.ToString() : "no layer");
                });
            }
        }

        {
            const string cell = "nested/sign-then-encrypt/detached";
            Cell(cell, From, () =>
            {
                var signer = new CmsSigner(alice.Pfx, "pw") { DigestAlgorithm = DigestAlgorithm.Sha256 };
                var rcpt = new MkRecipient(X509CertificateLoader.LoadPkcs12FromFile(bob.Pfx, "pw")) { RsaEncryptionPadding = RsaEncryptionPadding.OaepSha256, EncryptionAlgorithms = [EncryptionAlgorithm.Aes256] };
                MimeKit.MimeEntity body = ApplicationPkcs7Mime.Encrypt(ctx, new CmsRecipientCollection { rcpt }, MultipartSigned.Create(ctx, signer, Part(cell)));
                string path = Path.Combine(work, "mk-" + cell.Replace('/', '_') + ".eml");
                var message = new MimeMessage { Subject = cell, Body = body };
                message.From.Add(new MailboxAddress("alice", alice.Email));
                message.To.Add(new MailboxAddress("bob", bob.Email));
                message.WriteTo(path);
                return OursOk(SmimeProcessor.Open(File.ReadAllBytes(path), Trusting(bob.Decrypt!())));
            });
        }
    }
}
