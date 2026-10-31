using System.Globalization;
using System.Text;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Stores.Emlx;

/// <summary>
/// One <c>.emlx</c> file taken apart: the decimal byte-count line, that many bytes of message, then (usually) an XML
/// property list. The three ranges tile the file.
/// </summary>
/// <param name="ByteCount">The count the first line states.</param>
/// <param name="CountLineLength">Length of the first line including its line ending.</param>
/// <param name="MessageOffset">Offset of the message, always equal to <paramref name="CountLineLength"/>.</param>
/// <param name="MessageLength">Length of the message: the byte count, or less when the file ends first.</param>
/// <param name="TrailerOffset">Offset of the bytes after the message.</param>
/// <param name="TrailerLength">Length of those bytes; 0 when the file ends with the message.</param>
/// <param name="Defects">What was irregular.</param>
public readonly record struct EmlxEntry(
    long ByteCount,
    int CountLineLength,
    long MessageOffset,
    long MessageLength,
    long TrailerOffset,
    long TrailerLength,
    StoreDefects Defects);

/// <summary>The properties Apple Mail keeps in an <c>.emlx</c> property list.</summary>
public sealed class EmlxMetadata
{
    // Mail's flag word. The low bits below are the ones every write-up of the format agrees on; OneMail has not seen a
    // real Mail store, so the raw word is always available in Flags and unknown bits are left alone.
    private const long ReadBit = 1L << 0;
    private const long DeletedBit = 1L << 1;
    private const long AnsweredBit = 1L << 2;
    private const long FlaggedBit = 1L << 4;
    private const long DraftBit = 1L << 6;
    private const long ForwardedBit = 1L << 8;

    /// <summary>All keys of the property list as parsed, empty when the trailer is missing or not a property list.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();

    /// <summary>The <c>flags</c> word, or null if absent.</summary>
    public long? Flags { get; init; }

    /// <summary>The flags that map onto <see cref="MessageMarks"/>: read, deleted, answered, flagged, draft, forwarded.</summary>
    public MessageMarks Marks => Flags is long f ? FromWord(f) : MessageMarks.None;

    /// <summary>The <c>date-sent</c> value, or null.</summary>
    public DateTimeOffset? DateSent { get; init; }

    /// <summary>The <c>date-received</c> value, or null.</summary>
    public DateTimeOffset? DateReceived { get; init; }

    /// <summary>The <c>remote-id</c> (the message's UID on its IMAP server), or null.</summary>
    public string? RemoteId { get; init; }

    /// <summary>The <c>conversation-id</c>, or null.</summary>
    public long? ConversationId { get; init; }

    /// <summary>Builds the metadata from a parsed property list.</summary>
    /// <param name="root">The parsed top-level value; anything but a dictionary gives empty metadata.</param>
    /// <returns>The metadata.</returns>
    public static EmlxMetadata FromPlist(object? root)
    {
        if (root is not Dictionary<string, object?> d)
        {
            return new EmlxMetadata();
        }

        return new EmlxMetadata
        {
            Values = d,
            Flags = d.TryGetValue("flags", out object? f) && f is long fl ? fl : null,
            DateSent = d.TryGetValue("date-sent", out object? ds) ? ToDate(ds) : null,
            DateReceived = d.TryGetValue("date-received", out object? dr) ? ToDate(dr) : null,
            RemoteId = d.TryGetValue("remote-id", out object? ri) ? Convert.ToString(ri, CultureInfo.InvariantCulture) : null,
            ConversationId = d.TryGetValue("conversation-id", out object? ci) && ci is long cl ? cl : null,
        };
    }

    /// <summary>Maps a Mail flag word onto the shared marks.</summary>
    /// <param name="word">The <c>flags</c> value.</param>
    /// <returns>The marks.</returns>
    public static MessageMarks FromWord(long word)
    {
        MessageMarks m = MessageMarks.None;
        if ((word & ReadBit) != 0) { m |= MessageMarks.Seen; }
        if ((word & DeletedBit) != 0) { m |= MessageMarks.Deleted; }
        if ((word & AnsweredBit) != 0) { m |= MessageMarks.Answered; }
        if ((word & FlaggedBit) != 0) { m |= MessageMarks.Flagged; }
        if ((word & DraftBit) != 0) { m |= MessageMarks.Draft; }
        if ((word & ForwardedBit) != 0) { m |= MessageMarks.Forwarded; }
        return m;
    }

