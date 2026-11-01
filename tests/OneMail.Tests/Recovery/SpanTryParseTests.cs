using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Stores.Dbx;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Tests.Pst;
using RecursiveAI.OneMail.Tests.Stores;

namespace RecursiveAI.OneMail.Tests.Recovery;

public class SpanTryParseTests
{
    public static TheoryData<NdbFormat> Formats => new() { NdbFormat.Ansi, NdbFormat.Unicode, NdbFormat.Unicode4K };

    private static byte[] Pattern(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Theory, MemberData(nameof(Formats))]
    public void NdbPagesAndBlocksParseExactlyAsTheFileReaderChecksThem(NdbFormat format)
    {
        int max = format == NdbFormat.Unicode4K ? 65536 - 24 : 8192 - (format == NdbFormat.Ansi ? 12 : 16);
        var built = NdbTestBuilder.Build(format, [new TestNode { Nid = 0x21, Data = Pattern(100, 1) }, new TestNode { Nid = 0x8082, Data = Pattern(max + 500, 2) }]);
        Assert.True(NdbFile.TryOpen(built.Bytes, out NdbFile? f, out _));
        int pages = 0, blocks = 0;
        using (f)
        {
            foreach (NdbScanItem item in f!.Scan(null, TestContext.Current.CancellationToken))
            {
                var defects = new List<NdbDefect>();
                ReadOnlySpan<byte> at = built.Bytes.AsSpan((int)item.Ib);
                if (item.Kind == NdbScanKind.Page)
                {
                    pages++;
                    Assert.True(NdbSpan.TryParsePage(at, format, item.Ib, out NdbPageInfo p, defects));
                    Assert.Equal(item.PageType, p.PageType);
                    Assert.Equal(item.Bid, p.Bid);
                    Assert.True(p.Check.SignatureValid && p.Check.CrcValid && p.Check.TypeValid, string.Join(";", defects));
                }
                else
                {
                    blocks++;
                    Assert.True(NdbSpan.TryParseBlock(at, format, item.Ib, out NdbBlockInfo b, defects));
                    Assert.Equal(item.DataSize, b.DataLength);
                    Assert.Equal(item.Size, b.TotalLength);
                    Assert.Equal(item.Bid, b.Bid);
                    Assert.True(b.Check.IsClean);
                    Assert.Empty(defects);
                }
            }
        }

        Assert.True(pages > 0 && blocks > 0);
    }

    [Fact]
    public void NdbDamageIsReportedNotThrown()
    {
        var built = NdbTestBuilder.Build(NdbFormat.Unicode, [new TestNode { Nid = 0x21, Data = Pattern(300, 1) }]);
        long ib = built.BlockOffsets.Values.First();
        byte[] copy = (byte[])built.Bytes.Clone();
        copy[ib + 3] ^= 0x55;   // block data
        var defects = new List<NdbDefect>();
        Assert.True(NdbSpan.TryParseBlock(copy.AsSpan((int)ib), NdbFormat.Unicode, ib, out NdbBlockInfo info, defects));
        Assert.False(info.Check.CrcValid);
        Assert.Contains(defects, d => d.Kind == NdbDefectKind.BlockCrcMismatch);

        // Wrong offset: the signature mixes it in.
        Assert.True(NdbSpan.TryParseBlock(built.Bytes.AsSpan((int)ib), NdbFormat.Unicode, ib + 64, out info));
        Assert.False(info.Check.SignatureValid);

        // Short, empty and random spans.
        Assert.False(NdbSpan.TryParsePage(built.Bytes.AsSpan(0, 100), NdbFormat.Unicode, 0, out _));
        Assert.False(NdbSpan.TryParseBlock([], NdbFormat.Unicode, 0, out _));
        var rng = new Random(9);
        var junk = new byte[70000];
        for (int i = 0; i < 300; i++)
        {
            rng.NextBytes(junk);
            foreach (NdbFormat fmt in new[] { NdbFormat.Ansi, NdbFormat.Unicode, NdbFormat.Unicode4K })
            {
                _ = NdbSpan.TryParsePage(junk.AsSpan(0, rng.Next(0, junk.Length)), fmt, rng.Next(), out _);
                _ = NdbSpan.TryParseBlock(junk.AsSpan(0, rng.Next(0, junk.Length)), fmt, rng.Next(), out _);
            }
        }
    }

