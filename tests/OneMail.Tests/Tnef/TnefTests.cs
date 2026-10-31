using System.Buffers.Binary;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Tnef;
using static RecursiveAI.OneMail.Tests.Tnef.TnefTestBuilder;

namespace RecursiveAI.OneMail.Tests.Tnef;

internal static class TnefTestHelpers
{
    /// <summary>Every difference in tag, name or raw value between two messages, recursively through recipients, attachments and embedded messages; empty when property-exact.</summary>
    public static List<string> Differences(MapiObject a, MapiObject b, string path = "message")
    {
        var diffs = new List<string>();
        Compare(a.Properties, b.Properties, path, diffs);
        if (a is MapiMessage ma && b is MapiMessage mb)
        {
            if (ma.Recipients.Count != mb.Recipients.Count)
            {
                diffs.Add(path + ": recipients " + ma.Recipients.Count + " vs " + mb.Recipients.Count);
            }
            else
            {
                for (int i = 0; i < ma.Recipients.Count; i++)
                {
                    diffs.AddRange(Differences(ma.Recipients[i], mb.Recipients[i], path + "/recipient " + i));
                }
            }

            if (ma.Attachments.Count != mb.Attachments.Count)
            {
                diffs.Add(path + ": attachments " + ma.Attachments.Count + " vs " + mb.Attachments.Count);
            }
            else
            {
                for (int i = 0; i < ma.Attachments.Count; i++)
                {
                    diffs.AddRange(Differences(ma.Attachments[i], mb.Attachments[i], path + "/attachment " + i));
                    MapiMessage? ea = ma.Attachments[i].EmbeddedMessage;
                    MapiMessage? eb = mb.Attachments[i].EmbeddedMessage;
                    if ((ea is null) != (eb is null))
                    {
                        diffs.Add(path + "/attachment " + i + ": embedded message presence differs");
                    }
                    else if (ea is not null)
                    {
                        diffs.AddRange(Differences(ea, eb!, path + "/attachment " + i + "/embedded"));
                    }
                }
            }
        }

        return diffs;
    }

    private static void Compare(MapiPropertyBag a, MapiPropertyBag b, string path, List<string> diffs)
    {
        static string Key(MapiProperty p) => p.Tag.ToHex() + (p.Name is { } n ? "/" + n : "");
        List<MapiProperty> la = a.OrderBy(Key, StringComparer.Ordinal).ToList();
        List<MapiProperty> lb = b.OrderBy(Key, StringComparer.Ordinal).ToList();
        if (la.Count != lb.Count)
        {
            diffs.Add(path + ": " + la.Count + " properties vs " + lb.Count + " (" + string.Join(",", la.Select(Key).Except(lb.Select(Key))) + " | " + string.Join(",", lb.Select(Key).Except(la.Select(Key))) + ")");
            return;
        }

        for (int i = 0; i < la.Count; i++)
        {
            if (Key(la[i]) != Key(lb[i]) || !la[i].ValueEquals(lb[i]))
            {
                diffs.Add(path + ": " + Key(la[i]) + " differs from " + Key(lb[i]));
            }
        }
    }

    public static byte[] Write(MapiMessage m, out IReadOnlyList<string> warnings, TnefWriterOptions? o = null)
    {
        using var ms = new MemoryStream();
        warnings = TnefMessageWriter.Write(m, ms, o);
        return ms.ToArray();
    }

    public static byte[] DocBytes(TnefDocument d)
    {
        using var ms = new MemoryStream();
        d.WriteTo(ms);
        return ms.ToArray();
    }

    public static IEnumerable<MapiObject> Walk(MapiMessage m)
    {
        yield return m;
        foreach (MapiRecipient r in m.Recipients)
        {
            yield return r;
        }

        foreach (MapiAttachment a in m.Attachments)
        {
            yield return a;
            if (a.EmbeddedMessage is { } e)
            {
                foreach (MapiObject o in Walk(e))
                {
                    yield return o;
                }
            }
        }
    }

    public static void Touch(MapiMessage m)
    {
        foreach (MapiObject o in Walk(m))
        {
            foreach (MapiProperty p in o.Properties)
            {
                for (int i = 0; i < p.ValueCount; i++)
                {
                    _ = p.GetByteLength(i);
                    if (p.GetByteLength(i) < (1 << 20))
                    {
                        _ = p.GetBytes(i);
                    }
                }
            }
        }
    }
}

public class TnefAttributeTests
{
    [Fact]
    public void SpecSampleHasValidChecksumsAndNoDefects()
    {
        // MS-OXTNEF 3.1: the stored checksums of the sample are the sums of its data bytes.
        TnefDocument d = TnefDocument.Read(SpecSampleStart());
        Assert.True(d.HasSignature);
        Assert.Equal(1, d.LegacyKey);
        Assert.Equal(8, d.Attributes.Count);
        Assert.Empty(d.Defects);
        Assert.All(d.Attributes, a => Assert.True(a.ChecksumValid));
        Assert.Equal(TnefAttributeIds.MessageClass, d.Attributes[2].Id);
        Assert.Equal(0x0831, d.Attributes[2].StoredChecksum);
        Assert.Equal(0, d.TrailingLength);
        Assert.Equal(SpecSampleStart(), TnefTestHelpers.DocBytes(d));
    }

