using System.Text;
using RecursiveAI.OneCrypto.Online;
using RecursiveAI.OneCrypto.OpenPgp;
using RecursiveAI.OneMail.Cryptography.OpenPgp;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Cryptography;

/// <summary>Protected headers on write, the recipient check, attached keys and encrypted attachments, HTML bodies and the WKD source.</summary>
public class PgpFollowUpTests
{
    private static readonly PgpCertificate Alice = PgpFixtures.Key("alice@example.org");
    private static readonly PgpCertificate Bob = PgpFixtures.Key("bob@example.org");
    private static readonly PgpCertificate Carol = PgpFixtures.Key("carol@example.org");

    private static MimeEntity Draft(string to = "bob@example.org", string body = "The body.\r\n")
    {
        MimeEntity e = PgpFixtures.Text(body);
        e.Headers.Insert(0, new MimeHeader("From", "Alice <alice@example.org>"));
        e.Headers.Insert(1, new MimeHeader("To", to));
        e.Headers.Insert(2, new MimeHeader("Subject", "The real subject"));
        e.Headers.Insert(3, new MimeHeader("Date", "Mon, 01 Jun 2026 00:00:00 +0000"));
        e.Headers.Insert(4, new MimeHeader("Bcc", "secret@example.org"));
        return e;
    }

    private static PgpMimeEncryptOptions Enc(PgpProtectedHeaders? ph, params PgpCertificate[] to) => new()
    {
        Now = PgpFixtures.Now,
        Recipients = [.. to.Select(static c => c.ToPublicCertificate())],
        Signers = [PgpFixtures.Signer(Alice)],
        ProtectedHeaders = ph,
    };

    [Fact]
    public void SignedProtectedHeadersAreOnTheOuterMessageAndInsideTheSignature()
    {
        MimeEntity draft = Draft();
        MimeMultipart signed = PgpMimeSigner.SignDetached(draft, new PgpMimeSignOptions
        {
            Now = PgpFixtures.Now,
            Signers = [PgpFixtures.Signer(Alice)],
            ProtectedHeaders = PgpProtectedHeaders.FromMessage(draft),
        });
        Assert.Equal("The real subject", signed.Headers.GetFirst("Subject")!.Value);
        Assert.Null(signed.Headers.GetFirst("Bcc"));
        Assert.NotNull(signed.Headers.GetFirst("MIME-Version"));
        string inner = Encoding.UTF8.GetString(((MimeEntity)signed.Children[0]).ToArray());
        Assert.Contains("protected-headers=\"v1\"", inner, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.org", Encoding.UTF8.GetString(signed.ToArray()), StringComparison.Ordinal);

        // A relay or attacker that edits the outer Subject changes nothing a verifier shows.
        MimeEntity tampered = PgpFixtures.Reparse(signed);
        tampered.Headers.Set("Subject", "Pay now");
        PgpResult result = PgpMimeProcessor.Open(tampered, PgpFixtures.Open(verify: Alice));
        Assert.True(result.HeadersProtected);
        Assert.Equal("The real subject", result.InnerItem!.Subject);
        Assert.True(result.IsSenderAuthenticated);
    }

    [Fact]
    public void EncryptedProtectedHeadersCanObscureTheOuterSubject()
    {
        MimeEntity draft = Draft();
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(draft, Enc(PgpProtectedHeaders.FromMessage(draft, obscureSubject: true), Bob));
        Assert.Equal("...", encrypted.Headers.GetFirst("Subject")!.Value);
        Assert.DoesNotContain("The real subject", Encoding.UTF8.GetString(encrypted.ToArray()), StringComparison.Ordinal);

        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(encrypted), PgpFixtures.Open(verify: Alice, secret: Bob));
        Assert.True(result.HeadersProtected);
        Assert.Equal("The real subject", result.InnerItem!.Subject);
        Assert.Contains("Subject", result.Outside!.ProtectedHeaders);
        Assert.DoesNotContain(PgpIssue.ProtectedRecipientMismatch, result.Issues);
        Assert.DoesNotContain(PgpIssue.ProtectedRecipientsMissing, result.Issues);
    }

