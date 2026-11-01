using System.Security.Cryptography;
using RecursiveAI.OneCrypto;
using RecursiveAI.OneCrypto.Issuance;
using RecursiveAI.OneCrypto.Keys;
using RecursiveAI.OneCrypto.X509;
using RecursiveAI.OneMail.Cryptography.Smime;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Fuzz;

// S/MIME MIME structure on hostile input. The first byte picks a mode: 0 reads the rest as a message; 1 to 3 take a valid
// multipart/signed, enveloped or triple-wrapped message and apply (position, value) edits from the rest, so signature-valid
// and signature-broken paths both get deep coverage. Only typed results may come out of SmimeProcessor.Open.
internal static class SmimeTargets
{
    private static readonly Lazy<Templates> Fixture = new(Templates.Build);

    private sealed class Templates
    {
        public required byte[] Signed { get; init; }

        public required byte[] Enveloped { get; init; }

        public required byte[] Triple { get; init; }

        public required SmimeOpenOptions Options { get; init; }

        public static Templates Build()
        {
            var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var caKey = RSA.Create(2048);
            var ca = CertificateAuthority.CreateRoot(DistinguishedName.FromString("CN=Fuzz Root"), X509Signer.FromRsa(caKey, HashAlgorithmName.SHA256), notBefore, TimeSpan.FromDays(3650));
            var rsa = RSA.Create(2048);
            Certificate cert = ca.Issue(PublicKeyInfo.FromAsymmetricAlgorithm(rsa), DistinguishedName.FromString("CN=fuzz@example.test"), new CertificateIssueOptions
            {
                Profile = CertificateProfile.SMime,
                NotBefore = notBefore,
                SubjectAlternativeNames = [GeneralName.Email("fuzz@example.test")],
            });
            var body = new MimePart("text/plain; charset=utf-8");
            body.SetDecodedContent("fuzz body\r\n"u8, RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.SevenBit);
            var sign = new SmimeSignOptions { Signers = [new CmsSignerSpec { Certificate = cert, Key = PrivateKey.FromRsa(rsa) }], Boundary = "fuzz-b", SigningTime = notBefore };
            var enc = new SmimeEncryptOptions { Recipients = [cert] };

            static byte[] Wire(MimeEntity e)
            {
                e.Headers.Add("From", "fuzz@example.test");
                return e.ToArray();
            }

            MimeMultipart signed = SmimeSigner.SignDetached(body, sign);
            byte[] signedWire = Wire(signed);
            MimePart envelope = SmimeEncryptor.Encrypt(body, enc);
            byte[] envelopedWire = Wire(envelope);
            MimeMultipart inner = SmimeSigner.SignDetached(body, sign);
            MimePart triple = SmimeEncryptor.Encrypt(inner, enc);
            MimeMultipart outer = SmimeSigner.SignDetached(triple, new SmimeSignOptions { Signers = sign.Signers, Boundary = "fuzz-o" });
            return new Templates
            {
                Signed = signedWire,
                Enveloped = envelopedWire,
                Triple = Wire(outer),
                Options = new SmimeOpenOptions { DecryptionKeys = [CmsDecryptionKey.FromRsa(rsa, cert)], Certificates = [cert] },
            };
        }
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        Templates t = Fixture.Value;
        byte mode = (byte)(data[0] & 3);
        ReadOnlySpan<byte> rest = data[1..];
        byte[] message;
        if (mode == 0)
        {
            message = rest.ToArray();
        }
        else
        {
            message = (mode == 1 ? t.Signed : mode == 2 ? t.Enveloped : t.Triple).ToArray();
            for (int i = 0; i + 2 < rest.Length; i += 3)
            {
                int pos = ((rest[i] << 8) | rest[i + 1]) % message.Length;
                message[pos] = rest[i + 2];
            }
        }

        SmimeResult r = SmimeProcessor.Open(message, t.Options);
        if (r.Layers.Count > 8)
        {
            throw new InvalidOperationException("more layers than the depth limit");
        }

        foreach (SmimeLayer layer in r.Layers)
        {
            if (!layer.Succeeded && layer.Status is SmimeStatus.Valid or SmimeStatus.Decrypted or SmimeStatus.Decompressed)
            {
                throw new InvalidOperationException("a successful status on a failed layer");
            }

            if (layer is SmimeSignedLayer { Status: SmimeStatus.Valid } s && (s.Signers.Count == 0 || s.Signers.Any(static x => !x.IsValid)))
            {
                throw new InvalidOperationException("a valid layer without valid signers");
            }
        }
    }
}