    [Fact]
    public void AttributeWriterAddsTheChecksum()
    {
        using var ms = new MemoryStream();
        var w = new TnefWriter(ms, 1);
        w.WriteAttribute(1, TnefAttributeIds.Subject, "Simple subject\0"u8);
        Assert.Equal(new byte[] { 0x7a, 0x05 }, ms.ToArray()[^2..]);
    }

    [Fact]
    public void NotTnefIsReportedNotThrown()
    {
        TnefDocument d = TnefDocument.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        Assert.False(d.HasSignature);
        Assert.Equal(TnefDefectKind.NotTnef, d.Defects[0].Kind);
        Assert.Throws<InvalidOperationException>(() => TnefTestHelpers.DocBytes(d));
        TnefMessage m = TnefMessage.Read(new byte[] { 1, 2, 3 });
        Assert.Equal(TnefDefectKind.NotTnef, m.TnefDefects[0].Kind);
        Assert.Empty(m.Attachments);
    }

    [Fact]
    public void BadChecksumIsADefectWithItsOffsetAndTheDataStillReads()
    {
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.Subject, Str8("hello"), checksum: 1).ToArray();
        TnefDocument d = TnefDocument.Read(bytes);
        TnefDefect defect = Assert.Single(d.Defects);
        Assert.Equal(TnefDefectKind.ChecksumMismatch, defect.Kind);
        Assert.Equal(bytes.Length - 2, defect.Offset);
        Assert.Equal(bytes, TnefTestHelpers.DocBytes(d));
        Assert.Equal("hello", TnefMessage.Read(d).Subject);
    }

    [Fact]
    public void BadChecksumOnTheMessageClassIsIgnored()
    {
        // MS-OXTNEF 2.3.3.4: legacy writers produced bad checksums on class attributes.
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.MessageClass, Str8("IPM.Note"), checksum: 1).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.AmbiguousMessageClassChecksum);
        Assert.DoesNotContain(m.TnefDefects, d => d.Kind == TnefDefectKind.ChecksumMismatch);
        Assert.Equal("IPM.Note", m.MessageClass);
    }

    [Fact]
    public void ChecksumsCanBeLeftUncomputed()
    {
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.Subject, Str8("hello"), checksum: 1).ToArray();
        TnefDocument d = TnefDocument.Read(bytes, new TnefReaderOptions { VerifyChecksums = false });
        Assert.Empty(d.Defects);
        Assert.Null(d.Attributes[^1].ComputedChecksum);
    }

    [Fact]
    public void EveryTruncationReadsWithoutThrowingAndWritesBackIdentically()
    {
        byte[] full = Rich();
        for (int n = 0; n <= full.Length; n++)
        {
            byte[] cut = full[..n];
            TnefDocument d = TnefDocument.Read(cut);
            TnefMessage m = TnefMessage.Read(d);
            TnefTestHelpers.Touch(m);
            if (d.HasSignature)
            {
                Assert.True(cut.AsSpan().SequenceEqual(TnefTestHelpers.DocBytes(d)), "truncated at " + n);
            }
        }
    }

    [Fact]
    public void TrailingGarbageIsKeptAndWrittenBack()
    {
        byte[] bytes = [.. Rich(), 1, 2, 3];
        TnefDocument d = TnefDocument.Read(bytes);
        Assert.Equal(3, d.TrailingLength);
        Assert.Contains(d.Defects, x => x.Kind == TnefDefectKind.TrailingBytes);
        Assert.Equal(bytes, TnefTestHelpers.DocBytes(d));
    }

    [Fact]
    public void AnOversizeAttributeEndsTheReadWithALimitDefect()
    {
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.Subject, new byte[5000]).ToArray();
        TnefDocument d = TnefDocument.Read(bytes, new TnefReaderOptions { MaxAttributeBytes = 1000 });
        Assert.Equal(2, d.Attributes.Count);
        Assert.Contains(d.Defects, x => x.Kind == TnefDefectKind.LimitReached);
        Assert.Equal(bytes, TnefTestHelpers.DocBytes(d));
    }

    [Fact]
    public void TooManyAttributesStopsTheRead()
    {
        var b = new StreamBuilder().Header();
        for (int i = 0; i < 50; i++)
        {
            b.Msg(0x00018004, [0]);
        }

        TnefDocument d = TnefDocument.Read(b.ToArray(), new TnefReaderOptions { MaxAttributes = 10 });
        Assert.Equal(10, d.Attributes.Count);
        Assert.Contains(d.Defects, x => x.Kind == TnefDefectKind.LimitReached);
    }

    [Fact]
    public void AnInvalidLevelIsAttributeDefectButStillRoundTrips()
    {
        byte[] bytes = new StreamBuilder().Header().Raw([7, 0x04, 0x80, 0x01, 0x00, 1, 0, 0, 0, 65, 65, 0]).ToArray();
        TnefDocument d = TnefDocument.Read(bytes);
        Assert.Contains(d.Defects, x => x.Kind == TnefDefectKind.InvalidLevel);
        Assert.Equal(bytes, TnefTestHelpers.DocBytes(d));
    }

    [Fact]
    public void LargeAttachmentDataStaysInTheSource()
    {
        byte[] payload = new byte[3 << 20];
        new Random(5).NextBytes(payload);
        byte[] bytes = new StreamBuilder().Header().Att(TnefAttributeIds.AttachRendData, RendData(1, 0, 0, 0, 0)).Att(TnefAttributeIds.AttachData, payload).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        TnefMessage m = TnefMessage.Read(bytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 * 1024, "opening allocated " + allocated);
        MapiProperty data = m.Attachments[0].Properties.Get(MapiPid.AttachDataBinary)!;
        Assert.True(data.IsLazy);
        Assert.Equal(payload.Length, data.GetByteLength());
        using Stream s = m.Attachments[0].OpenData()!;
        s.Seek(1_000_000, SeekOrigin.Begin);
        Assert.Equal(payload[1_000_000], (byte)s.ReadByte());
    }
}

