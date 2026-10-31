using System.Collections;

namespace RecursiveAI.OneMail.Mapi;

/// <summary>
/// The properties of one MAPI object (a message, recipient or attachment) in the order the file or store listed them.
/// The same type serves MSG, PST and TNEF; it keeps duplicates and unknown types so that nothing a file said is lost.
/// </summary>
/// <remarks>
/// Lookups scan the list, which is meant for the few accessor calls a reader makes; the list itself is the unit of enumeration.
/// When a tag occurs more than once, lookups return the last occurrence (a later value overrides an earlier one, as sequential
/// property setting would); the earlier ones stay in the list.
/// </remarks>
public sealed class MapiPropertyBag : IReadOnlyList<MapiProperty>
{
    private readonly List<MapiProperty> _items = [];

    /// <summary>Changes whenever a property is added, replaced or removed; lets callers cache what they derived from the bag.</summary>
    internal int Version { get; private set; }

    /// <summary>The number of properties, duplicates included.</summary>
    public int Count => _items.Count;

    /// <summary>The property at a position in file order.</summary>
    /// <param name="index">The zero-based position.</param>
    /// <returns>The property.</returns>
    public MapiProperty this[int index] => _items[index];

    /// <summary>Appends a property.</summary>
    /// <param name="property">The property; not null.</param>
    public void Add(MapiProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        _items.Add(property);
        Version++;
    }

    /// <summary>Replaces every property with the same tag by this one, or appends it when there is none.</summary>
    /// <param name="property">The property; not null.</param>
    public void Set(MapiProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        bool placed = false;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Tag == property.Tag)
            {
                if (!placed)
                {
                    _items[i] = property;
                    placed = true;
                }
                else
                {
                    _items.RemoveAt(i--);
                }
            }
        }

        if (!placed)
        {
            _items.Add(property);
        }

        Version++;
    }

    /// <summary>Removes every property with the tag.</summary>
    /// <param name="tag">The tag to remove.</param>
    /// <returns>True when something was removed.</returns>
    public bool Remove(MapiTag tag)
    {
        Version++;
        return _items.RemoveAll(p => p.Tag == tag) > 0;
    }

    /// <summary>Finds a property by its exact tag (ID and type).</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The last property with that tag, or null.</returns>
    public MapiProperty? Get(MapiTag tag)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Tag == tag)
            {
                return _items[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a property by ID. When several types share the ID, a Unicode string wins over an 8-bit string, and otherwise the
    /// last one in file order is returned.
    /// </summary>
    /// <param name="id">The property ID.</param>
    /// <returns>The property, or null.</returns>
    public MapiProperty? Get(ushort id)
    {
        MapiProperty? best = null;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            MapiProperty p = _items[i];
            if (p.Id != id)
            {
                continue;
            }

            if (p.Type == MapiType.UnicodeString)
            {
                return p;
            }

            best ??= p;
            if (p.Type == MapiType.String8)
            {
                best = p;
            }
        }

        return best;
    }

    /// <summary>Finds a property by ID, requiring one of the given types and preferring them in the order given.</summary>
    /// <param name="id">The property ID.</param>
    /// <param name="types">The acceptable types, best first.</param>
    /// <returns>The property, or null.</returns>
    public MapiProperty? Get(ushort id, params ReadOnlySpan<MapiType> types)
    {
        foreach (MapiType t in types)
        {
            if (Get(new MapiTag(id, t)) is { } p)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>Finds a named property by its name.</summary>
    /// <param name="name">The property set and numeric ID or string.</param>
    /// <param name="type">The type to require, or null for any (Unicode strings preferred, as for IDs).</param>
    /// <returns>The last matching property, or null.</returns>
    public MapiProperty? Get(MapiNamedProperty name, MapiType? type = null)
    {
        MapiProperty? best = null;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            MapiProperty p = _items[i];
            if (p.Name != name || (type is { } t && p.Type != t))
            {
                continue;
            }

            if (p.Type == MapiType.UnicodeString)
            {
                return p;
            }

            best ??= p;
        }

        return best;
    }

    /// <inheritdoc/>
    public IEnumerator<MapiProperty> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
