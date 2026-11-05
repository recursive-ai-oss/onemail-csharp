using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Pst.Messaging;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>The one ANSI store Outlook wrote that we have (testdata/pst/README.md): ANSI table row indexes are 2 bytes (MS-PST 2.3.4.3) and
/// Outlook leaves stale bytes after the NUL of a PtypString8.</summary>
public class PstOutlookAnsiTests
{
    private static string Path_([CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "pst", "outlook-ansi.pst"));

    [Fact]
    public void FoldersMessagesRecipientsAndAttachmentsOfAnOutlookAnsiStoreRead()
    {
        Assert.True(PstStore.TryOpen(Path_(), out PstStore? store, out _));
        using (store)
        {
            Assert.Empty(store!.Database.Defects);
            Assert.Empty(store.Defects);
            PstFolder[] folders = store.EnumerateFolders(TestContext.Current.CancellationToken).ToArray();
            PstFolder big = folders.Single(f => f.Name == "Big");
            PstFolder rec = folders.Single(f => f.Name == "Recipients");
            Assert.Equal(8, big.ContentCount);
            Assert.Equal(20, folders.Single(f => f.Name == "To delete").ContentCount);

            PstMessage[] messages = big.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).ToArray();
            Assert.Equal(8, messages.Length);
            // The stored subject is 20 bytes whatever its length: the prefix, the text, a NUL and what was left in the buffer.
            Assert.Contains(messages, m => m.Subject == "\u0001\u0001message 1");
            Assert.All(messages, m => Assert.DoesNotContain('\0', m.Subject!));

            PstMessage[] withAttachment = rec.EnumerateMessages(cancellationToken: TestContext.Current.CancellationToken).ToArray();
            Assert.Equal(8, withAttachment.Length);
            Assert.All(withAttachment, m =>
            {
                Assert.Equal(3, m.Recipients.Count);
                Assert.Single(m.Attachments);
            });
        }
    }
}