public class TnefMessageTests
{
    private static readonly DateTime T = new(2004, 2, 17, 11, 25, 35, DateTimeKind.Utc);

    [Fact]
    public void SpecSampleMapsLegacyAttributes()
    {
        TnefMessage m = TnefMessage.Read(SpecSampleStart());
        Assert.Equal("IPM.Note", m.MessageClass);
        Assert.Equal("Simple subject", m.Subject);
        Assert.Equal(1, m.GetInt32(MapiPid.Importance));
        Assert.Equal(new DateTimeOffset(T), m.GetTime(MapiPid.ClientSubmitTime));
        Assert.Equal(new DateTimeOffset(2004, 2, 17, 12, 26, 9, TimeSpan.Zero), m.GetTime(MapiPid.LastModificationTime));
        Assert.Equal(Convert.FromHexString("757BB19CDE936A4087D90BB784C58E3B"), m.Properties.Get(MapiPid.SearchKey)!.GetBytes().ToArray());
        Assert.Equal(1252, m.OemCodePage);
        Assert.Equal(1252, m.GetInt32(MapiPid.MessageCodepage));
        Assert.Equal([TnefDefectKind.AmbiguousMessageClass], m.TnefDefects.Select(d => d.Kind).ToArray());
    }

    [Fact]
    public void RichMessageReadsEveryPropertyKind()
    {
        TnefMessage m = TnefMessage.Read(Rich());
        Assert.DoesNotContain(m.TnefDefects, d => !d.IsAmbiguity);
        MapiPropertyBag p = m.Properties;
        Assert.Equal("Caf\u00e9 menu \u2713", m.Subject);
        Assert.Equal("plain body \u00e9", m.Body);
        Assert.True(p.Get(0x0029)!.AsBoolean());
        Assert.Equal(1, p.Get(0x0029)!.GetBytes().Length);
        Assert.Equal((short)-7, p.Get(0x6601)!.AsInt16());
        Assert.Equal(1.5f, p.Get(0x6602)!.AsFloat());
        Assert.Equal(-2.25, p.Get(0x6603)!.AsDouble());
        Assert.Equal(123456, p.Get(0x6604)!.AsCurrencyUnits());
        Assert.Equal(long.MinValue + 3, p.Get(0x6607)!.AsInt64());
        Assert.Equal(new DateTimeOffset(2004, 2, 17, 19, 25, 35, TimeSpan.Zero), p.Get(0x0039)!.AsTime());
        Assert.Equal(Guid.Parse("00020307-0000-0000-c000-000000000046"), p.Get(0x6608)!.AsGuid());
        Assert.Equal(3, p.Get(0x6611)!.ValueCount);
        Assert.Equal((short)-2, p.Get(0x6611)!.AsInt16(1));
        Assert.Equal(20, p.Get(0x6612)!.AsInt32(1));
        Assert.Equal(3, p.Get(0x6616)!.ValueCount);
        Assert.Equal("two", p.Get(0x6616)!.AsString(m.String8Encoding, null, 1));
        Assert.Equal("\u00df\u00e9", p.Get(0x6617)!.AsString(m.String8Encoding, null, 1));
        Assert.Equal(3, p.Get(0x6618)!.ValueCount);
        Assert.Empty(p.Get(0x6618)!.GetBytes(2).ToArray());

        // named properties keep their GUID and number or name
        MapiProperty common = p.Get(0x8000)!;
        Assert.Equal(new MapiNamedProperty(PsCommon, 0x8501), common.Name);
        Assert.Equal(42, common.AsInt32());
        Assert.Equal(new MapiNamedProperty(PsPublic, "Keywords"), p.Get(0x8001)!.Name);
        Assert.Equal(new MapiNamedProperty(PsPublic, "ab"), p.Get(0x8002)!.Name);
        Assert.Equal(new MapiNamedProperty(PsPublic, "abc"), p.Get(0x8003)!.Name);
        Assert.Equal(2, p.Get(new MapiNamedProperty(PsPublic, "Keywords"))!.ValueCount);

        Assert.Equal(2, m.Recipients.Count);
        Assert.Equal("Ann Example", m.Recipients[0].DisplayName);
        Assert.Equal("ann@example.test", m.Recipients[0].EmailAddress);
        Assert.Equal(MapiRecipientType.Cc, m.Recipients[1].RecipientType);
        Assert.Equal("bob@example.test", m.Recipients[1].EmailAddress);
    }

