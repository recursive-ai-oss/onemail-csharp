using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Stores.Dbx;

/// <summary>Which of the Outlook Express database files a file is, from the class id in its header.</summary>
public enum DbxFileKind
{
    /// <summary>Not an Outlook Express 5/6 database (or the header is too short to tell).</summary>
    Unknown = 0,

    /// <summary>A message folder (<c>Inbox.dbx</c>, <c>Sent Items.dbx</c>, …): an index of messages and the messages.</summary>
    MessageDatabase,

    /// <summary><c>Folders.dbx</c>: the folder hierarchy and the file each folder lives in.</summary>
    FolderDatabase,

    /// <summary><c>Pop3uidl.dbx</c>: the POP3 unique ids already downloaded. Recognised; holds no mail.</summary>
    Pop3Uidl,

    /// <summary><c>Offline.dbx</c>: queued offline IMAP operations. Recognised; holds no mail.</summary>
    Offline,
}

/// <summary>Message flags of a DBX message record (the record's flag word). Bits the format notes call unknown are not named.</summary>
[Flags]
public enum DbxMessageStatus : uint
{
    /// <summary>No bits set.</summary>
    None = 0,

    /// <summary>The body has been downloaded (IMAP and news headers can exist without one).</summary>
    BodyDownloaded = 0x01,

    /// <summary>The message is a draft that is still being written.</summary>
    Draft = 0x08,

    /// <summary>The message is flagged.</summary>
    Flagged = 0x20,

    /// <summary>The message has been read.</summary>
    Read = 0x80,

    /// <summary>The message is marked for download.</summary>
    MarkedForDownload = 0x100,

    /// <summary>The message is a news posting.</summary>
    NewsPosting = 0x800,

    /// <summary>The message is digitally signed.</summary>
    DigitallySigned = 0x1000,

    /// <summary>The message has an attachment.</summary>
    HasAttachment = 0x4000,

    /// <summary>The message has been answered.</summary>
    Answered = 0x80000,

    /// <summary>The message has been forwarded.</summary>
    Forwarded = 0x100000,

    /// <summary>The thread is watched.</summary>
    ThreadWatched = 0x400000,

    /// <summary>The thread is ignored.</summary>
    ThreadIgnored = 0x800000,
}

/// <summary>Limits and the defect handler of a <see cref="DbxReader"/>.</summary>
public sealed class DbxReaderOptions
{
    /// <summary>The largest indexed-info object that is read, in bytes. Larger ones are skipped with a defect. Default 1 MiB.</summary>
    public int MaxInfoBytes { get; init; } = 1 << 20;

    /// <summary>The deepest index tree that is walked. Real trees are two or three levels deep (fan-out 51); deeper ones are cycles or hostile. Default 32.</summary>
    public int MaxTreeDepth { get; init; } = 32;

    /// <summary>The longest message that is read, in bytes. A longer chain is cut with a defect. Default 2 GiB, the format's own limit.</summary>
    public long MaxMessageBytes { get; init; } = 2L << 30;

    /// <summary>Receives every defect with the offset it was found at, as it is found. May be null.</summary>
    public Action<StoreDefect>? DefectHandler { get; init; }
}

/// <summary>
/// An indexed-info object: the record format of both message records and folder records. A header, an index of up to 255
/// (field number, value or offset) entries and a data area the offsets point into. The notes call fields by number; the
/// typed members on the entries are named for what the numbers hold, and this is the escape hatch for the rest.
/// </summary>
/// <remarks>oedbx DBX format notes, OE_Dbx_IndexedInfo: header 12 bytes; each index entry is a field byte (low 7 bits the number, top bit "value is stored in the entry") and 3 value bytes.</remarks>
public class DbxRecord
{
    internal const int HeaderLength = 12;

    private readonly byte[] _object;
    private readonly int _count;
    private readonly int _dataStart;

