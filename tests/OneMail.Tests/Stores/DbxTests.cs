using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Dbx;

namespace RecursiveAI.OneMail.Tests.Stores;

public class DbxTests
{
    private static byte[] Msg(int n, int size)
    {
        // CR LF line ends, as Outlook Express stores them, and a body whose length is chosen by the caller.
        StringBuilder sb = new();
        sb.Append("From: a").Append(n).Append("@example.com\r\nTo: b@example.com\r\nSubject: Message ").Append(n)
            .Append("\r\nMessage-ID: <m").Append(n).Append("@example.com>\r\n\r\n");
        while (sb.Length < size)
        {
            sb.Append("line ").Append(sb.Length).Append("\r\n");
        }

        return Encoding.Latin1.GetBytes(sb.ToString(0, Math.Max(size, 1)));
    }

    private static List<DbxTestMessage> Messages(int count, params int[] sizes)
    {
        List<DbxTestMessage> l = [];
        for (int i = 0; i < count; i++)
        {
            l.Add(new DbxTestMessage
            {
                Text = Msg(i, sizes[i % sizes.Length]),
                Subject = "Message " + i,
                MessageId = "<m" + i + "@example.com>",
                SenderName = "Sender " + i,
                SenderAddress = "a" + i + "@example.com",
            });
        }

        return l;
    }

    private static List<(DbxMessageEntry Entry, byte[] Bytes, DbxChainInfo Info)> ReadAll(byte[] file, DbxReaderOptions? options = null)
    {
        using MemorySource src = new(file);
        DbxReader r = new(src, options);
        List<(DbxMessageEntry, byte[], DbxChainInfo)> l = [];
        foreach (DbxMessageEntry e in r.EnumerateMessages())
        {
            byte[] b = r.ReadMessage(e, out DbxChainInfo info);
            l.Add((e, b, info));
        }

        return l;
    }