    [Fact]
    public void RichAttachmentsIncludingTheEmbeddedMessage()
    {
        TnefMessage m = TnefMessage.Read(Rich());
        Assert.Equal(3, m.Attachments.Count);

        MapiAttachment a0 = m.Attachments[0];
        Assert.Equal("report \u00e9.txt", a0.FileName);
        Assert.Equal(MapiAttachMethod.ByValue, a0.AttachMethod);
        Assert.Equal(-1, a0.GetInt32(MapiPid.RenderingPosition));
        using (Stream s = a0.OpenData()!)
        {
            Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, new BinaryReader(s).ReadBytes(100));
        }

        Assert.NotNull(a0.Properties.Get(MapiPid.CreationTime));

        MapiAttachment a1 = m.Attachments[1];
        Assert.Equal("props.bin", a1.FileName);
        Assert.Equal(new byte[] { 9, 9, 9 }, a1.Properties.Get(MapiPid.AttachDataBinary)!.GetBytes().ToArray());

        MapiAttachment a2 = m.Attachments[2];
        Assert.Equal(MapiAttachMethod.EmbeddedMessage, a2.AttachMethod);
        MapiMessage inner = Assert.IsType<TnefMessage>(a2.EmbeddedMessage);
        Assert.Equal("inner subject", inner.Subject);
        Assert.Equal("inner body", inner.Body);
        Assert.Equal(1, ((TnefMessage)inner).EmbeddingDepth);
        Assert.Null(a2.Properties.Get(MapiPid.AttachDataBinary));
        Assert.Same(a2, inner.Parent);
    }

    [Fact]
    public void LegacyOnlyMessageFillsTheBag()
    {
        TnefMessage m = TnefMessage.Read(LegacyOnly());
        Assert.Equal("IPM.Schedule.Meeting.Request", m.MessageClass);
        Assert.Equal("IPM.Note", m.GetString(MapiPid.OriginalMessageClass));
        Assert.Equal("\u041f\u0440\u0438\u0432\u0435\u0442", m.Subject);
        Assert.Equal("\u0442\u0435\u043b\u043e", m.Body);
        Assert.Equal("\u041e\u043b\u044f", m.GetString(MapiPid.SenderName));
        Assert.Equal("SMTP", m.GetString(MapiPid.SenderAddressType));
        Assert.Equal("olya@example.test", m.GetString(MapiPid.SenderEmailAddress));
        Assert.NotNull(m.Properties.Get(MapiPid.SenderEntryId));
        Assert.Equal(new DateTimeOffset(2020, 12, 31, 23, 59, 58, TimeSpan.Zero), m.GetTime(MapiPid.ClientSubmitTime));
        Assert.Equal(new DateTimeOffset(2021, 1, 1, 23, 59, 58, TimeSpan.Zero), m.GetTime(MapiPid.StartDate));
        Assert.Equal(2, m.GetInt32(MapiPid.Importance) is 2 ? 2 : -1);

        // attMessageStatus 0xA1 = fmsRead | fmsModified | fmsHasAttach -> read, not unmodified, has attachments
        Assert.Equal(0x01 | 0x10, m.GetInt32(MapiPid.MessageFlags));
        Assert.Equal(new byte[] { 0x0A, 0x1B, 0x2C }, m.Properties.Get(MapiPid.SearchKey)!.GetBytes().ToArray());

        // a meeting request puts attOwner in SentRepresenting, and attSentFor always does too (the later one finds the group present)
        Assert.Equal("Owner Name", m.GetString(MapiPid.SentRepresentingName));
        Assert.Equal("owner@example.test", m.GetString(MapiPid.SentRepresentingEmailAddress));
        Assert.Null(m.GetString(MapiPid.ReceivedRepresentingName));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, m.Properties.Get(MapiPid.ReceivedRepresentingEntryId)!.GetBytes().ToArray());
        Assert.Equal(77, m.GetInt32(MapiPid.OwnerAppointmentId));
        Assert.True(m.GetBoolean(MapiPid.ResponseRequested));
        Assert.DoesNotContain(m.TnefDefects, d => !d.IsAmbiguity);

        Assert.Equal(2, m.Attachments.Count);
        MapiAttachment a = m.Attachments[0];
        Assert.Equal("data.bin", a.FileName);
        Assert.Equal(MapiAttachMethod.ByValue, a.AttachMethod);
        Assert.Equal(12, a.GetInt32(MapiPid.RenderingPosition));
        Assert.NotNull(a.Properties.Get(MapiPid.AttachEncoding));
        Assert.Null(a.Properties.Get(MapiPid.AttachTag));
        Assert.Equal(new byte[] { 7, 7 }, a.Properties.Get(MapiPid.AttachRendering)!.GetBytes().ToArray());
        Assert.Equal("DATA~1.BIN", a.GetString(MapiPid.AttachTransportName));
        Assert.NotNull(a.Properties.Get(MapiPid.LastModificationTime));
        MapiAttachment ole = m.Attachments[1];
        Assert.Equal(MapiAttachMethod.Ole, ole.AttachMethod);
        Assert.NotNull(ole.Properties.Get(MapiPid.AttachTag));
    }

    [Fact]
    public void ResponseOwnerGoesToReceivedRepresenting()
    {
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.MessageClass, Str8("IPM.Microsoft Schedule.MtgRespP"))
            .Msg(TnefAttributeIds.Owner, Owner("Pat", "SMTP:pat@example.test")).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Equal("IPM.Schedule.Meeting.Resp.Pos", m.MessageClass);
        Assert.Equal("pat@example.test", m.GetString(MapiPid.ReceivedRepresentingEmailAddress));
        Assert.Null(m.GetString(MapiPid.SentRepresentingName));
    }

    [Fact]
    public void OwnerWithoutAMeetingClassIsAnAmbiguity()
    {
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.MessageClass, Str8("IPM.Note")).Msg(TnefAttributeIds.Owner, Owner("Pat", "SMTP:pat@example.test")).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.AmbiguousOwner);
        Assert.Equal("Pat", m.GetString(MapiPid.SentRepresentingName));
    }

    [Fact]
    public void EncapsulatedPropertyBeatsLegacyAttribute()
    {
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.Subject, Str8("legacy")).Msg(TnefAttributeIds.Priority, U16_(3))
            .Msg(TnefAttributeIds.MsgProps, List(Unicode(0x0037, "property"), Int32(0x0017, 2))).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Equal("property", m.Subject);
        Assert.Equal(2, m.GetInt32(MapiPid.Importance));
        Assert.Equal(2, m.TnefDefects.Count(d => d.Kind == TnefDefectKind.AmbiguousLegacyConflict));
    }

    private static byte[] U16_(int v) => BitConverter.GetBytes((ushort)v);

    [Fact]
    public void LegacyAndPropertyThatAgreeAreNotAConflict()
    {
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.Subject, Str8("same")).Msg(TnefAttributeIds.MsgProps, List(String8(0x0037, "same"))).ToArray();
        Assert.DoesNotContain(TnefMessage.Read(bytes).TnefDefects, d => d.Kind == TnefDefectKind.AmbiguousLegacyConflict);
    }

    [Fact]
    public void OemCodePageDecidesEightBitProperties()
    {
        byte[] bytes = new StreamBuilder().Header(1251).Msg(TnefAttributeIds.MsgProps, List(String8(0x0037, "\u041f\u0440\u0438\u0432\u0435\u0442", 1251))).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Equal("\u041f\u0440\u0438\u0432\u0435\u0442", m.Subject);
        Assert.Equal(MapiCodePageSource.MessageCodepage, m.String8EncodingSource);
    }

    [Fact]
    public void ConflictingMessageCodepageIsReported()
    {
        byte[] bytes = new StreamBuilder().Header(1252).Msg(TnefAttributeIds.MsgProps, List(Int32(0x3FFD, 1251), String8(0x0037, "\u041f", 1251))).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.AmbiguousCodePage);
        Assert.Equal("\u041f", m.Subject);
    }

    [Fact]
    public void MissingVersionAndCodePageAreDefects()
    {
        TnefMessage m = TnefMessage.Read(new StreamBuilder().Msg(TnefAttributeIds.Subject, Str8("x")).ToArray());
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.UnexpectedVersion);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.MissingCodePage);
        Assert.Equal("x", m.Subject);
    }

    [Fact]
    public void AttachmentAttributesBeforeRendDataStartAnAttachment()
    {
        TnefMessage m = TnefMessage.Read(new StreamBuilder().Header().Att(TnefAttributeIds.AttachData, [1, 2]).ToArray());
        Assert.Single(m.Attachments);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.AttachmentWithoutRendData);
    }

    [Fact]
    public void AttachDataIsIgnoredForAnEmbeddedMessage()
    {
        byte[] bytes = new StreamBuilder().Header().Att(TnefAttributeIds.AttachRendData, RendData(1, 0, 0, 0, 0)).Att(TnefAttributeIds.AttachData, [1, 2, 3])
            .Att(TnefAttributeIds.Attachment, List(Int32(0x3705, 5))).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.AmbiguousAttachDataIgnored);
        Assert.Null(m.Attachments[0].Properties.Get(MapiPid.AttachDataBinary));
    }

    [Fact]
    public void ObjectThatIsNotATnefStreamStaysAsAProperty()
    {
        byte[] bytes = new StreamBuilder().Header().Att(TnefAttributeIds.AttachRendData, RendData(1, 0, 0, 0, 0))
            .Att(TnefAttributeIds.Attachment, List(Int32(0x3705, 5), Object(0x3701, [.. MessageIid, 1, 2, 3, 4, 5, 6, 7, 8]))).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.EmbeddedMessageInvalid);
        Assert.Null(m.Attachments[0].EmbeddedMessage);
        Assert.Equal(24, m.Attachments[0].Properties.Get(MapiPid.AttachDataBinary)!.GetByteLength());
    }

    [Fact]
    public void DeeplyNestedMessagesStopAtTheLimit()
    {
        byte[] inner = new StreamBuilder().Header().Msg(TnefAttributeIds.Subject, Str8("leaf")).ToArray();
        for (int i = 0; i < 40; i++)
        {
            inner = new StreamBuilder().Header().Att(TnefAttributeIds.AttachRendData, RendData(1, 0, 0, 0, 0))
                .Att(TnefAttributeIds.Attachment, List(Int32(0x3705, 5), Object(0x3701, [.. MessageIid, .. inner]))).ToArray();
        }

        TnefMessage m = TnefMessage.Read(inner, new TnefReaderOptions { MaxEmbeddingDepth = 5 });
        int depth = 0;
        MapiMessage cur = m;
        var all = new List<TnefMessage>();
        while (cur.Attachments.Count > 0 && cur.Attachments[0].EmbeddedMessage is { } next)
        {
            depth++;
            cur = next;
            all.Add((TnefMessage)cur);
        }

        Assert.Equal(5, depth);
        Assert.Contains(all[^1].TnefDefects, d => d.Kind == TnefDefectKind.EmbeddingTooDeep);
    }

    [Fact]
    public void TooManyAttachmentsAndRecipientsStopAtTheLimit()
    {
        var b = new StreamBuilder().Header();
        for (int i = 0; i < 20; i++)
        {
            b.Att(TnefAttributeIds.AttachRendData, RendData(1, 0, 0, 0, 0));
        }

        TnefMessage m = TnefMessage.Read(b.ToArray(), new TnefReaderOptions { MaxAttachments = 4 });
        Assert.Equal(4, m.Attachments.Count);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.LimitReached);

        byte[] rows = new StreamBuilder().Header().Msg(TnefAttributeIds.RecipTable, RecipTable(Enumerable.Range(0, 30).Select(_ => List(Int32(0x0C15, 1))).ToArray())).ToArray();
        Assert.Equal(7, TnefMessage.Read(rows, new TnefReaderOptions { MaxRecipients = 7 }).Recipients.Count);
    }

    [Fact]
    public void HugeCountsInPropertyListsAreRefusedBeforeAllocating()
    {
        // The shape of the Apache POI oom.tnef: a multi-valued property whose count promises billions of values.
        byte[] hostile = Cat(BitConverter.GetBytes(1u), [0x03, 0x10, 0x00, 0x80], PsMapi.ToByteArray(), BitConverter.GetBytes(0u), BitConverter.GetBytes(0x8544u), [0xFF, 0xFF, 0xFF, 0x7F]);
        TnefMessage m = TnefMessage.Read(new StreamBuilder(1).Msg(TnefAttributeIds.MsgProps, hostile).ToArray());
        Assert.Contains(m.TnefDefects, d => d.Kind is TnefDefectKind.PropertyListTruncated or TnefDefectKind.LimitReached);
        Assert.Empty(m.Properties);

        byte[] manyProps = Cat(BitConverter.GetBytes(uint.MaxValue), new byte[16]);
        TnefMessage m2 = TnefMessage.Read(new StreamBuilder(1).Msg(TnefAttributeIds.MsgProps, manyProps).ToArray());
        Assert.Contains(m2.TnefDefects, d => d.Kind == TnefDefectKind.PropertyListTruncated);

        byte[] rowsLie = Cat(BitConverter.GetBytes(uint.MaxValue), new byte[8]);
        TnefMessage m3 = TnefMessage.Read(new StreamBuilder(1).Msg(TnefAttributeIds.RecipTable, rowsLie).ToArray());
        Assert.True(m3.Recipients.Count <= 2);
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void CompressedRtfHeaderOverTheLimitIsReportedAndKept()
    {
        byte[] rtf = Cat(BitConverter.GetBytes(30u), BitConverter.GetBytes(0x7FFFFFF0u), "LZFu"u8.ToArray(), BitConverter.GetBytes(0u), new byte[14]);
        TnefMessage m = TnefMessage.Read(new StreamBuilder().Header().Msg(TnefAttributeIds.MsgProps, List(Binary(0x1009, rtf))).ToArray());
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.RtfExceedsLimit);
        Assert.Equal(rtf, m.Properties.Get(MapiPid.RtfCompressed)!.GetBytes().ToArray());
    }

    [Fact]
    public void NullPropertyHasNoData()
    {
        // MS-OXCDATA 2.11.1: PtypNull carries no value. MS-OXTNEF gives it no layout of its own, so it takes no bytes (ambiguity T3).
        byte[] bytes = new StreamBuilder().Header().Msg(TnefAttributeIds.MsgProps, List(Int32(0x6601, 1), Null(0x6609), Int32(0x6602, 2))).ToArray();
        TnefMessage m = TnefMessage.Read(bytes);
        Assert.Equal(0, m.Properties.Get(0x6609)!.GetBytes().Length);
        Assert.Equal(2, m.GetInt32(0x6602));
        Assert.DoesNotContain(m.TnefDefects, d => !d.IsAmbiguity);
    }

    [Fact]
    public void StringsWithoutTerminatorsAndPaddingGarbageAreTolerated()
    {
        // tag 0x001E/0x0037, one value, size 3, "abc" with no terminator, pad byte non-zero
        byte[] list = Cat(BitConverter.GetBytes(1u), [0x1E, 0x00, 0x37, 0x00], BitConverter.GetBytes(1u), BitConverter.GetBytes(3u), "abc"u8.ToArray(), [0x7F]);
        TnefMessage m = TnefMessage.Read(new StreamBuilder().Header().Msg(TnefAttributeIds.MsgProps, list).ToArray());
        Assert.Equal("abc", m.Subject);
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.StringNotTerminated);
    }

    [Fact]
    public void ValueCountOtherThanOneKeepsTheFirst()
    {
        byte[] list = Cat(BitConverter.GetBytes(2u), [0x1E, 0x00, 0x37, 0x00], BitConverter.GetBytes(2u), BitConverter.GetBytes(2u), "a\0"u8.ToArray(), [0, 0], BitConverter.GetBytes(2u), "b\0"u8.ToArray(), [0, 0],
            [0x03, 0x00, 0x17, 0x00], BitConverter.GetBytes(1));
        TnefMessage m = TnefMessage.Read(new StreamBuilder().Header().Msg(TnefAttributeIds.MsgProps, list).ToArray());
        Assert.Equal("a", m.Subject);
        Assert.Equal(1, m.GetInt32(MapiPid.Importance));
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.ValueCountMismatch);
    }

    [Fact]
    public void UnknownPropertyTypeEndsTheListWithADefect()
    {
        byte[] list = Cat(BitConverter.GetBytes(2u), [0x03, 0x00, 0x17, 0x00], BitConverter.GetBytes(1), [0xFB, 0x00, 0x99, 0x66], BitConverter.GetBytes(5), [0x03, 0x00, 0x18, 0x00], BitConverter.GetBytes(1));
        TnefMessage m = TnefMessage.Read(new StreamBuilder().Header().Msg(TnefAttributeIds.MsgProps, list).ToArray());
        Assert.NotNull(m.Properties.Get(0x0017));
        Assert.Null(m.Properties.Get(0x0018));
        Assert.Contains(m.TnefDefects, d => d.Kind == TnefDefectKind.UnknownPropertyType);
    }

    [Fact]
    public void RandomDamageNeverThrows()
    {
        byte[] seed = Rich();
        var rng = new Random(20241031);
        for (int i = 0; i < 4000; i++)
        {
            byte[] b = (byte[])seed.Clone();
            int edits = 1 + rng.Next(6);
            for (int e = 0; e < edits; e++)
            {
                b[rng.Next(b.Length)] = (byte)rng.Next(256);
            }

            if (i % 7 == 0)
            {
                b = b[..rng.Next(b.Length)];
            }

            TnefMessage m = TnefMessage.Read(b, new TnefReaderOptions { MaxEmbeddingDepth = 4 });
            TnefTestHelpers.Touch(m);
            using var ms = new MemoryStream();
            m.WriteTo(ms);
            if (m.Document.HasSignature)
            {
                Assert.True(b.AsSpan().SequenceEqual(TnefTestHelpers.DocBytes(m.Document)), "attribute round trip failed at iteration " + i);
            }
        }
    }
}

