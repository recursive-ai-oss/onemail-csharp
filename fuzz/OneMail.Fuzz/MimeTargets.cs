using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Fuzz;

/// <summary>
/// Targets for the MIME layer. Only the library's own typed results may escape: any exception, and any broken invariant
/// (events not covering the input, a tree that does not write back byte for byte), is a finding.
/// </summary>
internal static class MimeTargets
{
    /// <summary>The reader over memory and over a stream that returns a few bytes at a time must agree, and cover the input.</summary>
    public static void Reader(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();
        byte[] viaMemory = Cover(new MimeReader(input, SmallLimits()), input.Length);
        if (!viaMemory.AsSpan().SequenceEqual(input))
        {
            throw new InvalidOperationException("reader events do not cover the input");
        }

        int chunk = 1 + (input.Length > 0 ? input[0] % 13 : 0);
        byte[] viaStream = Cover(new MimeReader(new ChunkedStream(input, chunk), SmallLimits()), input.Length);
        if (!viaStream.AsSpan().SequenceEqual(input))
        {
            throw new InvalidOperationException("streamed reader events do not cover the input");
        }
    }

    /// <summary>Parse then write must reproduce the input; the decoders must not throw on the same bytes.</summary>
    public static void Tree(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();
        MimeEntity root = MimeEntity.Parse(input, SmallLimits());
        if (!root.ToArray().AsSpan().SequenceEqual(input))
        {
            throw new InvalidOperationException("tree does not write back identically");
        }

        Walk(root);
    }

    // Small limits so the limit paths are reachable from short inputs.
    private static MimeReaderOptions SmallLimits() => new()
    {
        BufferSize = 4096,
        Limits = new MimeLimits { MaxDepth = 6, MaxEntityCount = 40, MaxHeaderBlockBytes = 2048, MaxHeaderFieldBytes = 1024, MaxHeadersPerEntity = 30, MaxBoundaryLength = 40 },
    };

    private static byte[] Cover(MimeReader reader, int length)
    {
        using (reader)
        {
            var ms = new MemoryStream(length);
            long expected = 0;
            while (reader.Read())
            {
                if (reader.Kind == MimeEventKind.Defect || reader.Length == 0)
                {
                    continue;
                }

                if (reader.Offset != expected)
                {
                    throw new InvalidOperationException("event offset is not contiguous");
                }

                ms.Write(reader.Raw);
                expected += reader.Length;
            }

            return ms.ToArray();
        }
    }

    // The copy-into and borrowing readers must give what GetDecodedContent gives.
    private static void CheckCopyingApis(MimePart p, byte[] decoded)
    {
        byte[] room = new byte[p.GetMaxDecodedContentLength()];
        int n = p.CopyDecodedContentTo(room);
        if (!room.AsSpan(0, n).SequenceEqual(decoded))
        {
            throw new InvalidOperationException("CopyDecodedContentTo differs from GetDecodedContent");
        }

        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        if (p.CopyDecodedContentTo(writer) != decoded.Length || !writer.WrittenSpan.SequenceEqual(decoded))
        {
            throw new InvalidOperationException("CopyDecodedContentTo(IBufferWriter) differs from GetDecodedContent");
        }

        if (p.TryGetDecodedContentMemory(out ReadOnlyMemory<byte> view) && !view.Span.SequenceEqual(decoded))
        {
            throw new InvalidOperationException("TryGetDecodedContentMemory differs from GetDecodedContent");
        }
    }

    // The bulk base64 paths must give what feeding the decoder one byte at a time gives.
    private static void CheckBase64AgainstByteAtATime(MimePart p, byte[] decoded)
    {
        using var raw = new MemoryStream();
        using (Stream r = p.OpenRawContent())
        {
            r.CopyTo(raw);
        }

        var d = new RecursiveAI.OneMail.Mime.Transfer.Base64Decoder();
        var expected = new List<byte>();
        byte[] one = new byte[1];
        byte[] room = new byte[16];
        foreach (byte b in raw.ToArray())
        {
            one[0] = b;
            int n = d.Decode(one, room);
            for (int i = 0; i < n; i++)
            {
                expected.Add(room[i]);
            }
        }

        int f = d.Finish(room);
        for (int i = 0; i < f; i++)
        {
            expected.Add(room[i]);
        }

        if (!expected.ToArray().AsSpan().SequenceEqual(decoded))
        {
            throw new InvalidOperationException("bulk base64 decode differs from the byte-at-a-time decode");
        }
    }

    private static void Walk(MimeEntity e)
    {
        _ = e.MediaType;
        _ = e.TransferEncoding;
        _ = e.GetContentTypeParameter("boundary");

        // The decoding layer: text, parameters, addresses, dates and identifiers parse on demand from raw bytes.
        var defects = new List<MimeDefect>();
        foreach (MimeHeader h in e.Headers)
        {
            _ = h.Value;
            _ = h.GetValue(defects);
        }

        foreach (string name in new[] { "From", "To", "Cc", "Bcc", "Reply-To", "Sender" })
        {
            foreach (MimeMailbox m in AddressParser.Flatten(e.GetAddresses(name, defects)))
            {
                _ = m.UnquotedAddress;
                _ = m.LocalPart;
                _ = m.Domain;
                _ = m.ToString();
            }
        }

        _ = e.GetDate(defects);
        _ = e.GetMessageId(defects);
        _ = e.GetContentId(defects);
        _ = e.GetInReplyTo(defects);
        _ = e.GetReferences(defects);
        _ = e.GetContentTypeParameters(defects);
        _ = e.GetDispositionParameters(defects);
        _ = e.Disposition;
        _ = e.FileName;
        _ = e.ContentDescription;
        _ = e.MimeVersion;
        foreach (MimeDefect d in defects)
        {
            if (d.Offset < -1 || d.Length < 0)
            {
                throw new InvalidOperationException("defect with a negative offset or length");
            }
        }

        switch (e)
        {
            case MimePart p:
                byte[] decoded = p.GetDecodedContent();
                CheckCopyingApis(p, decoded);
                if (p.TransferEncoding == RecursiveAI.OneMail.Mime.Transfer.ContentTransferEncoding.Base64)
                {
                    CheckBase64AgainstByteAtATime(p, decoded);
                }

                break;
            case MimeMultipart m:
                foreach (MimeEntity c in m.Children)
                {
                    Walk(c);
                }

                break;
            case MimeMessagePart mp when mp.Message is not null:
                Walk(mp.Message);
                break;
        }
    }

    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _pos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(chunk, count), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