    [Theory]
    [InlineData(0, 0x33, 0)]
    [InlineData(1, 0x33, 0)]
    [InlineData(7, 3, 0)]
    [InlineData(100, 1, 0)]
    [InlineData(100, 2, 77)]
    [InlineData(500, 0x33, 5)]
    [InlineData(2000, 0x33, 9)]
    [InlineData(300, 4, 11)]
    public void EnumeratesEveryMessageInIndexOrderWithExactBytes(int count, int fanout, int shuffle)
    {
        List<DbxTestMessage> m = Messages(count, 0, 1, 511, 512, 513, 1024, 5000, 100);
        byte[] file = DbxTestBuilder.MessageDatabase(m, new DbxBuild { TreeFanout = fanout, ShuffleSeed = shuffle });
        var got = ReadAll(file);
        Assert.Equal(count, got.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i + 1, got[i].Entry.Index);
            Assert.Equal(m[i].Text, got[i].Bytes);
            Assert.Equal(m[i].Text.Length, got[i].Info.Length);
            Assert.Equal(StoreDefects.None, got[i].Entry.Defects | got[i].Info.Defects);
            Assert.Equal(m[i].Subject, got[i].Entry.OriginalSubject);
            Assert.Equal(m[i].MessageId, got[i].Entry.MessageId);
            Assert.Equal(m[i].SenderAddress, got[i].Entry.SenderAddress);
            Assert.Equal(m[i].Received, got[i].Entry.DateReceived);
            Assert.True(got[i].Entry.HasMessage);
        }
    }

    [Fact]
    public void ReadsRecordsAndGivesHeaderOnlyOnesAnEmptyMessage()
    {
        List<DbxTestMessage> m =
        [
            new DbxTestMessage { Text = Msg(0, 300), Flags = 0x80 | 0x20 | 0x80000, Priority = 1 },
            new DbxTestMessage { NoBody = true, Subject = "header only" },
        ];
        var got = ReadAll(DbxTestBuilder.MessageDatabase(m));
        Assert.Equal(2, got.Count);
        Assert.Equal(MessageMarks.Seen | MessageMarks.Flagged | MessageMarks.Answered, got[0].Entry.Marks);
        Assert.Equal(1, got[0].Entry.Priority);
        Assert.False(got[1].Entry.HasMessage);
        Assert.Equal(StoreDefects.AmbiguousDbxNoMessageText, got[1].Entry.Defects);
        Assert.Empty(got[1].Bytes);
        Assert.Equal(0, got[1].Info.Length);
    }

    [Fact]
    public void ReadsPointersBeyondTheDirectFieldRange()
    {
        // A field value of 3 bytes cannot hold an offset past 16 MiB; the record stores it in the data area.
        List<DbxTestMessage> m =
        [
            new DbxTestMessage { Text = Msg(0, 17 << 20) },
            new DbxTestMessage { Text = Msg(1, 1000) },
        ];
        using MemorySource src = new(DbxTestBuilder.MessageDatabase(m));
        DbxReader r = new(src);
        List<DbxMessageEntry> e = [.. r.EnumerateMessages()];
        Assert.Equal(2, e.Count);
        Assert.True(e[1].ChainOffset > 0xFFFFFF);
        Assert.Equal(m[1].Text, r.ReadMessage(e[1], out _));
        Assert.Equal(m[0].Text, r.ReadMessage(e[0], out _));
    }

    [Fact]
    public void ChainLengthWinsOverTheDeclaredLength()
    {
        List<DbxTestMessage> m = [new DbxTestMessage { Text = Msg(0, 900), DeclaredLength = 5 }];
        var got = ReadAll(DbxTestBuilder.MessageDatabase(m));
        Assert.Equal(m[0].Text, got[0].Bytes);
        Assert.True((got[0].Info.Defects & StoreDefects.AmbiguousDbxDeclaredLength) != 0);
    }

    [Fact]
    public void MessageStreamReadsTheChainInSmallPieces()
    {
        List<DbxTestMessage> m = Messages(1, 5000);
        using MemorySource src = new(DbxTestBuilder.MessageDatabase(m, new DbxBuild { ShuffleSeed = 3 }));
        DbxReader r = new(src);
        DbxMessageEntry e = r.EnumerateMessages().Single();
        using DbxMessageStream s = r.OpenMessage(e);
        using MemoryStream ms = new();
        byte[] buf = new byte[37];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, n);
        }

        Assert.Equal(m[0].Text, ms.ToArray());
        Assert.Equal(m[0].Text.Length, s.Position);
    }

    [Fact]
    public void ReadsFolderHierarchyAndNamesTheFiles()
    {
        List<DbxTestFolder> f =
        [
            new() { Index = 0, Parent = -1, Name = "Outlook Express" },
            new() { Index = 1, Parent = 0, Name = "Local Folders" },
            new() { Index = 2, Parent = 1, Name = "Inbox", File = "Inbox.dbx", Special = 1, Messages = 3 },
            new() { Index = 3, Parent = 1, Name = "Work", File = "Work.dbx" },
            new() { Index = 4, Parent = 3, Name = "Old", File = "Old.dbx" },
        ];
        using MemorySource src = new(DbxTestBuilder.FolderDatabase(f));
        DbxReader r = new(src);
        Assert.Equal(DbxFileKind.FolderDatabase, r.Kind);
        List<DbxFolderEntry> got = [.. r.EnumerateFolders()];
        Assert.Equal(5, got.Count);
        Assert.Equal(["Outlook Express", "Local Folders", "Inbox", "Work", "Old"], got.Select(x => x.Name));
        Assert.Equal(-1, got[0].ParentIndex);
        Assert.Equal("Inbox.dbx", got[2].FileName);
        Assert.Equal(1, got[2].SpecialFolder);
        Assert.Equal(3, got[2].MessageCount);
        Assert.Empty(r.EnumerateMessages());
    }

    [Fact]
    public void StoreFolderEnumeratesFoldersCaseInsensitivelyAndFlagsUnlistedFiles()
    {
        MemoryStoreFileSystem fs = new();
        Write(fs, "Folders.dbx", DbxTestBuilder.FolderDatabase(
        [
            new() { Index = 0, Parent = -1, Name = "Outlook Express" },
            new() { Index = 1, Parent = 0, Name = "Local Folders" },
            new() { Index = 2, Parent = 1, Name = "Inbox", File = "Inbox.dbx" },
            new() { Index = 3, Parent = 2, Name = "Sub", File = "sub.dbx" },
        ]));
        Write(fs, "inbox.DBX", DbxTestBuilder.MessageDatabase(Messages(3, 600)));
        Write(fs, "Sub.dbx", DbxTestBuilder.MessageDatabase(Messages(2, 10)));
        Write(fs, "Stray.dbx", DbxTestBuilder.MessageDatabase(Messages(1, 10)));
        Write(fs, "Pop3uidl.dbx", Header(0xC7));
        Assert.True(DbxStoreFolder.Sniff(fs));
        Assert.Equal(StoreFormat.DbxStoreFolder, StoreSniffer.SniffDirectory(fs));
        DbxStoreFolder store = new(fs);
        List<DbxStoreMessage> all = [.. store.EnumerateMessages()];
        Assert.Equal(["Local Folders/Inbox", "Local Folders/Inbox", "Local Folders/Inbox", "Local Folders/Inbox/Sub", "Local Folders/Inbox/Sub", "Stray"], all.Select(x => x.Folder));
        Assert.Equal(StoreDefects.DbxUnlistedFile, all[^1].Defects);
        byte[] b = store.ReadMessage(all[0], out _);
        Assert.Equal(Msg(0, 600), b);
    }

    private static void Write(MemoryStoreFileSystem fs, string name, byte[] data)
    {
        using Stream s = fs.CreateNew(name);
        s.Write(data);
    }

    private static byte[] Header(byte clsid)
    {
        byte[] h = new byte[0x24BC + 0x20];
        h[0] = 0xCF;
        h[1] = 0xAD;
        h[2] = 0x12;
        h[3] = 0xFE;
        h[4] = clsid;
        h[5] = 0xFD;
        h[6] = 0x74;
        h[7] = 0x6F;
        return h;
    }

    [Fact]
    public void SniffsByContentNotName()
    {
        byte[] file = DbxTestBuilder.MessageDatabase(Messages(2, 100));
        using MemorySource src = new(file);
        Assert.Equal(MailFormat.Dbx, MailFormatSniffer.Sniff(src));
        Assert.Equal(StoreFormat.Dbx, StoreSniffer.Sniff(src));
        Assert.Equal(DbxFileKind.MessageDatabase, DbxReader.Detect(file));
        Assert.Equal(DbxFileKind.Pop3Uidl, DbxReader.Detect(Header(0xC7)));
        Assert.Equal(DbxFileKind.Offline, DbxReader.Detect(new byte[] { 0xCF, 0xAD, 0x12, 0xFE, 0x30, 0x9D, 0xFE, 0x26 }));
        Assert.Equal(DbxFileKind.Unknown, DbxReader.Detect(new byte[] { 0xCF, 0xAD, 0x12, 0xFE, 1, 2, 3, 4 }));
        Assert.Equal(DbxFileKind.Unknown, DbxReader.Detect(new byte[3]));
    }

    [Fact]
    public async Task MailOpenReadsADbxFileAndAStoreFolder()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "dbx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Folders.dbx"), DbxTestBuilder.FolderDatabase(
            [
                new() { Index = 0, Parent = -1, Name = "Outlook Express" },
                new() { Index = 2, Parent = 0, Name = "Inbox", File = "Inbox.dbx" },
            ]));
            string inbox = Path.Combine(dir, "Inbox.dbx");
            File.WriteAllBytes(inbox, DbxTestBuilder.MessageDatabase(Messages(4, 200, 3000)));

            using (MailContainer c = Mail.Open(inbox))
            {
                Assert.Equal(MailFormat.Dbx, c.Format);
                List<IMailItem> items = [.. c.Enumerate()];
                Assert.Equal(4, items.Count);
                Assert.Equal("Message 3", items[3].Subject);
                Assert.Equal(MailFormat.Dbx, items[0].Provenance.Format);
                Assert.True(items[0].TryGetDate(MailDateSource.Delivery, out MailDate delivered));
                Assert.Equal(new DateTimeOffset(2004, 5, 6, 7, 8, 9, TimeSpan.Zero), delivered.Value);
            }

            using (MailContainer c = Mail.Open(dir))
            {
                Assert.Equal(MailFormat.DbxStore, c.Format);
                List<IMailItem> items = [];
                await foreach (IMailItem it in c)
                {
                    items.Add(it);
                }

                Assert.Equal(4, items.Count);
                Assert.Equal("Inbox", items[0].Provenance.Folder);
                Assert.Equal("Message 2", items[2].Subject);
            }

            using (MailContainer c = Mail.Open(Path.Combine(dir, "Folders.dbx")))
            {
                Assert.Equal(MailFormat.Dbx, c.Format);
                Assert.Empty(c.Enumerate());
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- hostile input ----

    private static void Drain(byte[] file)
    {
        using MemorySource src = new(file);
        DbxReader r = new(src, new DbxReaderOptions { MaxMessageBytes = 1 << 24 });
        foreach (DbxMessageEntry e in r.EnumerateMessages())
        {
            _ = r.ReadMessage(e, out _);
            _ = e.Subject;
            _ = e.DateReceived;
            _ = e.DeclaredLength;
        }

        foreach (DbxFolderEntry f in r.EnumerateFolders())
        {
            _ = f.Name;
        }
    }

    [Fact]
    public void TruncatedFilesNeverThrowAndGetShorterResults()
    {
        byte[] file = DbxTestBuilder.MessageDatabase(Messages(30, 700), new DbxBuild { TreeFanout = 4, ShuffleSeed = 5 });
        int full = ReadAll(file).Count;
        Assert.Equal(30, full);
        for (int len = 0; len < file.Length; len += len < 0x2600 ? 97 : 211)
        {
            Drain(file.AsSpan(0, len).ToArray());
        }

        // Cut inside the last chain: the earlier messages survive.
        Assert.True(ReadAll(file.AsSpan(0, file.Length - 100).ToArray()).Count <= full);
        List<StoreDefect> defects = [];
        byte[] cut = file.AsSpan(0, 0x1000).ToArray();
        using MemorySource src = new(cut);
        DbxReader r = new(src, new DbxReaderOptions { DefectHandler = defects.Add });
        Assert.Equal(DbxFileKind.Unknown, r.Kind);
        Assert.Contains(defects, d => d.Kind == StoreDefects.DbxTruncatedHeader);
    }

    [Fact]
    public void ACycleInTheTreeEndsTheWalk()
    {
        byte[] file = DbxTestBuilder.MessageDatabase(Messages(200, 10), new DbxBuild { TreeFanout = 3 });
        int root = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0xE4));
        // The root's left child points back at the root.
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(root + 8), root);
        List<StoreDefect> defects = [];
        using MemorySource src = new(file);
        DbxReader r = new(src, new DbxReaderOptions { DefectHandler = defects.Add });
        int n = r.EnumerateMessages().Count();
        Assert.True(n < 100_000);
        Assert.Contains(defects, d => d.Kind == StoreDefects.DbxTreeLoop);
    }

    [Fact]
    public void ACycleInAChainEndsTheMessage()
    {
        byte[] file = DbxTestBuilder.MessageDatabase(Messages(1, 5000));
        using MemorySource src = new(file);
        DbxMessageEntry e = new DbxReader(src).EnumerateMessages().Single();
        long first = e.ChainOffset;
        // Make the third segment point back at the first.
        long third = first + (2 * 0x210);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan((int)third + 12), (int)first);
        using MemorySource src2 = new(file);
        DbxReader r = new(src2);
        DbxMessageEntry e2 = r.EnumerateMessages().Single();
        byte[] b = r.ReadMessage(e2, out DbxChainInfo info);
        Assert.True((info.Defects & StoreDefects.DbxChainLoop) != 0);
        // Revisits are noticed within one more lap of the loop, so a little of it repeats; the message ends there.
        Assert.InRange(b.Length, 3 * 0x200, 8 * 0x200);
    }

    [Fact]
    public void BadPointersMarkersAndLengthsAreDefectsNotExceptions()
    {
        byte[] good = DbxTestBuilder.MessageDatabase(Messages(10, 900), new DbxBuild { TreeFanout = 3 });
        Random rng = new(1234);
        int mutated = 0;
        for (int round = 0; round < 3000; round++)
        {
            byte[] f = (byte[])good.Clone();
            int edits = 1 + rng.Next(6);
            for (int k = 0; k < edits; k++)
            {
                // Hit the header, the tree and the records often: that is where a damaged file differs most.
                int at = rng.Next(3) == 0 ? rng.Next(0x24BC) : rng.Next(f.Length);
                f[at] = (byte)rng.Next(256);
            }

            Drain(f);
            mutated++;
        }

        Assert.Equal(3000, mutated);
    }

    [Fact]
    public void HugeCountsAndLengthsAreCheckedBeforeAllocation()
    {
        byte[] file = DbxTestBuilder.MessageDatabase(Messages(3, 900));
        int root = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0xE4));
        file[root + 17] = 0xFF; // more entries than a node holds
        Drain(file);

        file = DbxTestBuilder.MessageDatabase(Messages(3, 900));
        using MemorySource src = new(file);
        DbxMessageEntry e = new DbxReader(src).EnumerateMessages().First();
        // The record states an enormous body length.
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan((int)e.Offset + 4), int.MaxValue);
        Drain(file);
        // A segment states an enormous text length.
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan((int)e.ChainOffset + 8), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan((int)e.ChainOffset + 4), int.MaxValue);
        Drain(file);
        // The header claims millions of entries.
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0xC4), int.MaxValue);
        Drain(file);
    }

    [Fact]
    public void ReportsTheEntryCountMismatch()
    {
        byte[] file = DbxTestBuilder.MessageDatabase(Messages(5, 100));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0xC4), 9);
        List<StoreDefect> defects = [];
        using MemorySource src = new(file);
        DbxReader r = new(src, new DbxReaderOptions { DefectHandler = defects.Add });
        Assert.Equal(5, r.EnumerateMessages().Count());
        Assert.Contains(defects, d => d.Kind == StoreDefects.DbxEntryCountMismatch);
    }

    [Fact]
    public async Task AsyncEnumerationYieldsTheSameEntries()
    {
        using MemorySource src = new(DbxTestBuilder.MessageDatabase(Messages(25, 100), new DbxBuild { TreeFanout = 3 }));
        DbxReader r = new(src);
        int n = 0;
        await foreach (DbxMessageEntry e in r.EnumerateMessagesAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(++n, e.Index);
        }

        Assert.Equal(25, n);
    }
}
