using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RecursiveAI.OneMail.Recovery;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores.Olm;

namespace RecursiveAI.OneMail.Tests.Olm;

public class OlmRecoveryTests
{
    private const int Messages = 8;

    // Eight folders of one message each, every message with a deflated body of pseudo-random words and one attachment entry.
    private static byte[] Archive()
    {
        var rng = new Random(2026);
        string[] words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"];
        var a = OlmTestArchive.WithFolders();
        for (int i = 0; i < Messages; i++)
        {
            var text = new StringBuilder();
            while (text.Length < 6000)
            {
                text.Append(words[rng.Next(words.Length)]).Append(rng.Next(100000)).Append(' ');
            }

            string att = OlmTestArchive.Root + "F" + i + "/com.microsoft.__Attachments/" + i + "_0001";
            string body = OlmTestArchive.El("OPFMessageCopyBody", text.ToString()) + OlmTestArchive.El("OPFMessageCopySubject", "Message " + i)
                + OlmTestArchive.El("OPFMessageGetHasHTML", "0E0")
                + "<OPFMessageCopyAttachmentList xml:space=\"preserve\"><messageAttachment xml:space=\"preserve\" OPFAttachmentName=\"a" + i + ".bin\" OPFAttachmentContentType=\"application/octet-stream\" OPFAttachmentURL=\"" + att + "\"/></OPFMessageCopyAttachmentList>";
            a.Message("F" + i, 0, body);
            byte[] bin = new byte[3000];
            rng.NextBytes(bin);
            a.Add(att, bin);
        }

        return a.Build(CompressionLevel.Optimal);
    }

    private sealed class CountingSource(byte[] data) : IRandomAccessSource
    {
        private readonly MemorySource _inner = new(data);
        public long BytesRead { get; private set; }
        public long Length => _inner.Length;
        public bool SupportsSyncRead => true;
        public int Read(long offset, Span<byte> buffer)
        {
            int n = _inner.Read(offset, buffer);
            BytesRead += n;
            return n;
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await _inner.ReadAsync(offset, buffer, cancellationToken);
            BytesRead += n;
            return n;
        }

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void RecoveryReadsEachRecordEntryOnceNotTwice()
    {
        // Recovery judges an entry for damage before handing out its records. That used to be a second pass over the compressed bytes.
        byte[] zip = Archive();
        long Bytes(bool recovery)
        {
            var src = new CountingSource(zip);
            using OlmArchive a = OlmArchive.Open(src, false, null, new OlmReaderOptions { Recovery = recovery });
            int n = a.Enumerate().Count();
            Assert.Equal(Messages, n);
            return src.BytesRead;
        }

        long plain = Bytes(false);
        long recovered = Bytes(true);
        Assert.True(recovered < plain * 1.3, $"recovery read {recovered} bytes, a plain read {plain}");
    }

    private static List<long> HeaderOffsets(byte[] zip)
    {
        var l = new List<long>();
        for (int i = 0; i + 4 <= zip.Length; i++)
        {
            if (zip[i] == 'P' && zip[i + 1] == 'K' && zip[i + 2] == 3 && zip[i + 3] == 4)
            {
                l.Add(i);
            }
        }

        return l;
    }

    private static (List<OlmItem> Items, OlmArchive Archive) Read(byte[] zip, bool recovery = true)
    {
        OlmArchive a = OlmArchive.Open(new MemorySource(zip), false, null, new OlmReaderOptions { Recovery = recovery });
        return ([.. a.Enumerate()], a);
    }

    private static string Subject(OlmItem i) => i.Subject ?? string.Empty;

    [Fact]
    public void AnIntactArchiveHasNoDamageAndEveryItemIsLive()
    {
        (List<OlmItem> items, OlmArchive a) = Read(Archive());
        using (a)
        {
            Assert.Equal(Messages, items.Count);
            Assert.Empty(a.Damage);
            foreach (OlmItem i in items)
            {
                Assert.Equal(RecoveryStatus.Live, i.Recovery!.Status);
                Assert.NotEmpty(i.Recovery.Ranges);
            }

            Assert.DoesNotContain(a.Defects, d => d.Kind is OlmDefectKind.ArchiveRecovered or OlmDefectKind.EntryDamaged);
        }
    }

    [Fact]
    public void WithoutRecoveryTheItemsCarryNoTag()
    {
        (List<OlmItem> items, OlmArchive a) = Read(Archive(), recovery: false);
        using (a)
        {
            Assert.All(items, i => Assert.Null(i.Recovery));
        }
    }

    [Fact]
    public void ATruncatedArchiveKeepsTheCompleteEntries()
    {
        byte[] zip = Archive();
        List<long> offs = HeaderOffsets(zip);
        // Cut in the middle of the 11th entry (entries alternate folder, message... the exact count does not matter).
        byte[] cut = zip.AsSpan(0, (int)(offs[offs.Count * 2 / 3] + 40)).ToArray();
        (List<OlmItem> items, OlmArchive a) = Read(cut);
        using (a)
        {
            Assert.InRange(items.Count, 1, Messages - 1);
            Assert.NotEmpty(a.Damage);
            Assert.Contains(a.Defects, d => d.Kind == OlmDefectKind.ArchiveRecovered);
            foreach (OlmItem i in items)
            {
                Assert.NotNull(i.Recovery);
                Assert.True(i.Recovery!.Ranges.All(r => r.End <= cut.Length));
            }
        }
    }

