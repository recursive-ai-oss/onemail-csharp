using System.Text;
using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneMail.Cryptography.OpenPgp;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Fuzz;

// PGP/MIME and inline OpenPGP reading. Mode 0 feeds the input as a message; modes 1-5 start from a real signed, encrypted,
// signed+encrypted, cleartext-signed or armored-encrypted message and overwrite bytes, so both the structure checks and the
// signature or decryption failure paths get coverage. Only typed layer statuses may come out of PgpMimeProcessor.Open.
internal static class PgpMimeTarget
{
    private static readonly Lazy<Templates> Fixture = new(Templates.Build);

    private sealed class Templates
    {
        public required byte[][] Messages { get; init; }

        public required PgpOpenOptions Options { get; init; }

        public static Templates Build()
        {
            var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
            PgpCertificate key = PgpKeyGenerator.Generate(new PgpKeyGenerationOptions { Now = now.AddDays(-30), UserIds = ["Fuzz <fuzz@example.test>"] });
            PgpSecretKey signer = PgpSecretKey.Unlock(key.FindSigningKey(now)!);
            MimeEntity Body() => MimeEntity.Parse("Content-Type: text/plain; charset=utf-8\r\n\r\nfuzz body\r\nFrom me\r\n"u8.ToArray());
            static byte[] Wire(MimeEntity e)
            {
                e.Headers.Add("From", "Fuzz <fuzz@example.test>");
                return e.ToArray();
            }

            var sign = new PgpMimeSignOptions { Now = now, Signers = [signer], Boundary = "fuzz-b" };
            var enc = new PgpMimeEncryptOptions { Now = now, Recipients = [key.ToPublicCertificate()], Boundary = "fuzz-e" };
            var both = new PgpMimeEncryptOptions { Now = now, Recipients = [key.ToPublicCertificate()], Signers = [signer], Boundary = "fuzz-c" };

            var clear = new MemoryStream();
            PgpMessageWriter.SignCleartext(new MemoryStream("fuzz line\nsecond\n"u8.ToArray()), clear, new PgpWriteOptions { Now = now, Signers = [signer] });
            var armored = new MemoryStream();
            PgpMessageWriter.Write(new MemoryStream("fuzz secret\n"u8.ToArray()), armored, new PgpWriteOptions { Now = now, Recipients = [key.ToPublicCertificate()], Armor = true });
            static byte[] Text(byte[] t) => [.. "From: fuzz@example.test\r\nContent-Type: text/plain\r\n\r\n"u8, .. t];

            return new Templates
            {
                Messages =
                [
                    [],
                    Wire(PgpMimeSigner.SignDetached(Body(), sign)),
                    Wire(PgpMimeEncryptor.Encrypt(Body(), enc)),
                    Wire(PgpMimeEncryptor.Encrypt(Body(), both)),
                    Text(clear.ToArray()),
                    Text(armored.ToArray()),
                ],
                Options = new PgpOpenOptions { Now = now, Certificates = [key.ToPublicCertificate()], SecretKeys = [key], MaxPlaintextBytes = 1 << 20 },
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
        int mode = data[0] % t.Messages.Length;
        ReadOnlySpan<byte> rest = data[1..];
        byte[] message;
        if (mode == 0)
        {
            message = rest.ToArray();
        }
        else
        {
            message = (byte[])t.Messages[mode].Clone();
            for (int i = 0; i + 2 < rest.Length; i += 3)
            {
                int pos = ((rest[i] << 8) | rest[i + 1]) % message.Length;
                message[pos] = rest[i + 2];
            }
        }

        PgpResult r = PgpMimeProcessor.Open(message, t.Options);
        if (r.Layers.Count > t.Options.MaxDepth)
        {
            throw new InvalidOperationException("more layers than the depth limit");
        }

        foreach (PgpLayer layer in r.Layers)
        {
            if (layer.Succeeded != (layer.Status is PgpStatus.Valid or PgpStatus.Decrypted))
            {
                throw new InvalidOperationException("success and status disagree");
            }

            if (layer.Status == PgpStatus.Valid && (layer.Signers.Count == 0 || layer.Signers.Any(static s => s.Status == PgpSignatureStatus.Invalid)))
            {
                throw new InvalidOperationException("a valid layer without a valid signer");
            }
        }

        _ = PgpAutocrypt.Read(MimeEntity.Parse(message));
    }
}
