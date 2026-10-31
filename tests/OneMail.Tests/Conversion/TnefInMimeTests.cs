using System.Text;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcMail;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Tests.Tnef;
using RecursiveAI.OneMail.Tnef;
using static RecursiveAI.OneMail.Tests.Tnef.TnefTestBuilder;

namespace RecursiveAI.OneMail.Tests.Conversion;

/// <summary>A TNEF part inside a MIME message: the merge into the MAPI message (MS-OXCMAIL 2.2.3.8), the view, and the TNEF export (2.1.3.7).</summary>
public class TnefInMimeTests
{
    private const string Envelope = "From: Alice Example <alice@example.com>\r\nTo: Bob Example <bob@example.com>\r\nSubject: RE: wrapped\r\nDate: Mon, 02 Jan 2023 10:00:00 +0000\r\nMessage-ID: <w@example.com>\r\n";

    private static string TnefPart(byte[] tnef) =>
        "Content-Type: application/ms-tnef; name=\"winmail.dat\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(tnef, Base64FormattingOptions.InsertLineBreaks) + "\r\n";

    // Outlook's shape: envelope fields, a plain text part and winmail.dat.
    private static byte[] Outlook(byte[] tnef, string? extraField = null, bool envelope = true, bool text = true) =>
        Encoding.ASCII.GetBytes((envelope ? Envelope : string.Empty) + "MIME-Version: 1.0\r\n" + extraField + "Content-Type: multipart/mixed; boundary=\"b\"\r\n\r\n--b\r\n" + (text ? "Content-Type: text/plain\r\n\r\nmime text\r\n--b\r\n" : string.Empty) + TnefPart(tnef) + "--b--\r\n");

    private static byte[] WithCorrelation(string? key)
    {
        StreamBuilder b = new StreamBuilder(1).Header().Msg(TnefAttributeIds.MessageClass, Str8("IPM.Note"));
        if (key is not null)
        {
            b.Msg(TnefAttributeIds.MsgProps, List(Binary(0x007F, Encoding.ASCII.GetBytes(key))));
        }

        b.Att(TnefAttributeIds.AttachRendData, RendData(1, -1, -1, -1, 0)).Att(TnefAttributeIds.AttachData, [1, 2, 3]).Att(TnefAttributeIds.AttachTitle, Str8("a.bin"));
        return b.ToArray();
    }

    [Fact]
    public void TnefPropertiesRecipientsAndAttachmentsBecomeTheMessages()
    {
        byte[] tnef = Rich(1252, everyType: false);
        TnefMessage reference = TnefMessage.Read(tnef);
        ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(MimeEntity.Parse(Outlook(tnef, envelope: false, text: false)));
        MapiMessage m = r.Value;
        Assert.Equal("Caf\u00e9 menu \u2713", m.GetString(MapiPid.Subject));
        Assert.Equal("plain body \u00e9", m.GetString(MapiPid.Body));
        Assert.Equal(2, m.Recipients.Count);
        Assert.Equal(3, m.Attachments.Count);
        Assert.NotNull(m.Attachments[2].EmbeddedMessage);
        Assert.Contains(r.Report, e => e.Code == "TnefPartUnpacked");
        Assert.False(r.Report.HasContentLoss);

        // every property is carried exactly as the TNEF reader gave it (named ones by name: their local IDs are renumbered)
        foreach (MapiProperty p in reference.Properties)
        {
            if (p.Id is MapiPid.InternetCodepage or MapiPid.NativeBody)
            {
                continue;
            }

            MapiProperty? got = p.Name is { } n ? m.Properties.Get(n, p.Type) : m.Properties.Get(p.Tag);
            Assert.True(got is not null && p.Type == got.Type && p.GetBytes().Span.SequenceEqual(got.GetBytes().Span), "property " + p);
        }

        // 8-bit text keeps the code page the TNEF declared, wherever it is read from
        Assert.Equal("report \u00e9.txt", m.Attachments[0].FileName);
    }

