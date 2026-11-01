using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Writes stores with <see cref="PstBuilder"/> and reads them back through the readers.</summary>
public sealed class PstBuilderTests
{
    private static readonly DateTimeOffset Stamp = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static MapiProperty Str(ushort id, string v) => MapiProperty.Create(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    private static MapiProperty Str8(ushort id, string v) => MapiProperty.Create(new MapiTag(id, MapiType.String8), Encoding.Latin1.GetBytes(v));

    private static MapiProperty Int(ushort id, int v) => MapiProperty.Create(new MapiTag(id, MapiType.Integer32), BitConverter.GetBytes(v));

    private static MapiProperty Bin(ushort id, byte[] v) => MapiProperty.Create(new MapiTag(id, MapiType.Binary), v);

    private static MapiProperty Time(ushort id, long ft) => MapiProperty.Create(new MapiTag(id, MapiType.Time), BitConverter.GetBytes(ft));

    internal static MapiMessage Message(string subject, int recipients = 2, int attachments = 0, string? body = null)
    {
        var m = new MapiMessage();
        m.Properties.Add(Str(0x001A, "IPM.Note"));
        m.Properties.Add(Str(0x0037, subject));
        m.Properties.Add(Time(0x0039, 133_500_000_000_000_000L));
        m.Properties.Add(Str(0x0C1A, "Sender"));
        if (body is not null)
        {
            m.Properties.Add(Str(0x1000, body));
        }

        for (int i = 0; i < recipients; i++)
        {
            var r = new MapiRecipient(m);
            r.Properties.Add(Int(0x0C15, 1 + (i % 3)));
            r.Properties.Add(Str(0x3001, "Person " + i));
            r.Properties.Add(Str(0x3002, "SMTP"));
            r.Properties.Add(Str(0x3003, $"p{i}@example.com"));
            m.AddRecipient(r);
        }

        for (int i = 0; i < attachments; i++)
        {
            var a = new MapiAttachment(m);
            a.Properties.Add(Int(0x3705, 1));
            a.Properties.Add(Str(0x3707, $"file{i}.bin"));
            a.Properties.Add(Bin(0x3701, Enumerable.Range(0, 20_000 + i).Select(x => (byte)(x * 7)).ToArray()));
            m.AddAttachment(a);
        }

        return m;
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "onemail-pstb-" + Guid.NewGuid().ToString("N") + ".pst");

    private static (PstStore Store, string Path) Write(Action<PstBuilder> fill)
    {
        string path = TempFile();
        using (PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = Stamp, StoreId = Guid.Parse("11111111-2222-3333-4444-555555555555") }))
        {
            fill(b);
            b.Complete();
        }

        Assert.True(PstStore.TryOpen(path, out PstStore? store, out IReadOnlyList<MapiDefect> defects), string.Join("; ", defects));
        return (store!, path);
    }

    [Fact]
    public void EmptyStoreOpensCleanAndIsOneRangeLong()
    {
        (PstStore store, string path) = Write(_ => { });
        using (store)
        {
            Assert.Equal(0x4400 + 253_952, new FileInfo(path).Length);
            Assert.Empty(store.Database.Defects);
            Assert.Empty(store.Defects);
            Assert.Equal("Personal Folders", store.DisplayName);
            Assert.Equal(["", "Deleted Items", "SPAM Search Folder 2", "Search Root", "Top of Personal Folders"], store.EnumerateFolders(TestContext.Current.CancellationToken).Select(f => f.Name ?? "").Order(StringComparer.Ordinal).ToArray());
            NdbAllocationSummary a = store.Database.ValidateAllocation(TestContext.Current.CancellationToken);
            Assert.Equal(0, a.InvalidCount);
            Assert.Empty(store.Database.Defects);
        }

        File.Delete(path);
    }

    [Fact]
    public void String8TextTakesItsCodePageFromTheItemAndTheNameMapIsNeverEmpty()
    {
        var m = new MapiMessage();
        m.Properties.Add(Str(0x001A, "IPM.Note"));
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x0037, MapiType.String8), Encoding.GetEncoding("iso-8859-1").GetBytes("caf\u00e9")));
        m.Properties.Add(Int(0x3FDE, 28592));   // PidTagInternetCodepage only: the item names no message code page
        (PstStore store, string path) = Write(b => b.IpmSubtree.CreateFolder("Inbox").AddMessage(m));
        using (store)
        {
            PstMessage written = store.EnumerateFolders(TestContext.Current.CancellationToken).Single(f => f.Name == "Inbox").EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).Single();
            Assert.Equal(28592, written.Properties.Get(0x3FFD)?.AsInt32());
            Assert.Equal(28592, written.String8Encoding.CodePage);
            Assert.True(store.NamedPropertyCount >= 2);   // libpff refuses a map with no numbered, no string or no GUID entries
        }

        File.Delete(path);
    }

    [Fact]
    public void MessagesRecipientsAndAttachmentsReadBackExactly()
    {
        MapiMessage m1 = Message("first", recipients: 3, attachments: 2, body: "hello");
        MapiMessage m2 = Message("second", recipients: 0, body: new string('x', 20_000));
        (PstStore store, string path) = Write(b =>
        {
            PstFolderWriter inbox = b.IpmSubtree.CreateFolder("Inbox");
            inbox.AddMessage(m1);
            inbox.AddMessage(m2);
            inbox.CreateFolder("Sub").AddMessage(Message("third"));
        });
        using (store)
        {
            Assert.Empty(store.Database.Defects);
            Assert.Empty(store.Defects);
            Assert.Equal(0, store.Database.ValidateAllocation(TestContext.Current.CancellationToken).InvalidCount);
            PstFolder inbox = store.EnumerateFolders(TestContext.Current.CancellationToken).Single(f => f.Name == "Inbox");
            PstMessage[] read = inbox.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).ToArray();
            Assert.Equal(2, read.Length);
            Assert.Equal("first", read[0].Subject);
            Assert.Equal(3, read[0].Recipients.Count);
            Assert.Equal("p2@example.com", read[0].Recipients[2].EmailAddress);
            Assert.Equal(2, read[0].Attachments.Count);
            Assert.Equal(20_001, read[0].Attachments[1].OpenData()!.Length);
            Assert.Equal(m2.Body, read[1].Body);
            Assert.Single(inbox.EnumerateSubfolders(TestContext.Current.CancellationToken));
            Assert.Equal(2, inbox.ContentCount);
        }

        File.Delete(path);
    }
}
