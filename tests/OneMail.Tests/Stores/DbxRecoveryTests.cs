using System.Buffers.Binary;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Dbx;

namespace RecursiveAI.OneMail.Tests.Stores;

public class DbxRecoveryTests
{
    private static List<DbxTestMessage> Messages(int count)
    {
        // Sizes that end inside a segment, exactly on a segment end, and in a one-segment message.
        int[] sizes = [1500, 1024, 300, 2600, 512, 90];
        var l = new List<DbxTestMessage>();
        for (int i = 0; i < count; i++)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("From: a").Append(i).Append("@example.com\r\nSubject: Message ").Append(i).Append("\r\n\r\n");
            while (sb.Length < sizes[i % sizes.Length])
            {
                sb.Append("line ").Append(sb.Length).Append(" of message ").Append(i).Append("\r\n");
            }

            l.Add(new DbxTestMessage
            {
                Text = System.Text.Encoding.Latin1.GetBytes(sb.ToString(0, sizes[i % sizes.Length])),
                Subject = "Message " + i,
                MessageId = "<m" + i + "@example.com>",
            });
        }

        return l;
    }

    // The text of a deleted message as the file keeps it: every segment loses its first four bytes to the free-list link.
    private static byte[] ExpectedAfterDeletion(byte[] text)
    {
        var o = new List<byte>();
        for (int at = 0; at < text.Length; at += 0x200)
        {
            int len = Math.Min(0x200, text.Length - at);
            if (len > 4)
            {
                o.AddRange(text.AsSpan(at + 4, len - 4).ToArray());
            }
        }

        return [.. o];
    }

    private static List<DbxRecoveredMessage> Recover(byte[] file, DbxRecoveryOptions? o = null)
    {
        using var src = new MemorySource(file);
        return [.. new DbxReader(src).RecoverDeleted(o)];
    }

    private static int LiveCount(byte[] file)
    {
        using var src = new MemorySource(file);
        return new DbxReader(src).EnumerateMessages().Count();
    }

    [Fact]
    public void DeletedMessagesAreFoundWithTheirBytesAndRanges()
    {
        List<DbxTestMessage> msgs = Messages(6);
        foreach (int seed in new[] { 0, 7 })
        {
            byte[] file = DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Deleted = new HashSet<int> { 1, 3, 4 }, ShuffleSeed = seed });
            Assert.Equal(3, LiveCount(file));
            List<DbxRecoveredMessage> got = Recover(file);
            Assert.Equal(3, got.Count);
            foreach (int d in new[] { 1, 3, 4 })
            {
                byte[] want = ExpectedAfterDeletion(msgs[d].Text);
                DbxRecoveredMessage m = Assert.Single(got, g => g.Content.Span.SequenceEqual(want));
                Assert.Equal(RecoveryStatus.FreeSpace, m.Provenance.Status);
                Assert.Contains(m.Provenance.Defects, x => x.Code == nameof(DbxRecoveryDefect.LeadingBytesOverwritten));
                // The ranges re-read from the file give the content back.
                // Ranges are sorted by file offset; the content is in chain order, so compare piece by piece.
                var pieces = new List<(long At, byte[] Bytes)>();
                for (int k = 0; k < m.Segments.Count; k++)
                {
                    int from = k * 0x1FC;
                    pieces.Add((m.Segments[k], m.Content.Span.Slice(from, Math.Min(0x1FC, m.Content.Length - from)).ToArray()));
                }

                var joined = new List<byte>();
                foreach (ByteRange r in m.Provenance.Ranges)
                {
                    joined.AddRange(file.AsSpan((int)r.Offset, (int)r.Length).ToArray());
                }

                Assert.Equal(pieces.OrderBy(x => x.At).SelectMany(x => x.Bytes).ToArray(), joined.ToArray());
                Assert.All(m.Provenance.Lost, r => Assert.Equal(4, r.Length));
            }
        }
    }

    [Fact]
    public void NothingIsFoundInAFileWithoutDeletions()
    {
        byte[] file = DbxTestBuilder.MessageDatabase(Messages(6), new DbxBuild { ShuffleSeed = 3 });
        Assert.Empty(Recover(file));
    }

    [Fact]
    public void RecoveredMessagesNeverCarryTheLiveTagOrALiveSegment()
    {
        List<DbxTestMessage> msgs = Messages(6);
        byte[] file = DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Deleted = new HashSet<int> { 0, 2 }, Unlinked = new HashSet<int> { 5 } });
        using var src = new MemorySource(file);
        var r = new DbxReader(src);
        var liveRanges = new List<(long, long)>();
        foreach (DbxMessageEntry e in r.EnumerateMessages())
        {
            long p = e.ChainOffset;
            while (p != 0)
            {
                liveRanges.Add((p, p + 0x210));
                p = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)p + 12));
            }
        }

        foreach (DbxRecoveredMessage m in r.RecoverDeleted())
        {
            Assert.NotEqual(RecoveryStatus.Live, m.Provenance.Status);
            foreach (ByteRange br in m.Provenance.Ranges)
            {
                Assert.DoesNotContain(liveRanges, l => br.Offset < l.Item2 && br.End > l.Item1);
            }
        }
    }

    [Fact]
    public void AnUnlinkedChainIsOrphanedAndWhole()
    {
        List<DbxTestMessage> msgs = Messages(5);
        byte[] file = DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Unlinked = new HashSet<int> { 3 } });
        DbxRecoveredMessage m = Assert.Single(Recover(file));
        Assert.Equal(RecoveryStatus.Orphaned, m.Provenance.Status);
        Assert.Equal(msgs[3].Text, m.Content.ToArray());
        Assert.Empty(m.Provenance.Defects);
        Assert.Empty(m.Provenance.Lost);
        Assert.Equal(msgs[3].Text.Length, m.Provenance.TotalLength);
    }

    [Fact]
    public void AnOrphanThatIsACopyOfALiveMessageIsLeftOutUnlessAsked()
    {
        List<DbxTestMessage> msgs = Messages(3);
        msgs.Add(new DbxTestMessage { Text = msgs[1].Text, Subject = "copy" });
        byte[] file = DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Unlinked = new HashSet<int> { 3 } });
        Assert.Empty(Recover(file));
        DbxRecoveredMessage m = Assert.Single(Recover(file, new DbxRecoveryOptions { IncludeDuplicatesOfLive = true }));
        Assert.Contains(m.Provenance.Defects, d => d.Code == nameof(DbxRecoveryDefect.DuplicateOfLive));
    }

    [Fact]
    public void AChainThatLeadsIntoALiveSegmentIsPartial()
    {
        List<DbxTestMessage> msgs = Messages(4);
        byte[] file = DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Deleted = new HashSet<int> { 3 } });
        // Message 3 has five segments; point the old next pointer of its first segment at a live message's first segment.
        long head;
        {
            using var src = new MemorySource(file);
            var r = new DbxReader(src);
            head = r.RecoverDeleted().Single().Segments[0];
            long liveSeg = r.EnumerateMessages().First().ChainOffset;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan((int)head + 12), (uint)liveSeg);
        }

        // The first segment is cut off from the rest, which no longer has a known start.
        List<DbxRecoveredMessage> got = Recover(file);
        DbxRecoveredMessage m = Assert.Single(got, g => g.Segments[0] == head);
        Assert.Equal(RecoveryStatus.Partial, m.Provenance.Status);
        Assert.Contains(m.Provenance.Defects, d => d.Code == nameof(DbxRecoveryDefect.ChainContinuesIntoOtherObject));
        Assert.Equal(2, got.Count);
    }

    [Fact]
    public void ARecoveredMessageParsesAndCarriesItsProvenanceThroughTheSharedView()
    {
        List<DbxTestMessage> msgs = Messages(4);
        byte[] file = DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Unlinked = new HashSet<int> { 2 } });
        DbxRecoveredMessage m = Assert.Single(Recover(file));
        IMailItem item = m.ToMailItem();
        Assert.Equal("Message 2", item.Subject);
        Assert.Same(m.Provenance, item.Recovery);
    }

    [Fact]
    public void HostileFilesAreReadWithoutThrowing()
    {
        List<DbxTestMessage> msgs = Messages(6);
        byte[] good = DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Deleted = new HashSet<int> { 1, 2 } });
        var rng = new Random(44);
        for (int round = 0; round < 60; round++)
        {
            byte[] f = (byte[])good.Clone();
            switch (round % 4)
            {
                case 0:
                    Array.Resize(ref f, rng.Next(0, f.Length));
                    break;
                case 1:
                    for (int k = 0; k < 20; k++)
                    {
                        f[rng.Next(0x24BC, f.Length)] = (byte)rng.Next(256);
                    }

                    break;
                case 2:
                    // A free list that loops on itself.
                    BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x48), BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(0x48)));
                    int head = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(0x48));
                    if (head > 0 && head + 20 <= f.Length)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(head + 16), head);
                    }

                    break;
                default:
                    for (int k = 0x48; k < 0x50; k++)
                    {
                        f[k] = 0xFF;
                    }

                    break;
            }

            List<DbxRecoveredMessage> got = Recover(f);
            Assert.All(got, g => Assert.NotEqual(RecoveryStatus.Live, g.Provenance.Status));
        }
    }
}