public class TnefRoundTripTests
{
    public static IEnumerable<object[]> Fixtures() =>
    [
        ["spec", SpecSampleStart()],
        ["rich", Rich()],
        ["rich-1251", Rich(1251)],
        ["legacy", LegacyOnly()],
    ];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AttributeLayerIsByteIdentical(string name, byte[] bytes)
    {
        _ = name;
        Assert.Equal(bytes, TnefTestHelpers.DocBytes(TnefDocument.Read(bytes)));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void MapiModelIsPropertyExactThroughWriteAndRead(string name, byte[] bytes)
    {
        _ = name;
        TnefMessage first = TnefMessage.Read(bytes);
        byte[] written = TnefTestHelpers.Write(first, out IReadOnlyList<string> warnings);
        Assert.Empty(warnings);
        TnefMessage second = TnefMessage.Read(written);
        Assert.DoesNotContain(second.TnefDefects, d => !d.IsAmbiguity);
        Assert.Empty(TnefTestHelpers.Differences(first, second));

        // and the writer is stable: writing the second reading gives the same bytes as the first writing
        Assert.Equal(written, TnefTestHelpers.Write(second, out _));
    }

    [Fact]
    public void WrittenStreamHasTheSpecifiedShape()
    {
        TnefMessage m = TnefMessage.Read(Rich());
        TnefDocument d = TnefDocument.Read(TnefTestHelpers.Write(m, out _));
        Assert.Empty(d.Defects);
        Assert.Equal(TnefAttributeIds.TnefVersion, d.Attributes[0].Id);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, d.Attributes[0].ReadData());
        Assert.Equal(TnefAttributeIds.OemCodepage, d.Attributes[1].Id);
        // MS-OXTNEF 2.2.3.14: attMsgProps after the other message attributes and just before the attachments; each attachment starts with attAttachRendData
        int props = d.Attributes.ToList().FindIndex(a => a.Id == TnefAttributeIds.MsgProps);
        Assert.Equal(2, d.Attributes[props + 1].LevelValue);
        Assert.Equal(TnefAttributeIds.AttachRendData, d.Attributes[props + 1].Id);
        Assert.Equal(3, d.Attributes.Count(a => a.Id == TnefAttributeIds.AttachRendData));
        Assert.All(d.Attributes, a => Assert.True(a.ChecksumValid));
    }

