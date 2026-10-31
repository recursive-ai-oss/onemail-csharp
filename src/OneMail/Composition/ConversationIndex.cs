using System.Buffers.Binary;

namespace RecursiveAI.OneMail.Composition;

/// <summary>One response level of a conversation index.</summary>
/// <param name="DeltaCode">MS-OXOMSG 2.2.1.3 DC: false when the time delta counts units of 2^18 FILETIME ticks (about 26 ms), true for units of 2^23 (about 0.84 s).</param>
/// <param name="TimeDelta">The 31-bit time delta in the units <paramref name="DeltaCode"/> selects.</param>
/// <param name="Random">The level's random byte.</param>
public readonly record struct ConversationIndexLevel(bool DeltaCode, int TimeDelta, byte Random)
{
    /// <summary>The time since the header's time that the level stands for (truncated to the unit).</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks((long)TimeDelta << (DeltaCode ? 23 : 18));
}

/// <summary>A decoded <c>PidTagConversationIndex</c>: the 22-byte header block and the five-byte response levels (MS-OXOMSG 2.2.1.3).</summary>
public sealed class ConversationIndexInfo
{
    internal ConversationIndexInfo(DateTimeOffset headerTime, Guid conversationId, IReadOnlyList<ConversationIndexLevel> levels)
    {
        HeaderTime = headerTime;
        ConversationId = conversationId;
        Levels = levels;
    }

    /// <summary>The time in the header, to the precision it is stored in (the top 48 bits of the FILETIME: about 6.5 milliseconds).</summary>
    public DateTimeOffset HeaderTime { get; }

    /// <summary>The GUID that names the conversation thread.</summary>
    public Guid ConversationId { get; }

    /// <summary>The response levels, oldest first. Each is one reply or forward deeper in the thread.</summary>
    public IReadOnlyList<ConversationIndexLevel> Levels { get; }
}

/// <summary>
/// Reads and extends conversation indexes as MS-OXOMSG 2.2.1.3 describes: a header block of the top 48 bits of a FILETIME (big-endian; its first byte is 0x01)
/// and a GUID, then one five-byte block per level: a delta code bit, a 31-bit time delta from the header's time, and a random byte.
/// </summary>
public static class ConversationIndex
{
    /// <summary>Length of the header block.</summary>
    public const int HeaderLength = 22;

    /// <summary>Length of one response level.</summary>
    public const int LevelLength = 5;

    /// <summary>The most bytes an index grows to; a longer one is not extended (an agent default: it fits a one-byte length).</summary>
    public const int MaxLength = 255;

    // FILETIME counts 100 ns ticks from 1601-01-01, which is .NET tick 504911232000000000.
    private const long FileTimeEpochTicks = 504911232000000000L;

    /// <summary>Decodes an index.</summary>
    /// <param name="index">The bytes of the property.</param>
    /// <param name="info">The decoded index when the result is true.</param>
    /// <returns>False when the bytes are shorter than a header, the first byte is not 0x01, or the levels do not divide evenly into five-byte blocks.</returns>
    public static bool TryParse(ReadOnlySpan<byte> index, out ConversationIndexInfo? info)
    {
        info = null;
        if (index.Length < HeaderLength || index[0] != 1 || (index.Length - HeaderLength) % LevelLength != 0)
        {
            return false;
        }

        // The first six bytes are the top 48 bits of the FILETIME. MS-OXOMSG presents the first byte as a reserved 0x01 followed by 40 bits,
        // but real indexes carry the time from the first byte on (0x01 is the FILETIME's own top byte for dates from 1829 to 2057), so the
        // six bytes are read together.
        long top48 = 0;
        for (int i = 0; i < 6; i++)
        {
            top48 = (top48 << 8) | index[i];
        }

        long ticks = (top48 << 16) + FileTimeEpochTicks;
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        // The GUID's Data1..Data3 are stored big-endian.
        var guid = new Guid(index.Slice(6, 16), bigEndian: true);
        int n = (index.Length - HeaderLength) / LevelLength;
        var levels = new ConversationIndexLevel[n];
        for (int k = 0; k < n; k++)
        {
            ReadOnlySpan<byte> b = index.Slice(HeaderLength + k * LevelLength, LevelLength);
            uint v = BinaryPrimitives.ReadUInt32BigEndian(b);
            levels[k] = new ConversationIndexLevel((v & 0x80000000u) != 0, (int)(v & 0x7FFFFFFFu), b[4]);
        }

        info = new ConversationIndexInfo(new DateTimeOffset(ticks, TimeSpan.Zero), guid, levels);
        return true;
    }

    /// <summary>Makes a header block for a new conversation thread.</summary>
    /// <param name="time">The time to store (UTC; times before 1601 store 0).</param>
    /// <param name="conversationId">The thread's GUID.</param>
    /// <returns>The 22 bytes.</returns>
    public static byte[] CreateHeader(DateTimeOffset time, Guid conversationId)
    {
        byte[] r = new byte[HeaderLength];
        // Only FILETIMEs whose top byte is 0x01 (1829 to 2057) fit the form; others are held at the nearest representable time.
        long ft = Math.Clamp(time.UtcTicks - FileTimeEpochTicks, 0x0100000000000000L, 0x01FFFFFFFFFFFFFFL);
        long top48 = ft >> 16;
        for (int i = 5; i >= 0; i--)
        {
            r[i] = (byte)top48;
            top48 >>= 8;
        }

        conversationId.TryWriteBytes(r.AsSpan(6, 16), bigEndian: true, out _);
        return r;
    }

    /// <summary>Adds one response level to an index.</summary>
    /// <param name="index">A valid index (see <see cref="TryParse"/>).</param>
    /// <param name="now">The time of the new message.</param>
    /// <param name="random">The level's random byte.</param>
    /// <param name="clamped">True when <paramref name="now"/> is before the header's time and the delta was set to zero.</param>
    /// <returns>The index with the new level, or null when the index is invalid or already at <see cref="MaxLength"/>.</returns>
    public static byte[]? AppendLevel(ReadOnlySpan<byte> index, DateTimeOffset now, byte random, out bool clamped)
    {
        clamped = false;
        if (!TryParse(index, out ConversationIndexInfo? info) || index.Length + LevelLength > MaxLength)
        {
            return null;
        }

        long headerFt = info!.HeaderTime.UtcTicks - FileTimeEpochTicks;
        long diff = (now.UtcTicks - FileTimeEpochTicks) - headerFt;
        if (diff < 0)
        {
            diff = 0;
            clamped = true;
        }

        // MS-OXOMSG 2.2.1.3: when the high 32 bits of the difference AND 0x00FE0000 are zero (under about 1.7 years) the delta is bits 18..48,
        // otherwise bits 23..53 with the delta code set.
        bool large = ((uint)(diff >> 32) & 0x00FE0000u) != 0;
        uint delta = (uint)((diff >> (large ? 23 : 18)) & 0x7FFFFFFF);
        byte[] r = new byte[index.Length + LevelLength];
        index.CopyTo(r);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(index.Length), delta | (large ? 0x80000000u : 0u));
        r[^1] = random;
        return r;
    }
}
