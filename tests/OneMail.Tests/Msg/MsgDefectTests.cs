using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using static RecursiveAI.OneMail.Tests.Msg.MsgTestHelpers;

namespace RecursiveAI.OneMail.Tests.Msg;

/// <summary>Each defect kind the MSG reader reports, from a file damaged on purpose, and the guarantee that damage never throws.</summary>
public class MsgDefectTests
{
    private static byte[] Damaged(BuildObject o, Action<List<(string Path, byte[] Data)>> damage, bool unicode = true)
    {
        (List<(string Path, byte[] Data)> streams, List<string> empty) = MsgTestBuilder.BuildStreams(o, unicode);
        damage(streams);
        return MsgTestBuilder.Pack(streams, empty);
    }

    private static void Remove(List<(string Path, byte[] Data)> s, string path) => Assert.Equal(1, s.RemoveAll(x => x.Path == path));

    private static void Replace(List<(string Path, byte[] Data)> s, string path, byte[] data)
    {
        int i = s.FindIndex(x => x.Path == path);
        Assert.True(i >= 0, path);
        s[i] = (path, data);
    }

    private static byte[] Data(List<(string Path, byte[] Data)> s, string path) => s.First(x => x.Path == path).Data;

    private static List<MapiDefect> Defects(byte[] file, MsgReaderOptions? options = null)
    {
        using MsgFile f = Open(file, options);
        f.ReadEverything();
        foreach (MapiObject o in Walk(f.Message))
        {
            foreach (MapiProperty p in o.Properties)
            {
                for (int i = 0; i < p.ValueCount; i++)
                {
                    _ = p.GetBytes(i);
                }
            }
        }

        return [.. f.Defects];
    }

    private static BuildObject OneNote() => MsgFixtures.Simple("s");

    [Fact]
    public void NotACompoundFile()
    {
        Assert.False(MsgFile.TryOpen(Encoding.ASCII.GetBytes("definitely not a compound file"), out _, out IReadOnlyList<MapiDefect> d));
        Assert.Equal(MapiDefectKind.CompoundFileInvalid, d[0].Kind);
        Assert.False(MsgFile.TryOpen(new MemoryStream([1, 2, 3]), false, out _, out d));
        Assert.Equal(MapiDefectKind.NotACompoundFile, d[0].Kind);
    }

    [Fact]
    public void ADamagedContainerIsRefusedWithADefect()
    {
        byte[] file = MsgFixtures.All()["all-types.msg"];
        byte[] cut = file[..(file.Length / 2)];
        Assert.False(MsgFile.TryOpen(cut, out _, out IReadOnlyList<MapiDefect> d));
        Assert.Equal(MapiDefectKind.CompoundFileInvalid, d[0].Kind);
    }

    [Fact]
    public void MissingPropertyStream()
    {
        byte[] f = Damaged(OneNote(), s => Remove(s, "__properties_version1.0"));
        Assert.Contains(Defects(f), d => d.Kind == MapiDefectKind.PropertyStreamMissing);
    }

    [Fact]
    public void TruncatedPropertyStream()
    {
        byte[] f = Damaged(OneNote(), s => Replace(s, "__properties_version1.0", Data(s, "__properties_version1.0")[..^5]));
        Assert.Contains(Defects(f), d => d.Kind == MapiDefectKind.PropertyStreamTruncated);
        byte[] g = Damaged(OneNote(), s => Replace(s, "__properties_version1.0", new byte[10]));
        Assert.Contains(Defects(g), d => d.Kind == MapiDefectKind.PropertyStreamTruncated);
    }

    [Fact]
    public void DuplicateEntriesAreKeptAndTheLastWins()
    {
        byte[] f = Damaged(OneNote(), s =>
        {
            byte[] ps = Data(s, "__properties_version1.0");
            byte[] dup = ps[32..48].ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(dup.AsSpan(8), 0x1234);
            Replace(s, "__properties_version1.0", [.. ps, .. dup]);
        });
        using MsgFile m = Open(f);
        Assert.Contains(m.Defects, d => d.Kind == MapiDefectKind.AmbiguousDuplicateProperty && d.IsAmbiguity);
        Assert.Equal(4, m.Message.Properties.Count);
    }

    [Fact]
    public void ValueStreamMissingSizeMismatchAndOddUnicode()
    {
        string subject = "__substg1.0_0037001F";
        byte[] f = Damaged(OneNote(), s => Remove(s, subject));
        Assert.Contains(Defects(f), d => d.Kind == MapiDefectKind.ValueStreamMissing);
        byte[] g = Damaged(OneNote(), s => Replace(s, subject, [(byte)'a', 0, (byte)'b']));
        List<MapiDefect> gd = Defects(g);
        Assert.Contains(gd, d => d.Kind == MapiDefectKind.ValueSizeMismatch);
        Assert.Contains(gd, d => d.Kind == MapiDefectKind.OddStringLength);
        using MsgFile m = Open(g);
        Assert.Equal("a", m.Message.Subject);
    }