    [Fact]
    public void MessagesBuiltInMemoryWriteAndReadBack()
    {
        var m = new MapiMessage();
        m.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.MessageClass, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("IPM.Note")));
        m.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.Subject, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("\u4f60\u597d")));
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x8000, MapiType.Integer32), BitConverter.GetBytes(5), new MapiNamedProperty(PsPublic, "n")));
        var r = new MapiRecipient(m);
        r.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.DisplayName, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("R")));
        m.AddRecipient(r);
        var inner = new MapiMessage();
        inner.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.Subject, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("inner")));
        var a = new MapiAttachment(m);
        a.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachMethod, MapiType.Integer32), BitConverter.GetBytes(5)));
        a.SetEmbeddedMessage(inner);
        m.AddAttachment(a);
        var b = new MapiAttachment(m);
        b.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachMethod, MapiType.Integer32), BitConverter.GetBytes(1)));
        b.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachDataBinary, MapiType.Binary), new byte[] { 1, 2, 3 }));
        b.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.AttachLongFilename, MapiType.UnicodeString), System.Text.Encoding.Unicode.GetBytes("f.bin")));
        m.AddAttachment(b);

        byte[] bytes = TnefTestHelpers.Write(m, out IReadOnlyList<string> warnings);
        Assert.Empty(warnings);
        TnefMessage back = TnefMessage.Read(bytes);
        Assert.Equal("\u4f60\u597d", back.Subject);
        Assert.Equal("inner", back.Attachments[0].EmbeddedMessage!.Subject);
        Assert.Equal("f.bin", back.Attachments[1].FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Attachments[1].Properties.Get(MapiPid.AttachDataBinary)!.GetBytes().ToArray());
        Assert.Equal(new MapiNamedProperty(PsPublic, "n"), back.Properties.Get(0x8000)!.Name);
        Assert.Equal("R", back.Recipients[0].DisplayName);
    }

    [Fact]
    public void PropertiesTheFormatCannotCarryAreNamedNotSilentlyDropped()
    {
        var m = new MapiMessage();
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x6700, MapiType.ServerId), new byte[] { 1 }));
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x8000, MapiType.Integer32), BitConverter.GetBytes(5)));
        byte[] bytes = TnefTestHelpers.Write(m, out IReadOnlyList<string> warnings);
        Assert.Equal(2, warnings.Count);
        // only the code page the reader adds from attOemCodepage is there
        Assert.Equal([MapiPid.MessageCodepage], TnefMessage.Read(bytes).Properties.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void RecipientsCanBeLeftOut()
    {
        TnefMessage m = TnefMessage.Read(Rich());
        byte[] bytes = TnefTestHelpers.Write(m, out _, new TnefWriterOptions { IncludeRecipients = false });
        Assert.Empty(TnefMessage.Read(bytes).Recipients);
    }
}