    [Fact]
    public void HeadersWinAndEachIgnoredPropertyIsReported()
    {
        ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(MimeEntity.Parse(Outlook(Rich(1252, everyType: false))));
        MapiMessage m = r.Value;
        Assert.Equal("RE: wrapped", m.GetString(MapiPid.Subject));
        Assert.Contains(r.Report, e => e.Code == "TnefPropertyIgnored" && e.Name == "PidTagSubject" && e.Kind == LossKind.Overridden);

        // the TNEF's recipient table is ignored in favour of the To field; its two recipients are not among the header's
        Assert.Single(m.Recipients);
        Assert.Contains(r.Report, e => e.Code == "TnefRecipientIgnored");

        // the plain text of the MIME side stays; the TNEF's richer body (compressed RTF) is kept next to it
        Assert.Equal("mime text", m.GetString(MapiPid.Body)?.Trim());
        Assert.NotNull(m.Properties.Get(MapiPid.RtfCompressed));
        Assert.Equal(2, m.GetInt32(MapiPid.NativeBody));
        Assert.Equal(3, m.Attachments.Count);
    }

    [Fact]
    public void ADefaultNoteClassGivesWayToTheTnefsClass()
    {
        byte[] tnef = LegacyOnly();
        ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(MimeEntity.Parse(Outlook(tnef)));
        Assert.Equal("IPM.Schedule.Meeting.Request", r.Value.MessageClass);
        Assert.Contains(r.Report, e => e.Code == "TnefMessageClassUsed");
    }

    [Fact]
    public void ACorrelationKeyThatDoesNotMatchLeavesTheMessagePureMime()
    {
        byte[] tnef = WithCorrelation("<key-1>");
        ConversionResult<MapiMessage> bad = new MimeToMapiConverter().Convert(MimeEntity.Parse(Outlook(tnef, "X-MS-TNEF-Correlator: <other>\r\n")));
        MapiAttachment a = Assert.Single(bad.Value.Attachments);
        Assert.Equal("application/octet-stream", a.MimeType);
        Assert.Contains(bad.Report, e => e.Code == "TnefNotUsed");

        ConversionResult<MapiMessage> good = new MimeToMapiConverter().Convert(MimeEntity.Parse(Outlook(tnef, "X-MS-TNEF-Correlator: <key-1>\r\n")));
        Assert.Equal("a.bin", Assert.Single(good.Value.Attachments).FileName);

        // no field, or no key, cannot be compared and counts as a match
        Assert.Equal("a.bin", Assert.Single(new MimeToMapiConverter().Convert(MimeEntity.Parse(Outlook(tnef))).Value.Attachments).FileName);
        Assert.Equal("a.bin", Assert.Single(new MimeToMapiConverter().Convert(MimeEntity.Parse(Outlook(WithCorrelation(null), "X-MS-TNEF-Correlator: <x>\r\n"))).Value.Attachments).FileName);
    }

    [Fact]
    public void AStreamEndingInCrLfIsUnpackedAndReportedAndADamagedOneIsKept()
    {
        byte[] crlf = [.. WithCorrelation(null), 0x0D, 0x0A];
        byte[] eml = Outlook(crlf);
        ConversionResult<MapiMessage> c = new MimeToMapiConverter().Convert(MimeEntity.Parse(eml));
        Assert.Equal("a.bin", Assert.Single(c.Value.Attachments).FileName);
        Assert.Contains(c.Report, e => e.Code == "TnefTrailingCrLfIgnored" && e.Kind == LossKind.Internal);
        var v = MimeMailItem.Parse(eml);
        Assert.Equal("a.bin", Assert.Single(v.Attachments).FileName);
        Assert.Contains(v.Defects, d => d.Code == "AmbiguousTnefTrailingCrLf" && d.IsAmbiguity);

        byte[] damaged = WithCorrelation(null);
        damaged[^3] ^= 0x55; // a wrong attribute checksum
        ConversionResult<MapiMessage> r = new MimeToMapiConverter().Convert(MimeEntity.Parse(Outlook(damaged)));
        Assert.Equal("winmail.dat", Assert.Single(r.Value.Attachments).FileName);
        Assert.Contains(r.Report, e => e.Code == "TnefNotUsed");
    }

