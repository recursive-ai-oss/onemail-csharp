using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Dbx;
using RecursiveAI.OneMail.Tests.Stores;

namespace RecursiveAI.OneMail.Fuzz;

// The Outlook Express database reader on damaged files. A fuzzer rarely invents a valid header, so most inputs are patch lists over a
// valid file made by the test builder:
//   byte 0, bits 0-1: which file (0 message database, 1 deep-tree message database, 2 folder database; 3 = the input is the file)
//   then 4-byte records: 24-bit offset (little endian, taken modulo the file length) and the byte to put there.
// What must hold on any bytes: nothing throws, the walks end (budgets), a message read to an array has the length the chain walk
// measured, and the streaming read gives the same bytes as the array read.
internal static class DbxTarget
{
    private static readonly byte[][] Bases = [MakeMessages(0x33, 0), MakeMessages(3, 5), MakeFolders()];

    private static byte[] MakeMessages(int fanout, int shuffle)
    {
        List<DbxTestMessage> m = [];
        for (int i = 0; i < 40; i++)
        {
            byte[] text = new byte[(i * 97) % 1500];
            new Random(i).NextBytes(text);
            m.Add(new DbxTestMessage { Text = text, Subject = "s" + i, MessageId = "<" + i + "@x>", SenderAddress = "a@x", NoBody = i == 7 });
        }

        return DbxTestBuilder.MessageDatabase(m, new DbxBuild { TreeFanout = fanout, ShuffleSeed = shuffle });
    }

    private static byte[] MakeFolders() => DbxTestBuilder.FolderDatabase(
    [
        new DbxTestFolder { Index = 0, Parent = -1, Name = "Outlook Express" },
        new DbxTestFolder { Index = 1, Parent = 0, Name = "Local Folders" },
        new DbxTestFolder { Index = 2, Parent = 1, Name = "Inbox", File = "Inbox.dbx" },
        new DbxTestFolder { Index = 3, Parent = 2, Name = "Sub", File = "Sub.dbx" },
    ], new DbxBuild { TreeFanout = 2 });

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] file;
        int which = data[0] & 3;
        if (which == 3)
        {
            file = data[1..].ToArray();
        }
        else
        {
            file = (byte[])Bases[which].Clone();
            for (int i = 1; i + 3 < data.Length; i += 4)
            {
                int pos = (data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) % file.Length;
                file[pos] = data[i + 3];
            }
        }

        using MemorySource src = new(file);
        DbxReader r = new(src, new DbxReaderOptions { MaxMessageBytes = 1 << 22 });
        int budget = 2000;
        foreach (DbxMessageEntry e in r.EnumerateMessages())
        {
            if (--budget < 0)
            {
                break;
            }

            _ = (e.Subject, e.MessageId, e.DateReceived, e.DateSent, e.Marks, e.Sender, e.Priority, e.DeclaredLength);
            byte[] a = r.ReadMessage(e, out DbxChainInfo info);
            if (a.Length != info.Length)
            {
                throw new InvalidOperationException("read length differs from the measured chain");
            }

            using DbxMessageStream s = r.OpenMessage(e);
            byte[] b = new byte[a.Length + 1];
            int n = 0;
            int got;
            while (n < b.Length && (got = s.Read(b, n, b.Length - n)) > 0)
            {
                n += got;
            }

            if (n != a.Length || !b.AsSpan(0, n).SequenceEqual(a))
            {
                throw new InvalidOperationException("stream and array reads differ");
            }
        }

        budget = 2000;
        foreach (DbxFolderEntry f in r.EnumerateFolders())
        {
            if (--budget < 0)
            {
                break;
            }

            _ = (f.Name, f.FileName, f.ParentIndex, f.SpecialFolder);
        }

        // The same bytes as the folder file of a store folder, and as a file through the happy path.
        MemoryStoreFileSystem fs = new();
        using (Stream w = fs.CreateNew("Folders.dbx"))
        {
            w.Write(file);
        }

        using (Stream w = fs.CreateNew("Inbox.dbx"))
        {
            w.Write(file);
        }

        _ = DbxStoreFolder.Sniff(fs);
        DbxStoreFolder store = new(fs, "", new DbxReaderOptions { MaxMessageBytes = 1 << 22 });
        budget = 500;
        foreach (DbxStoreMessage m in store.EnumerateMessages())
        {
            if (--budget < 0)
            {
                break;
            }

            _ = (m.Folder, m.Defects);
        }

        foreach (DbxFolderEntry f in store.Folders)
        {
            _ = store.PathOf(f);
        }
    }
}