    /// <summary>Maps shared marks onto a Mail flag word (the inverse of <see cref="FromWord"/> for the bits it knows).</summary>
    /// <param name="marks">The marks.</param>
    /// <returns>The flag word.</returns>
    public static long ToWord(MessageMarks marks)
    {
        long w = 0;
        if ((marks & MessageMarks.Seen) != 0) { w |= ReadBit; }
        if ((marks & MessageMarks.Deleted) != 0) { w |= DeletedBit; }
        if ((marks & MessageMarks.Answered) != 0) { w |= AnsweredBit; }
        if ((marks & MessageMarks.Flagged) != 0) { w |= FlaggedBit; }
        if ((marks & MessageMarks.Draft) != 0) { w |= DraftBit; }
        if ((marks & MessageMarks.Forwarded) != 0) { w |= ForwardedBit; }
        return w;
    }

    // A date element is read as is. A real is seconds: Unix seconds when it is at least 1e9, otherwise seconds since
    // 2001-01-01 (Apple's reference date), the two forms Mail has used for these keys.
    private static DateTimeOffset? ToDate(object? v)
    {
        switch (v)
        {
            case DateTimeOffset d:
                return d;
            case double s when s is >= 0 and < 4e9:
                return s >= 1e9
                    ? DateTimeOffset.UnixEpoch.AddSeconds(s)
                    : new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(s);
            default:
                return null;
        }
    }
}

/// <summary>Reads <c>.emlx</c> files.</summary>
public static class EmlxReader
{
    private const int MaxCountDigits = 15;

    /// <summary>Takes an <c>.emlx</c> file apart without reading the message.</summary>
    /// <param name="source">The file's bytes.</param>
    /// <returns>The entry. A file with no valid count line has no message and keeps all its bytes as trailer (<see cref="StoreDefects.BadByteCount"/>).</returns>
    public static EmlxEntry Read(IRandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] head = new byte[32];
        int n = source.ReadFully(0, head);
        return Parse(head.AsSpan(0, n), source.Length);
    }

    /// <summary>Asynchronous twin of <see cref="Read"/>.</summary>
    /// <param name="source">The file's bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entry.</returns>
    public static async ValueTask<EmlxEntry> ReadAsync(IRandomAccessSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] head = new byte[32];
        int n = await source.ReadFullyAsync(0, head, cancellationToken).ConfigureAwait(false);
        return Parse(head.AsSpan(0, n), source.Length);
    }

    /// <summary>Takes an <c>.emlx</c> file apart from its first bytes and its length. Span-level form of <see cref="Read"/>.</summary>
    /// <param name="head">At least the first 32 bytes of the file (or all of it when shorter).</param>
    /// <param name="fileLength">The file's length.</param>
    /// <returns>The entry.</returns>
    public static EmlxEntry Parse(ReadOnlySpan<byte> head, long fileLength)
    {
        int i = 0;
        while (i < head.Length && i <= MaxCountDigits && head[i] is >= (byte)'0' and <= (byte)'9')
        {
            i++;
        }

        int nl = -1;
        if (i > 0 && i <= MaxCountDigits)
        {
            if (i < head.Length && head[i] == (byte)'\n')
            {
                nl = i + 1;
            }
            else if (i + 1 < head.Length && head[i] == (byte)'\r' && head[i + 1] == (byte)'\n')
            {
                nl = i + 2;
            }
        }

        if (nl < 0)
        {
            return new EmlxEntry(0, 0, 0, 0, 0, fileLength, StoreDefects.BadByteCount);
        }

        long count = long.Parse(Encoding.ASCII.GetString(head[..i]), NumberStyles.None, CultureInfo.InvariantCulture);
        long available = Math.Max(0, fileLength - nl);
        StoreDefects defects = StoreDefects.None;
        long len = count;
        if (count > available)
        {
            len = available;
            defects |= StoreDefects.TruncatedMessage;
        }

        long trailerOffset = nl + len;
        return new EmlxEntry(count, nl, nl, len, trailerOffset, fileLength - trailerOffset, defects);
    }

    /// <summary>Reads and parses the property list after the message.</summary>
    /// <param name="source">The file's bytes.</param>
    /// <param name="entry">The entry from <see cref="Read(IRandomAccessSource)"/>.</param>
    /// <returns>The metadata; empty if there is no trailer, or it is not a property list (then <paramref name="entry"/> does not change, so callers that care check <see cref="EmlxMetadata.Values"/>).</returns>
    public static EmlxMetadata ReadMetadata(IRandomAccessSource source, in EmlxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (entry.TrailerLength == 0 || entry.TrailerLength > (1 << 20))
        {
            return new EmlxMetadata();
        }

        byte[] b = new byte[entry.TrailerLength];
        int n = source.ReadFully(entry.TrailerOffset, b);
        return Plist.TryParse(b.AsSpan(0, n), out object? root) ? EmlxMetadata.FromPlist(root) : new EmlxMetadata();
    }

    /// <summary>Asynchronous twin of <see cref="ReadMetadata"/>.</summary>
    /// <param name="source">The file's bytes.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The metadata.</returns>
    public static async ValueTask<EmlxMetadata> ReadMetadataAsync(IRandomAccessSource source, EmlxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (entry.TrailerLength == 0 || entry.TrailerLength > (1 << 20))
        {
            return new EmlxMetadata();
        }

        byte[] b = new byte[entry.TrailerLength];
        int n = await source.ReadFullyAsync(entry.TrailerOffset, b, cancellationToken).ConfigureAwait(false);
        return Plist.TryParse(b.AsSpan(0, n), out object? root) ? EmlxMetadata.FromPlist(root) : new EmlxMetadata();
    }

    /// <summary>Opens the message bytes as a lazy stream.</summary>
    /// <param name="source">The file's bytes; must outlive the stream.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>A seekable, read-only stream over the message.</returns>
    public static Stream OpenMessage(IRandomAccessSource source, in EmlxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.OpenRead(entry.MessageOffset, entry.MessageLength);
    }
}

