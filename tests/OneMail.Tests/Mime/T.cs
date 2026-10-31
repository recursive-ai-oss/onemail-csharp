using System.Text;
using RecursiveAI.OneMail.Mime.Reading;

namespace RecursiveAI.OneMail.Tests.Mime;

/// <summary>Shared helpers for the MIME tests.</summary>
internal static class T
{
    public static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>Joins lines with CRLF and ends with CRLF.</summary>
    public static string Crlf(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    /// <summary>A non-seekable stream that hands out at most <c>chunk</c> bytes per read.</summary>
    public sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
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

    /// <summary>One line per event: kind, absolute offset, length, depth, plus defect kind where relevant.</summary>
    public static List<string> Events(MimeReader r)
    {
        var list = new List<string>();
        while (r.Read())
        {
            list.Add(Describe(r));
        }

        return list;
    }

    public static string Describe(MimeReader r) => r.Kind switch
    {
        MimeEventKind.Defect => $"Defect {r.Defect.Kind}@{r.Defect.Offset}+{r.Defect.Length} d{r.Defect.Depth}",
        MimeEventKind.HeadersEnd => $"HeadersEnd@{r.Offset}+{r.Length} {r.EntityType}",
        MimeEventKind.EntityEnd => $"EntityEnd@{r.Offset} {r.EntityType} d{r.Depth}",
        MimeEventKind.Delimiter => $"Delimiter@{r.Offset}+{r.Length} close={r.IsCloseDelimiter} d{r.Depth}",
        _ => $"{r.Kind}@{r.Offset}+{r.Length} d{r.Depth}",
    };

    /// <summary>Reads everything and returns the concatenated event bytes plus the defects.</summary>
    public static (byte[] Covered, List<MimeDefect> Defects) Cover(MimeReader r)
    {
        var ms = new MemoryStream();
        var defects = new List<MimeDefect>();
        long expected = 0;
        while (r.Read())
        {
            if (r.Kind == MimeEventKind.Defect)
            {
                defects.Add(r.Defect);
                continue;
            }

            if (r.Length > 0)
            {
                Assert.Equal(expected, r.Offset);
                ms.Write(r.Raw);
                expected += r.Length;
            }
        }

        return (ms.ToArray(), defects);
    }

    public static List<MimeDefect> Defects(string message, MimeReaderOptions? options = null)
    {
        using var r = new MimeReader(B(message), options);
        return Cover(r).Defects;
    }
}
