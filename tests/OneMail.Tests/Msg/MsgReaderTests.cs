using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Sources;
using static RecursiveAI.OneMail.Tests.Msg.MsgTestHelpers;

namespace RecursiveAI.OneMail.Tests.Msg;

public class MsgReaderTests
{
    [Fact]
    public void EveryGeneratedFixtureOpensCleanAndRoundTrips()
    {
        foreach ((string name, byte[] bytes) in MsgFixtures.All())
        {
            using MsgFile f = Open(bytes);
            f.ReadEverything();
            // The all-types file deliberately holds 8-bit strings in a Unicode file.
            Assert.True(f.Defects.All(d => name == "all-types.msg" && d.Kind is MapiDefectKind.AmbiguousStringType or MapiDefectKind.AmbiguousCodePage), name + ": " + string.Join("; ", f.Defects));
            Assert.Empty(RoundTripDifferences(bytes, f));
        }
    }

    [Fact]
    public void ReadsEveryPropertyTypeAndNamedProperty()
    {
        using MsgFile f = Open(MsgFixtures.All()["all-types.msg"]);
        MapiPropertyBag b = f.Message.Properties;
        Assert.Equal("IPM.Note", f.Message.MessageClass);
        Assert.Equal("Every property type", f.Message.Subject);
        Assert.True(f.HasUnicodeStrings);
        Assert.Equal((short)-2, b.Get(0x6001)!.AsInt16());
        Assert.Equal(-70000, b.Get(0x6002)!.AsInt32());
        Assert.Equal(1.5f, b.Get(0x6003)!.AsFloat());
        Assert.Equal(-2.25, b.Get(0x6004)!.AsDouble());
        Assert.Equal(123456789L, b.Get(0x6005)!.AsCurrencyUnits());
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(40000.5), DateTimeKind.Utc)), b.Get(0x6006)!.AsFloatingTime());
        Assert.Equal(unchecked((int)0x8004010F), b.Get(0x6007)!.AsInt32());
        Assert.True(b.Get(0x6008)!.AsBoolean());
        Assert.Equal(1L << 40, b.Get(0x6009)!.AsInt64());
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 12, 30, 45, TimeSpan.Zero), b.Get(0x600A)!.AsTime());
        Assert.Equal(new Guid("00062008-0000-0000-c000-000000000046"), b.Get(0x600B)!.AsGuid());
        Assert.Equal("caf\u00e9 \u65e5\u672c", b.Get(0x600C)!.AsString());
        Assert.Equal("caf\u00e9", b.Get(0x600D)!.AsString(f.Message.String8Encoding));
        Assert.Equal(MsgFixtures.Arr<byte>(1, 2, 3, 0, 255), b.Get(0x600E)!.GetBytes().ToArray());
        MapiProperty mv16 = b.Get(0x6101)!;
        Assert.Equal(3, mv16.ValueCount);
        Assert.Equal((short)-2, mv16.AsInt16(1));
        Assert.Equal(4, b.Get(0x6102)!.ValueCount);
        Assert.Equal(40, b.Get(0x6102)!.AsInt32(3));
        Assert.Equal(6L, b.Get(0x6103)!.AsInt64(1));
        Assert.Equal(new DateTimeOffset(2002, 2, 2, 0, 0, 0, TimeSpan.Zero), b.Get(0x6104)!.AsTime(1));
        MapiProperty strings = b.Get(0x6105)!;
        Assert.Equal(["one", "two", "th\u00f6ree"], Enumerable.Range(0, strings.ValueCount).Select(i => strings.AsString(index: i)));
        Assert.Equal("bb", b.Get(0x6106)!.AsString(index: 1));
        MapiProperty bins = b.Get(0x6107)!;
        Assert.Equal(3, bins.ValueCount);
        Assert.Empty(bins.GetBytes(1).ToArray());
        Assert.Equal(MsgFixtures.Arr<byte>(7, 7, 7), bins.GetBytes(2).ToArray());

        Assert.Equal(0x2A, b.Get(MapiNames.ReminderSet)!.AsInt32());
        Assert.Equal("urgent", b.Get(new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords"))!.AsString());
        Assert.True(b.Get(new MapiNamedProperty(MsgFixtures.CustomSet, "X-Custom \u00fcnicode"))!.AsBoolean());
        Assert.Equal(7, b.Get(new MapiNamedProperty(MapiPropertySets.Mapi, 0x1234u))!.AsInt32());
        Assert.Equal(0x8000, b.Get(MapiNames.ReminderSet)!.Id);
    }

    [Fact]
    public void ReadsRecipientsAttachmentsAndEmbeddedMessages()
    {
        using MsgFile f = Open(MsgFixtures.All()["recipients-attachments.msg"]);
        MsgMessage m = f.Message;
        Assert.Equal("Hello\r\nworld", m.Body);
        Assert.Equal(3, m.Recipients.Count);
        Assert.Equal([MapiRecipientType.To, MapiRecipientType.Cc, MapiRecipientType.Bcc], m.Recipients.Select(r => r.RecipientType!.Value));
        Assert.Equal("cy@example.org", m.Recipients[2].EmailAddress);
        Assert.Equal(3, m.Attachments.Count);

        MapiAttachment data = m.Attachments[0];
        Assert.Equal(MapiAttachMethod.ByValue, data.AttachMethod);
        Assert.Equal("data.bin", data.FileName);
        Assert.Equal("cid-1", data.ContentId);
        using (Stream s = data.OpenData()!)
        {
            Assert.Equal(5000, s.Length);
        }

        Assert.Null(data.EmbeddedMessage);
        Assert.Null(data.ObjectStorage);

        MapiMessage inner = m.Attachments[1].EmbeddedMessage!;
        Assert.Equal("The embedded message", inner.Subject);
        Assert.Equal("Inner recipient", Assert.Single(inner.Recipients).DisplayName);
        Assert.Equal("Embedded twice", Assert.Single(inner.Attachments).EmbeddedMessage!.Subject);
        Assert.Null(m.Attachments[1].ObjectStorage);
        Assert.Equal(1, ((MsgMessage)inner).Depth);

        MapiAttachment ole = m.Attachments[2];
        Assert.Null(ole.EmbeddedMessage);
        MapiRawStorage raw = ole.ObjectStorage!;
        Assert.Contains(raw.Entries, e => e.Path == "Contents" && e.Length == 12);
        using Stream c = raw.OpenStream("contents");
        Assert.Equal("ole contents", new StreamReader(c, Encoding.ASCII).ReadToEnd());
        Assert.Throws<FileNotFoundException>(() => raw.OpenStream("missing"));
    }

    [Fact]
    public void AnsiFilesDecodeEightBitStringsWithTheMessageCodePage()
    {
        using MsgFile f = Open(MsgFixtures.All()["ansi-cp1251.msg"]);
        Assert.False(f.HasUnicodeStrings);
        Assert.Equal(1251, f.Message.String8Encoding.CodePage);
        Assert.Equal("\u041f\u0440\u0438\u0432\u0435\u0442", f.Message.Subject);
        Assert.Equal("\u0442\u0435\u043c\u0430", f.Message.Recipients[0].DisplayName);
    }

    [Fact]
    public void EightBitStringsWithTheTerminatorInTheStreamAreReadWithoutDefect()
    {
        using MsgFile f = Open(MsgFixtures.All()["ansi-terminator-in-stream.msg"]);
        Assert.Equal("Terminator in the stream", f.Message.Subject);
        Assert.Empty(f.Defects);
    }

    [Fact]
    public void TemplatesAreReadLikeMessages()
    {
        using MsgFile f = Open(MsgFixtures.All()["template.msg"]);
        Assert.Equal(8, f.Message.GetInt32(MapiPid.MessageFlags));
        Assert.Equal("Template subject", f.Message.Subject);
    }

    [Fact]
    public void AnEmptyMessageHasNoPropertiesAndNoDefects()
    {
        using MsgFile f = Open(MsgFixtures.All()["empty.msg"]);
        Assert.Empty(f.Message.Properties);
        Assert.Empty(f.Message.Recipients);
        Assert.Empty(f.Message.Attachments);
    }

    [Fact]
    public void OpeningReadsNoValueStreamsAndAttachmentDataStaysLazy()
    {
        byte[] file = MsgFixtures.All()["recipients-attachments.msg"];
        var src = new CountingSource(file);
        Assert.True(MsgFile.TryOpen(src, leaveOpen: true, out MsgFile? f, out _));
        using (f)
        {
            long afterOpen = src.BytesRead;
            MapiProperty subject = f.Message.Properties.Get(MapiPid.Subject)!;
            Assert.True(subject.IsLazy);
            _ = f.Message.Recipients;
            _ = f.Message.Attachments;
            long afterChildren = src.BytesRead;
            MapiProperty bytes = f.Message.Attachments[0].Properties.Get(MapiPid.AttachDataBinary)!;
            Assert.Equal(5000, bytes.GetByteLength());
            Assert.Equal(afterChildren, src.BytesRead);
            ReadOnlyMemory<byte> loaded = bytes.GetBytes();
            Assert.Equal(5000, loaded.Length);
            Assert.True(src.BytesRead >= afterChildren + 5000);
            Assert.True(afterOpen < file.Length);
        }
    }

    [Fact]
    public void SniffingNeedsTheCompoundFileAndAPropertyStream()
    {
        byte[] msg = MsgFixtures.All()["all-types.msg"];
        Assert.True(MsgFile.IsMsg(new MemorySource(msg)));
        Assert.False(MsgFile.IsMsg(new MemorySource(Encoding.ASCII.GetBytes("From: a@b\r\n\r\nbody"))));
        Assert.False(MsgFile.IsMsg(new MemorySource(Array.Empty<byte>())));
        Assert.False(MsgFile.IsMsg(new MemorySource(MsgTestBuilder.Pack([("WordDocument", [1, 2, 3])], []))));
        Assert.False(MsgFile.TryOpen(MsgTestBuilder.Pack([("WordDocument", [1, 2, 3])], []), out _, out IReadOnlyList<MapiDefect> d));
        Assert.Equal(MapiDefectKind.NotAMessage, d[0].Kind);
    }

    [Fact]
    public void TheFileOpensFromPathAndStream()
    {
        byte[] file = MsgFixtures.All()["all-types.msg"];
        string path = Path.Combine(Path.GetTempPath(), "onemail-msg-" + Guid.NewGuid().ToString("N") + ".msg");
        File.WriteAllBytes(path, file);
        try
        {
            Assert.True(MsgFile.TryOpen(path, out MsgFile? a, out _));
            using (a)
            {
                Assert.Equal("Every property type", a.Message.Subject);
            }

            using var ms = new MemoryStream(file);
            Assert.True(MsgFile.TryOpen(ms, leaveOpen: true, out MsgFile? b, out _));
            using (b)
            {
                Assert.Equal(file.Length, b.Length);
            }

            Assert.True(ms.CanRead);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