/// <summary>Writes <c>.emlx</c> files.</summary>
public static class EmlxWriter
{
    /// <summary>Writes a message with a freshly generated property list.</summary>
    /// <param name="output">Where the file goes.</param>
    /// <param name="message">The message bytes, written unchanged. The byte count line counts them.</param>
    /// <param name="marks">Flags to record in the <c>flags</c> word.</param>
    /// <param name="dateReceived">Recorded as <c>date-received</c> in Unix seconds when given.</param>
    /// <param name="dateSent">Recorded as <c>date-sent</c> in Unix seconds when given.</param>
    public static void Write(Stream output, ReadOnlySpan<byte> message, MessageMarks marks = MessageMarks.None, DateTimeOffset? dateReceived = null, DateTimeOffset? dateSent = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        Dictionary<string, object?> d = new(StringComparer.Ordinal);
        if (dateReceived is { } dr)
        {
            d["date-received"] = (double)dr.ToUnixTimeSeconds();
        }

        if (dateSent is { } ds)
        {
            d["date-sent"] = (double)ds.ToUnixTimeSeconds();
        }

        d["flags"] = EmlxMetadata.ToWord(marks);
        byte[] plist = Plist.Write(d);
        output.Write(Encoding.ASCII.GetBytes(message.Length.ToString(CultureInfo.InvariantCulture) + "\n"));
        output.Write(message);
        output.Write(plist);
    }

    /// <summary>
    /// Writes a file from the parts of another one: a canonical count line (decimal, LF), the message, and the original
    /// trailer bytes verbatim. A file whose count line is canonical is reproduced byte for byte.
    /// </summary>
    /// <param name="output">Where the file goes.</param>
    /// <param name="source">The file the entry came from.</param>
    /// <param name="entry">The entry.</param>
    public static void WriteFrom(Stream output, IRandomAccessSource source, in EmlxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);
        output.Write(Encoding.ASCII.GetBytes(entry.MessageLength.ToString(CultureInfo.InvariantCulture) + "\n"));
        Copy(source, entry.MessageOffset, entry.MessageLength, output);
        Copy(source, entry.TrailerOffset, entry.TrailerLength, output);
    }

    private static void Copy(IRandomAccessSource source, long offset, long length, Stream output)
    {
        byte[] buf = new byte[65536];
        long done = 0;
        while (done < length)
        {
            int n = source.ReadFully(offset + done, buf.AsSpan(0, (int)Math.Min(buf.Length, length - done)));
            if (n == 0)
            {
                throw new EndOfStreamException("The source ended before the range did.");
            }

            output.Write(buf, 0, n);
            done += n;
        }
    }
}
