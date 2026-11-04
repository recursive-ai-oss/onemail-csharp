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

            // Shapes a reader can be fooled by: unsigned encrypted content repeating headers, a signed part beside an unsigned one in a wrapper,
            // text around a cleartext signature.
            MimeEntity inner = MimeEntity.Parse("Content-Type: text/plain; charset=utf-8; protected-headers=\"v1\"\r\nFrom: Boss <boss@example.test>\r\nSubject: s\r\n\r\nbody\r\n"u8.ToArray());
            var mixed = new MimeMultipart("mixed", "fuzz-m");
            mixed.Add(PgpMimeSigner.SignDetached(Body(), sign));
            mixed.Add(Body());

            // Protected headers written by us, an attached public key, an encrypted attachment and a cleartext signature in an HTML part.
            MimeEntity Draft()
            {
                MimeEntity d = Body();
                d.Headers.Insert(0, new MimeHeader("From", "Fuzz <fuzz@example.test>"));
                d.Headers.Insert(1, new MimeHeader("To", "fuzz@example.test"));
                d.Headers.Insert(2, new MimeHeader("Subject", "Fuzz subject"));
                return d;
            }

            var ph = PgpProtectedHeaders.FromMessage(Draft(), obscureSubject: true);
            var keyed = new MimeMultipart("mixed", "fuzz-k");
            keyed.Add(Body());
            var keyPart = new MimePart("application/pgp-keys");
            keyPart.SetRawContent(key.ToPublicCertificate().ExportPublic(true));
            keyed.Add(keyPart);
            var attData = new MemoryStream();
            PgpMessageWriter.Write(new MemoryStream("fuzz attachment\n"u8.ToArray()), attData, new PgpWriteOptions { Now = now, Recipients = [key.ToPublicCertificate()], Armor = true });
            var attPart = new MimePart("application/octet-stream");
            attPart.Headers.Set("Content-Type", "application/octet-stream; name=\"a.txt.pgp\"");
            attPart.SetRawContent(attData.ToArray());
            keyed.Add(attPart);
            byte[] htmlClear = [.. "From: fuzz@example.test\r\nContent-Type: text/html\r\n\r\n"u8, .. clear.ToArray()];

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
                    Wire(PgpMimeEncryptor.Encrypt(inner, enc)),
                    Wire(mixed),
                    Text([.. "before\r\n"u8, .. clear.ToArray(), .. "\r\nafter\r\n"u8]),
                    Wire(PgpMimeEncryptor.Encrypt(Draft(), new PgpMimeEncryptOptions { Now = now, Recipients = [key.ToPublicCertificate()], Signers = [signer], Boundary = "fuzz-h", ProtectedHeaders = ph })),
                    Wire(PgpMimeSigner.SignDetached(Draft(), new PgpMimeSignOptions { Now = now, Signers = [signer], Boundary = "fuzz-s", ProtectedHeaders = ph })),
                    Wire(PgpMimeEncryptor.Encrypt(keyed, both)),
                    Wire(keyed),
                    htmlClear,
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

        if (!r.IsAuthenticated && r.Outside is { ProtectedHeaders.Count: > 0 })
        {
            throw new InvalidOperationException("headers counted as protected without authentication");
        }

        if (r.IsSenderAuthenticated && !r.Signers.Any(static s => s.IsValid && s.UserIdMatchesSender))
        {
            throw new InvalidOperationException("sender authenticated without a matching valid signer");
        }

        foreach (PgpAttachmentRef a in r.OpenPgpAttachments)
        {
            PgpAttachmentResult ar = PgpMimeProcessor.OpenAttachment(a.Part, t.Options);
            if (ar.Succeeded != (ar.Layer.Status is PgpStatus.Valid or PgpStatus.Decrypted))
            {
                throw new InvalidOperationException("attachment success and status disagree");
            }
        }

        if (r.AttachedKeys.Any(static k => k.Certificates.Any(static c => c.HasSecretKeyMaterial)))
        {
            throw new InvalidOperationException("a secret key surfaced from an attachment");
        }

        _ = PgpDisplay.StatusHtml(r);
        _ = PgpAutocrypt.Read(MimeEntity.Parse(message));
    }
}
