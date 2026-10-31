namespace RecursiveAI.OneMail.Mapi;

/// <summary>
/// Canonical names (MS-OXPROPS) for the properties the readers use, for diagnostics and tools. The table is a fixed static array: it is
/// not a registry of every property in the master list, and an ID that is missing here is simply unknown.
/// </summary>
public static partial class MapiPropertyNames
{
    /// <summary>The canonical name of a tagged property.</summary>
    /// <param name="id">The property ID (not a named property's local ID).</param>
    /// <returns>For example <c>PidTagSubject</c>, or null when the ID is not in the table.</returns>
    public static string? GetName(ushort id)
    {
        int i = Array.BinarySearch(s_ids, id);
        return i >= 0 ? s_names[i] : null;
    }

    /// <summary>The canonical name of a named property.</summary>
    /// <param name="property">The property set and numeric ID.</param>
    /// <returns>For example <c>PidLidAppointmentStartWhole</c>, or null when it is not in the table or is a string name.</returns>
    public static string? GetName(MapiNamedProperty property)
    {
        foreach ((MapiNamedProperty p, string name) in s_named)
        {
            if (p == property)
            {
                return name;
            }
        }

        return null;
    }
}