    [Fact]
    public void SignThenEncryptKeepsTheHeadersInsideBothLayers()
    {
        MimeEntity draft = Draft();
        var ph = PgpProtectedHeaders.FromMessage(draft, obscureSubject: true);
        MimeMultipart signed = PgpMimeSigner.SignDetached(draft, new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(Alice)], ProtectedHeaders = ph });
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(signed, new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()], ProtectedHeaders = ph });
        Assert.Equal("...", encrypted.Headers.GetFirst("Subject")!.Value);

        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(encrypted), PgpFixtures.Open(verify: Alice, secret: Bob));
        Assert.True(result.Succeeded);
        Assert.True(result.HeadersProtected);
        Assert.Equal("The real subject", result.InnerItem!.Subject);
        Assert.DoesNotContain(PgpIssue.ProtectedRecipientMismatch, result.Issues);
    }

    [Fact]
    public void BccCannotBeAProtectedHeader()
    {
        MimeEntity draft = Draft();
        Assert.Throws<ArgumentException>(() => new PgpProtectedHeaders(draft.Headers.Where(static h => h.Name == "Bcc").ToArray()));
    }

    [Fact]
    public void ForwardingASignedMessageToAnotherRecipientIsSeen()
    {
        // Alice writes to Bob only. Bob decrypts and re-encrypts the same signed content to Carol (surreptitious forwarding).
        MimeEntity draft = Draft(to: "bob@example.org");
        MimeMultipart toBob = PgpMimeEncryptor.Encrypt(draft, Enc(PgpProtectedHeaders.FromMessage(draft), Bob));
        PgpResult bobSees = PgpMimeProcessor.Open(PgpFixtures.Reparse(toBob), PgpFixtures.Open(verify: Alice, secret: Bob));
        Assert.DoesNotContain(PgpIssue.ProtectedRecipientMismatch, bobSees.Issues);

        // Sign-then-encrypt carries the signature along: Bob re-encrypts the signed entity to Carol.
        MimeMultipart signed = PgpMimeSigner.SignDetached(draft, new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(Alice)], ProtectedHeaders = PgpProtectedHeaders.FromMessage(draft) });
        MimeMultipart forwarded = PgpMimeEncryptor.Encrypt(signed, new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Carol.ToPublicCertificate()] });
        PgpResult carolSees = PgpMimeProcessor.Open(PgpFixtures.Reparse(forwarded), PgpFixtures.Open(verify: Alice, secret: Carol));
        Assert.True(carolSees.IsAuthenticated);
        Assert.Contains(PgpIssue.ProtectedRecipientMismatch, carolSees.Issues);

        // With the address given explicitly the answer is the same; naming Bob as the reader finds nothing wrong.
        PgpResult asBob = PgpMimeProcessor.Open(PgpFixtures.Reparse(forwarded), new PgpOpenOptions
        {
            Now = PgpFixtures.Now,
            Certificates = [Alice.ToPublicCertificate()],
            SecretKeys = [Carol],
            RecipientAddresses = ["Bob@Example.org"],
        });
        Assert.DoesNotContain(PgpIssue.ProtectedRecipientMismatch, asBob.Issues);
    }

    [Fact]
    public void ProtectedHeadersWithoutRecipientsAreFlagged()
    {
        MimeEntity draft = Draft();
        var ph = PgpProtectedHeaders.FromMessage(draft, names: ["From", "Subject"]);
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(draft, Enc(ph, Bob));
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(encrypted), PgpFixtures.Open(verify: Alice, secret: Bob));
        Assert.Contains(PgpIssue.ProtectedRecipientsMissing, result.Issues);
    }

    [Fact]
    public void AttachedPublicKeysAreSurfacedAndSecretKeysAreNot()
    {
        byte[] armored = Alice.ToPublicCertificate().ExportPublic(true);
        MimeMultipart mixed = new("mixed");
        mixed.Add(PgpFixtures.Text("see my key\r\n"));
        var key = new MimePart("application/pgp-keys");
        key.Headers.Set("Content-Type", "application/pgp-keys; name=\"OpenPGP_0x1.asc\"");
        key.Headers.Add("Content-Disposition", "attachment; filename=\"OpenPGP_0x1.asc\"");
        key.SetRawContent(armored);
        mixed.Add(key);
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(mixed)), PgpFixtures.Open());
        PgpAttachedKey k = Assert.Single(result.AttachedKeys);
        Assert.Equal("2", k.Path);
        Assert.Equal("OpenPGP_0x1.asc", k.FileName);
        Assert.Equal(Alice.Fingerprint, Assert.Single(k.Certificates).Fingerprint);
        Assert.False(k.FromProtectedContent);
        Assert.False(result.IsPgp);
    }

    [Fact]
    public void AKeyInsideEncryptedContentIsMarkedProtected()
    {
        byte[] armored = Alice.ToPublicCertificate().ExportPublic(true);
        MimeMultipart mixed = new("mixed");
        mixed.Add(PgpFixtures.Text("see my key\r\n"));
        var key = new MimePart("application/pgp-keys");
        key.SetRawContent(armored);
        mixed.Add(key);
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(mixed, Enc(null, Bob));
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(encrypted)), PgpFixtures.Open(verify: Alice, secret: Bob));
        PgpAttachedKey k = Assert.Single(result.AttachedKeys);
        Assert.True(k.FromProtectedContent);
        Assert.StartsWith("protected:", k.Path, StringComparison.Ordinal);
        Assert.True(result.IsAuthenticated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncryptedAttachmentsAreOpened(bool armor)
    {
        byte[] payload = Encoding.UTF8.GetBytes("%PDF-fake contents\r\n\u00e9");
        var enc = new MemoryStream();
        PgpMessageWriter.Write(new MemoryStream(payload), enc, new PgpWriteOptions
        {
            Now = PgpFixtures.Now,
            Recipients = [Bob.ToPublicCertificate()],
            Signers = [PgpFixtures.Signer(Alice)],
            Armor = armor,
        });
        MimeMultipart mixed = new("mixed");
        mixed.Add(PgpFixtures.Text("attached\r\n"));
        var att = new MimePart("application/octet-stream");
        att.Headers.Set("Content-Type", "application/octet-stream; name=\"report.pdf.pgp\"");
        att.Headers.Add("Content-Disposition", "attachment; filename=\"report.pdf.pgp\"");
        att.SetDecodedContent(enc.ToArray(), armor ? RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.SevenBit : RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.Base64);
        mixed.Add(att);

        PgpResult listing = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(mixed)), PgpFixtures.Open());
        PgpAttachmentRef r = Assert.Single(listing.OpenPgpAttachments);
        Assert.Equal("report.pdf.pgp", r.FileName);

        PgpAttachmentResult opened = PgpMimeProcessor.OpenAttachment(r.Part, PgpFixtures.Open(verify: Alice, secret: Bob));
        Assert.True(opened.Succeeded, opened.Layer.Detail);
        Assert.True(opened.IsAuthenticated);
        Assert.Equal("report.pdf", opened.FileName);
        Assert.Equal(payload, opened.Data.ToArray());

        PgpAttachmentResult noKey = PgpMimeProcessor.OpenAttachment(r.Part, PgpFixtures.Open(verify: Alice));
        Assert.False(noKey.Succeeded);
        Assert.Empty(noKey.Data.ToArray());
    }

    [Fact]
    public void AnAscFileThatIsNotOpenPgpDataIsNotListed()
    {
        MimeMultipart mixed = new("mixed");
        mixed.Add(PgpFixtures.Text("x\r\n"));
        var att = new MimePart("application/octet-stream");
        att.Headers.Set("Content-Type", "application/octet-stream; name=\"notes.asc\"");
        att.Headers.Add("Content-Disposition", "attachment; filename=\"notes.asc\"");
        att.SetRawContent("just some text\r\n"u8.ToArray());
        mixed.Add(att);
        PgpResult r = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(mixed)), PgpFixtures.Open());
        Assert.Empty(r.OpenPgpAttachments);
        Assert.Empty(r.AttachedKeys);
        Assert.False(PgpMimeProcessor.OpenAttachment(att, PgpFixtures.Open(secret: Bob)).Succeeded);
    }

    private static MimeEntity Html(string html) =>
        MimeEntity.Parse(Encoding.UTF8.GetBytes("Content-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n" + html));

    [Fact]
    public void AHtmlBodyInsideASignedAndAnEncryptedMessageReachesTheSharedView()
    {
        MimeMultipart alt = new("alternative");
        alt.Add(PgpFixtures.Text("plain version\r\n"));
        alt.Add(Html("<p>html <b>version</b></p>\r\n"));
        MimeMultipart signed = PgpMimeSigner.SignDetached(alt, new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(Alice)] });
        MimeMultipart encrypted = PgpMimeEncryptor.Encrypt(signed, new PgpMimeEncryptOptions { Now = PgpFixtures.Now, Recipients = [Bob.ToPublicCertificate()] });
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(encrypted)), PgpFixtures.Open(verify: Alice, secret: Bob));
        Assert.True(result.IsAuthenticated);
        Assert.Contains("<b>version</b>", result.InnerItem!.HtmlBody!.ReadText(), StringComparison.Ordinal);
        Assert.Contains("plain version", result.InnerItem.TextBody!.ReadText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADisplayShowsTheStatusAndNothingThatWasNotSigned()
    {
        MimeMultipart signed = PgpMimeSigner.SignDetached(Html("<p>signed <script>alert(1)</script>words</p>\r\n"), new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(Alice)] });
        MimeMultipart mixed = new("mixed");
        mixed.Add(signed);
        mixed.Add(PgpFixtures.Text("INJECTED by a list\r\n"));
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(mixed)), PgpFixtures.Open(verify: Alice));
        string html = result.ToDisplayHtml().Html;
        Assert.Contains("signed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("INJECTED", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omx-status", html, StringComparison.Ordinal);
        Assert.Contains("not signed or encrypted", html, StringComparison.Ordinal);
        Assert.Contains("The signature is valid", html, StringComparison.Ordinal);

        PgpResult unverified = PgpMimeProcessor.Open(PgpFixtures.Reparse(PgpFixtures.Message(signed)), PgpFixtures.Open());
        Assert.Contains("Not verified", unverified.ToDisplayHtml().Html, StringComparison.Ordinal);

        PgpResult plain = PgpMimeProcessor.Open(PgpFixtures.Message(Html("<p>hello</p>")), PgpFixtures.Open());
        Assert.DoesNotContain("omx-status", plain.ToDisplayHtml().Html, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisplayEscapesTheUserIdInTheStatus()
    {
        PgpCertificate evil = PgpKeyGenerator.Generate(new PgpKeyGenerationOptions
        {
            Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UserIds = ["<img src=x onerror=alert(1)> <alice@example.org>"],
            Profile = PgpKeyProfile.Rfc4880Curve25519,
        });
        MimeMultipart signed = PgpMimeSigner.SignDetached(PgpFixtures.Text("hi\r\n"), new PgpMimeSignOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(evil)] });
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(signed), PgpFixtures.Open(verify: evil));
        string html = result.ToDisplayHtml().Html;
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleartextSignedHtmlPartFlowsThroughAsHtml()
    {
        var o = new MemoryStream();
        PgpMessageWriter.SignCleartext(new MemoryStream(Encoding.UTF8.GetBytes("<p>signed <i>html</i></p>\n")), o, new PgpWriteOptions { Now = PgpFixtures.Now, Signers = [PgpFixtures.Signer(Alice)] });
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(Html(Encoding.UTF8.GetString(o.ToArray()).Replace("\n", "\r\n", StringComparison.Ordinal))), PgpFixtures.Open(verify: Alice));
        Assert.True(result.IsAuthenticated);
        Assert.Contains(PgpIssue.InlineHtmlPart, result.Layers[0].Issues);
        Assert.Contains("<i>html</i>", result.InnerItem!.HtmlBody!.ReadText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnArmoredBlockQuotedInsideOrdinaryHtmlIsNotInlinePgp()
    {
        PgpResult result = PgpMimeProcessor.Open(PgpFixtures.Message(Html("<p>Here is the format: -----BEGIN PGP MESSAGE----- and so on</p>")), PgpFixtures.Open());
        Assert.False(result.IsPgp);
    }

    // ---- WKD ----

    private sealed class FakeDirectory(Dictionary<string, byte[]> files) : ITransport
    {
        public List<string> Requests { get; } = [];

        public ValueTask<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Uri.AbsoluteUri);
            if (files.TryGetValue(request.Uri.AbsolutePath, out byte[]? body))
            {
                return ValueTask.FromResult(new TransportResponse(200, [new("Content-Type", "application/octet-stream")], new MemoryStream(body), null));
            }

            return ValueTask.FromResult(new TransportResponse(404, [], new MemoryStream(), null));
        }
    }

    [Fact]
    public async Task AWkdSourceReturnsTheCertificatesPublishedForTheAddress()
    {
        string hash = WkdAddress.Parse("alice@example.org").Hash;
        byte[] pub = Alice.ToPublicCertificate().ExportPublic(false);
        var dir = new FakeDirectory(new() { ["/.well-known/openpgpkey/hu/" + hash] = pub, ["/.well-known/openpgpkey/example.org/hu/" + hash] = pub });
        var source = new PgpWkdKeySource(new WkdClient(new Fetcher(dir)));

        IReadOnlyList<PgpCertificate> found = await source.FindAsync("alice@example.org", TestContext.Current.CancellationToken);
        Assert.Equal(Alice.Fingerprint, Assert.Single(found).Fingerprint);
        Assert.NotEmpty(dir.Requests);
        Assert.Empty(await source.FindAsync("nobody@example.org", TestContext.Current.CancellationToken));

        (IReadOnlyList<PgpCertificate> usable, IReadOnlyList<string> missing) = await PgpKeySource.ResolveRecipientsAsync(source, ["alice@example.org", "nobody@example.org"], PgpFixtures.Now, TestContext.Current.CancellationToken);
        Assert.Single(usable);
        Assert.Equal(["nobody@example.org"], missing);
    }
}