    internal DbxRecord(long offset, byte[] obj, StoreDefects defects)
    {
        Offset = offset;
        _object = obj;
        _count = obj[10];
        _dataStart = HeaderLength + (4 * _count);
        Defects = defects;
    }

    /// <summary>Where the record starts in the file.</summary>
    public long Offset { get; }

    /// <summary>What was irregular about the record itself.</summary>
    public StoreDefects Defects { get; }

    /// <summary>The numbers of the fields present, ascending as stored.</summary>
    public IEnumerable<int> FieldNumbers
    {
        get
        {
            for (int i = 0; i < _count; i++)
            {
                yield return _object[HeaderLength + (4 * i)] & 0x7F;
            }
        }
    }

    /// <summary>Reads a field as a 32-bit integer.</summary>
    /// <param name="field">The field number.</param>
    /// <param name="value">The value; zero when absent. A stored value shorter than four bytes is zero-extended.</param>
    /// <returns>False when the record has no such field.</returns>
    public bool TryGetInt32(int field, out int value)
    {
        if (!Find(field, out bool direct, out int v, out int start, out int length))
        {
            value = 0;
            return false;
        }

        if (direct)
        {
            value = v;
            return true;
        }

        Span<byte> four = stackalloc byte[4];
        _object.AsSpan(start, Math.Min(4, length)).CopyTo(four);
        value = BinaryPrimitives.ReadInt32LittleEndian(four);
        return true;
    }

    /// <summary>Reads a field as a Windows FILETIME.</summary>
    /// <param name="field">The field number.</param>
    /// <param name="value">The time; null when absent, shorter than 8 bytes, zero or out of the representable range.</param>
    /// <returns>True when a time was read.</returns>
    public bool TryGetFileTime(int field, out DateTimeOffset value)
    {
        value = default;
        if (!Find(field, out bool direct, out _, out int start, out int length) || direct || length < 8)
        {
            return false;
        }

        long ticks = BinaryPrimitives.ReadInt64LittleEndian(_object.AsSpan(start, 8));
        // A FILETIME counts 100 ns from 1601-01-01; DateTime's range ends in year 9999.
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks - FileTimeEpochTicks)
        {
            return false;
        }

        value = new DateTimeOffset(ticks + FileTimeEpochTicks, TimeSpan.Zero);
        return true;
    }

    /// <summary>Reads a field as raw bytes.</summary>
    /// <param name="field">The field number.</param>
    /// <returns>The bytes up to the next field (a stored-in-entry value gives its three bytes), or an empty array when absent.</returns>
    public byte[] GetBytes(int field)
    {
        if (!Find(field, out bool direct, out int v, out int start, out int length))
        {
            return [];
        }

        if (direct)
        {
            return [(byte)v, (byte)(v >> 8), (byte)(v >> 16)];
        }

        return _object.AsSpan(start, length).ToArray();
    }

    /// <summary>Reads a field as text: the bytes up to the first zero, decoded as windows-1252 (ambiguity S15).</summary>
    /// <param name="field">The field number.</param>
    /// <returns>The text, or null when the field is absent.</returns>
    public string? GetString(int field)
    {
        if (!Find(field, out bool direct, out _, out int start, out int length) || direct)
        {
            return null;
        }

        ReadOnlySpan<byte> b = _object.AsSpan(start, length);
        int z = b.IndexOf((byte)0);
        if (z >= 0)
        {
            b = b[..z];
        }

        return Text.MailEncoding.Windows1252.GetString(b);
    }

    private const long FileTimeEpochTicks = 504911232000000000L;

    // The extent of a field kept in the data area runs to the start of the next such field, or the end of the area.
    private bool Find(int field, out bool direct, out int value, out int start, out int length)
    {
        direct = false;
        value = 0;
        start = 0;
        length = 0;
        int dataLength = _object.Length - _dataStart;
        for (int i = 0; i < _count; i++)
        {
            int at = HeaderLength + (4 * i);
            if ((_object[at] & 0x7F) != field)
            {
                continue;
            }

            int v = _object[at + 1] | (_object[at + 2] << 8) | (_object[at + 3] << 16);
            if ((_object[at] & 0x80) != 0)
            {
                direct = true;
                value = v;
                return true;
            }

            if (v > dataLength)
            {
                return false;
            }

            int end = dataLength;
            for (int j = 0; j < _count; j++)
            {
                int atj = HeaderLength + (4 * j);
                if ((_object[atj] & 0x80) != 0)
                {
                    continue;
                }

                int vj = _object[atj + 1] | (_object[atj + 2] << 8) | (_object[atj + 3] << 16);
                if (vj > v && vj < end)
                {
                    end = vj;
                }
            }

            start = _dataStart + v;
            length = end - v;
            return true;
        }

        return false;
    }
}

