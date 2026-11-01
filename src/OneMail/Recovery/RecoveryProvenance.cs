namespace RecursiveAI.OneMail.Recovery;

/// <summary>
/// How a recovered item relates to the store's live structure. Forensic readers tag every item they hand out with one of these; nothing
/// that a normal (strict) reader would not list is ever tagged <see cref="Live"/>.
/// </summary>
/// <remarks>The members are documented per format in <c>docs/recovery.md</c>. New members are only ever appended.</remarks>
public enum RecoveryStatus
{
    /// <summary>The item is reachable from the store's live structure (index tree, node B-tree, zip directory or scan of intact entries) and its bytes are whole.</summary>
    Live = 0,

    /// <summary>The item is still linked into the structure but the store marks it deleted (a PST item in Deleted Items' hidden set, a flagged record). No DBX or OLM state is tagged this.</summary>
    SoftDeleted = 1,

    /// <summary>The item's content is intact and shaped like a live item, but nothing in the live structure reaches it any more.</summary>
    Orphaned = 2,

    /// <summary>The item was read out of space the store has freed (a free list, a free block): its structure was overwritten in part, so its defects say what is missing.</summary>
    FreeSpace = 3,

    /// <summary>Only part of the item could be recovered: a cut chain, a damaged or truncated archive entry. The ranges and defects say which part.</summary>
    Partial = 4,
}

/// <summary>A run of bytes in the source file.</summary>
/// <param name="Offset">The offset of the first byte, from the start of the file or archive the item came from.</param>
/// <param name="Length">The number of bytes; never negative.</param>
public readonly record struct ByteRange(long Offset, long Length)
{
    /// <summary>The offset just past the last byte.</summary>
    public long End => Offset + Length;

    /// <inheritdoc />
    public override string ToString() => Offset + "+" + Length;
}

/// <summary>
/// Where a recovered item came from and how far it can be trusted: a status, the byte ranges in the source it was read from, the ranges
/// that are known to be missing from it, and the defects that qualify it. One shape for every store: DBX and OLM today, PST and OST scan-mode
/// recovery through the same members (<see cref="Nid"/> and <see cref="Bid"/> carry the PST locators).
/// </summary>
public sealed class RecoveryProvenance
{
    private static readonly ByteRange[] NoRanges = [];
    private static readonly MailDefect[] NoDefects = [];

    /// <summary>The provenance of an item read in the ordinary way, with no ranges.</summary>
    public static RecoveryProvenance Live { get; } = new(RecoveryStatus.Live);

    /// <summary>Creates a provenance.</summary>
    /// <param name="status">The tag.</param>
    public RecoveryProvenance(RecoveryStatus status) => Status = status;

    /// <summary>The tag.</summary>
    public RecoveryStatus Status { get; }

    /// <summary>The container format of the source (<see cref="MailFormat.Dbx"/>, <see cref="MailFormat.Olm"/>, <see cref="MailFormat.Pst"/>).</summary>
    public MailFormat Format { get; init; }

    /// <summary>
    /// The ranges of the source file the item's bytes came from, sorted by offset and merged where they touch. Offsets are absolute in the
    /// file. When the item was rebuilt from pieces in a different order (a shuffled chain), the item's own order is the reader's to document. Empty when the item is not a range of the source (an item rebuilt from several places is listed by all of them).
    /// </summary>
    public IReadOnlyList<ByteRange> Ranges { get; init; } = NoRanges;

    /// <summary>The ranges inside <see cref="Ranges"/> (or just outside them) that held part of the item and could not be read back, such as the bytes a free list overwrote.</summary>
    public IReadOnlyList<ByteRange> Lost { get; init; } = NoRanges;

    /// <summary>The defects that make the tag what it is. <see cref="MailDefect.Offset"/> is an absolute offset in the source file, unlike the item's own defects.</summary>
    public IReadOnlyList<MailDefect> Defects { get; init; } = NoDefects;

    /// <summary>The PST node id of the item when the source is a PST or OST and the item has one; otherwise null.</summary>
    public uint? Nid { get; init; }

    /// <summary>The PST block id of the item's data block when known; otherwise null.</summary>
    public ulong? Bid { get; init; }

    /// <summary>One line saying why the item has this tag, for reports. Free text, not for matching.</summary>
    public string? Reason { get; init; }

    /// <summary>The total number of bytes in <see cref="Ranges"/>.</summary>
    public long TotalLength
    {
        get
        {
            long n = 0;
            foreach (ByteRange r in Ranges)
            {
                n += r.Length;
            }

            return n;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Status + (Ranges.Count == 0 ? string.Empty : " " + Ranges.Count + " range(s) at " + Ranges[0]);

    /// <summary>Sorts ranges by offset and merges those that touch or overlap, which is how <see cref="Ranges"/> is meant to be built.</summary>
    /// <param name="ranges">The ranges, in any order. Not changed.</param>
    /// <returns>The merged list.</returns>
    public static IReadOnlyList<ByteRange> Merge(IEnumerable<ByteRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var list = new List<ByteRange>(ranges);
        list.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        var merged = new List<ByteRange>(list.Count);
        foreach (ByteRange r in list)
        {
            if (r.Length <= 0)
            {
                continue;
            }

            if (merged.Count > 0 && r.Offset <= merged[^1].End)
            {
                ByteRange last = merged[^1];
                merged[^1] = new ByteRange(last.Offset, Math.Max(last.End, r.End) - last.Offset);
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged;
    }
}
