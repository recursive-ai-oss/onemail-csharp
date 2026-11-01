using System.Security.Cryptography;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.Stores;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Maildir;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.Tests.Conversion;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onemail-store-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }
    }
}

public class StoreConversionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", "..", ".."));

    private static string Data(params string[] parts) => System.IO.Path.Combine([Root(), "testdata", .. parts]);

    // The probe messages without the table that lists them.
    private static string Probes(TempDir tmp)
    {
        string dir = tmp.Sub("probes");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(Data("differential"), "*.eml"))
        {
            File.Copy(f, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(f)));
        }

        return dir;
    }

    private static string Hash(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b));

    public static TheoryData<ConvertFormat> MailTargets => new() { ConvertFormat.Eml, ConvertFormat.Mbox, ConvertFormat.Maildir, ConvertFormat.AppleMail };

    public static TheoryData<ConvertFormat> AllTargets
    {
        get
        {
            var d = new TheoryData<ConvertFormat>();
            foreach (ConvertFormat f in Enum.GetValues<ConvertFormat>())
            {
                d.Add(f);
            }

            return d;
        }
    }

    private sealed record Written(string Folder, byte[] Bytes, MessageMarks Marks, IReadOnlyList<string> Keywords);

    // Reads a destination back through the library's own readers.
    private static List<Written> Reread(string dest, ConvertFormat format)
    {
        List<Written> list = [];
        switch (format)
        {
            case ConvertFormat.Eml:
                foreach (string f in Directory.EnumerateFiles(dest, "*.eml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    string rel = System.IO.Path.GetRelativePath(dest, System.IO.Path.GetDirectoryName(f)!).Replace('\\', '/');
                    list.Add(new Written(rel == "." ? string.Empty : rel, File.ReadAllBytes(f), MessageMarks.None, []));
                }

                break;
            case ConvertFormat.Mbox:
                foreach (string f in Directory.EnumerateFiles(dest, "*.mbox", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    string rel = System.IO.Path.GetRelativePath(dest, f).Replace('\\', '/');
                    string folder = rel.EndsWith("_root.mbox", StringComparison.Ordinal) ? string.Empty : rel[..^5];
                    using MailContainer c = Mail.Open(f);
                    foreach (IMailItem item in c.Enumerate())
                    {
                        var m = (MimeMailItem)item;
                        list.Add(new Written(folder, m.Entity.ToArray(), m.StoreFlags().Marks, m.StoreFlags().Keywords));
                    }
                }

                break;
            default:
                using (MailContainer c = Mail.Open(dest))
                {
                    foreach (IMailItem item in c.Enumerate())
                    {
                        var m = (MimeMailItem)item;
                        (MessageMarks marks, IReadOnlyList<string> kw) = m.StoreFlags();
                        list.Add(new Written((item.Provenance.Folder ?? string.Empty).Replace('.', '/'), m.Entity.ToArray(), marks & ~MessageMarks.Recent, kw));
                    }
                }

                break;
        }

        return list;
    }

    private static void AssertWithinCell(StoreConversionReport report, string source, ConvertFormat target)
    {
        FidelityCell cell = FidelityMatrix.Get(source, target);
        foreach (LossTotal t in report.Losses)
        {
            Assert.True(cell.Codes.Contains(t.Code), $"{source} -> {target}: code {t.Code} ({t.Family}, e.g. {t.Example}) is not in the matrix cell");
        }

        foreach (string reason in report.SkippedByReason.Keys.Where(k => k != "Unreadable"))
        {
            Assert.Equal(cell.SkipReason, reason);
        }
    }

    [Theory, MemberData(nameof(MailTargets))]
    public async Task MimeMessagesAreWrittenByteForByte(ConvertFormat target)
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out");
        string src = Probes(tmp);
        int files = Directory.GetFiles(src, "*.eml").Length;
        StoreConversionReport r = await Mail.ConvertAsync(src, target, dest, cancellationToken: Ct);
        Assert.Equal(files, r.ItemsRead);
        Assert.Equal(files, r.ItemsWritten + r.ItemsSkipped);

        // A few probes are not messages by content (no header block at all); they are skipped as unreadable, never guessed at.
        Assert.Equal(r.ItemsSkipped, r.Errors.Count);
        Assert.Equal(r.ItemsSkipped, r.SkippedByReason.GetValueOrDefault("Unreadable"));
        Assert.True(r.ItemsWritten > 200, "written " + r.ItemsWritten);

        // The multiset of message bytes read back from the destination equals that of the source files that were written.
        Dictionary<string, List<string>> expected = [];
        foreach (string f in Directory.GetFiles(src, "*.eml"))
        {
            byte[] bytes = File.ReadAllBytes(f);
            if (bytes.AsSpan().StartsWith("From "u8))
            {
                // A file that starts with an envelope line is an mbox of one message: the message is what follows it.
                bytes = bytes[(Array.IndexOf(bytes, (byte)'\n') + 1)..];
            }

            if (target == ConvertFormat.Mbox && bytes.Length > 0 && bytes[^1] != (byte)'\n')
            {
                // mbox cannot end a message without a line ending; the writer adds one and says so (FinalLineEndingAdded).
                bytes = [.. bytes, (byte)'\n'];
            }

            string h = Hash(bytes);
            if (!expected.TryGetValue(h, out List<string>? names))
            {
                expected[h] = names = [];
            }

            names.Add(System.IO.Path.GetFileName(f));
        }

        List<Written> back = Reread(dest, target);
        Assert.Equal(r.ItemsWritten, back.Count);
        List<string> changed = [];
        foreach (Written w in back)
        {
            if (expected.TryGetValue(Hash(w.Bytes), out List<string>? names) && names.Count > 0)
            {
                names.RemoveAt(0);
            }
            else
            {
                changed.Add(Hash(w.Bytes)[..8]);
            }
        }

        Assert.True(changed.Count == 0, "changed on the way: " + string.Join(' ', changed) + "; unmatched sources: " + string.Join(' ', expected.Values.SelectMany(x => x)));

        AssertWithinCell(r, "mime", target);
    }

    [Fact]
    public async Task MaildirFlagsAndFoldersCarryToEveryMailStore()
    {
        using var tmp = new TempDir();
        string src = tmp.Sub("in");
        var store = new MaildirStore(new PhysicalStoreFileSystem(src));
        byte[] Msg(string n) => System.Text.Encoding.ASCII.GetBytes($"From: a@example.com\r\nSubject: {n}\r\nMessage-ID: <{n}@example.com>\r\n\r\nbody {n}\r\n");
        store.Deliver(Msg("one"), "", MessageMarks.Seen | MessageMarks.Flagged);
        store.Deliver(Msg("two"), "Archive", MessageMarks.Answered | MessageMarks.Seen);
        store.Deliver(Msg("three"), "Archive.2024", MessageMarks.Draft, ["work"]);

        foreach (ConvertFormat target in new[] { ConvertFormat.Maildir, ConvertFormat.Mbox, ConvertFormat.AppleMail })
        {
            string dest = tmp.Sub("out-" + target);
            StoreConversionReport r = await Mail.ConvertAsync(src, target, dest, cancellationToken: Ct);
            Assert.Equal(3, r.ItemsWritten);
            Assert.Equal(["", "Archive", "Archive/2024"], r.Folders.Keys.Order(StringComparer.Ordinal));
            Dictionary<string, Written> back = Reread(dest, target).ToDictionary(w => System.Text.Encoding.ASCII.GetString(w.Bytes).Split("Subject: ")[1].Split('\r')[0]);
            Assert.Equal("", back["one"].Folder);
            Assert.Equal("Archive", back["two"].Folder);
            Assert.Equal("Archive/2024", back["three"].Folder);
            Assert.Equal(MessageMarks.Seen | MessageMarks.Flagged, back["one"].Marks);
            Assert.Equal(MessageMarks.Answered | MessageMarks.Seen, back["two"].Marks);
            Assert.Equal(MessageMarks.Draft, back["three"].Marks);
            if (target != ConvertFormat.AppleMail)
            {
                Assert.Equal(["work"], back["three"].Keywords);
            }
            else
            {
                Assert.Equal(1, r.Losses.First(t => t.Code == "KeywordsNotCarried").Items);
            }

            AssertWithinCell(r, "mime", target);
        }

        // An .eml tree has no flags and says so.
        StoreConversionReport e = await Mail.ConvertAsync(src, ConvertFormat.Eml, tmp.Sub("out-eml"), cancellationToken: Ct);
        Assert.Equal(3, e.Losses.First(t => t.Code == "FlagsNotCarried").Items);
        Assert.True(File.Exists(System.IO.Path.Combine(tmp.Sub("out-eml"), "Archive", "2024", "000001-three.eml")));
    }

    [Fact]
    public async Task MboxFlagsAreWrittenAsStatusFieldsOnlyWhenTheMessageHasNone()
    {
        using var tmp = new TempDir();
        string src = tmp.Sub("in");
        var store = new MaildirStore(new PhysicalStoreFileSystem(src));
        store.Deliver("From: a@example.com\nSubject: x\n\nbody\n"u8.ToArray(), "", MessageMarks.Seen);
        store.Deliver("From: a@example.com\nStatus: RO\nSubject: y\n\nbody\n"u8.ToArray(), "", MessageMarks.Seen);
        string dest = tmp.Sub("out");
        await Mail.ConvertAsync(src, ConvertFormat.Mbox, dest, cancellationToken: Ct);
        List<Written> back = Reread(dest, ConvertFormat.Mbox);
        string x = System.Text.Encoding.ASCII.GetString(back.Single(w => System.Text.Encoding.ASCII.GetString(w.Bytes).Contains("Subject: x", StringComparison.Ordinal)).Bytes);
        Assert.StartsWith("Status: R\nFrom: a@example.com\n", x, StringComparison.Ordinal);
        string y = System.Text.Encoding.ASCII.GetString(back.Single(w => System.Text.Encoding.ASCII.GetString(w.Bytes).Contains("Subject: y", StringComparison.Ordinal)).Bytes);
        Assert.Equal("From: a@example.com\nStatus: RO\nSubject: y\n\nbody\n", y);
    }

    [Fact]
    public async Task KeywordsWithSpacesAreNormalisedNotFatal()
    {
        using var tmp = new TempDir();
        string src = tmp.Sub("takeout.mbox");
        File.WriteAllText(src, "From a@b.c Mon Jan  1 00:00:00 2024\nFrom: a@b.c\nSubject: labelled\nX-Gmail-Labels: Sent Mail,Inbox\n\nbody\n");
        StoreConversionReport r = await Mail.ConvertAsync(src, ConvertFormat.Maildir, tmp.Sub("out"), cancellationToken: Ct);
        Assert.Equal(1, r.ItemsWritten);
        Assert.Equal(1, r.Losses.First(t => t.Code == "KeywordsNormalized").Items);
        Assert.Equal(["Sent_Mail", "Inbox"], Reread(tmp.Sub("out"), ConvertFormat.Maildir).Single().Keywords.Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task SingleMboxFileFlattensFoldersAndSaysSo()
    {
        using var tmp = new TempDir();
        string src = tmp.Sub("in");
        var store = new MaildirStore(new PhysicalStoreFileSystem(src));
        store.Deliver("From: a@example.com\n\none\n"u8.ToArray(), "A");
        store.Deliver("From: a@example.com\n\ntwo\n"u8.ToArray(), "B");
        string dest = tmp.Sub("all.mbox");
        StoreConversionReport r = await Mail.ConvertAsync(src, ConvertFormat.Mbox, dest, cancellationToken: Ct);
        Assert.Equal(2, r.ItemsWritten);
        Assert.Equal(2, r.Losses.First(t => t.Code == "FolderFlattened").Items);
        using MailContainer c = Mail.Open(dest);
        Assert.Equal(2, c.Enumerate().Count());
    }

    private static string WriteSamplePst(TempDir tmp)
    {
        string path = tmp.Sub("sample.pst");
        File.WriteAllBytes(path, PstMessagingTests.Build(RecursiveAI.OneMail.Pst.Ndb.NdbFormat.Unicode, PstFixtures.Sample()));
        return path;
    }

    [Theory, MemberData(nameof(AllTargets))]
    public async Task OutlookStoreCountsAddUpForEveryTarget(ConvertFormat target)
    {
        using var tmp = new TempDir();
        string pst = WriteSamplePst(tmp);
        int items;
        using (MailContainer c = Mail.Open(pst))
        {
            items = c.Enumerate().Count();
        }

        var results = new List<StoreItemResult>();
        StoreConversionReport r = await Mail.ConvertAsync(pst, target, tmp.Sub("out"), progress: new SyncProgress(results), cancellationToken: Ct);
        Assert.Equal(items, r.ItemsRead);
        Assert.Equal(items, r.ItemsWritten + r.ItemsSkipped);
        Assert.Equal(items, results.Count);
        Assert.Empty(r.Errors);
        Assert.Contains("Inbox/Deep", r.Folders.Keys);
        Assert.Contains("Calendar", r.Folders.Keys);
        if (target is ConvertFormat.Eml or ConvertFormat.Maildir or ConvertFormat.AppleMail or ConvertFormat.Mbox)
        {
            Assert.True(r.ItemsWritten >= 4);
        }
    }

    private sealed class SyncProgress(List<StoreItemResult> list) : IProgress<StoreItemResult>
    {
        public void Report(StoreItemResult value) => list.Add(value);
    }

    [Fact]
    public async Task OutlookMailToEmlKeepsTheFolderTreeAndReadsBack()
    {
        using var tmp = new TempDir();
        string pst = WriteSamplePst(tmp);
        string dest = tmp.Sub("out");
        StoreConversionReport r = await Mail.ConvertAsync(pst, ConvertFormat.Eml, dest, cancellationToken: Ct);
        Assert.Equal(["Calendar", "Inbox", "Inbox/Deep"], r.Folders.Keys.Order(StringComparer.Ordinal));
        string inbox = System.IO.Path.Combine(dest, "Inbox");
        string[] files = Directory.GetFiles(inbox, "*.eml");
        Assert.Equal(2, files.Length);
        // The file system lists files in no fixed order, so pick the message by its subject.
        IMailItem back = Assert.Single(files.Select(f => Mail.Parse(File.ReadAllBytes(f))), i => i.Subject == "Hello world");
        Assert.Equal(4, back.Attachments.Count);
        Assert.Equal(["Ann <ann@example.com>"], back.ToRecipients.Select(a => a.ToString()));
    }

    [Fact]
    public async Task OlmItemsReachMailStoresAndSideFiles()
    {
        using var tmp = new TempDir();
        string olm = Data("olm", "generated.olm");
        int items;
        using (MailContainer c = Mail.Open(olm))
        {
            items = c.Enumerate().Count();
        }

        StoreConversionReport r = await Mail.ConvertAsync(olm, ConvertFormat.Maildir, tmp.Sub("out"), cancellationToken: Ct);
        Assert.Equal(items, r.ItemsRead);
        Assert.Equal(items, r.ItemsWritten + r.ItemsSkipped);
        Assert.Empty(r.Errors);
        Assert.True(r.ItemsWritten > 0);
        Assert.True(Directory.Exists(tmp.Sub("out")));

        StoreConversionReport skip = await Mail.ConvertAsync(olm, ConvertFormat.Eml, tmp.Sub("skip"), new StoreConversionOptions { OtherItems = OtherItemsMode.Skip }, cancellationToken: Ct);
        Assert.Equal(items, skip.ItemsRead);
        Assert.True(skip.SkippedByReason.ContainsKey("OtherItemsSkipped") || skip.ItemsSkipped == 0);
        Assert.True(skip.ItemsWritten <= r.ItemsWritten);
    }

    [Fact]
    public async Task CalendarAndCardFilesConvertBetweenForms()
    {
        using var tmp = new TempDir();
        string ics = Data("calendar", "rfc", "rfc2445-L7634.ics");
        string dest = tmp.Sub("same");
        await Mail.ConvertAsync(ics, ConvertFormat.Ics, dest, cancellationToken: Ct);
        string only = Directory.GetFiles(dest, "*.ics", SearchOption.AllDirectories).Single();
        Assert.Equal(File.ReadAllBytes(ics), File.ReadAllBytes(only));

        StoreConversionReport j = await Mail.ConvertAsync(ics, ConvertFormat.JCal, tmp.Sub("j"), cancellationToken: Ct);
        Assert.Equal(1, j.ItemsWritten);
        string jfile = Directory.GetFiles(tmp.Sub("j"), "*.jcal").Single();
        StoreConversionReport back = await Mail.ConvertAsync(jfile, ConvertFormat.Ics, tmp.Sub("back"), cancellationToken: Ct);
        Assert.Equal(1, back.ItemsWritten);

        string vcf = Data("calendar", "rfc", "rfc2426-L2146.vcf");
        StoreConversionReport skipped = await Mail.ConvertAsync(vcf, ConvertFormat.Ics, tmp.Sub("nope"), cancellationToken: Ct);
        Assert.Equal(1, skipped.SkippedByReason["NotCalendarItem"]);
        StoreConversionReport x = await Mail.ConvertAsync(vcf, ConvertFormat.XCard, tmp.Sub("x"), cancellationToken: Ct);
        Assert.Equal(1, x.ItemsWritten);

        AssertWithinCell(j, "calendar-file", ConvertFormat.JCal);
        AssertWithinCell(skipped, "card-file", ConvertFormat.Ics);
    }

    [Fact]
    public async Task CancellationLeavesWholeStoresAndAConsistentReport()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("out.mbox");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var conv = new StoreConverter();
        int seen = 0;
        var progress = new SyncProgressAction(_ =>
        {
            if (++seen == 5)
            {
                cts.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conv.ConvertAsync(Probes(tmp), ConvertFormat.Mbox, dest, progress, cts.Token));
        Assert.Equal(5, conv.Report.ItemsRead);
        using MailContainer c = Mail.Open(dest);
        Assert.Equal(conv.Report.ItemsWritten, c.Enumerate().Count());
    }

    private sealed class SyncProgressAction(Action<StoreItemResult> a) : IProgress<StoreItemResult>
    {
        public void Report(StoreItemResult value) => a(value);
    }

    [Fact]
    public async Task ADestinationFailureStopsButAnUnreadableItemDoesNot()
    {
        using var tmp = new TempDir();
        string src = tmp.Sub("in");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(System.IO.Path.Combine(src, "a.eml"), "From: a@example.com\r\nSubject: ok\r\n\r\nbody\r\n"u8.ToArray());
        File.WriteAllBytes(System.IO.Path.Combine(src, "b.txt"), "just text, no header\r\n"u8.ToArray());
        StoreConversionReport r = await Mail.ConvertAsync(src, ConvertFormat.Eml, tmp.Sub("out"), cancellationToken: Ct);
        Assert.Equal(2, r.ItemsRead);
        Assert.Equal(1, r.ItemsWritten);
        Assert.Equal(1, r.SkippedByReason["Unreadable"]);

        Directory.CreateDirectory(tmp.Sub("full"));
        File.WriteAllText(System.IO.Path.Combine(tmp.Sub("full"), "x"), "x");
        await Assert.ThrowsAsync<IOException>(() => Mail.ConvertAsync(src, ConvertFormat.Eml, tmp.Sub("full"), cancellationToken: Ct));
    }

    [Theory, MemberData(nameof(AllTargets))]
    public void EveryItemKindMatchesItsMatrixCell(ConvertFormat target)
    {
        using var tmp = new TempDir();
        string pst = WriteSamplePst(tmp);
        foreach (string source in new[] { pst, Data("olm", "generated.olm"), Data("differential", "bnd-01-plain.eml") })
        {
            using MailContainer c = Mail.Open(source);
            foreach (IMailItem item in c.Enumerate())
            {
                string family = FamilyOf(item);
                FidelityCell cell = FidelityMatrix.Get(family, target);
                ConversionResult<byte[]?> r = Mail.Convert(item, target);
                if (r.Value is null)
                {
                    Assert.True(cell.SkipReason.Length > 0, $"{family} -> {target} was skipped but the matrix says it converts ({item.Kind}, {item.GetType().Name}, {item.Subject})");
                }
                else if (cell.SkipReason.Length > 0)
                {
                    Assert.Fail($"{family} -> {target} converted but the matrix says it is skipped ({cell.SkipReason})");
                }

                foreach (LossEntry e in r.Value is null ? [] : r.Report)
                {
                    Assert.True(cell.Codes.Contains(e.Code), $"{family} -> {target}: {e.Code} ({e.Name}) is not in the matrix cell");
                }
            }
        }
    }

    private static string FamilyOf(IMailItem item) => item switch
    {
        MimeMailItem => "mime",
        MapiMailItem { Kind: MailItemKind.Appointment or MailItemKind.Task } => "outlook-calendar",
        MapiMailItem { Kind: MailItemKind.Contact } => "outlook-contact",
        MapiMailItem => "outlook-mail",
        RecursiveAI.OneMail.Stores.Olm.OlmItem { Kind: MailItemKind.Appointment or MailItemKind.Task or MailItemKind.Note } => "olm-calendar",
        RecursiveAI.OneMail.Stores.Olm.OlmItem { Kind: MailItemKind.Contact or MailItemKind.DistributionList } => "olm-contact",
        RecursiveAI.OneMail.Stores.Olm.OlmItem { Kind: MailItemKind.Other } => "olm-other",
        _ => "olm-mail",
    };

    [Fact]
    public void FidelityDocumentIsGeneratedFromTheMatrix()
    {
        string doc = File.ReadAllText(System.IO.Path.Combine(Root(), "docs", "fidelity.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string table = FidelityMatrix.ToMarkdown();
        if (Environment.GetEnvironmentVariable("ONEMAIL_WRITE_FIDELITY") == "1")
        {
            int a = doc.IndexOf("<!-- matrix:start -->\n", StringComparison.Ordinal) + "<!-- matrix:start -->\n".Length;
            int b = doc.IndexOf("<!-- matrix:end -->", StringComparison.Ordinal);
            File.WriteAllText(System.IO.Path.Combine(Root(), "docs", "fidelity.md"), doc[..a] + table + doc[b..]);
            return;
        }

        Assert.Contains(table, doc, StringComparison.Ordinal);

        // Every code the matrix names is catalogued in the conversion notes or in this document.
        string catalogue = File.ReadAllText(System.IO.Path.Combine(Root(), "docs", "conversion.md")) + doc + File.ReadAllText(System.IO.Path.Combine(Root(), "docs", "calendar-items.md")) + File.ReadAllText(System.IO.Path.Combine(Root(), "docs", "contacts.md")) + File.ReadAllText(System.IO.Path.Combine(Root(), "docs", "olm.md"));
        foreach (string code in FidelityMatrix.Cells.SelectMany(c => c.Codes).Distinct())
        {
            Assert.True(catalogue.Contains(code, StringComparison.Ordinal), "undocumented code " + code);
        }
    }
}