/// <summary>One message of a message database: its record, and where its text starts. Holds no reference to the file.</summary>
/// <remarks>oedbx DBX format notes, OE_Dbx_MessageInfo (field numbers).</remarks>
public sealed class DbxMessageEntry : DbxRecord
{
    internal DbxMessageEntry(long offset, byte[] obj, StoreDefects defects, int position)
        : base(offset, obj, defects)
    {
        Position = position;
    }

    /// <summary>The entry's position in the enumeration, from 0.</summary>
    public int Position { get; }

    /// <summary>The message's index (field 0x00), unique in the file; the order of the main tree.</summary>
    public int Index => TryGetInt32(0x00, out int v) ? v : 0;

    /// <summary>The flag word (field 0x01).</summary>
    public DbxMessageStatus Flags => TryGetInt32(0x01, out int v) ? (DbxMessageStatus)(uint)v : DbxMessageStatus.None;

    /// <summary>The flags that map onto <see cref="MessageMarks"/>.</summary>
    public MessageMarks Marks
    {
        get
        {
            DbxMessageStatus f = Flags;
            MessageMarks m = MessageMarks.None;
            if ((f & DbxMessageStatus.Read) != 0)
            {
                m |= MessageMarks.Seen;
            }

            if ((f & DbxMessageStatus.Answered) != 0)
            {
                m |= MessageMarks.Answered;
            }

            if ((f & DbxMessageStatus.Flagged) != 0)
            {
                m |= MessageMarks.Flagged;
            }

            if ((f & DbxMessageStatus.Draft) != 0)
            {
                m |= MessageMarks.Draft;
            }

            if ((f & DbxMessageStatus.Forwarded) != 0)
            {
                m |= MessageMarks.Forwarded;
            }

            return m;
        }
    }

    /// <summary>The time the message was created or sent (field 0x02), or null.</summary>
    public DateTimeOffset? DateSent => TryGetFileTime(0x02, out DateTimeOffset d) ? d : null;

    /// <summary>The time the message was received (field 0x12), or null.</summary>
    public DateTimeOffset? DateReceived => TryGetFileTime(0x12, out DateTimeOffset d) ? d : null;

    /// <summary>The time the message was saved in this folder (field 0x06), or null.</summary>
    public DateTimeOffset? DateSaved => TryGetFileTime(0x06, out DateTimeOffset d) ? d : null;

    /// <summary>The subject without its "Re:" prefixes (field 0x05), or null.</summary>
    public string? OriginalSubject => GetString(0x05);

    /// <summary>The subject (field 0x08), or null.</summary>
    public string? Subject => GetString(0x08);

    /// <summary>The Message-ID (field 0x07), or null.</summary>
    public string? MessageId => GetString(0x07);

    /// <summary>The Message-ID of the message this one answers (field 0x0a), or null.</summary>
    public string? InReplyToId => GetString(0x0A);

    /// <summary>The sender's name and address as one string (field 0x09), or null.</summary>
    public string? Sender => GetString(0x09);

    /// <summary>The sender's name (field 0x0d), or null.</summary>
    public string? SenderName => GetString(0x0D);

