using System.Text;
using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Emlx;
using RecursiveAI.OneMail.Stores.Maildir;
using PlistFormat = RecursiveAI.OneMail.Stores.Emlx.Plist;

namespace RecursiveAI.OneMail.Tests.Stores;

public class MaildirTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static MaildirStore NewStore(MemoryStoreFileSystem fs, string root = "", char separator = ':')
    {
        int n = 0;
        return new MaildirStore(fs, root, new MaildirStoreOptions { InfoSeparator = separator, UniqueNameFactory = () => $"170000000{n++}.M1P2.host" });
    }

    [Theory]
    [InlineData("1.M1P2.host;2,", "1.M1P2.host", MessageMarks.None, "")]
    [InlineData("1.M1P2.host;2,FRS", "1.M1P2.host", MessageMarks.Flagged | MessageMarks.Answered | MessageMarks.Seen, "")]
    [InlineData("1.M1P2.host,S=10,W=12;2,STab", "1.M1P2.host,S=10,W=12", MessageMarks.Seen | MessageMarks.Deleted, "ab")]
    public void SemicolonIsReadAsTheInfoSeparatorAndWrittenBack(string name, string unique, MessageMarks marks, string letters)
    {
        MaildirName n = MaildirName.Parse(name);
        Assert.Equal(unique, n.Unique);
        Assert.Equal(marks, n.Marks);
        Assert.Equal(letters, n.KeywordLetters);
        Assert.Equal(StoreDefects.None, n.Defects & StoreDefects.UnrecognisedFileName);
        if (!name.EndsWith(";2,", StringComparison.Ordinal)) // a name without flags is written without an info section
        {
            Assert.Equal(name, MaildirName.Build(n.Unique, n.Marks, n.KeywordLetters, n.Extra, ';'));
            Assert.Equal(name.Replace(';', ':'), MaildirName.Build(n.Unique, n.Marks, n.KeywordLetters, n.Extra));
        }
    }

    [Fact]
    public void ASemicolonThatIsNotFollowedByTheInfoVersionStaysInTheUniquePart()
    {
        Assert.Equal("a;b", MaildirName.Parse("a;b").Unique);
        Assert.Equal("a;b", MaildirName.Parse("a;b:2,S").Unique);
        Assert.Equal("a", MaildirName.Parse("a:2,S;2,F").Unique);
        Assert.Throws<ArgumentException>(() => MaildirName.Build("a", MessageMarks.Seen, "", null, '!'));
        Assert.Throws<ArgumentException>(() => new MaildirStore(new MemoryStoreFileSystem(), "", new MaildirStoreOptions { InfoSeparator = '!' }));
        Assert.Equal(OperatingSystem.IsWindows() ? ';' : ':', new MaildirStoreOptions().InfoSeparator);
    }

    [Theory]
    [InlineData(':')]
    [InlineData(';')]
    public void TheStoreWritesItsSeparatorAndReadsBoth(char separator)
    {
        char other = separator == ':' ? ';' : ':';
        MemoryStoreFileSystem fs = new();
        MaildirStore store = NewStore(fs, "", separator);
        MaildirEntry a = store.Deliver(B("Subject: a\n\nx\n"), marks: MessageMarks.Seen | MessageMarks.Flagged);
        Assert.EndsWith(separator + "2,FS", a.FileName, StringComparison.Ordinal);
        Assert.Equal(MessageMarks.Seen | MessageMarks.Flagged, a.Marks);

        // A file the other convention wrote is read the same, and a flag change writes this store's separator.
        fs.SetFile("cur/9.M1P1.h" + other + "2,S", B("x"));
        MaildirEntry foreign = store.Enumerate().Single(e => e.Name.Unique == "9.M1P1.h");
        Assert.Equal(MessageMarks.Seen, foreign.Marks);
        MaildirEntry changed = store.SetFlags(foreign, MessageMarks.Seen | MessageMarks.Answered);
        Assert.Equal("9.M1P1.h" + separator + "2,RS", changed.FileName);

        // Dovecot's uidlist names the file with either separator; the UID attaches to the unique part.
        fs.SetFile("dovecot-uidlist", B($"3 V1 N4\n42 :{a.Name.Unique}{other}2,FS\n"));
        Assert.Equal(42, store.Enumerate().Single(e => e.Name.Unique == a.Name.Unique).Uid);
    }

    [Theory]
    [InlineData("1.M1P2.host", "1.M1P2.host", MessageMarks.None, "")]
    [InlineData("1.M1P2.host:2,", "1.M1P2.host", MessageMarks.None, "")]
    [InlineData("1.M1P2.host:2,FRS", "1.M1P2.host", MessageMarks.Flagged | MessageMarks.Answered | MessageMarks.Seen, "")]
    [InlineData("1.M1P2.host,S=10,W=12:2,STab", "1.M1P2.host,S=10,W=12", MessageMarks.Seen | MessageMarks.Deleted, "ab")]
    [InlineData("1.M1P2.host:2,DPT", "1.M1P2.host", MessageMarks.Draft | MessageMarks.Forwarded | MessageMarks.Deleted, "")]
    public void ParsesNamesAndBuildsThemBack(string name, string unique, MessageMarks marks, string letters)
    {
        MaildirName n = MaildirName.Parse(name);
        Assert.Equal(unique, n.Unique);
        Assert.Equal(marks, n.Marks);
        Assert.Equal(letters, n.KeywordLetters);
        if (name.Contains(":2,", StringComparison.Ordinal) && !name.EndsWith(":2,", StringComparison.Ordinal))
        {
            Assert.Equal(name, MaildirName.Build(n.Unique, n.Marks, n.KeywordLetters));
        }
    }

    [Fact]
    public void SizesAndExtraFieldsAreRead()
    {
        MaildirName n = MaildirName.Parse("1.M1,S=1355,W=1394:2,S,x=1");
        Assert.Equal(1355, n.Size);
        Assert.Equal(1394, n.VirtualSize);
        Assert.Equal("x=1", n.Extra);
        Assert.Equal("1.M1,S=1355,W=1394:2,S,x=1", MaildirName.Build(n.Unique, n.Marks, n.KeywordLetters, n.Extra));
    }

    [Fact]
    public void UnsortedFlagsAndOtherInfoVersionsAreDefects()
    {
        Assert.True((MaildirName.Parse("a:2,SF").Defects & StoreDefects.UnsortedFlags) != 0);
        MaildirName n = MaildirName.Parse("a:1,whatever");
        Assert.True((n.Defects & StoreDefects.UnrecognisedFileName) != 0);
        Assert.Equal("1,whatever", n.RawInfo);
    }

    [Fact]
    public void DeliveryGoesThroughTmpIntoNewOrCurWithFlags()
    {
        MemoryStoreFileSystem fs = new();
        MaildirStore store = NewStore(fs);
        MaildirEntry a = store.Deliver(B("Subject: a\n\nx\n"));
        Assert.Equal(MaildirSubdirectory.New, a.Subdirectory);
        Assert.True((a.Marks & MessageMarks.Recent) != 0);
        MaildirEntry b = store.Deliver(B("Subject: b\n\ny\n"), marks: MessageMarks.Seen | MessageMarks.Flagged);
        Assert.Equal(MaildirSubdirectory.Cur, b.Subdirectory);
        Assert.EndsWith(":2,FS", b.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain(fs.AllFiles(), f => f.Contains("/tmp/", StringComparison.Ordinal) || f.StartsWith("tmp/", StringComparison.Ordinal));

        List<MaildirEntry> all = [.. store.Enumerate()];
        Assert.Equal(2, all.Count);
        Assert.Equal(MaildirSubdirectory.Cur, all[0].Subdirectory); // cur first
        using IRandomAccessSource s = store.Open(all[1]);
        byte[] buf = new byte[s.Length];
        s.ReadFully(0, buf);
        Assert.Equal("Subject: a\n\nx\n", Encoding.Latin1.GetString(buf));
    }

    [Fact]
    public void SetFlagsRenamesIntoCurAndKeepsContentAndUnique()
    {
        MemoryStoreFileSystem fs = new();
        MaildirStore store = NewStore(fs);
        MaildirEntry a = store.Deliver(B("Subject: a\n\nx\n"));
        MaildirEntry seen = store.SetFlags(a, MessageMarks.Seen | MessageMarks.Answered);
        Assert.Equal(a.Name.Unique, seen.Name.Unique);
        Assert.Equal(MessageMarks.Seen | MessageMarks.Answered, seen.Marks);
        Assert.Equal(MaildirSubdirectory.Cur, seen.Subdirectory);
        Assert.Single(store.Enumerate());
    }

    [Fact]
    public void KeywordsRoundTripThroughDovecotKeywordsAndUidlistIsRead()
    {
        MemoryStoreFileSystem fs = new();
        MaildirStore store = NewStore(fs);
        MaildirEntry a = store.Deliver(B("Subject: a\n\nx\n"), marks: MessageMarks.Seen, keywords: ["$Junk", "Work"]);
        Assert.Equal(["$Junk", "Work"], a.Keywords);
        Assert.Equal("0 $Junk\n1 Work\n", Encoding.UTF8.GetString(fs.GetFile("dovecot-keywords")!));
        Assert.EndsWith(":2,Sab", a.FileName, StringComparison.Ordinal);

        fs.SetFile("dovecot-uidlist", B($"3 V1275660208 N25022 G3085f01b7f11094c501100008c4a11c1\n25006 :{a.Name.Unique}:2,Sab\n"));
        Assert.Equal(25006, store.Enumerate().Single().Uid);

        // A letter with no name is reported and shown as itself.
        fs.SetFile("cur/9.M1P1.h:2,Sz", B("x"));
        List<StoreDefect> defects = [];
        MaildirStore s2 = new(fs, "", new MaildirStoreOptions { DefectHandler = defects.Add });
        Assert.Contains("z", s2.Enumerate().Single(e => e.FileName.StartsWith("9.", StringComparison.Ordinal)).Keywords);
        Assert.Contains(defects, d => d.Kind == StoreDefects.UnknownKeywordLetter);
    }

    [Fact]
    public void MalformedControlFilesAreReportedAndSkipped()
    {
        MemoryStoreFileSystem fs = new();
        fs.SetFile("cur/1.M1P1.h:2,Sa", B("x"));
        fs.SetFile("dovecot-keywords", B("garbage\n0 $Junk\n"));
        fs.SetFile("dovecot-uidlist", B("3 V1 N4\nnot a uid line\n7 :1.M1P1.h:2,Sa\n"));
        List<StoreDefect> defects = [];
        MaildirEntry e = new MaildirStore(fs, "", new MaildirStoreOptions { DefectHandler = defects.Add }).Enumerate().Single();
        Assert.Equal(["$Junk"], e.Keywords);
        Assert.Equal(7, e.Uid);
        Assert.Equal(2, defects.Count(d => d.Kind == StoreDefects.MalformedControlFile));
    }

    [Fact]
    public void CourierKeywordListIsRead()
    {
        MemoryStoreFileSystem fs = new();
        fs.SetFile("cur/1.M1P1.h:2,S", B("x"));
        fs.SetFile("courierimapkeywords/:list", B("Work\n$Label1\n\n1.M1P1.h:0 1\n"));
        Assert.Equal(["Work", "$Label1"], new MaildirStore(fs).Enumerate().Single().Keywords);
    }

    [Fact]
    public void MaildirPlusPlusFoldersAreListedAndSeparated()
    {
        MemoryStoreFileSystem fs = new();
        MaildirStore store = NewStore(fs);
        store.Deliver(B("Subject: inbox\n\n1\n"));
        store.Deliver(B("Subject: arch\n\n2\n"), "Archive");
        store.Deliver(B("Subject: arch24\n\n3\n"), "Archive.2024");
        Assert.Equal(["", "Archive", "Archive.2024"], store.ListFolders());
        Assert.Single(store.Enumerate("Archive.2024"));
        Assert.True(fs.FileExists(".Archive/maildirfolder"));
        Assert.Equal(StoreFormat.MaildirRoot, StoreSniffer.SniffDirectory(fs));
    }

    [Fact]
    public async Task CopyThroughTheModelReproducesTheTreeByteForByte()
    {
        MemoryStoreFileSystem src = new();
        src.SetFile("cur/1.M1P1.h,S=3:2,SF", B("abc"));
        src.SetFile("cur/2.M1P1.h:2,", B("def"));
        src.SetFile("new/3.M1P1.h", B("ghi\r\njkl"));
        src.SetFile("tmp/ignored", B("half written"));
        src.SetFile("dovecot-keywords", B("0 $Junk\n"));
        src.SetFile("dovecot-uidlist", B("3 V1 N4\n1 :1.M1P1.h,S=3:2,SF\n"));
        src.SetFile("courierimapkeywords/:list", B("a\n\n"));
        src.SetFile(".Sub/cur/4.M1P1.h:2,Sa", B("mno"));
        src.SetFile(".Sub/new/.hidden", B("not a message"));
        src.SetFile(".Sub/maildirfolder", []);

        MemoryStoreFileSystem dst = new();
        int n = await new MaildirStore(src).CopyToAsync(dst, "copy", TestContext.Current.CancellationToken);
        Assert.Equal(4, n);
        foreach (string path in src.AllFiles().Where(p => !p.StartsWith("tmp/", StringComparison.Ordinal) && !p.Contains(".hidden", StringComparison.Ordinal)))
        {
            Assert.Equal(src.GetFile(path), dst.GetFile("copy/" + path));
        }

        Assert.Equal(StoreFormat.MaildirRoot, StoreSniffer.SniffDirectory(dst, "copy"));
    }

    [Fact]
    public void PhysicalFileSystemRefusesPathsOutsideItsRootAndDeliversForReal()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "maildir-" + Guid.NewGuid().ToString("N"));
        try
        {
            PhysicalStoreFileSystem fs = new(dir);
            Assert.Throws<ArgumentException>(() => fs.FileExists("../escape"));
            MaildirStore store = new(fs, "Maildir");
            MaildirEntry e = store.Deliver(B("Subject: x\n\ny\n"), marks: MessageMarks.Seen);
            Assert.True(File.Exists(Path.Combine(dir, "Maildir", "cur", e.FileName)));
            Assert.Equal(StoreFormat.MaildirFolder, StoreSniffer.SniffDirectory(fs, "Maildir"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

public class EmlxTests
{
    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Emlx(string message, string trailer) => B(message.Length + "\n" + message + trailer);

    private const string PlistText = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n\t<key>date-received</key>\n\t<real>1700000000</real>\n\t<key>date-sent</key>\n\t<real>700000000</real>\n\t<key>flags</key>\n\t<integer>8589934593</integer>\n\t<key>remote-id</key>\n\t<string>4242</string>\n</dict>\n</plist>\n";

    [Fact]
    public void ReadsCountMessageAndPropertyList()
    {
        const string msg = "Subject: hi\n\nhello\n";
        byte[] data = Emlx(msg, PlistText);
        using MemorySource src = new(data);
        EmlxEntry e = EmlxReader.Read(src);
        Assert.Equal(msg.Length, e.ByteCount);
        Assert.Equal(StoreDefects.None, e.Defects);
        Assert.Equal(msg, Encoding.Latin1.GetString(data, (int)e.MessageOffset, (int)e.MessageLength));
        Assert.Equal(data.Length, e.TrailerOffset + e.TrailerLength);

        EmlxMetadata m = EmlxReader.ReadMetadata(src, e);
        Assert.Equal(8589934593, m.Flags);
        Assert.Equal(MessageMarks.Seen, m.Marks);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), m.DateReceived);
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(700000000), m.DateSent);
        Assert.Equal("4242", m.RemoteId);
        Assert.Equal(StoreFormat.Emlx, StoreSniffer.Sniff(src));
    }

    [Fact]
    public void BadAndTruncatedInputIsReportedNotThrown()
    {
        using MemorySource bad = new(B("hello\nworld"));
        Assert.Equal(StoreDefects.BadByteCount, EmlxReader.Read(bad).Defects);
        Assert.Equal(StoreFormat.Unknown, StoreSniffer.Sniff(bad));

        using MemorySource cut = new(B("100\nSubject: x\n\nshort"));
        EmlxEntry e = EmlxReader.Read(cut);
        Assert.Equal(StoreDefects.TruncatedMessage, e.Defects);
        Assert.Equal(17, e.MessageLength);
        Assert.Equal(0, e.TrailerLength);

        using MemorySource junk = new(Emlx("Subject: x\n\ny\n", "not a plist at all"));
        EmlxEntry j = EmlxReader.Read(junk);
        Assert.Empty(EmlxReader.ReadMetadata(junk, j).Values);
        Assert.Equal(StoreFormat.Unknown, StoreSniffer.Sniff(junk));

        using MemorySource huge = new(B("99999999999999999999\nx"));
        Assert.Equal(StoreDefects.BadByteCount, EmlxReader.Read(huge).Defects);
    }

    [Fact]
    public void WriterOutputReadsBackAndRewritingIsByteIdentical()
    {
        byte[] msg = B("Subject: x\r\n\r\nbody\r\n");
        using MemoryStream ms = new();
        EmlxWriter.Write(ms, msg, MessageMarks.Seen | MessageMarks.Flagged, DateTimeOffset.FromUnixTimeSeconds(1700000000));
        byte[] file = ms.ToArray();
        using MemorySource src = new(file);
        EmlxEntry e = EmlxReader.Read(src);
        Assert.Equal(msg, file.AsSpan((int)e.MessageOffset, (int)e.MessageLength).ToArray());
        EmlxMetadata m = EmlxReader.ReadMetadata(src, e);
        Assert.Equal(MessageMarks.Seen | MessageMarks.Flagged, m.Marks);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), m.DateReceived);

        using MemoryStream again = new();
        EmlxWriter.WriteFrom(again, src, e);
        Assert.Equal(file, again.ToArray());
    }

    [Fact]
    public void PlistParsingIsBoundedAndNeverThrows()
    {
        Assert.False(PlistFormat.TryParse("<plist><dict><key>a</key></dict></plist>"u8, out _));
        Assert.False(PlistFormat.TryParse("garbage"u8, out _));
        string deep = "<plist>" + string.Concat(Enumerable.Repeat("<array>", 100)) + string.Concat(Enumerable.Repeat("</array>", 100)) + "</plist>";
        Assert.False(PlistFormat.TryParse(B(deep), out _));
        // A DOCTYPE with an external entity must not be fetched or expanded.
        string xxe = "<?xml version=\"1.0\"?><!DOCTYPE plist [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><plist><dict><key>k</key><string>&x;</string></dict></plist>";
        Assert.False(PlistFormat.TryParse(B(xxe), out _));
        Assert.True(PlistFormat.TryParse(B(PlistText), out object? root));
        Assert.IsType<Dictionary<string, object?>>(root);
    }

    [Fact]
    public void FuzzReproBadBytesInTheXmlDeclarationGiveFalse()
    {
        // Found by the emlx fuzz target: the framework's reader threw ArgumentOutOfRangeException on this input.
        byte[] repro = File.ReadAllBytes(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "..", "testdata", "fuzz", "emlx-xml-declaration-bad-bytes.bin")));
        Assert.False(PlistFormat.TryParse(repro, out _));
    }

    [Theory]
    [InlineData("<integer>99999999999999999999</integer>")]
    [InlineData("<integer>x</integer>")]
    [InlineData("<real>nope</real>")]
    [InlineData("<date>not a date</date>")]
    [InlineData("<data>!!!</data>")]
    public void FuzzReproBadScalarValuesGiveFalse(string value)
    {
        // Found by the emlx fuzz target: an integer too large for 64 bits escaped as OverflowException.
        Assert.False(PlistFormat.TryParse(B("<plist><dict><key>k</key>" + value + "</dict></plist>"), out _));
    }

    [Fact]
    public void BundleFindsMessagesInOldAndNewLayoutsAndPartialFiles()
    {
        MemoryStoreFileSystem fs = new();
        byte[] f = Emlx("Subject: x\n\ny\n", PlistText);
        fs.SetFile("Inbox.mbox/Messages/2.emlx", f);
        fs.SetFile("Inbox.mbox/Messages/10.partial.emlx", f);
        fs.SetFile("Inbox.mbox/Messages/1.emlx", f);
        fs.SetFile("Inbox.mbox/Attachments/10/1/a.pdf", B("x"));
        fs.SetFile("Inbox.mbox/Info.plist", B(PlistText));
        fs.SetFile("New.mbox/0123-GUID/Data/1/2/Messages/5.emlx", f);

        AppleMailBundle old = new(fs, "Inbox.mbox");
        List<EmlxItem> items = [.. old.Enumerate()];
        Assert.Equal([1L, 2L, 10L], items.Select(i => i.Number));
        Assert.True(items[2].IsPartial);
        Assert.Equal("Inbox.mbox/Attachments/10", items[2].AttachmentsDirectory);
        Assert.NotNull(old.ReadInfo());
        Assert.Equal(StoreFormat.AppleMailBundle, StoreSniffer.SniffDirectory(fs, "Inbox.mbox"));

        Assert.Single(new AppleMailBundle(fs, "New.mbox").Enumerate());
        Assert.Equal(StoreFormat.Unknown, StoreSniffer.SniffDirectory(fs, "Inbox.mbox/Attachments"));
    }
}
