using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.Fuzz;

// The messaging layer (store, name map, folders, messages, recipients, attachments, embedded messages) and the MAPI view over it, on
// damaged files. As for the ltp target, inputs are patch lists over valid generated files (byte 0, bits 0-1: ANSI, Unicode, 4 KiB;
// 3 = the input is the file; then 4-byte records of a 24-bit offset and the byte to put there). Everything reachable is then read:
// the whole folder tree, every message with its recipients, attachments (streamed), embedded messages, named properties, the view's
// accessors and bodies. Only typed results may come out.
internal static class PstMessagingTarget
{
    private static readonly (byte[] File, long From)[] Bases = Build();

    private static (byte[], long)[] Build()
    {
        var result = new (byte[], long)[3];
        NdbFormat[] formats = [NdbFormat.Ansi, NdbFormat.Unicode, NdbFormat.Unicode4K];
        for (int i = 0; i < 3; i++)
        {
            byte[] f = new PstFixtureBuilder(formats[i]).Build(PstFixtures.Sample(), compress: formats[i] == NdbFormat.Unicode4K).Bytes;
            result[i] = (f, 0x400);
        }

        return result;
    }

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
            (byte[] baseFile, long from) = Bases[which];
            file = (byte[])baseFile.Clone();
            long span = file.Length - from;
            for (int i = 1; i + 3 < data.Length; i += 4)
            {
                file[from + ((data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) % span)] = data[i + 3];
            }
        }

        var options = new PstReaderOptions
        {
            Ndb = new NdbOptions { MaxDefects = 1000, MaxDataTreeLeaves = 10_000, MaxSubnodeEntries = 10_000 },
            MaxDefects = 1000,
            MaxFolderDepth = 16,
        };
        if (!PstStore.TryOpen(file, out PstStore? store, out _, options) || store is null)
        {
            return;
        }

        using (store)
        {
            int budget = 400;
            foreach (PstFolder folder in store.EnumerateFolders())
            {
                _ = folder.Path;
                foreach (PstMessage m in folder.EnumerateMessages(PstMessageSet.All))
                {
                    if (budget-- <= 0)
                    {
                        return;
                    }

                    Read(m, 0);
                }
            }
        }
    }

    private static void Read(MapiMessage m, int depth)
    {
        var view = new MapiMailItem(m, default);
        _ = view.Kind;
        _ = view.Subject;
        _ = view.From;
        _ = view.Sender;
        _ = view.ToRecipients.Count + view.Cc.Count + view.Bcc.Count;
        _ = view.MessageId;
        _ = view.References.Count + view.InReplyTo.Count;
        _ = view.TryGetDate(MailDateSource.Delivery, out _);
        _ = view.Sent;
        _ = view.TextBody?.ReadText();
        _ = view.HtmlBody?.ReadText();
        _ = view.ConversationIndex.Length;
        foreach (MapiProperty p in m.Properties)
        {
            _ = p.Name;
            _ = p.IsLazy && p.ValueCount > 0 ? p.GetByteLength() : 0;
        }

        foreach (IMailAttachment a in view.Attachments)
        {
            _ = a.FileName;
            _ = a.Size;
            using Stream s = a.Open();
            byte[] buf = new byte[4096];
            int total = 0;
            int n;
            while (total < 16_384 && (n = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
            }
        }

        if (depth < 3)
        {
            foreach (MapiAttachment a in m.Attachments)
            {
                if (a.EmbeddedMessage is { } e)
                {
                    Read(e, depth + 1);
                }
            }
        }

        _ = view.Defects.Count();
    }
}