    [Fact]
    public void OrphanValueStreamIsReadAsAProperty()
    {
        byte[] f = Damaged(OneNote(), s => s.Add(("__substg1.0_0E04001F", Encoding.Unicode.GetBytes("Ann"))));
        using MsgFile m = Open(f);
        Assert.Contains(m.Defects, d => d.Kind == MapiDefectKind.AmbiguousOrphanValueStream);
        Assert.Equal("Ann", m.Message.GetString(MapiPid.DisplayTo));
        Assert.Empty(RoundTripDifferences(f, m));
    }

    [Fact]
    public void UnreferencedStreamsAreReportedNotRead()
    {
        byte[] f = Damaged(OneNote(), s => s.Add(("Extra", [1])));
        Assert.Contains(Defects(f), d => d.Kind == MapiDefectKind.UnreferencedStream && d.Location == "/Extra");
    }

    [Fact]
    public void MultiValuedDamage()
    {
        BuildObject o = OneNote();
        o.Props.Add(new BuildProp(MsgFixtures.T(0x6105, MapiType.MultipleString), MsgFixtures.Arr("a", "b", "c")));
        o.Props.Add(new BuildProp(MsgFixtures.T(0x6102, MapiType.MultipleInteger32), MsgFixtures.Arr(1, 2)));
        string lengths = "__substg1.0_6105101F";
        Assert.Contains(Defects(Damaged(o, s => Remove(s, lengths + "-00000001"))), d => d.Kind == MapiDefectKind.MultiValueStreamMissing);
        Assert.Contains(Defects(Damaged(o, s => Replace(s, lengths, Data(s, lengths)[..^1]))), d => d.Kind == MapiDefectKind.MultiValueLengthMismatch);
        Assert.Contains(Defects(Damaged(o, s => Replace(s, lengths + "-00000000", new byte[2]))), d => d.Kind == MapiDefectKind.MultiValueLengthMismatch);
        Assert.Contains(Defects(Damaged(o, s => Replace(s, "__substg1.0_61021003", new byte[7]))), d => d.Kind == MapiDefectKind.MultiValueTrailingBytes);
        Assert.Contains(Defects(Damaged(o, s => Remove(s, lengths))), d => d.Kind == MapiDefectKind.ValueStreamMissing);
    }

    [Fact]
    public void NamedPropertyMapDamage()
    {
        BuildObject o = MsgFixtures.AllTypes();
        string dir = "__nameid_version1.0/";
        Assert.Contains(Defects(Damaged(o, s => Remove(s, dir + "__substg1.0_00030102"))), d => d.Kind == MapiDefectKind.NamedPropertyUnmapped);
        Assert.Contains(Defects(Damaged(o, s => Replace(s, dir + "__substg1.0_00020102", Data(s, dir + "__substg1.0_00020102")[..^3]))), d => d.Kind == MapiDefectKind.NamedPropertyGuidStreamInvalid);
        Assert.Contains(Defects(Damaged(o, s => Replace(s, dir + "__substg1.0_00030102", [.. Data(s, dir + "__substg1.0_00030102"), 1, 2, 3]))), d => d.Kind == MapiDefectKind.NamedPropertyEntryStreamInvalid);
        Assert.Contains(Defects(Damaged(o, s => Replace(s, dir + "__substg1.0_00040102", []))), d => d.Kind == MapiDefectKind.NamedPropertyReferenceInvalid);
        Assert.Contains(Defects(Damaged(o, s => Replace(s, dir + "__substg1.0_00020102", []))), d => d.Kind == MapiDefectKind.NamedPropertyReferenceInvalid);
        byte[] entries = Data(MsgTestBuilder.BuildStreams(o).Streams, dir + "__substg1.0_00030102").ToArray();
        entries[6] = 9;
        Assert.Contains(Defects(Damaged(o, s => Replace(s, dir + "__substg1.0_00030102", entries))), d => d.Kind == MapiDefectKind.NamedPropertyIndexMismatch);
    }