    [Fact]
    public void KeepAsAttachmentLeavesThePartAlone()
    {
        var options = new OxcMailImportOptions { Tnef = OxcMailTnefHandling.KeepAsAttachment };
        MapiMessage m = new MimeToMapiConverter(options).Convert(MimeEntity.Parse(Outlook(WithCorrelation(null)))).Value;
        MapiAttachment a = Assert.Single(m.Attachments);
        Assert.Equal("application/ms-tnef", a.MimeType);
    }

    [Fact]
    public void TheViewListsWhatTheTnefHoldsInPlaceOfWinmailDat()
    {
        var item = MimeMailItem.Parse(Outlook(Rich(1252, everyType: false)));
        Assert.Equal(["report \u00e9.txt", "props.bin", "inner.msg"], item.Attachments.Select(a => a.FileName).ToArray());
        Assert.Equal(6, item.Attachments[0].Size);
        Assert.NotNull(item.Attachments[2].Embedded);
        Assert.Equal("mime text", item.TextBody?.ReadText().Trim());
        Assert.NotNull(item.RtfBody);
        MimePart part = Assert.Single(item.TnefParts);
        Assert.True(part.IsTnef());
        Assert.Equal("winmail.dat", part.FileName);

        // a part that does not qualify is an ordinary attachment
        var refused = MimeMailItem.Parse(Outlook(WithCorrelation("<k>"), "X-MS-TNEF-Correlator: <other>\r\n"));
        Assert.Empty(refused.TnefParts);
        Assert.Equal("winmail.dat", Assert.Single(refused.Attachments).FileName);
    }

    [Fact]
    public void ExportAsTnefMessageAndImportKeepsTheProperties()
    {
        var m = new MapiMessage();
        m.Properties.Add(MapiProps(MapiPid.MessageClass, "IPM.Note"));
        m.Properties.Add(MapiProps(MapiPid.Subject, "Report \u2713"));
        m.Properties.Add(MapiProps(MapiPid.SenderName, "Ann"));
        m.Properties.Add(MapiProps(MapiPid.SenderAddressType, "SMTP"));
        m.Properties.Add(MapiProps(MapiPid.SenderEmailAddress, "ann@example.com"));
        m.Properties.Add(MapiProps(MapiPid.Body, "Hello"));
        var a = new MapiAttachment(m);
        a.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.AttachDataBinary, MapiType.Binary), [1, 2, 3]));
        a.Properties.Add(MapiProps(MapiPid.AttachLongFilename, "x.bin"));
        a.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.AttachMethod, MapiType.Integer32), [1, 0, 0, 0]));
        m.AddAttachment(a);

        ConversionResult<MimeEntity> ex = new MapiToMimeConverter(new OxcMailExportOptions { Tnef = OxcMailTnefExport.TnefMessage, BoundaryFactory = () => "bnd" }).Convert(m);
        byte[] eml = ex.Value.ToArray();
        MimeEntity back = MimeEntity.Parse(eml);
        MimeMultipart mixed = Assert.IsType<MimeMultipart>(back);
        Assert.Equal("multipart/mixed", mixed.MediaType);
        Assert.Equal("text/plain", mixed.Children[0].MediaType);
        Assert.Equal("application/ms-tnef", mixed.Children[1].MediaType);
        Assert.NotNull(back.Headers.GetFirst("X-MS-TNEF-Correlator"));

        MapiMessage again = new MimeToMapiConverter().Convert(back).Value;
        Assert.Equal("Report \u2713", again.GetString(MapiPid.Subject));
        Assert.Equal("x.bin", Assert.Single(again.Attachments).FileName);
        Assert.Equal([1, 2, 3], again.Attachments[0].OpenData()!.ReadAllBytes());
        Assert.NotNull(again.Properties.Get(MapiPid.TnefCorrelationKey));
    }

    private static MapiProperty MapiProps(ushort id, string v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));
}
