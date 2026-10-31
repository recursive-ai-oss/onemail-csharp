using System.Text;
using RecursiveAI.OneMail.Composition;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mime;

namespace RecursiveAI.OneMail.Tests.Composition;

public class CompositionTests
{
    private static readonly DateTimeOffset Now = new(2024, 3, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CompositionOptions Options(bool noMid = false, string[]? own = null, ForwardStyle style = ForwardStyle.Inline) => new()
    {
        TimeProvider = new FixedClock(),
        RandomBytes = b => b.Fill(0x5A),
        MessageIdDomain = noMid ? null : "me.example",
        OwnAddresses = own ?? [],
        From = new MailAddress("Me", "me@me.example"),
        ForwardStyle = style,
    };

    private static MimeMailItem Eml(string headers, string body) =>
        MimeMailItem.Parse(Encoding.UTF8.GetBytes(headers.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n" + body.Replace("\n", "\r\n", StringComparison.Ordinal)));

    private const string Basic =
        "From: Ann <ann@x.example>\nTo: Me <me@me.example>, Bob <bob@x.example>\nCc: cy@x.example, BOB@x.example\n" +
        "Subject: Re: RE: Fwd: Plans\nDate: Mon, 4 Mar 2024 09:00:00 +0100\nMessage-ID: <m3@x.example>\n" +
        "References: <m1@x.example> <m2@x.example>\nIn-Reply-To: <m2@x.example>\nContent-Type: text/plain; charset=utf-8\n";

    private static MimeEntity Reparse(ComposedMessage c) => MimeEntity.Parse(c.Message.ToArray());

    [Theory]
    [InlineData("Plans", "Plans")]
    [InlineData("Re: RE: Fwd: AW: Plans", "Plans")]
    [InlineData("Re[2]: Plans", "Plans")]
    [InlineData("Antw: WG: Plans", "Plans")]
    [InlineData("Status: Plans", "Status: Plans")]
    [InlineData("Re:", "")]
    [InlineData("  fw : Plans", "Plans")]
    [InlineData("Resume", "Resume")]
    public void SubjectPrefixesNeverStack(string input, string expected) => Assert.Equal(expected, SubjectPrefixes.Strip(input));

    [Fact]
    public void ReplyThreadsAndAddresses()
    {
        ComposedMessage c = Eml(Basic, "hello\n").CreateReply(Options());
        MimeEntity m = Reparse(c);
        Assert.Equal("Re: Plans", m.Headers.GetFirst("Subject")!.Value);
        Assert.Equal("<m3@x.example>", m.Headers.GetFirst("In-Reply-To")!.Value);
        Assert.Equal("<m1@x.example> <m2@x.example> <m3@x.example>", m.Headers.GetFirst("References")!.Value);
        Assert.Equal("ann@x.example", Assert.Single(c.To).Address);
        Assert.Empty(c.Cc);
        Assert.StartsWith("<5a5a", m.Headers.GetFirst("Message-ID")!.Value, StringComparison.Ordinal);
        Assert.Equal("Tue, 5 Mar 2024 12:00:00 +0000", m.Headers.GetFirst("Date")!.Value);
        Assert.Null(m.Headers.GetFirst("Thread-Index"));
        string body = Encoding.UTF8.GetString(((MimePart)m).GetDecodedContent());
        Assert.Contains("On Mon, 4 Mar 2024 09:00:00 +0100, Ann <ann@x.example> wrote:\r\n> hello\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencesFallBackToInReplyToAndTruncate()
    {
        ComposedMessage a = Eml("From: a@x.example\nMessage-ID: <b@x>\nIn-Reply-To: <a@x>\n", "x\n").CreateReply(Options());
        Assert.Equal("<a@x> <b@x>", Reparse(a).Headers.GetFirst("References")!.Value);

        string refs = string.Join(' ', Enumerable.Range(1, 60).Select(i => "<r" + i + "@x>"));
        ComposedMessage t = Eml("From: a@x.example\nMessage-ID: <last@x>\nReferences: " + refs + "\n", "x\n").CreateReply(new CompositionOptions { MaxReferences = 5, TimeProvider = new FixedClock() });
        string[] ids = Reparse(t).Headers.GetFirst("References")!.Value.Split(' ');
        Assert.Equal(["<r1@x>", "<r58@x>", "<r59@x>", "<r60@x>", "<last@x>"], ids);
        Assert.Contains(t.Defects, d => d.Kind == CompositionDefectKind.ReferencesTruncated);
    }

    [Fact]
    public void MissingAndBrokenIdsAreDefects()
    {
        ComposedMessage c = Eml("From: a@x.example\nReferences: <ok@x> <bad id@x>\n", "x\n").CreateReply(Options());
        MimeEntity m = Reparse(c);
        Assert.Null(m.Headers.GetFirst("In-Reply-To"));
        Assert.Contains(c.Defects, d => d.Kind == CompositionDefectKind.ParentHasNoMessageId);
        // The reader already joins the white-space split identifier; what is written is always well-formed.
        Assert.Equal("<ok@x> <badid@x>", m.Headers.GetFirst("References")!.Value);
    }

    [Fact]
    public void ReplyToAndMailReplyToWin()
    {
        Assert.Equal("list@x.example", Assert.Single(Eml("From: a@x.example\nReply-To: list@x.example\n", "x\n").CreateReply(Options()).To).Address);
        Assert.Equal("mrt@x.example", Assert.Single(Eml("From: a@x.example\nReply-To: list@x.example\nMail-Reply-To: mrt@x.example\n", "x\n").CreateReply(Options()).To).Address);
    }

    [Fact]
    public void ReplyAllDropsOwnAndDuplicates()
    {
        ComposedMessage c = Eml(Basic, "hello\n").CreateReplyAll(Options(own: ["ME@me.example"]));
        Assert.Equal(["ann@x.example", "bob@x.example"], c.To.Select(a => a.Address));
        Assert.Equal(["cy@x.example"], c.Cc.Select(a => a.Address));
    }

    [Fact]
    public void MailFollowupToReplacesTheList()
    {
        ComposedMessage c = Eml(Basic + "Mail-Followup-To: list@x.example, ann@x.example\n", "x\n").CreateReplyAll(Options());
        Assert.Equal(["list@x.example", "ann@x.example"], c.To.Select(a => a.Address));
        Assert.Empty(c.Cc);
    }

    [Fact]
    public void ReplyToOwnMessageGoesToItsRecipients()
    {
        ComposedMessage c = Eml("From: me@me.example\nTo: Bob <bob@x.example>\n", "x\n").CreateReply(Options(own: ["me@me.example"]));
        Assert.Equal("bob@x.example", Assert.Single(c.To).Address);
    }

    [Fact]
    public void BrokenAndExchangeAddressesAreReported()
    {
        MimeToMapiConverter conv = new();
        var item = new MapiMailItem(conv.Convert(MimeEntity.Parse(Encoding.UTF8.GetBytes("From: a@x.example\r\nTo: nobody, ok@x.example\r\n\r\nx\r\n"))).Value, default);
        ComposedMessage c = item.CreateReplyAll(Options());
        Assert.DoesNotContain(c.To, a => !a.Address.Contains('@', StringComparison.Ordinal));
        Assert.Contains(c.Defects, d => d.Kind == CompositionDefectKind.InvalidAddressDropped || d.Kind == CompositionDefectKind.UnaddressableRecipient);
        Assert.Contains(c.To, a => a.Address == "ok@x.example");
    }

    [Fact]
    public void NonAsciiNamesAndSubjectsAreEncoded()
    {
        MimeMailItem p = Eml("From: =?utf-8?B?SsO8cmdlbg==?= <j@x.example>\nSubject: =?utf-8?Q?caf=C3=A9?=\nMessage-ID: <q@x>\n", "x\n");
        ComposedMessage c = p.CreateReply(Options());
        byte[] bytes = c.Message.ToArray();
        Assert.True(System.Text.Ascii.IsValid(bytes.AsSpan(0, Array.IndexOf(bytes, (byte)'\n', Encoding.ASCII.GetString(bytes).IndexOf("\r\n\r\n", StringComparison.Ordinal)))));
        MimeEntity m = MimeEntity.Parse(bytes);
        Assert.Equal("Jürgen", Assert.Single(m.GetAddresses("To")).Name);
        Assert.Equal("Re: café", c.Subject);
    }

    [Fact]
    public void InjectionThroughDecodedHeadersIsNeutralised()
    {
        // An encoded word can decode to a line break; it must not start a new header in the reply.
        MimeMailItem p = Eml("From: \"A=?utf-8?Q?=0D=0ABcc:_evil@x.example?=\" <a@x.example>\nSubject: =?utf-8?Q?s=0D=0ABcc:_evil@x.example?=\n", "x\n");
        MimeEntity m = Reparse(p.CreateReply(Options()));
        Assert.Null(m.Headers.GetFirst("Bcc"));
    }

    [Fact]
    public void FlowedQuotingKeepsSoftBreaks()
    {
        MimeMailItem p = Eml("From: a@x.example\nContent-Type: text/plain; charset=utf-8; format=flowed\n", "long line \nsecond\n> quoted\n\nend\n");
        ComposedMessage c = p.CreateReply(Options());
        MimePart part = (MimePart)Reparse(c);
        Assert.Equal("flowed", part.GetContentTypeParameter("format"));
        string body = Encoding.UTF8.GetString(part.GetDecodedContent());
        Assert.Contains("> long line \r\n> second\r\n>> quoted\r\n>\r\n> end\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlRepliesQuoteInABlockquoteAndCarryInlineImages()
    {
        const string eml = "From: a@x.example\nMessage-ID: <h@x>\nSubject: pics\nContent-Type: multipart/related; boundary=r\n\n--r\nContent-Type: text/html; charset=utf-8\n\n<html><body><p>hi <img src=\"cid:img1\"></p></body></html>\n--r\nContent-Type: image/png\nContent-ID: <img1>\nContent-Disposition: inline\nContent-Transfer-Encoding: base64\n\niVBORw0K\n--r--\n";
        MimeMailItem p = MimeMailItem.Parse(Encoding.UTF8.GetBytes(eml.Replace("\n", "\r\n", StringComparison.Ordinal)));
        ComposedMessage c = p.CreateReply(Options());
        var rel = Assert.IsType<MimeMultipart>(Reparse(c));
        Assert.Equal("multipart/related", rel.MediaType);
        string html = Encoding.UTF8.GetString(((MimePart)rel.Children[0]).GetDecodedContent());
        Assert.Contains("<blockquote type=\"cite\"><p>hi <img src=\"cid:img1\"></p></blockquote>", html, StringComparison.Ordinal);
        Assert.Equal("<img1>", rel.Children[1].Headers.GetFirst("Content-ID")!.Value);
    }

    [Fact]
    public void InlineForwardCarriesHeaderBlockAndAttachments()
    {
        const string eml = "From: Ann <a@x.example>\nTo: b@x.example\nSubject: report\nDate: Mon, 4 Mar 2024 09:00:00 +0000\nContent-Type: multipart/mixed; boundary=m\n\n--m\nContent-Type: text/plain\n\nbody\n--m\nContent-Type: application/pdf; name=r.pdf\nContent-Disposition: attachment; filename=r.pdf\nContent-Transfer-Encoding: base64\n\nAAEC\n--m--\n";
        ComposedMessage c = MimeMailItem.Parse(Encoding.UTF8.GetBytes(eml.Replace("\n", "\r\n", StringComparison.Ordinal))).CreateForward(Options());
        var mixed = Assert.IsType<MimeMultipart>(Reparse(c));
        string text = Encoding.UTF8.GetString(((MimePart)mixed.Children[0]).GetDecodedContent());
        Assert.Contains("---------- Forwarded message ----------\r\nFrom: Ann <a@x.example>\r\nDate: Mon, 4 Mar 2024 09:00:00 +0000\r\nSubject: report\r\nTo: b@x.example\r\n\r\nbody", text, StringComparison.Ordinal);
        Assert.Equal([0, 1, 2], ((MimePart)mixed.Children[1]).GetDecodedContent());
        Assert.Equal("Fwd: report", c.Subject);
        Assert.Null(Reparse(c).Headers.GetFirst("In-Reply-To"));
    }

    [Fact]
    public void AttachmentForwardEmbedsTheOriginalByteForByte()
    {
        // Malformed on purpose: bare LFs, a duplicated header and no final newline survive untouched.
        byte[] original = Encoding.Latin1.GetBytes("From: a@x.example\nSubject: odd\nSubject: twice\n\nbare lf body\r\nmixed\n\xE9");
        ComposedMessage c = MimeMailItem.Parse(original).CreateForward(Options(style: ForwardStyle.Attachment));
        var top = Assert.IsType<MimeMultipart>(Reparse(c));
        var emb = Assert.IsType<MimeMessagePart>(top.Children[1]);
        Assert.Equal(original, emb.Message!.ToArray());
        Assert.Equal("odd.eml", emb.FileName);
    }

    [Fact]
    public void ConversationIndexLevelsDecode()
    {
        byte[] header = ConversationIndex.CreateHeader(Now.AddHours(-1), Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        Assert.Equal(22, header.Length);
        Assert.Equal(1, header[0]);
        Assert.Equal([0x00, 0x11, 0x22, 0x33], header[6..10]);
        byte[]? child = ConversationIndex.AppendLevel(header, Now, 7, out bool clamped);
        Assert.False(clamped);
        Assert.True(ConversationIndex.TryParse(child, out ConversationIndexInfo? info));
        Assert.Equal(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), info!.ConversationId);
        ConversationIndexLevel level = Assert.Single(info.Levels);
        Assert.False(level.DeltaCode);
        Assert.Equal((byte)7, level.Random);
        // The header stores the time to 2^24 ticks (1.7 s); the delta is in units of 2^18 ticks.
        Assert.InRange((Now - info.HeaderTime - level.Elapsed).TotalSeconds, -0.1, 0.1);
        Assert.InRange((Now - info.HeaderTime).TotalMinutes, 59, 61);

        // Past about 1.7 years the delta code flips and the unit grows to 2^23 ticks.
        byte[] old = ConversationIndex.CreateHeader(Now.AddYears(-3), Guid.NewGuid());
        ConversationIndexLevel far = ConversationIndex.TryParse(ConversationIndex.AppendLevel(old, Now, 0, out _), out ConversationIndexInfo? i2) ? i2!.Levels[0] : default;
        Assert.True(far.DeltaCode);
        Assert.InRange((Now.AddYears(-3) - Now + far.Elapsed).Duration().TotalDays, 0, 1.1);
    }

    [Fact]
    public void ConversationIndexIsExtendedInReplies()
    {
        byte[] header = ConversationIndex.CreateHeader(Now.AddMinutes(-5), Guid.NewGuid());
        string b64 = Convert.ToBase64String(header);
        MimeMailItem p = Eml("From: a@x.example\nSubject: Re: topic\nThread-Topic: topic\nThread-Index: " + b64 + "\n", "x\n");
        ComposedMessage c = p.CreateReply(Options());
        MimeEntity m = Reparse(c);
        byte[] idx = Convert.FromBase64String(m.Headers.GetFirst("Thread-Index")!.Value);
        Assert.Equal(27, idx.Length);
        Assert.Equal(header, idx[..22]);
        Assert.Equal("topic", m.Headers.GetFirst("Thread-Topic")!.Value);

        // Without an index there is none, unless asked for.
        Assert.Null(Reparse(Eml("From: a@x.example\n", "x\n").CreateReply(Options())).Headers.GetFirst("Thread-Index"));
        byte[] fresh = Convert.FromBase64String(Reparse(Eml("From: a@x.example\nSubject: s\nDate: Mon, 4 Mar 2024 09:00:00 +0000\n", "x\n").CreateReply(new CompositionOptions { TimeProvider = new FixedClock(), ConversationIndex = ConversationIndexMode.Always })).Headers.GetFirst("Thread-Index")!.Value);
        Assert.Equal(27, fresh.Length);
    }

    [Fact]
    public void MapiRepliesCarryThreadingPropertiesExactly()
    {
        byte[] header = ConversationIndex.CreateHeader(Now.AddMinutes(-5), Guid.NewGuid());
        const string h = "From: Ann <a@x.example>\r\nTo: me@me.example\r\nSubject: Fwd: Plans\r\nMessage-ID: <m9@x>\r\nReferences: <m1@x> <m2@x>\r\nThread-Topic: Plans\r\n";
        var src = new MapiMailItem(new MimeToMapiConverter().Convert(MimeEntity.Parse(Encoding.UTF8.GetBytes(h + "Thread-Index: " + Convert.ToBase64String(header) + "\r\n\r\nbody\r\n"))).Value, default);
        ComposedMessage c = src.CreateReply(Options());
        Assert.NotNull(c.Mapi);
        var view = new MapiMailItem(c.Mapi!, default);
        Assert.Equal("Re: Plans", view.Subject);
        Assert.Equal(["m9@x"], view.InReplyTo);
        Assert.Equal(["m1@x", "m2@x", "m9@x"], view.References);
        Assert.Equal("Plans", view.ConversationTopic);
        Assert.Equal(27, view.ConversationIndex.Length);
        Assert.Equal(header, view.ConversationIndex.Span[..22].ToArray());
        Assert.Equal("Re: ", c.Mapi!.GetString(RecursiveAI.OneMail.Mapi.MapiPid.SubjectPrefix));
        Assert.Equal("Plans", c.Mapi.GetString(RecursiveAI.OneMail.Mapi.MapiPid.NormalizedSubject));

        // MAPI -> MIME -> MAPI keeps them.
        MimeEntity back = new MapiToMimeConverter().Convert(c.Mapi).Value;
        var again = new MapiMailItem(new MimeToMapiConverter().Convert(back).Value, default);
        Assert.Equal(view.ConversationIndex.ToArray(), again.ConversationIndex.ToArray());
        Assert.Equal(view.References, again.References);
        Assert.Equal(view.InReplyTo, again.InReplyTo);
    }

    [Fact]
    public void OutlookForwardAsAttachmentEmbedsAMessage()
    {
        var src = new MapiMailItem(new MimeToMapiConverter().Convert(MimeEntity.Parse(Encoding.UTF8.GetBytes("From: a@x.example\r\nSubject: inner\r\n\r\nbody\r\n"))).Value, default);
        ComposedMessage c = src.CreateForward(Options(style: ForwardStyle.Attachment));
        Assert.Single(c.Mapi!.Attachments);
        Assert.NotNull(c.Mapi.Attachments[0].EmbeddedMessage);
        Assert.Equal("inner", c.Mapi.Attachments[0].EmbeddedMessage!.Subject);
    }

    [Fact]
    public void DisplayNamesWithSpecialsStayOneMailbox()
    {
        // A name that holds an @ and a non-ASCII word must not leave the @ in plain text between encoded words.
        MimeMailItem p = Eml("From: =?utf-8?B?VGVzdHMgQ2hhbmdAZnQg5bqG?= <a@x.example>\nReply-To: \"x@y\" <b@x.example>\n", "x\n");
        MimeEntity m = Reparse(p.CreateReply(Options()));
        MimeMailbox box = Assert.IsType<MimeMailbox>(Assert.Single(m.GetAddresses("To")));
        Assert.Equal("b@x.example", box.Address);
        MimeMailItem q = Eml("From: =?utf-8?B?VGVzdHMgQ2hhbmdAZnQg5bqG?= <a@x.example>\n", "x\n");
        MimeMailbox box2 = Assert.IsType<MimeMailbox>(Assert.Single(Reparse(q.CreateReply(Options())).GetAddresses("To")));
        Assert.Equal("a@x.example", box2.Address);
        Assert.Equal("Tests Chang@ft 庆", box2.Name);
    }

    [Fact]
    public void AddressesWithWhiteSpaceCharactersAreDropped()
    {
        ComposedMessage c = Eml("From: a@x.example\nTo: \"a\u00a0b\"@x.example, ok@x.example\n", "x\n").CreateReplyAll(Options());
        Assert.DoesNotContain(c.To, a => a.Address.Contains('\u00a0', StringComparison.Ordinal));
    }

    [Fact]
    public void HostileParentsAreBounded()
    {
        string many = string.Join(", ", Enumerable.Range(0, 3000).Select(i => "u" + i + "@x.example"));
        ComposedMessage c = Eml("From: a@x.example\nTo: " + many + "\nMessage-ID: <x>\n", "x\n").CreateReplyAll(new CompositionOptions { MaxRecipients = 50 });
        Assert.Equal(50, c.To.Count + c.Cc.Count);
        Assert.Contains(c.Defects, d => d.Kind == CompositionDefectKind.TooManyRecipients);

        string big = new('y', 3_000_000);
        ComposedMessage q = Eml("From: a@x.example\nSubject: big\n", big + "\n").CreateReply(new CompositionOptions { MaxQuoteCharacters = 1000 });
        Assert.Contains(q.Defects, d => d.Kind == CompositionDefectKind.QuoteTruncated);
        Assert.True(q.Message.ToArray().Length < 5000);

        ComposedMessage none = Eml("Subject: no sender\n", "x\n").CreateReply(Options());
        Assert.Contains(none.Defects, d => d.Kind == CompositionDefectKind.NoReplyTarget);
        Assert.Equal("Re: no sender", none.Subject);
    }
}