    [Fact]
    public void ChildStorageDamage()
    {
        BuildObject o = MsgFixtures.Conversation();
        byte[] f = Damaged(o, s =>
        {
            byte[] ps = Data(s, "__properties_version1.0");
            BinaryPrimitives.WriteInt32LittleEndian(ps.AsSpan(16), 9);
            BinaryPrimitives.WriteInt32LittleEndian(ps.AsSpan(20), 9);
            Replace(s, "__properties_version1.0", ps);
        });
        List<MapiDefect> d = Defects(f);
        Assert.Contains(d, x => x.Kind == MapiDefectKind.RecipientCountMismatch);
        Assert.Contains(d, x => x.Kind == MapiDefectKind.AttachmentCountMismatch);

        byte[] g = Damaged(o, s => Remove(s, "__attach_version1.0_#00000001/__substg1.0_3701000D/__properties_version1.0"));
        Assert.Contains(Defects(g), x => x.Kind is MapiDefectKind.EmbeddedObjectMissing);

        byte[] bad = Damaged(o, s =>
        {
            for (int i = 0; i < s.Count; i++)
            {
                s[i] = (s[i].Path.Replace("__recip_version1.0_#00000001", "__recip_version1.0_#zz"), s[i].Data);
            }
        });
        Assert.Contains(Defects(bad), x => x.Kind == MapiDefectKind.StorageNameInvalid);
    }

    [Fact]
    public void EmbeddedObjectDamage()
    {
        BuildObject o = MsgFixtures.Conversation();
        // Attachment method 5 with no embedded storage.
        byte[] f = Damaged(o, s => s.RemoveAll(x => x.Path.StartsWith("__attach_version1.0_#00000001/__substg1.0_3701000D", StringComparison.Ordinal)));
        List<MapiDefect> d = Defects(f);
        Assert.Contains(d, x => x.Kind == MapiDefectKind.EmbeddedObjectMissing);

        // An embedded storage with its own name map is ignored.
        byte[] g = Damaged(o, s => s.Add(("__attach_version1.0_#00000001/__substg1.0_3701000D/__nameid_version1.0/__substg1.0_00020102", [])));
        Assert.Contains(Defects(g), x => x.Kind == MapiDefectKind.NestedNamedPropertyMap);
    }

    [Fact]
    public void EmbeddingDepthIsLimited()
    {
        BuildObject o = MsgFixtures.Simple("level 0");
        BuildObject cur = o;
        for (int i = 1; i <= 6; i++)
        {
            BuildObject inner = MsgFixtures.Simple("level " + i);
            var a = new BuildAttachment { Embedded = inner };
            a.Props.Add(new BuildProp(MsgFixtures.T(MapiPid.AttachMethod, MapiType.Integer32), 5));
            cur.Attachments.Add(a);
            cur = inner;
        }

        byte[] file = MsgTestBuilder.Build(o);
        using MsgFile f = Open(file, new MsgReaderOptions { MaxEmbeddingDepth = 3 });
        f.ReadEverything();
        Assert.Contains(f.Defects, d => d.Kind == MapiDefectKind.EmbeddingTooDeep);
        MapiAttachment deepest = Walk(f.Message).OfType<MapiAttachment>().Last();
        Assert.Null(deepest.EmbeddedMessage);
        Assert.NotNull(deepest.ObjectStorage);
        Assert.Equal(3, Walk(f.Message).OfType<MsgMessage>().Max(m => m.Depth));
    }

    [Fact]
    public void LimitsStopTheReadAndSayWhy()
    {
        byte[] file = MsgFixtures.All()["recipients-attachments.msg"];
        List<MapiDefect> d = Defects(file, new MsgReaderOptions { MaxProperties = 2, MaxRecipients = 1, MaxAttachments = 1, MaxMultiValueCount = 1 });
        Assert.True(d.Count(x => x.Kind == MapiDefectKind.LimitReached) >= 3);
        using MsgFile f = Open(file, new MsgReaderOptions { MaxRecipients = 1, MaxAttachments = 2 });
        Assert.Single(f.Message.Recipients);
        Assert.Equal(2, f.Message.Attachments.Count);
        using MsgFile g = Open(MsgFixtures.All()["all-types.msg"], new MsgReaderOptions { MaxValueBytes = 4 });
        Assert.Empty(g.Message.Properties.Get(0x600E)!.GetBytes().ToArray());
        Assert.Contains(g.Defects, x => x.Kind == MapiDefectKind.LimitReached);
        using MsgFile h = Open(MsgFixtures.All()["all-types.msg"], new MsgReaderOptions { MaxDefects = 1 });
        _ = h.Message.Properties.Get(0x600E)!.GetBytes();
    }

    [Fact]
    public void DefectsAreCappedAndCounted()
    {
        byte[] f = Damaged(MsgFixtures.AllTypes(), s => Remove(s, "__nameid_version1.0/__substg1.0_00030102"));
        using MsgFile m = Open(f, new MsgReaderOptions { MaxDefects = 3 });
        Assert.Equal(4, m.Defects.Count);
        Assert.Equal(MapiDefectKind.DefectLimitReached, m.Defects[^1].Kind);
        Assert.True(m.DroppedDefects > 0);
    }

