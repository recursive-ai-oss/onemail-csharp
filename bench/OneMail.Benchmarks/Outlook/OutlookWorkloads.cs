using System.Text;
using MsgReader.Outlook;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Rtf;
using RecursiveAI.OneMail.Rtf.Compression;
using RecursiveAI.OneMail.Tnef;
using MimeKit.Tnef;
using XstReader;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>What a walk saw. Both sides fill the same fields so a run can be checked for equal work (the <c>verify</c> command).</summary>
internal struct Totals
{
    public long Folders;
    public long Messages;
    public long Recipients;
    public long Attachments;
    public long AttachmentBytes;
    public long Chars;

    /// <summary>One number that depends on everything read, so the work cannot be optimised away.</summary>
    public readonly long Checksum => Folders + (Messages * 3) + (Recipients * 5) + (Attachments * 7) + AttachmentBytes + Chars;

    public override readonly string ToString() =>
        $"folders={Folders} messages={Messages} recipients={Recipients} attachments={Attachments} attachment_bytes={AttachmentBytes}";
}

/// <summary>
/// The work each Outlook-format row does, on both sides. Every message: subject, sender (name and address), submit time, every
/// recipient (name and address), the body, and every attachment's bytes read to the end. Bodies are the best one (HTML, else RTF, else
/// text) for PST and TNEF, and all three for MSG, because that is what each library hands out in one call.
/// </summary>
internal static class OutlookWorkloads
{
    static OutlookWorkloads()
    {
        // MimeKit's TNEF reader decodes 8-bit strings with legacy code pages, which .NET only has once this provider is registered
        // (MimeKit's documentation asks .NET Core applications to do so). OneMail needs no registration. BenchmarkDotNet's child
        // processes have their own entry point, so this cannot live in Main.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Messages read so far by the walk in progress; the <c>curve</c> command samples it.</summary>
    public static long Progress;

    // XstReader's documented way to release what a message cached is ClearContents(). On a 50,000-message store it made the walk 16 times
    // slower and saved no memory worth having, so the timed rows leave it off (the comparator's fastest correct use). ONEMAIL_XST_CLEAR=1
    // turns it on, for the memory comparison in docs/benchmarks.md.
    private static readonly bool s_clear = Environment.GetEnvironmentVariable("ONEMAIL_XST_CLEAR") == "1";

    private enum BodyMode
    {
        Best,
        All,
    }

    // ---- OneMail: one reader for any MAPI message ----

    private static void ReadMapi(MapiMessage m, ref Totals t, BodyMode mode, byte[] buffer)
    {
        t.Messages++;
        Progress++;
        t.Chars += m.Subject?.Length ?? 0;
        t.Chars += (m.GetString(MapiPid.SenderName)?.Length ?? 0) + (m.GetString(MapiPid.SenderEmailAddress)?.Length ?? 0);
        t.Chars += m.GetTime(MapiPid.ClientSubmitTime)?.Year ?? 0;
        foreach (MapiRecipient r in m.Recipients)
        {
            t.Recipients++;
            t.Chars += (r.DisplayName?.Length ?? 0) + ((r.SmtpAddress ?? r.EmailAddress)?.Length ?? 0);
        }

        t.Chars += mode == BodyMode.All ? AllBodies(m) : BestBody(m);
        foreach (MapiAttachment a in m.Attachments)
        {
            t.Attachments++;
            t.Chars += a.FileName?.Length ?? 0;
            using Stream? s = a.OpenData();
            if (s is null)
            {
                continue;
            }

            int n;
            while ((n = s.Read(buffer)) > 0)
            {
                t.AttachmentBytes += n;
            }
        }
    }

    private static string? HtmlText(MapiMessage m)
    {
        MapiProperty? p = m.Properties.Get(MapiPid.Html);
        if (p is null)
        {
            return null;
        }

        return p.Type is MapiType.Binary ? m.String8Encoding.GetString(p.GetBytes().Span) : p.AsString(m.String8Encoding);
    }

    private static byte[]? RtfBytes(MapiMessage m) =>
        m.Properties.Get(MapiPid.RtfCompressed) is { } p ? RtfDecompressor.Decompress(p.GetBytes().Span).Data : null;

    private static long AllBodies(MapiMessage m)
    {
        long n = m.Body?.Length ?? 0;
        string? html = HtmlText(m);
        byte[]? rtf = RtfBytes(m);
        if (html is null && rtf is not null)
        {
            // MsgReader derives the HTML body from an encapsulating RTF when there is no stored HTML; so does this.
            html = RtfDeencapsulator.Deencapsulate(rtf).Text;
        }

        return n + (html?.Length ?? 0) + (rtf?.Length ?? 0);
    }

    private static long BestBody(MapiMessage m)
    {
        if (HtmlText(m) is { } html)
        {
            return html.Length;
        }

        if (RtfBytes(m) is { } rtf)
        {
            RtfDeencapsulationResult r = RtfDeencapsulator.Deencapsulate(rtf);
            return r.Kind == RtfEncapsulation.None ? rtf.Length : r.Text.Length;
        }

        return m.Body?.Length ?? 0;
    }

    public static Totals PstOurs(string path)
    {
        var t = new Totals();
        byte[] buffer = new byte[81920];
        if (!PstStore.TryOpen(path, out PstStore? store, out _))
        {
            return t;
        }

        using (store)
        {
            foreach (PstFolder f in store!.EnumerateFolders())
            {
                t.Folders++;
                if (f.IsSearchFolder)
                {
                    continue;
                }

                foreach (PstMessage m in f.EnumerateMessages(PstMessageSet.Normal))
                {
                    ReadMapi(m, ref t, BodyMode.Best, buffer);
                }
            }
        }

        return t;
    }

    public static Totals MsgOurs(string path)
    {
        var t = new Totals();
        if (MsgFile.TryOpen(path, out MsgFile? file, out _))
        {
            using (file)
            {
                ReadMapi(file.Message, ref t, BodyMode.All, new byte[81920]);
            }
        }

        return t;
    }

    public static Totals TnefOurs(byte[] data)
    {
        var t = new Totals();
        ReadMapi(TnefMessage.Read(data), ref t, BodyMode.Best, new byte[81920]);
        return t;
    }

    // ---- XstReader.Api 1.0.6 ----

    public static Totals PstTheirs(string path)
    {
        var t = new Totals();
        var sink = new CountingStream();
        // Not disposed: XstFile.Dispose starts a background release of the file's caches that reopens the file after it was closed, and
        // in a loop that thread throws on its own and ends the process (seen on a 2 MB store and on macOS). The file is left to the
        // garbage collector, which closes its handle; that costs the comparator nothing in time.
        var xst = new XstFile(path);
        WalkXst(xst.RootFolder, ref t, sink);
        return t;
    }

    private static void WalkXst(XstFolder folder, ref Totals t, CountingStream sink)
    {
        t.Folders++;
        foreach (XstMessage m in folder.Messages)
        {
            t.Messages++;
            Progress++;
            t.Chars += m.Subject?.Length ?? 0;
            t.Chars += m.From?.Length ?? 0;
            t.Chars += m.SubmittedTime?.Year ?? 0;
            foreach (XstRecipient r in Recipients(m))
            {
                t.Recipients++;
                t.Chars += (r.DisplayName?.Length ?? 0) + (r.Address?.Length ?? 0);
            }

            t.Chars += m.Body?.Text?.Length ?? 0;
            foreach (XstAttachment a in m.Attachments)
            {
                t.Attachments++;
                t.Chars += a.FileName?.Length ?? 0;
                if (a.IsFile)
                {
                    sink.Reset();
                    a.SaveToStream(sink);
                    t.AttachmentBytes += sink.Count;
                }
            }

            // Not called on folders: it starts a background re-read that races with disposing the file.
            if (s_clear)
            {
                m.ClearContents();
            }
        }

        foreach (XstFolder child in folder.Folders)
        {
            WalkXst(child, ref t, sink);
        }
    }

    private static IEnumerable<XstRecipient> Recipients(XstMessage m)
    {
        XstRecipientSet set = m.Recipients;
        foreach (XstRecipient r in set.To ?? [])
        {
            yield return r;
        }

        foreach (XstRecipient r in set.Cc ?? [])
        {
            yield return r;
        }

        foreach (XstRecipient r in set.Bcc ?? [])
        {
            yield return r;
        }
    }

    // ---- MsgReader 6.2.0 ----

    public static Totals MsgTheirs(string path)
    {
        var t = new Totals();
        using var msg = new Storage.Message(path, FileAccess.Read);
        t.Messages++;
        t.Chars += msg.Subject?.Length ?? 0;
        t.Chars += (msg.Sender?.DisplayName?.Length ?? 0) + (msg.Sender?.Email?.Length ?? 0);
        t.Chars += msg.SentOn?.Year ?? 0;
        foreach (Storage.Recipient r in msg.Recipients)
        {
            t.Recipients++;
            t.Chars += (r.DisplayName?.Length ?? 0) + (r.Email?.Length ?? 0);
        }

        t.Chars += (msg.BodyText?.Length ?? 0) + (msg.BodyHtml?.Length ?? 0) + (msg.BodyRtf?.Length ?? 0);
        foreach (object o in msg.Attachments)
        {
            t.Attachments++;
            if (o is Storage.Attachment a)
            {
                t.Chars += a.FileName?.Length ?? 0;
                t.AttachmentBytes += a.Data?.Length ?? 0;
            }
        }

        return t;
    }

    // ---- MimeKit 4.18.2 TNEF ----

    public static Totals TnefTheirs(byte[] data)
    {
        var t = new Totals();
        using var ms = new MemoryStream(data, writable: false);
        var part = new TnefPart { Content = new MimeKit.MimeContent(ms) };
        MimeKit.MimeMessage m = part.ConvertToMessage();
        t.Messages++;
        t.Chars += m.Subject?.Length ?? 0;
        t.Chars += m.From.ToString().Length;
        t.Chars += m.Date.Year;
        foreach (MimeKit.InternetAddress r in m.To.Concat(m.Cc).Concat(m.Bcc))
        {
            t.Recipients++;
            t.Chars += r.Name?.Length ?? 0;
        }

        t.Chars += (m.HtmlBody ?? m.TextBody)?.Length ?? 0;
        var sink = new CountingStream();
        foreach (MimeKit.MimeEntity e in m.Attachments)
        {
            t.Attachments++;
            if (e is MimeKit.MimePart p && p.Content is { } c)
            {
                sink.Reset();
                c.DecodeTo(sink);
                t.AttachmentBytes += sink.Count;
            }
        }

        return t;
    }

    /// <summary>A write-only stream that counts and discards.</summary>
    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; }

        public void Reset() => Count = 0;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Count;

        public override long Position
        {
            get => Count;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Count += count;

        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
    }
}
