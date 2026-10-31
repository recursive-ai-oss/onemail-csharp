using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;

namespace RecursiveAI.OneMail.Tests.Conversion;

public class OxcMailTests
{
    // ---- builders for MAPI messages in memory ---------------------------------------------------------------------------

    private static MapiProperty S(ushort id, string v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    private static MapiProperty I(ushort id, int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return MapiProperty.CreateOwned(new MapiTag(id, MapiType.Integer32), b);
    }

    private static MapiProperty T(ushort id, DateTimeOffset v)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v.UtcDateTime.ToFileTimeUtc());
        return MapiProperty.CreateOwned(new MapiTag(id, MapiType.Time), b);
    }

    private static MapiProperty Bin(ushort id, byte[] v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.Binary), v);

    private static MapiRecipient Rcpt(MapiMessage m, MapiRecipientType type, string name, string address, string addressType = "SMTP", string? smtp = null)
    {
        var r = new MapiRecipient(m);
        r.Properties.Add(I(MapiPid.RecipientType, (int)type));
        r.Properties.Add(S(MapiPid.DisplayName, name));
        r.Properties.Add(S(MapiPid.AddressType, addressType));
        r.Properties.Add(S(MapiPid.EmailAddress, address));
        if (smtp is not null)
        {
            r.Properties.Add(S(MapiPid.SmtpAddress, smtp));
        }

        m.AddRecipient(r);
        return r;
    }

    private static MapiMessage Basic()
    {
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.MessageClass, "IPM.Note"));
        m.Properties.Add(S(MapiPid.Subject, "Quarterly café report"));
        m.Properties.Add(S(MapiPid.SentRepresentingName, "Ann Author"));
        m.Properties.Add(S(MapiPid.SentRepresentingAddressType, "SMTP"));
        m.Properties.Add(S(MapiPid.SentRepresentingEmailAddress, "ann@example.com"));
        m.Properties.Add(T(MapiPid.ClientSubmitTime, new DateTimeOffset(2020, 3, 4, 5, 6, 7, TimeSpan.Zero)));
        m.Properties.Add(S(MapiPid.InternetMessageId, "<m1@example.com>"));
        m.Properties.Add(S(MapiPid.Body, "Hello\r\nworld"));
        Rcpt(m, MapiRecipientType.To, "Bob", "bob@example.com");
        Rcpt(m, MapiRecipientType.Cc, "Carol Ünï", "carol@example.com");
        Rcpt(m, MapiRecipientType.Bcc, "Dan", "dan@example.com");
        return m;
    }

    private static MimeMailItem Parse(MimeEntity e) => MimeMailItem.Parse(e.ToArray());

    // ---- MAPI to MIME ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ExportsEnvelopeBodyAndReportsBcc()
    {
        ConversionResult<MimeEntity> r = new MapiToMimeConverter().Convert(Basic());
        MimeMailItem back = Parse(r.Value);
        Assert.Equal("Quarterly café report", back.Subject);
        Assert.Equal("ann@example.com", Assert.Single(back.From).Address);
        Assert.Equal("Ann Author", back.From[0].Name);
        Assert.Equal("bob@example.com", Assert.Single(back.ToRecipients).Address);
        Assert.Equal("Carol Ünï", Assert.Single(back.Cc).Name);
        Assert.Empty(back.Bcc);
        Assert.Equal(new DateTimeOffset(2020, 3, 4, 5, 6, 7, TimeSpan.Zero), back.Sent?.Value);
        Assert.Equal("m1@example.com", back.MessageId);
        Assert.Equal("Hello\r\nworld", back.TextBody?.ReadText());
        Assert.Empty(back.Defects);
        Assert.Contains(r.Report, e => e.Code == "BccNotWritten" && e.Kind == LossKind.Dropped);

        var withBcc = new MapiToMimeConverter(new OxcMailExportOptions { IncludeBcc = true }).Convert(Basic());
        Assert.Equal("dan@example.com", Assert.Single(Parse(withBcc.Value).Bcc).Address);
    }

    [Fact]
    public void WritesImportanceSensitivityCategoriesAndThreadFields()
    {
        MapiMessage m = Basic();
        m.Properties.Add(I(MapiPid.Importance, 2));
        m.Properties.Add(I(MapiPid.Sensitivity, 3));
        m.Properties.Add(S(MapiPid.ConversationTopic, "topic"));
        m.Properties.Add(Bin(MapiPid.ConversationIndex, [1, 2, 3]));
        m.Properties.Add(MapiProperty.CreateMulti(new MapiTag(0x8001, MapiType.MultipleString), [Encoding.Unicode.GetBytes("red"), Encoding.Unicode.GetBytes("blue")], new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords")));
        MimeEntity e = new MapiToMimeConverter().Convert(m).Value;
        Assert.Equal("High", e.Headers.GetFirst("Importance")?.Value);
        Assert.Equal("Company-Confidential", e.Headers.GetFirst("Sensitivity")?.Value);
        Assert.Equal("red, blue", e.Headers.GetFirst("Keywords")?.Value);
        Assert.Equal("topic", e.Headers.GetFirst("Thread-Topic")?.Value);
        Assert.Equal("AQID", e.Headers.GetFirst("Thread-Index")?.Value);
    }

    [Fact]
    public void BuildsAlternativeRelatedAndMixedStructure()
    {
        MapiMessage m = Basic();
        m.Properties.Add(Bin(MapiPid.Html, "<p>Hi <img src=\"cid:pic@x\"></p>"u8.ToArray()));
        m.Properties.Add(I(MapiPid.InternetCodepage, 65001));
        var inline = new MapiAttachment(m);
        inline.Properties.Add(I(MapiPid.AttachMethod, 1));
        inline.Properties.Add(Bin(MapiPid.AttachDataBinary, [1, 2, 3, 4]));
        inline.Properties.Add(S(MapiPid.AttachLongFilename, "pic.png"));
        inline.Properties.Add(S(MapiPid.AttachMimeTag, "image/png"));
        inline.Properties.Add(S(MapiPid.AttachContentId, "pic@x"));
        m.AddAttachment(inline);
        var file = new MapiAttachment(m);
        file.Properties.Add(I(MapiPid.AttachMethod, 1));
        file.Properties.Add(Bin(MapiPid.AttachDataBinary, [9, 8, 7]));
        file.Properties.Add(S(MapiPid.AttachLongFilename, "näme.bin"));
        m.AddAttachment(file);

        MimeEntity root = new MapiToMimeConverter().Convert(m).Value;
        var mixed = Assert.IsType<MimeMultipart>(root);
        Assert.Equal("multipart/mixed", mixed.MediaType);
        var alt = Assert.IsType<MimeMultipart>(mixed.Children[0]);
        Assert.Equal("multipart/alternative", alt.MediaType);
        var related = Assert.IsType<MimeMultipart>(alt.Children[1]);
        Assert.Equal("multipart/related", related.MediaType);

        MimeMailItem back = Parse(root);
        Assert.Contains("cid:pic@x", back.HtmlBody?.ReadText());
        Assert.Equal(["pic.png", "näme.bin"], back.Attachments.Select(a => a.FileName));
        Assert.True(back.Attachments[0].IsInline);
        Assert.Equal("pic@x", back.Attachments[0].ContentId);
        Assert.Equal([9, 8, 7], back.Attachments[1].Open().ReadAllBytes());
    }

    [Fact]
    public void ExportsEmbeddedMessagesAsRfc822()
    {
        MapiMessage inner = Basic();
        var a = new MapiAttachment(null);
        a.Properties.Add(I(MapiPid.AttachMethod, 5));
        a.SetEmbeddedMessage(inner);
        MapiMessage outer = Basic();
        outer.AddAttachment(a);
        MimeMailItem back = Parse(new MapiToMimeConverter().Convert(outer).Value);
        IMailAttachment att = Assert.Single(back.Attachments);
        Assert.Equal("message/rfc822", att.ContentType);
        Assert.Equal("Quarterly café report", att.Embedded?.Subject);
    }

    [Fact]
    public void DeencapsulatesRtfOnlyHtml()
    {
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.Subject, "rtf"));
        m.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress(RtfEncapsulator.EncapsulateHtml("<b>Bold ü</b>"))));
        ConversionResult<MimeEntity> r = new MapiToMimeConverter().Convert(m);
        MimeMailItem back = Parse(r.Value);
        Assert.Contains("<b>Bold ü</b>", back.HtmlBody?.ReadText());
        Assert.Contains(r.Report, e => e.Code == "HtmlFromRtf");

        // The view reads the same body from the same property.
        var view = new MapiMailItem(m, default);
        Assert.Contains("<b>Bold ü</b>", view.HtmlBody?.ReadText());
        Assert.StartsWith("{\\rtf1", view.RtfBody?.ReadText(), StringComparison.Ordinal);
    }

    [Fact]
    public void CarriesPlainRtfAsTextRtf()
    {
        var m = new MapiMessage();
        m.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress("{\\rtf1\\ansi Plain {\\b bold}}"u8)));
        ConversionResult<MimeEntity> r = new MapiToMimeConverter().Convert(m);
        Assert.Equal("text/rtf", r.Value.MediaType);
        Assert.Contains(r.Report, e => e.Code == "RtfCarriedAsTextRtf");
    }

    [Fact]
    public void TransportHeadersAreTheBaseAndPropertiesWinOnConflict()
    {
        MapiMessage m = Basic();
        m.Properties.Add(S(MapiPid.TransportMessageHeaders,
            "Received: from a by b; Mon, 1 Jan 2020 00:00:00 +0000\r\nX-Custom: kept\r\nSubject: Old subject\r\nFrom: Ann Author <ann@example.com>\r\nContent-Type: text/html\r\n\r\n"));
        ConversionResult<MimeEntity> r = new MapiToMimeConverter().Convert(m);
        MimeEntity e = r.Value;
        Assert.Equal("kept", e.Headers.GetFirst("X-Custom")?.Value);
        Assert.NotNull(e.Headers.GetFirst("Received"));
        Assert.Equal("Quarterly café report", Parse(e).Subject);
        Assert.Single(e.Headers.GetAll("Content-Type"));
        Assert.Contains(r.Report, x => x.Code == "TransportHeaderOverridden" && x.Name == "Subject");
        Assert.Contains(r.Report, x => x.Code == "TransportHeadersRewritten");
        // A header that agrees with the properties is written as it was received.
        Assert.Equal("Ann Author <ann@example.com>", e.Headers.GetFirst("From")?.Value);
    }

    [Fact]
    public void ResolvesExchangeAddressesAndKeepsTheX500Form()
    {
        const string dn = "/o=Org/ou=First/cn=Recipients/cn=ann";
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.SenderName, "Ann"));
        m.Properties.Add(S(MapiPid.SenderAddressType, "EX"));
        m.Properties.Add(S(MapiPid.SenderEmailAddress, dn));
        m.Properties.Add(S(MapiPid.SenderSmtpAddress, "ann@example.com"));
        m.Properties.Add(S(MapiPid.SentRepresentingName, "Ann"));
        m.Properties.Add(S(MapiPid.SentRepresentingAddressType, "EX"));
        m.Properties.Add(S(MapiPid.SentRepresentingEmailAddress, dn));
        // The recipient table knows the sender's SMTP address, and another recipient has none anywhere.
        Rcpt(m, MapiRecipientType.To, "Ann", dn, "EX", "ann@example.com");
        Rcpt(m, MapiRecipientType.To, "Zed", "/o=Org/cn=zed", "EX");

        var view = new MapiMailItem(m, default);
        MailAddress from = Assert.Single(view.From);
        Assert.Equal("ann@example.com", from.Address);
        Assert.Equal(dn, from.X500Address);
        Assert.Equal(2, view.ToRecipients.Count);
        Assert.Equal("/o=Org/cn=zed", view.ToRecipients[1].Address);
        Assert.Equal("/o=Org/cn=zed", view.ToRecipients[1].X500Address);

        ConversionResult<MimeEntity> r = new MapiToMimeConverter().Convert(m);
        MimeMailItem back = Parse(r.Value);
        Assert.Equal("ann@example.com", Assert.Single(back.From).Address);
        Assert.Equal("ann@example.com", Assert.Single(back.ToRecipients).Address);
        Assert.Contains(r.Report, e => e.Code == "AddressUnresolved" && e.Name == "To");
        Assert.Contains(r.Report, e => e.Code == "ExchangeAddressWrittenAsSmtp");
    }

    [Fact]
    public void ResolvesAnExchangeSenderFromTheTransportFromField()
    {
        var m = new MapiMessage();
        m.Properties.Add(S(MapiPid.SentRepresentingName, "Ann"));
        m.Properties.Add(S(MapiPid.SentRepresentingAddressType, "EX"));
        m.Properties.Add(S(MapiPid.SentRepresentingEmailAddress, "/o=x/cn=ann"));
        m.Properties.Add(S(MapiPid.TransportMessageHeaders, "From: Ann <ann@corp.example>\r\nTo: a@b, c@d\r\n\r\n"));
        Assert.Equal("ann@corp.example", Assert.Single(new MapiMailItem(m, default).From).Address);
    }

    // ---- the view ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ViewReadsReplyToIdsFromTransportAndTextFromRtf()
    {
        var m = new MapiMessage();
        m.Properties.Add(Bin(MapiPid.ReplyRecipientEntries, OneOffList(("Rae", "SMTP", "rae@example.com"))));
        m.Properties.Add(S(MapiPid.TransportMessageHeaders, "Message-ID: <t1@x>\r\nIn-Reply-To: <t0@x>\r\nReferences: <a@x> <b@x>\r\n\r\n"));
        m.Properties.Add(Bin(MapiPid.RtfCompressed, RtfCompressor.Compress(RtfEncapsulator.EncapsulateText("plain ü"))));
        var view = new MapiMailItem(m, default);
        Assert.Equal(new MailAddress("Rae", "rae@example.com"), Assert.Single(view.ReplyTo));
        Assert.Equal("t1@x", view.MessageId);
        Assert.Equal(["t0@x"], view.InReplyTo);
        Assert.Equal(["a@x", "b@x"], view.References);
        Assert.Contains("plain ü", view.TextBody?.ReadText());
        Assert.Null(view.HtmlBody);
    }

    private static byte[] OneOffList(params (string Name, string Type, string Address)[] entries)
    {
        var list = new List<byte[]>();
        foreach ((string n, string t, string a) in entries)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[4]);
            ms.Write([0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02]);
            ms.Write([0, 0, 0x80, 0]);
            foreach (string s in new[] { n, t, a })
            {
                ms.Write(Encoding.Unicode.GetBytes(s + "\0"));
            }

            list.Add(ms.ToArray());
        }

        var o = new MemoryStream();
        o.Write(BitConverter.GetBytes(list.Count));
        o.Write(BitConverter.GetBytes(list.Sum(x => 4 + ((x.Length + 3) & ~3))));
        foreach (byte[] e in list)
        {
            o.Write(BitConverter.GetBytes(e.Length));
            o.Write(e);
            o.Write(new byte[(4 - (e.Length & 3)) & 3]);
        }

        return o.ToArray();
    }

    // ---- MIME to MAPI ---------------------------------------------------------------------------------------------------------

    private const string Sample =
        "Received: from a by b; Tue, 4 Mar 2020 05:06:10 +0000\r\nFrom: =?utf-8?B?QW5uIMOcbmk=?= <ann@example.com>\r\nTo: Bob <bob@example.com>, Team: x@example.com, y@example.com;\r\n" +
        "Cc: carol@example.com\r\nReply-To: Rae <rae@example.com>\r\nSubject: RE: Plans\r\nDate: Wed, 4 Mar 2020 06:06:07 +0100\r\nMessage-ID: <m1@example.com>\r\n" +
        "In-Reply-To: <m0@example.com>\r\nReferences: <m-1@example.com> <m0@example.com>\r\nImportance: high\r\nKeywords: red, blue\r\nX-Custom: yes\r\nMIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=B\r\n\r\n--B\r\nContent-Type: multipart/alternative; boundary=A\r\n\r\n--A\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello text\r\n" +
        "--A\r\nContent-Type: text/html; charset=windows-1252\r\n\r\n<p>Hello caf\xE9</p>\r\n--A--\r\n--B\r\nContent-Type: application/octet-stream; name=\"d.bin\"\r\nContent-Disposition: attachment; filename=\"d.bin\"\r\n" +
        "Content-Transfer-Encoding: base64\r\nContent-Description: data\r\n\r\nAQIDBA==\r\n--B\r\nContent-Type: message/rfc822\r\n\r\nSubject: inner\r\nFrom: i@example.com\r\n\r\ninner body\r\n--B--\r\n";

    [Fact]
    public void ImportsHeadersBodiesAndAttachments()
    {
        MimeEntity src = MimeEntity.Parse(Encoding.Latin1.GetBytes(Sample));
        ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(src);
        MapiMessage m = r.Value;
        Assert.Equal("IPM.Note", m.MessageClass);
        Assert.Equal("RE: Plans", m.Subject);
        Assert.Equal("RE: ", m.GetString(MapiPid.SubjectPrefix));
        Assert.Equal("Plans", m.GetString(MapiPid.NormalizedSubject));
        Assert.Equal("ann@example.com", m.GetString(MapiPid.SentRepresentingSmtpAddress));
        Assert.Equal("Ann Üni", m.GetString(MapiPid.SentRepresentingName));
        Assert.Equal(new DateTimeOffset(2020, 3, 4, 5, 6, 7, TimeSpan.Zero), m.GetTime(MapiPid.ClientSubmitTime));
        Assert.Equal(new DateTimeOffset(2020, 3, 4, 5, 6, 10, TimeSpan.Zero), m.GetTime(MapiPid.MessageDeliveryTime));
        Assert.Equal("<m1@example.com>", m.GetString(MapiPid.InternetMessageId));
        Assert.Equal("<m-1@example.com> <m0@example.com>", m.GetString(MapiPid.InternetReferences));
        Assert.Equal(2, m.GetInt32(MapiPid.Importance));
        Assert.Equal("Hello text", m.GetString(MapiPid.Body));
        Assert.Equal(1252, m.GetInt32(MapiPid.InternetCodepage));
        Assert.Contains("café", new MapiMailItem(m, default).HtmlBody?.ReadText());
        Assert.StartsWith("Received: from a by b", m.GetString(MapiPid.TransportMessageHeaders), StringComparison.Ordinal);

        // To: two members of the group and Bob, then Cc.
        Assert.Equal(["bob@example.com", "x@example.com", "y@example.com", "carol@example.com"], m.Recipients.Select(x => x.SmtpAddress));
        Assert.Equal(new MailAddress("Rae", "rae@example.com"), Assert.Single(new MapiMailItem(m, default).ReplyTo));
        Assert.Contains(r.Report, e => e.Code == "GroupFlattened" && e.Name == "To");
        Assert.Contains(r.Report, e => e.Code == "DateZoneNotStored");
        Assert.Contains(r.Report, e => e.Code == "PartHeaderNotMapped" && e.Name == "Content-Description" && e.Path == "attachment[0]");

        // The arbitrary field and the keywords are named properties.
        Assert.Equal("yes", m.Properties.Get(new MapiNamedProperty(MapiPropertySets.InternetHeaders, "X-Custom"))?.AsString());
        Assert.Equal(2, m.Properties.Get(new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords"))?.ValueCount);

        Assert.Equal(2, m.Attachments.Count);
        Assert.Equal("d.bin", m.Attachments[0].FileName);
        Assert.Equal([1, 2, 3, 4], m.Attachments[0].OpenData()!.ReadAllBytes());
        Assert.Equal("inner", m.Attachments[1].EmbeddedMessage?.Subject);
    }

    [Fact]
    public void ImportReportsAGroupAndKeepsGoingOnGarbage()
    {
        foreach (string text in new[] { "", "no headers at all", "From: <<<\r\nTo: ;;;\r\nDate: nonsense\r\nContent-Type: multipart/mixed\r\n\r\nx", "Thread-Index: !!!\r\n\r\n" })
        {
            ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(MimeEntity.Parse(Encoding.Latin1.GetBytes(text)));
            Assert.NotNull(r.Value);
            Assert.NotNull(new MapiToMimeConverter().Convert(r.Value).Value.ToArray());
        }
    }

    [Fact]
    public void DatesBefore1601AreReportedNotThrown()
    {
        // Found by fuzzing: a year-998 Date field made DateTime.ToFileTimeUtc throw.
        string text = "Date: Thu, 12 Apr 0998 01:10:30 -0500\r\nReceived: from a by b; Thu, 12 Apr 0998 01:10:30 -0500\r\n\r\nx";
        ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(MimeEntity.Parse(Encoding.Latin1.GetBytes(text)));
        Assert.Null(r.Value.Properties.Get(MapiPid.ClientSubmitTime));
        Assert.Contains(r.Report, x => x.Code == "DateBeforeFileTimeEpoch");
    }

    [Fact]
    public void PartHandlerSeesLeavesFirstAndCanClaimThem()
    {
        var seen = new List<string>();
        var options = new OxcMailImportOptions
        {
            PartHandler = (part, message, report) =>
            {
                seen.Add(part.MediaType);
                if (part.MediaType == "application/octet-stream")
                {
                    message.Properties.Add(S(MapiPid.Comment, "claimed"));
                    return true;
                }

                return false;
            },
        };
        MapiMessage m = new MimeToMapiConverter(options).Convert(MimeEntity.Parse(Encoding.Latin1.GetBytes(Sample))).Value;
        Assert.Contains("application/octet-stream", seen);
        Assert.Equal("claimed", m.GetString(MapiPid.Comment));
        Assert.DoesNotContain(m.Attachments, a => a.FileName == "d.bin");
    }

    [Fact]
    public void MapiToMimeToMapiKeepsTheMappedProperties()
    {
        MapiMessage src = Basic();
        src.Properties.Add(S(MapiPid.InReplyToId, "<m0@example.com>"));
        src.Properties.Add(I(MapiPid.Importance, 1));
        MapiMessage back = new MimeToMapiConverter().Convert(MimeEntity.Parse(new MapiToMimeConverter().Convert(src).Value.ToArray())).Value;
        foreach (ushort id in new[] { MapiPid.Subject, MapiPid.InternetMessageId, MapiPid.InReplyToId, MapiPid.Body, MapiPid.SentRepresentingEmailAddress })
        {
            Assert.Equal(src.GetString(id), back.GetString(id));
        }

        Assert.Equal(src.GetTime(MapiPid.ClientSubmitTime), back.GetTime(MapiPid.ClientSubmitTime));
        Assert.Equal(1, back.GetInt32(MapiPid.Importance));
        Assert.Equal(2, back.Recipients.Count(r => r.RecipientType is MapiRecipientType.To or MapiRecipientType.Cc));
    }

    [Fact]
    public void LossReportMergesNestedPaths()
    {
        var outer = new LossReport();
        var inner = new LossReport();
        inner.Add("mapi", "PidTagFoo", LossKind.Dropped, "X", "why", "attachment[0]");
        inner.Add("mapi", "PidTagFoo", LossKind.Dropped, "X", "why", "attachment[0]");
        outer.Merge(inner, "attachment[3]/message");
        LossEntry e = Assert.Single(outer);
        Assert.Equal("attachment[3]/message/attachment[0]", e.Path);
        Assert.True(outer.HasContentLoss);
    }
}

internal static class StreamExtensions
{
    public static byte[] ReadAllBytes(this Stream s)
    {
        using (s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