    /// <summary>The sender's address (field 0x0e), or null.</summary>
    public string? SenderAddress => GetString(0x0E);

    /// <summary>The first recipient's name (field 0x13), or null.</summary>
    public string? RecipientName => GetString(0x13);

    /// <summary>The first recipient's address (field 0x14), or null.</summary>
    public string? RecipientAddress => GetString(0x14);

    /// <summary>The priority (field 0x10): 1 high, 3 normal, 5 low; 0 when absent.</summary>
    public int Priority => TryGetInt32(0x10, out int v) ? v : 0;

    /// <summary>The name of the account the message came through (field 0x1a), or null.</summary>
    public string? AccountName => GetString(0x1A);

    /// <summary>The number of lines the record states for the body (field 0x03), or -1.</summary>
    public int Lines => TryGetInt32(0x03, out int v) ? v : -1;

    /// <summary>
    /// The length of the message the record states (field 0x11), or -1. The notes warn it can be wrong; the chain's own length is
    /// what is read (ambiguity S14).
    /// </summary>
    public long DeclaredLength => TryGetInt32(0x11, out int v) ? (uint)v : -1;

    /// <summary>The file offset of the first object of the message's text chain (field 0x04); 0 when the message has no text in this file.</summary>
    public long ChainOffset => TryGetInt32(0x04, out int v) ? (uint)v : 0;

    /// <summary>False for a record without text: a message header that was never downloaded.</summary>
    public bool HasMessage => ChainOffset != 0;
}

/// <summary>One folder of <c>Folders.dbx</c>. Holds no reference to the file.</summary>
/// <remarks>oedbx DBX format notes, OE_Dbx_FolderInfo (field numbers).</remarks>
public sealed class DbxFolderEntry : DbxRecord
{
    internal DbxFolderEntry(long offset, byte[] obj, StoreDefects defects, int position)
        : base(offset, obj, defects)
    {
        Position = position;
    }

    /// <summary>The entry's position in the enumeration, from 0.</summary>
    public int Position { get; }

    /// <summary>The folder's index (field 0x00); the root "Outlook Express" folder is 0 by default.</summary>
    public int Index => TryGetInt32(0x00, out int v) ? v : 0;

    /// <summary>The index of the parent folder (field 0x01); -1 for the root.</summary>
    public int ParentIndex => TryGetInt32(0x01, out int v) ? v : -1;

    /// <summary>The folder's name (field 0x02), or null.</summary>
    public string? Name => GetString(0x02);

    /// <summary>The name of the message database file that holds the folder (field 0x03, for example <c>Inbox.dbx</c>), or null for containers and accounts.</summary>
    public string? FileName => GetString(0x03);

    /// <summary>The folder flag word (field 0x06).</summary>
    public int Flags => TryGetInt32(0x06, out int v) ? v : 0;

    /// <summary>The number of messages the record states (field 0x07), or -1.</summary>
    public int MessageCount => TryGetInt32(0x07, out int v) ? v : -1;

    /// <summary>The number of unread messages the record states (field 0x08), or -1.</summary>
    public int UnreadCount => TryGetInt32(0x08, out int v) ? v : -1;

    /// <summary>
    /// The special-folder kind the notes place in field 0x09: 1 Inbox, 2 Outbox, 3 Sent, 4 Deleted, 5 Drafts, 0 none; -1 when absent.
    /// The notes mix this field with a per-folder index, so treat it as unverified.
    /// </summary>
    public int SpecialFolder => TryGetInt32(0x09, out int v) ? v : -1;
}

/// <summary>The size and defects of one message chain, from walking the segment headers without reading the text.</summary>
/// <param name="Length">The number of message bytes the chain holds.</param>
/// <param name="Segments">The number of segments.</param>
/// <param name="Defects">What was irregular in the chain.</param>
public readonly record struct DbxChainInfo(long Length, int Segments, StoreDefects Defects);