    [Fact]
    public void MimeHeaderBlocksAreRecognisedAndEntitiesParsed()
    {
        byte[] msg = "From: a@example.com\r\nSubject: one\r\n two\r\n\r\nbody\r\n"u8.ToArray();
        Assert.True(MimeHeaderBlock.TryParse(msg, out MimeHeaderBlockInfo info));
        Assert.Equal(2, info.HeaderCount);
        Assert.True(info.Terminated);
        Assert.Equal(msg.Length - "body\r\n".Length, info.BodyOffset);
        Assert.Equal(0, info.BareLineFeeds);

        Assert.True(MimeEntity.TryParse(msg, out MimeEntity? e));
        Assert.NotNull(e);
        Assert.Equal("a@example.com", e!.Headers.GetFirst("From")!.Value.Trim());

        // Cut in the middle of a header, bare LF line ends, and data that is not a message.
        Assert.True(MimeHeaderBlock.TryParse(msg.AsSpan(0, 30), out info));
        Assert.False(info.Terminated);
        Assert.True(MimeHeaderBlock.TryParse("A: b\nC: d\n\nx"u8, out info));
        Assert.Equal(3, info.BareLineFeeds);
        Assert.False(MimeHeaderBlock.TryParse("\r\nA: b\r\n"u8, out _));
        Assert.False(MimeHeaderBlock.TryParse(" folded first\r\n"u8, out _));
        Assert.False(MimeHeaderBlock.TryParse("no colon here\r\n\r\n"u8, out _));
        Assert.False(MimeHeaderBlock.TryParse([], out _));
        Assert.False(MimeEntity.TryParse([0xFF, 0x00, 0x01], out MimeEntity? none));
        Assert.Null(none);
        var rng = new Random(3);
        var junk = new byte[4000];
        for (int i = 0; i < 500; i++)
        {
            rng.NextBytes(junk);
            _ = MimeHeaderBlock.TryParse(junk.AsSpan(0, rng.Next(junk.Length)), out _);
        }
    }

    [Fact]
    public void ADbxSegmentIsRecognisedLiveOrFreed()
    {
        List<DbxTestMessage> msgs = [new() { Text = Enumerable.Repeat((byte)'x', 1200).ToArray() }, new() { Text = new byte[40] }];
        byte[] file = DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Deleted = new HashSet<int> { 1 } });
        int live = 0, freed = 0;
        for (int p = 0x24BC; p + 0x210 <= file.Length; p += 4)
        {
            if (DbxSpan.TryParseSegment(file.AsSpan(p, 0x210), p, out DbxSegmentInfo s))
            {
                if (s.Kind == DbxSegmentKind.Live)
                {
                    live++;
                    Assert.True(s.TextLengthKnown);
                }
                else
                {
                    freed++;
                    Assert.False(s.TextLengthKnown);
                    Assert.Equal(20, s.TextStart);
                }
            }
        }

        Assert.Equal(3, live);
        Assert.Equal(1, freed);
        Assert.False(DbxSpan.TryParseSegment(file.AsSpan(0x24BC, 0x210), 0x24C0, out _));
        Assert.False(DbxSpan.TryParseSegment([], 0x24BC, out _));
    }

    [Fact]
    public void ADbxRecordParsesFromASpanAndSaysWhenItIsCut()
    {
        byte[] file = DbxTestBuilder.MessageDatabase([new DbxTestMessage { Text = "Subject: s\r\n\r\nbody"u8.ToArray(), Subject = "s" }]);
        using var src = new RecursiveAI.OneMail.Sources.MemorySource(file);
        DbxMessageEntry e = new DbxReader(src).EnumerateMessages().Single();
        int at = (int)e.Offset;
        Assert.True(DbxSpan.TryParseRecord(file.AsSpan(at), at, out DbxRecord? r, out StoreDefects d));
        Assert.Equal(StoreDefects.None, d);
        Assert.Equal(e.FieldNumbers, r!.FieldNumbers);
        Assert.True(DbxSpan.TryParseRecord(file.AsSpan(at, 12 + (4 * e.FieldNumbers.Count()) + 2), at, out _, out d));
        Assert.True((d & StoreDefects.DbxBadIndexedInfo) != 0);
        Assert.False(DbxSpan.TryParseRecord(file.AsSpan(at), at + 4, out _, out _));
    }

    [Fact]
    public void ACfbHeaderAndDirectoryEntryAreRecognised()
    {
        var h = new byte[512];
        CfbSpan.Signature.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x18), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x2C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x38), 0x1000);
        Assert.True(CfbSpan.TryParseHeader(h, out CfbHeaderInfo info));
        Assert.Equal(CfbHeaderDefects.None, info.Defects);
        Assert.Equal(512, info.SectorSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1E), 12);
        Assert.True(CfbSpan.TryParseHeader(h, out info));
        Assert.True((info.Defects & CfbHeaderDefects.SectorShiftVersionMismatch) != 0);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1A), 9);
        Assert.False(CfbSpan.TryParseHeader(h, out _));
        Assert.False(CfbSpan.TryParseHeader(h.AsSpan(0, 100), out _));
        h[0] ^= 1;
        Assert.False(CfbSpan.TryParseHeader(h, out _));

        var e = new byte[128];
        Encoding.Unicode.GetBytes("__properties_version1.0").CopyTo(e, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(0x40), (ushort)(("__properties_version1.0".Length + 1) * 2));
        e[0x42] = 2;
        e[0x43] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(0x78), 32);
        Assert.True(CfbSpan.TryParseDirectoryEntry(e, out CfbDirectoryEntryInfo entry));
        Assert.Equal("__properties_version1.0", entry.Name);
        Assert.Equal(32UL, entry.Size);
        e[0x42] = 9;
        Assert.False(CfbSpan.TryParseDirectoryEntry(e, out _));
        Assert.False(CfbSpan.TryParseDirectoryEntry(e.AsSpan(0, 50), out _));
        var rng = new Random(1);
        for (int i = 0; i < 500; i++)
        {
            rng.NextBytes(e);
            _ = CfbSpan.TryParseDirectoryEntry(e, out _);
        }
    }
}