    [Fact]
    public void CodePageIsAmbiguousOnlyForEightBitStringsWithoutAMessageCodePage()
    {
        // 8-bit strings, no PidTagMessageCodepage: the locale decides, and that is reported.
        BuildObject o = MsgFixtures.Simple("s", ansi: true);
        o.Props.RemoveAll(p => (ushort)(p.Tag >> 16) == MapiPid.MessageCodepage);
        o.Props.Add(new BuildProp(MsgFixtures.T(MapiPid.MessageLocaleId, MapiType.Integer32), 0x0419));
        using (MsgFile f = Open(MsgTestBuilder.Build(o, unicode: false)))
        {
            Assert.Contains(f.Defects, d => d.Kind == MapiDefectKind.AmbiguousCodePage && d.IsAmbiguity);
            Assert.Equal(MapiCodePageSource.LocaleId, f.Message.String8EncodingSource);
            Assert.Equal(1251, f.Message.String8Encoding.CodePage);
        }

        // The same with a message code page: nothing to resolve.
        BuildObject p = MsgFixtures.Simple("s", ansi: true);
        p.Props.Add(new BuildProp(MsgFixtures.T(MapiPid.MessageLocaleId, MapiType.Integer32), 0x0419));
        Assert.DoesNotContain(Defects(MsgTestBuilder.Build(p, unicode: false)), d => d.Kind == MapiDefectKind.AmbiguousCodePage);

        // A Unicode file has no 8-bit strings to resolve.
        BuildObject q = MsgFixtures.Simple("s");
        q.Props.Add(new BuildProp(MsgFixtures.T(MapiPid.InternetCodepage, MapiType.Integer32), 65001));
        Assert.DoesNotContain(Defects(MsgTestBuilder.Build(q)), d => d.Kind == MapiDefectKind.AmbiguousCodePage);

        BuildObject r = MsgFixtures.Simple("s", ansi: true);
        r.Props.Add(new BuildProp(MsgFixtures.T(MapiPid.InternetCodepage, MapiType.Integer32), 31337));
        Assert.Contains(Defects(MsgTestBuilder.Build(r, unicode: false)), d => d.Kind == MapiDefectKind.UnsupportedCodePage);
    }

    [Fact]
    public void StringTypesThatDisagreeWithTheSupportMaskAreReportedAndStillRead()
    {
        BuildObject o = MsgFixtures.Simple("s");
        o.Props.Add(new BuildProp(MsgFixtures.T(0x6000, MapiType.String8), "narrow"));
        using MsgFile f = Open(MsgTestBuilder.Build(o));
        Assert.Contains(f.Defects, d => d.Kind == MapiDefectKind.AmbiguousStringType);
        Assert.Equal("narrow", f.Message.GetString(0x6000));
    }

    [Fact]
    public void UnknownPropertyTypesAreKeptAsBytes()
    {
        BuildObject o = MsgFixtures.Simple("s");
        o.Props.Add(new BuildProp(0x60000099, MsgFixtures.Arr<byte>(1, 2, 3)));
        using MsgFile f = Open(MsgTestBuilder.Build(o));
        Assert.Contains(f.Defects, d => d.Kind == MapiDefectKind.UnknownPropertyType);
        Assert.Equal(MsgFixtures.Arr<byte>(1, 2, 3), f.Message.Properties.Get(new MapiTag(0x60000099))!.GetBytes().ToArray());
    }

    [Fact]
    public void RandomDamageNeverThrows()
    {
        var rng = new Random(11);
        {
            for (int round = 0; round < 600; round++)
            {
                // Damage the streams, not the container, so the reader gets past the compound file layer most of the time.
                (List<(string Path, byte[] Data)> streams, List<string> empty) = MsgTestBuilder.BuildStreams(MsgFixtures.Conversation());
                int edits = 1 + rng.Next(6);
                for (int e = 0; e < edits; e++)
                {
                    int si = rng.Next(streams.Count);
                    byte[] d = streams[si].Data.ToArray();
                    switch (rng.Next(4))
                    {
                        case 0 when d.Length > 0:
                            d[rng.Next(d.Length)] = (byte)rng.Next(256);
                            break;
                        case 1:
                            d = d[..rng.Next(d.Length + 1)];
                            break;
                        case 2:
                            d = [.. d, .. new byte[rng.Next(40)]];
                            break;
                        default:
                            streams.RemoveAt(si);
                            continue;
                    }

                    streams[si] = (streams[si].Path, d);
                }

                byte[] file = MsgTestBuilder.Pack(streams, empty);
                if (MsgFile.TryOpen(file, out MsgFile? f, out _))
                {
                    using (f)
                    {
                        f.ReadEverything();
                        foreach (MapiObject o in Walk(f.Message))
                        {
                            foreach (MapiProperty p in o.Properties)
                            {
                                _ = p.AsString(o.String8Encoding);
                                _ = p.AsTime();
                                _ = p.AsInt32();
                            }
                        }
                    }
                }
            }
        }
    }
}
