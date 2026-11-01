using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using static RecursiveAI.OneMail.Tests.Msg.MsgWriterTestHelpers;

namespace RecursiveAI.OneMail.Tests.Msg;

public class MsgWriterTests
{
    private static MapiProperty Str(ushort id, string v, MapiNamedProperty? name = null) =>
        MapiProperty.CreateOwned(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v), name);

    private static MapiProperty Int(ushort id, int v, MapiNamedProperty? name = null) =>
        MapiProperty.CreateOwned(new MapiTag(id, MapiType.Integer32), BitConverter.GetBytes(v), name);

    private static MapiProperty Bin(ushort id, byte[] v) => MapiProperty.CreateOwned(new MapiTag(id, MapiType.Binary), v);

    private static MapiMessage Simple(string subject = "Hello")
    {
        var m = new MapiMessage();
        m.Properties.Add(Str(MapiPid.MessageClass, "IPM.Note"));
        m.Properties.Add(Str(MapiPid.Subject, subject));
        return m;
    }

    private static MsgMessage Read(byte[] file)
    {
        MsgFile f = OpenAll(file);
        Assert.Empty(f.Defects);
        return f.Message;
    }

    [Fact]
    public void EveryFixtureReadsBackPropertyExact()
    {
        foreach ((string name, byte[] bytes) in MsgFixtures.All())
        {
            using MsgFile f = MsgTestHelpers.Open(bytes);
            f.ReadEverything();
            byte[] written = MsgWriter.ToArray(f.Message, Exact);
            using MsgFile g = OpenAll(written);
            List<string> diffs = Differences(f.Message, g.Message);
            Assert.True(diffs.Count == 0, name + ": " + string.Join("; ", diffs.Take(4)));
            Assert.Equal(f.Defects.Count(d => d.Kind == MapiDefectKind.AmbiguousStringType), g.Defects.Count(d => d.Kind == MapiDefectKind.AmbiguousStringType));
        }
    }

    [Fact]
    public void FixtureStreamsAreTheSameBytesTheReaderRoundTripCheckExpects()
    {
        // The written property streams, not just the model, equal what Encode makes from the file the fixture builder wrote.
        byte[] original = MsgFixtures.All()["recipients-attachments.msg"];
        using MsgFile f = MsgTestHelpers.Open(original);
        f.ReadEverything();
        byte[] written = MsgWriter.ToArray(f.Message, Exact);
        using MsgFile g = MsgTestHelpers.Open(written);
        g.ReadEverything();
        Assert.Empty(MsgTestHelpers.RoundTripDifferences(written, g));
    }

    [Fact]
    public async Task OutputIsDeterministicAndAsyncGivesTheSameBytes()
    {
        MapiMessage m = Simple();
        m.Properties.Add(Str(0x8000, "x", new MapiNamedProperty(MapiPropertySets.Common, 0x8501u)));
        byte[] a = MsgWriter.ToArray(m);
        byte[] b = MsgWriter.ToArray(m);
        Assert.Equal(a, b);
        using var ms = new MemoryStream();
        await MsgWriter.WriteAsync(m, ms, null, TestContext.Current.CancellationToken);
        Assert.Equal(a, ms.ToArray());
    }

    [Fact]
    public void UnicodeByDefaultSetsTheStoreSupportMaskAndConvertsEightBitStrings()
    {
        var m = new MapiMessage();
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(MapiPid.Subject, MapiType.String8), Encoding.Latin1.GetBytes("caf\u00e9")));
        m.Properties.Add(MapiProperty.CreateMulti(new MapiTag(0x6105, MapiType.MultipleString8), [Encoding.Latin1.GetBytes("a\u00e9"), "b"u8.ToArray()]));
        MsgWriteResult result = default!;
        using var ms = new MemoryStream();
        result = MsgWriter.Write(m, ms);
        Assert.Contains(result.Defects, d => d.Kind == MsgWriteDefectKind.StringConverted);
        using MsgFile f = OpenAll(ms.ToArray());
        Assert.Empty(f.Defects);
        Assert.True(f.HasUnicodeStrings);
        Assert.Equal("caf\u00e9", f.Message.Subject);
        MapiProperty multi = f.Message.Properties.Get(new MapiTag(0x6105, MapiType.MultipleString))!;
        Assert.Equal(["a\u00e9", "b"], [multi.AsString(index: 0), multi.AsString(index: 1)]);
        Assert.Null(f.Message.Properties.Get(new MapiTag(MapiPid.Subject, MapiType.String8)));
    }

    [Fact]
    public void AnsiModeWritesTheCodePageAndReportsWhatItCannotHold()
    {
        MapiMessage m = Simple("\u041f\u0440\u0438\u0432\u0435\u0442 \u65e5");
        var o = new MsgWriterOptions { Strings = MsgStringMode.Ansi, AnsiCodePage = 1251 };
        using var ms = new MemoryStream();
        MsgWriteResult r = MsgWriter.Write(m, ms, o);
        Assert.Contains(r.Defects, d => d.Kind == MsgWriteDefectKind.StringNotRepresentable);
        using MsgFile f = OpenAll(ms.ToArray());
        Assert.Empty(f.Defects);
        Assert.NotEqual(true, f.HasUnicodeStrings);
        Assert.Equal(1251, f.Message.GetInt32(MapiPid.MessageCodepage));
        Assert.Equal("\u041f\u0440\u0438\u0432\u0435\u0442 ?", f.Message.Subject);
        Assert.Equal(MapiType.String8, f.Message.Properties.Get(MapiPid.Subject)!.Type);
    }

    [Fact]
    public void NamedPropertiesGetConsecutiveIdsAndTheirNamesRead()
    {
        var n1 = new MapiNamedProperty(MapiPropertySets.Common, 0x8503u);
        var n2 = new MapiNamedProperty(MapiPropertySets.PublicStrings, "Keywords");
        var n3 = new MapiNamedProperty(MapiPropertySets.InternetHeaders, "X-Mailer");
        var n4 = new MapiNamedProperty(MapiPropertySets.Mapi, 0x1234u);
        var m = Simple();
        m.Properties.Add(Int(0x9999, 1, n1));
        m.Properties.Add(Str(0x9998, "k", n2));
        m.Properties.Add(Str(0x9997, "mail", n3));
        m.Properties.Add(Int(0x9996, 4, n4));
        m.Properties.Add(Int(0x9999 - 1000, 5, n1)); // the same name again: one ID, the later value
        MsgWriteResult r = default!;
        using var ms = new MemoryStream();
        r = MsgWriter.Write(m, ms);
        Assert.Equal(4, r.NamedProperties);
        Assert.Contains(r.Defects, d => d.Kind == MsgWriteDefectKind.DuplicateProperty);
        MapiMessage back = Read(ms.ToArray());
        Assert.Equal(5, back.Properties.Get(n1)!.AsInt32());
        Assert.Equal("k", back.Properties.Get(n2)!.AsString());
        Assert.Equal("mail", back.Properties.Get(n3)!.AsString());
        Assert.Equal(4, back.Properties.Get(n4)!.AsInt32());
        Assert.All(back.Properties.Where(p => p.Name is not null), p => Assert.InRange(p.Id, (ushort)0x8000, (ushort)0x8003));
    }

    [Fact]
    public void ANamedPropertyWithoutANameIsLeftOutAndReported()
    {
        var m = Simple();
        m.Properties.Add(Int(0x8123, 9));
        using var ms = new MemoryStream();
        MsgWriteResult r = MsgWriter.Write(m, ms);
        Assert.Contains(r.Defects, d => d.Kind == MsgWriteDefectKind.NamedPropertyUnmapped);
        Assert.Equal(0, r.NamedProperties);
        Assert.Null(Read(ms.ToArray()).Properties.Get(0x8123));
    }

    [Fact]
    public void EmptyStringsAreLeftOutUnlessKept()
    {
        var m = Simple();
        m.Properties.Add(Str(0x6001, string.Empty));
        using var ms = new MemoryStream();
        Assert.Contains(MsgWriter.Write(m, ms).Defects, d => d.Kind == MsgWriteDefectKind.EmptyStringDropped);
        Assert.Null(Read(ms.ToArray()).Properties.Get(0x6001));
        byte[] kept = MsgWriter.ToArray(m, new MsgWriterOptions { KeepEmptyStrings = true });
        Assert.Equal(0, Read(kept).Properties.Get(0x6001)!.GetBytes().Length);
    }

    [Fact]
    public void ShortFixedValuesArePaddedAndReported()
    {
        var m = Simple();
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x6002, MapiType.Integer32), [1, 2]));
        m.Properties.Add(MapiProperty.CreateOwned(new MapiTag(0x6003, MapiType.Time), new byte[12]));
        using var ms = new MemoryStream();
        MsgWriteResult r = MsgWriter.Write(m, ms);
        Assert.Equal(2, r.Defects.Count(d => d.Kind == MsgWriteDefectKind.FixedValueResized));
        MapiMessage back = Read(ms.ToArray());
        Assert.Equal(0x0201, back.Properties.Get(0x6002)!.AsInt32());
        Assert.Equal(8, back.Properties.Get(0x6003)!.GetBytes().Length);
    }

    [Fact]
    public void TemplateSetsTheUnsentFlag()
    {
        var m = Simple();
        m.Properties.Add(Int(MapiPid.MessageFlags, 0x1));
        MapiMessage back = Read(MsgWriter.ToArray(m, new MsgWriterOptions { Template = true }));
        Assert.Equal(0x9, back.GetInt32(MapiPid.MessageFlags));
        Assert.Equal(0x1, Read(MsgWriter.ToArray(m)).GetInt32(MapiPid.MessageFlags));
    }

    [Fact]
    public void AttachmentsRecipientsAndEmbeddedMessagesRoundTrip()
    {
        var m = Simple("outer");
        var r = new MapiRecipient(m);
        r.Properties.Add(Int(MapiPid.RecipientType, 1));
        r.Properties.Add(Str(MapiPid.DisplayName, "Ann"));
        m.AddRecipient(r);

        var a = new MapiAttachment(m);
        a.Properties.Add(Int(MapiPid.AttachMethod, 1));
        a.Properties.Add(Str(MapiPid.AttachLongFilename, "a.bin"));
        a.Properties.Add(Bin(MapiPid.AttachDataBinary, [1, 2, 3, 4]));
        m.AddAttachment(a);

        var e = new MapiAttachment(m);
        MapiMessage inner = new(e);
        inner.Properties.Add(Str(MapiPid.Subject, "inner"));
        var ia = new MapiAttachment(inner);
        ia.Properties.Add(Bin(MapiPid.AttachDataBinary, [9]));
        inner.AddAttachment(ia);
        e.SetEmbeddedMessage(inner);
        m.AddAttachment(e);

        using var ms = new MemoryStream();
        MsgWriteResult res = MsgWriter.Write(m, ms);
        Assert.Equal((1, 3), (res.Recipients, res.Attachments));
        using MsgFile f = OpenAll(ms.ToArray());
        Assert.Empty(f.Defects);
        Assert.Equal("Ann", f.Message.Recipients[0].DisplayName);
        Assert.Equal(2, f.Message.Attachments.Count);
        Assert.Equal(5, f.Message.Attachments[1].AttachMethodValue);
        Assert.Equal("inner", f.Message.Attachments[1].EmbeddedMessage!.Subject);
        Assert.Single(f.Message.Attachments[1].EmbeddedMessage!.Attachments);
        Assert.Contains(res.Defects, d => d.Kind == MsgWriteDefectKind.PropertyAdjusted && d.Location.EndsWith("#00000001", StringComparison.Ordinal));
    }

    [Fact]
    public void OleStorageIsCopiedWithItsClassId()
    {
        byte[] original = MsgFixtures.All()["recipients-attachments.msg"];
        using MsgFile f = MsgTestHelpers.Open(original);
        f.ReadEverything();
        MapiAttachment? ole = f.Message.Attachments.FirstOrDefault(x => x.ObjectStorage is not null);
        Assert.NotNull(ole);
        using MsgFile g = OpenAll(MsgWriter.ToArray(f.Message, Exact));
        MapiRawStorage? back = g.Message.Attachments[f.Message.Attachments.ToList().IndexOf(ole)].ObjectStorage;
        Assert.NotNull(back);
        Assert.Equal(ole.ObjectStorage!.ClassId, back.ClassId);
        Assert.Equal(ole.ObjectStorage.Entries.Count, back.Entries.Count);
    }

    [Fact]
    public void LargeAttachmentDataIsCopiedFromTheCallersStream()
    {
        const int Size = 3_000_000;
        byte[] data = new byte[Size];
        new Random(5).NextBytes(data);
        int opened = 0;
        var m = Simple();
        var a = new MapiAttachment(m);
        a.Properties.Add(MapiProperty.CreateLazy(new MapiTag(MapiPid.AttachDataBinary, MapiType.Binary), new MapiStreamValue(() =>
        {
            opened++;
            return new MemoryStream(data, writable: false);
        }, Size)));
        m.AddAttachment(a);
        byte[] file = MsgWriter.ToArray(m);
        Assert.Equal(1, opened);
        using MsgFile f = OpenAll(file);
        using Stream s = f.Message.Attachments[0].OpenData()!;
        var back = new byte[Size];
        s.ReadExactly(back);
        Assert.True(back.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void HostileModelsAreRefusedBeforeAnythingIsWritten()
    {
        var m = Simple();
        for (int i = 0; i < 3; i++)
        {
            m.AddRecipient(new MapiRecipient(m));
        }

        var tight = new MsgWriterOptions { MaxRecipients = 2 };
        using var ms = new MemoryStream();
        MsgWriteException e = Assert.Throws<MsgWriteException>(() => MsgWriter.Write(m, ms, tight));
        Assert.Equal(MsgWriteDefectKind.TooManyRecipients, e.Defects[0].Kind);
        Assert.Equal(0, ms.Length);
        Assert.Equal(MsgWriteDefectKind.TooManyRecipients, MsgWriter.Check(m, tight)[0].Kind);

        var big = Simple();
        big.Properties.Add(Bin(0x6001, new byte[2000]));
        Assert.Equal(MsgWriteDefectKind.ValueTooLarge, Assert.Throws<MsgWriteException>(() => MsgWriter.ToArray(big, new MsgWriterOptions { MaxValueBytes = 1000 })).Defects[0].Kind);
        Assert.Equal(MsgWriteDefectKind.TotalTooLarge, Assert.Throws<MsgWriteException>(() => MsgWriter.ToArray(big, new MsgWriterOptions { MaxTotalBytes = 1000 })).Defects[0].Kind);

        var many = Simple();
        for (ushort i = 0; i < 20; i++)
        {
            many.Properties.Add(Int((ushort)(0x6100 + i), i));
        }

        Assert.Equal(MsgWriteDefectKind.TooManyProperties, Assert.Throws<MsgWriteException>(() => MsgWriter.ToArray(many, new MsgWriterOptions { MaxProperties = 10 })).Defects[0].Kind);

        // A message embedded in itself nests without end; the depth limit stops it.
        var loop = Simple();
        var att = new MapiAttachment(loop);
        att.SetEmbeddedMessage(loop);
        loop.AddAttachment(att);
        Assert.Equal(MsgWriteDefectKind.EmbeddingTooDeep, Assert.Throws<MsgWriteException>(() => MsgWriter.ToArray(loop)).Defects[0].Kind);

        var names = Simple();
        names.Properties.Add(Int(0x8000, 1, new MapiNamedProperty(MapiPropertySets.Common, 1u)));
        names.Properties.Add(Int(0x8001, 1, new MapiNamedProperty(MapiPropertySets.Common, 2u)));
        Assert.Equal(MsgWriteDefectKind.TooManyNamedProperties, Assert.Throws<MsgWriteException>(() => MsgWriter.ToArray(names, new MsgWriterOptions { MaxNamedProperties = 1 })).Defects[0].Kind);

        Assert.Equal(MsgWriteDefectKind.UnsupportedCodePage, Assert.Throws<MsgWriteException>(() => MsgWriter.ToArray(Simple(), new MsgWriterOptions { Strings = MsgStringMode.Ansi, AnsiCodePage = 99999 })).Defects[0].Kind);
    }

    [Fact]
    public void WritingToAPathCreatesNoFileWhenTheModelIsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), "onemail-msgwriter-" + Guid.NewGuid().ToString("N") + ".msg");
        var m = Simple();
        m.AddRecipient(new MapiRecipient(m));
        Assert.Throws<MsgWriteException>(() => MsgWriter.Write(m, path, new MsgWriterOptions { MaxRecipients = 0 }));
        Assert.False(File.Exists(path));
        MsgWriter.Write(Simple(), path);
        try
        {
            Assert.True(MsgFile.TryOpen(path, out MsgFile? f, out _));
            f.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
