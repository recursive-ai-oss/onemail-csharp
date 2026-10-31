using System.Collections;

namespace RecursiveAI.OneMail.Conversion;

/// <summary>How a piece of the source fared in a conversion.</summary>
public enum LossKind
{
    /// <summary>The value was not carried: the target format has nowhere to put it.</summary>
    Dropped = 0,

    /// <summary>The value was carried in a weaker form (a time zone reduced to UTC, a group flattened to its members, a type changed).</summary>
    Approximated,

    /// <summary>The conversion had two sources for one value and the other one won; the entry names the one that was set aside.</summary>
    Overridden,

    /// <summary>Store bookkeeping that has no meaning outside its store (entry IDs, search keys, access flags). Listed so that nothing is silent, but it is not a loss of content.</summary>
    Internal,
}

/// <summary>
/// One thing that did not carry over, or carried over changed. Entries are data, meant to be counted, filtered and shown; the
/// <see cref="Reason"/> is for people.
/// </summary>
/// <param name="Family">The format the thing belongs to in the source: <c>mapi</c> for a property, <c>mime</c> for a header or part.</param>
/// <param name="Name">The property (<c>PidTagSubject</c>, or <c>0x1234001F</c> when it has no canonical name; a named property as its set and name) or header field name.</param>
/// <param name="Kind">What happened to it.</param>
/// <param name="Code">A short stable identifier of the rule that applied, listed in <c>docs/conversion.md</c>.</param>
/// <param name="Reason">A sentence saying why.</param>
/// <param name="Path">Where in the item: empty for the message itself, otherwise <c>attachment[2]</c>, <c>recipient</c>, <c>attachment[0]/message</c> and so on.</param>
public sealed record LossEntry(string Family, string Name, LossKind Kind, string Code, string Reason, string Path = "")
{
    /// <summary>True for every kind except <see cref="LossKind.Internal"/>.</summary>
    public bool IsContentLoss => Kind != LossKind.Internal;
}

/// <summary>
/// What a conversion could not carry over. Conversions always return one, empty when everything carried. Reports from nested
/// items (an embedded message, an attachment) are merged into the parent's with their <see cref="LossEntry.Path"/> prefixed.
/// </summary>
public sealed class LossReport : IReadOnlyList<LossEntry>
{
    private readonly List<LossEntry> _entries = [];
    private readonly HashSet<LossEntry> _seen = [];

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>The entry at <paramref name="index"/>, in the order the conversion found them.</summary>
    /// <param name="index">The position.</param>
    public LossEntry this[int index] => _entries[index];

    /// <summary>True when any entry is more than store bookkeeping.</summary>
    public bool HasContentLoss => _entries.Exists(e => e.IsContentLoss);

    /// <summary>Adds an entry unless an identical one is already there (the same property reported twice for one object is one fact).</summary>
    /// <param name="entry">The entry.</param>
    public void Add(LossEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_seen.Add(entry))
        {
            _entries.Add(entry);
        }
    }

    /// <summary>Adds an entry.</summary>
    /// <param name="family">See <see cref="LossEntry.Family"/>.</param>
    /// <param name="name">See <see cref="LossEntry.Name"/>.</param>
    /// <param name="kind">See <see cref="LossEntry.Kind"/>.</param>
    /// <param name="code">See <see cref="LossEntry.Code"/>.</param>
    /// <param name="reason">See <see cref="LossEntry.Reason"/>.</param>
    /// <param name="path">See <see cref="LossEntry.Path"/>.</param>
    public void Add(string family, string name, LossKind kind, string code, string reason, string path = "") =>
        Add(new LossEntry(family, name, kind, code, reason, path));

    /// <summary>Adds every entry of a nested item's report, with <paramref name="pathPrefix"/> in front of each path.</summary>
    /// <param name="inner">The nested report.</param>
    /// <param name="pathPrefix">The nested item's own path, for example <c>attachment[1]/message</c>.</param>
    public void Merge(LossReport inner, string pathPrefix)
    {
        ArgumentNullException.ThrowIfNull(inner);
        foreach (LossEntry e in inner)
        {
            Add(e with { Path = e.Path.Length == 0 ? pathPrefix : pathPrefix + "/" + e.Path });
        }
    }

    /// <summary>Whether the report has an entry for <paramref name="name"/> (case-insensitive) at <paramref name="path"/>.</summary>
    /// <param name="name">The property or header name.</param>
    /// <param name="path">The path; empty for the message itself.</param>
    /// <returns>True when there is one.</returns>
    public bool Has(string name, string path = "") =>
        _entries.Exists(e => e.Path == path && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public IEnumerator<LossEntry> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>The outcome of a conversion: the new item and what did not carry over.</summary>
/// <typeparam name="T">The converted item's type.</typeparam>
public sealed class ConversionResult<T>
{
    /// <summary>Creates a result.</summary>
    /// <param name="value">The converted item.</param>
    /// <param name="report">The loss report.</param>
    public ConversionResult(T value, LossReport report)
    {
        Value = value;
        Report = report;
    }

    /// <summary>The converted item. A conversion always produces one; malformed input is repaired, not refused.</summary>
    public T Value { get; }

    /// <summary>What did not carry over; empty when everything did.</summary>
    public LossReport Report { get; }
}

/// <summary>
/// A conversion between two item models. Conversions are explicit: nothing in OneMail converts behind the caller's back, and every
/// conversion says what it left behind. Calendar, contact and store-to-store conversions implement this too.
/// </summary>
/// <typeparam name="TSource">The model converted from.</typeparam>
/// <typeparam name="TResult">The model converted to.</typeparam>
public interface IItemConversion<in TSource, TResult>
{
    /// <summary>Converts one item. Never throws for malformed content; throws only for a null argument.</summary>
    /// <param name="source">The item to convert; it is not changed.</param>
    /// <returns>The new item and the loss report.</returns>
    ConversionResult<TResult> Convert(TSource source);
}
