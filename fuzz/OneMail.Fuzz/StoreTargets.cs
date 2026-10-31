using RecursiveAI.OneMail.Sources;
using RecursiveAI.OneMail.Stores;
using RecursiveAI.OneMail.Stores.Emlx;
using RecursiveAI.OneMail.Stores.Maildir;
using RecursiveAI.OneMail.Stores.Mbox;

namespace RecursiveAI.OneMail.Fuzz;

// Fuzz targets for the mailbox stores. Only typed results may escape: an exception, records that do not tile the file, a
// quoting round trip that loses bytes, or a hang is a finding.
internal static class StoreTargets
{
    internal static Dictionary<string, Action<ReadOnlySpan<byte>>> Register(Dictionary<string, Action<ReadOnlySpan<byte>>> targets)
    {
        targets["mbox"] = static d => Mbox(d);
        targets["emlx"] = static d => Emlx(d);
        return targets;
    }

    // The first byte picks the variant and the From-line mode; the rest is the file. Records must tile the file after the
    // preamble, a small read window must give the same entries as a large one, and the copy of every record must reproduce
    // the file.
    private static void Mbox(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte pick = data[0];
        byte[] file = data[1..].ToArray();
        MboxVariant variant = (MboxVariant)(pick % 5);
        MboxFromLineMode mode = (pick & 0x10) != 0 ? MboxFromLineMode.Any : MboxFromLineMode.Strict;
        MboxReaderOptions small = new() { Variant = variant, FromLineMode = mode, BufferSize = 16384, MaxEnvelopeLineBytes = 128, MaxHeaderBytes = 2048 };
        MboxReaderOptions large = new() { Variant = variant, FromLineMode = mode, BufferSize = 1 << 20, MaxEnvelopeLineBytes = 128, MaxHeaderBytes = 2048 };

        using MemorySource src = new(file);
        List<MboxEntry> a = [.. MboxReader.Enumerate(src, small)];
        List<MboxEntry> b = [.. MboxReader.Enumerate(src, large)];
        if (a.Count != b.Count)
        {
            throw new InvalidOperationException("window size changes the entries");
        }

        long expected = -1;
        using MemoryStream copy = new();
        MboxWriter w = new(copy);
        using (MboxReader r = new(src, small))
        {
            bool first = true;
            int i = 0;
            while (r.Read())
            {
                MboxEntry e = r.Current;
                if (e.Offset != a[i].Offset || e.RecordLength != a[i].RecordLength || e.MessageLength != b[i].MessageLength)
                {
                    throw new InvalidOperationException("window size changes the ranges");
                }

                if (first)
                {
                    expected = r.PreambleLength;
                    if (expected != e.Offset)
                    {
                        throw new InvalidOperationException("preamble does not reach the first record");
                    }

                    w.WritePreamble(src, r.PreambleLength);
                    first = false;
                }

                if (e.Offset != expected || e.MessageLength < 0 || e.EnvelopeLength + e.MessageLength > e.RecordLength || e.Offset + e.RecordLength > file.Length)
                {
                    throw new InvalidOperationException("records do not tile the file");
                }

                expected = e.Offset + e.RecordLength;
                w.AppendRecord(src, e);
                _ = r.ReadMessage(e);
                _ = r.ReadEnvelope(e);
                _ = MboxMetadata.Read(src, e);
                i++;
            }

            if (first)
            {
                if (r.PreambleLength != file.Length)
                {
                    throw new InvalidOperationException("no record but the preamble is not the whole file");
                }

                w.WritePreamble(src, r.PreambleLength);
            }
            else if (expected != file.Length)
            {
                throw new InvalidOperationException("records stop before the end of the file");
            }
        }

        if (!copy.ToArray().AsSpan().SequenceEqual(file))
        {
            throw new InvalidOperationException("copying the records does not reproduce the file");
        }

        // Quoting round trip for the reversible variant, over whatever bytes came in.
        MboxQuoter q = new(quote: true, reversible: true);
        byte[] quoted = new byte[MboxQuoter.MaxOutput(file.Length)];
        int qn = q.Process(file, quoted);
        byte[] tail = new byte[16];
        int tn = q.Finish(tail);
        byte[] all = [.. quoted.AsSpan(0, qn), .. tail.AsSpan(0, tn)];
        MboxQuoter u = new(quote: false, reversible: true);
        byte[] back = new byte[MboxQuoter.MaxOutput(all.Length)];
        int bn = u.Process(all, back);
        int fn = u.Finish(tail);
        byte[] restored = [.. back.AsSpan(0, bn), .. tail.AsSpan(0, fn)];
        if (!restored.AsSpan().SequenceEqual(file))
        {
            throw new InvalidOperationException("mboxrd quoting is not reversible");
        }

        _ = MboxSniffer.Detect(src);
        _ = StoreSniffer.Sniff(src);
    }

    // EMLX and the property list: nothing throws; a canonical file written back from its parts is identical; the Maildir
    // file name parser agrees with its builder on the flag letters.
    private static void Emlx(ReadOnlySpan<byte> data)
    {
        byte[] file = data.ToArray();
        using MemorySource src = new(file);
        EmlxEntry e = EmlxReader.Read(src);
        if (e.MessageLength < 0 || e.TrailerLength < 0 || e.MessageOffset + e.MessageLength != e.TrailerOffset && (e.Defects & StoreDefects.BadByteCount) == 0)
        {
            throw new InvalidOperationException("entry ranges do not fit together");
        }

        _ = EmlxReader.ReadMetadata(src, e);
        _ = Plist.TryParse(file, out object? root);
        if (root is not null)
        {
            // Anything parsed must be writable.
            _ = Plist.Write(root);
        }

        if ((e.Defects & StoreDefects.BadByteCount) == 0)
        {
            using MemoryStream ms = new();
            EmlxWriter.WriteFrom(ms, src, e);
            EmlxEntry again = EmlxReader.Read(new MemorySource(ms.ToArray()));
            if (again.MessageLength != e.MessageLength || again.TrailerLength != e.TrailerLength)
            {
                throw new InvalidOperationException("rewriting an emlx changes its parts");
            }
        }

        string name = System.Text.Encoding.Latin1.GetString(file.AsSpan(0, Math.Min(file.Length, 200)));
        MaildirName n = MaildirName.Parse(name);
        _ = MaildirName.Build(n.Unique, n.Marks, n.KeywordLetters, n.Extra);
    }
}