    [Fact]
    public void ADestroyedDirectoryStillGivesEveryEntry()
    {
        byte[] zip = Archive();
        int end = zip.AsSpan().LastIndexOf("PK\x05\x06"u8);
        Assert.True(end > 0);
        zip[end] = 0;
        (List<OlmItem> items, OlmArchive a) = Read(zip);
        using (a)
        {
            Assert.Equal(Messages, items.Count);
            Assert.Contains(a.Damage, d => d.Kind == OlmDamageKind.DirectoryUnusable);
            Assert.All(items, i => Assert.Equal(RecoveryStatus.Live, i.Recovery!.Status));
            Assert.Equal(Enumerable.Range(0, Messages).Select(n => "Message " + n).Order(), items.Select(Subject).Order());
        }
    }

    [Fact]
    public void GarbageBetweenEntriesIsSkippedAndReported()
    {
        byte[] zip = Archive();
        List<long> offs = HeaderOffsets(zip);
        int at = (int)offs[offs.Count / 2];
        var garbage = new byte[300];
        new Random(5).NextBytes(garbage);
        garbage[0] = 0x7F;   // no accidental signature
        for (int i = 0; i + 3 < garbage.Length; i++)
        {
            if (garbage[i] == 'P' && garbage[i + 1] == 'K')
            {
                garbage[i] = 1;
            }
        }

        byte[] damaged = [.. zip.AsSpan(0, at).ToArray(), .. garbage, .. zip.AsSpan(at).ToArray()];
        (List<OlmItem> items, OlmArchive a) = Read(damaged);
        using (a)
        {
            Assert.Equal(Messages, items.Count);
            Assert.Contains(a.Damage, d => d.Kind == OlmDamageKind.SkippedBytes && d.Length >= 300);
            Assert.All(items, i => Assert.Equal(RecoveryStatus.Live, i.Recovery!.Status));
        }
    }

    [Fact]
    public void ADamagedEntryMakesItsItemPartialAndNothingElse()
    {
        byte[] zip = Archive();
        List<long> offs = HeaderOffsets(zip);
        // Corrupt the data of the 3rd message record (its entry is the first of the folder's pair after the folder entries).
        int target = -1;
        for (int k = 0; k < offs.Count && target < 0; k++)
        {
            int nameLen = zip[offs[k] + 26] | (zip[offs[k] + 27] << 8);
            string name = Encoding.ASCII.GetString(zip, (int)offs[k] + 30, nameLen);
            if (name.EndsWith("F2/message_00000.xml", StringComparison.Ordinal))
            {
                target = (int)offs[k] + 30 + nameLen + 40;
            }
        }

        Assert.True(target > 0);
        for (int k = 0; k < 6; k++)
        {
            zip[target + k] ^= 0xFF;
        }

        (List<OlmItem> items, OlmArchive a) = Read(zip);
        using (a)
        {
            Assert.Contains(a.Defects, d => d.Kind == OlmDefectKind.EntryDamaged);
            OlmItem[] partial = items.Where(i => i.Recovery!.Status == RecoveryStatus.Partial).ToArray();
            Assert.All(partial, p => Assert.Contains("F2", p.Entry));
            Assert.Equal(Messages - 1, items.Count(i => i.Recovery!.Status == RecoveryStatus.Live));
            Assert.Contains(a.Damage, d => d.Kind == OlmDamageKind.DamagedEntry && d.Entry!.Contains("F2", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RangesStayInsideTheFileWhateverTheDirectoryClaims()
    {
        // A directory that puts entries far past the end of the file (found by the fuzzer: testdata/fuzz/olm-recovery-range-past-end.bin).
        byte[] zip = Archive();
        int cd = zip.AsSpan().IndexOf("PK\x01\x02"u8);
        for (int k = 0; k < 3; k++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 42), 0x7FFFFFF0u);
            int nameLen = zip[cd + 28] | (zip[cd + 29] << 8);
            int extraLen = zip[cd + 30] | (zip[cd + 31] << 8);
            int commentLen = zip[cd + 32] | (zip[cd + 33] << 8);
            cd += 46 + nameLen + extraLen + commentLen;
        }

        (List<OlmItem> items, OlmArchive a) = Read(zip.AsSpan(0, zip.Length - 50).ToArray());
        using (a)
        {
            foreach (OlmItem i in items)
            {
                Assert.All(i.Recovery!.Ranges.Concat(i.Recovery.Lost), r => Assert.True(r.Offset >= 0 && r.Length >= 0 && r.End <= zip.Length - 50));
            }

            Assert.All(a.Damage, _ => { });
        }
    }

    [Fact]
    public void HostileDamageNeverThrows()
    {
        byte[] zip = Archive();
        var rng = new Random(44);
        for (int round = 0; round < 40; round++)
        {
            byte[] f = (byte[])zip.Clone();
            switch (round % 3)
            {
                case 0:
                    Array.Resize(ref f, rng.Next(30, f.Length));
                    break;
                case 1:
                    for (int k = 0; k < 30; k++)
                    {
                        f[rng.Next(f.Length)] = (byte)rng.Next(256);
                    }

                    break;
                default:
                    int from = rng.Next(f.Length - 200);
                    Array.Clear(f, from, rng.Next(1, 200));
                    break;
            }

            try
            {
                (List<OlmItem> items, OlmArchive a) = Read(f);
                using (a)
                {
                    foreach (OlmItem i in items)
                    {
                        _ = i.Recovery;
                        _ = i.Subject;
                    }
                }
            }
            catch (InvalidDataException)
            {
                // Nothing in the file is a zip at all: the one documented refusal.
            }
        }
    }
}