public class TnefMimeTests
{
    private static MimeEntity Eml(string tnefLabel, byte[] tnef, string pngLabelBody)
    {
        string b64 = Convert.ToBase64String(tnef, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        string text = "From: a@example.test\r\nTo: b@example.test\r\nSubject: t\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"B\"\r\n\r\n"
            + "--B\r\nContent-Type: text/plain\r\n\r\nhello\r\n"
            + "--B\r\n" + tnefLabel + "Content-Transfer-Encoding: base64\r\n\r\n" + b64 + "\r\n"
            + "--B\r\nContent-Type: image/png\r\nContent-Transfer-Encoding: base64\r\n\r\n" + pngLabelBody + "\r\n"
            + "--B--\r\n";
        return MimeEntity.Parse(System.Text.Encoding.ASCII.GetBytes(text));
    }

    [Theory]
    [InlineData("Content-Type: application/ms-tnef; name=\"winmail.dat\"\r\n")]
    [InlineData("Content-Type: application/octet-stream\r\n")]
    [InlineData("Content-Type: application/x-unknown\r\nContent-Disposition: attachment; filename=winmail.dat\r\n")]
    public void TnefPartsAreFoundByContent(string label)
    {
        byte[] tnef = Rich();
        MimeEntity e = Eml(label, tnef, Convert.ToBase64String(tnef));
        MimePart part = Assert.Single(e.EnumerateTnefParts());
        Assert.True(part.IsTnef());
        TnefMessage m = part.ReadTnef();
        Assert.Equal(3, m.Attachments.Count);
        Assert.Equal("Caf\u00e9 menu \u2713", m.Subject);
    }

    [Fact]
    public void ALabelWithoutTnefContentIsNotTnef()
    {
        MimeEntity e = Eml("Content-Type: application/ms-tnef\r\n", [1, 2, 3, 4, 5, 6], "AAAA");
        Assert.Empty(e.EnumerateTnefParts());
    }

    [Fact]
    public void SnifferAndMailOpenReadABareTnefFile()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "tnef-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "attachment.bin");
            File.WriteAllBytes(path, Rich());
            using var src = Sources.FileSource.Open(path);
            Assert.Equal(MailFormat.Tnef, MailFormatSniffer.Sniff(src));
            using MailContainer c = Mail.Open(path);
            Assert.False(c.IsStore);
            IMailItem item = Assert.Single(c.Enumerate());
            Assert.Equal("Caf\u00e9 menu \u2713", item.Subject);
            Assert.Equal(3, item.Attachments.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
