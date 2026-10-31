using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;

namespace RecursiveAI.OneMail.Calendar.TimeZones;

/// <summary>Windows zone names (the names Outlook writes as <c>TZID</c>) mapped to IANA names with CLDR's default-territory rows.</summary>
internal static partial class WindowsZones
{
    // Built on first use: the generated Rows live in another part of this class, and static initialisers of the parts have no fixed order.
    private static Dictionary<string, string>? s_map;

    private static Dictionary<string, string> Build()
    {
        Dictionary<string, string> d = new(Rows.Length, StringComparer.OrdinalIgnoreCase);
        foreach ((string windows, string iana) in Rows)
        {
            d[windows] = iana;
        }

        return d;
    }

    /// <summary>Looks up a Windows zone name, ignoring case.</summary>
    internal static bool TryGetIana(string windowsName, out string iana) => (s_map ??= Build()).TryGetValue(windowsName, out iana!);
}

/// <summary>What <see cref="TimeZoneResolver"/> may use besides the file's own VTIMEZONE components.</summary>
public sealed record TimeZoneResolverOptions
{
    /// <summary>
    /// Look zone names up in the runtime's time zone database (<see cref="TimeZoneInfo"/>). Default true. Turn it off for output
    /// that must not depend on the machine's tz database, or where there is none; the file's VTIMEZONE components still work.
    /// </summary>
    public bool UseRuntimeDatabase { get; init; } = true;

    /// <summary>Map Windows zone names (<c>Eastern Standard Time</c>) to IANA names before the runtime lookup. Default true.</summary>
    public bool UseWindowsNames { get; init; } = true;
}

/// <summary>
/// Resolves a <c>TZID</c> to a zone, in this order: a VTIMEZONE in the calendar (RFC 5545 §3.6.5, with RFC 7808
/// <c>TZID-ALIAS-OF</c> aliases); then an IANA name in the runtime's database, also after stripping a vendor path prefix
/// such as <c>/softwarestudio.org/Olson_20011030_5/</c>; then a Windows name through the CLDR table. A name that none of
/// them knows returns null and is reported as <see cref="RecurrenceDefectKind.UnknownTimeZone"/>; callers keep such values
/// floating. Results are cached; the resolver is not thread-safe.
/// </summary>
public sealed class TimeZoneResolver
{
    private readonly Dictionary<string, IcsComponent> _vtimezones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IcsTimeZone?> _cache = new(StringComparer.Ordinal);
    private readonly TimeZoneResolverOptions _options;
    private readonly ExpansionReport _report;

    /// <summary>Creates a resolver.</summary>
    /// <param name="calendar">A VCALENDAR whose VTIMEZONE children define zones, or null for none (only the runtime database is used).</param>
    /// <param name="options">Lookup options; null for the defaults.</param>
    /// <param name="report">Receives defects; null creates a private report.</param>
    public TimeZoneResolver(IcsComponent? calendar = null, TimeZoneResolverOptions? options = null, ExpansionReport? report = null)
    {
        _options = options ?? new TimeZoneResolverOptions();
        _report = report ?? new ExpansionReport();
        if (calendar is not null)
        {
            foreach (IcsComponent tz in calendar.TimeZones)
            {
                if (tz.TimeZoneId is { } id)
                {
                    _vtimezones.TryAdd(id.Trim(), tz);
                }
            }
        }
    }

    /// <summary>The defects found while resolving.</summary>
    public ExpansionReport Report => _report;

    /// <summary>The TZIDs defined by VTIMEZONE components, in no particular order.</summary>
    public IReadOnlyCollection<string> DefinedZones => _vtimezones.Keys;

    /// <summary>Resolves a TZID.</summary>
    /// <param name="tzid">The <c>TZID</c> parameter value, with or without the RFC 5545 leading solidus for globally unique ids.</param>
    /// <returns>The zone, or null when it cannot be resolved (a defect is recorded once per TZID).</returns>
    public IcsTimeZone? Resolve(string? tzid)
    {
        if (string.IsNullOrWhiteSpace(tzid))
        {
            return null;
        }

        string id = tzid.Trim();
        if (_cache.TryGetValue(id, out IcsTimeZone? cached))
        {
            return cached;
        }

        IcsTimeZone? zone = ResolveCore(id, 0);
        if (zone is null)
        {
            _report.Add(RecurrenceDefectKind.UnknownTimeZone, id);
        }

        _cache[id] = zone;
        return zone;
    }

    private IcsTimeZone? ResolveCore(string id, int depth)
    {
        if (_vtimezones.TryGetValue(id, out IcsComponent? vtz))
        {
            VTimeZoneZone? z = VTimeZoneZone.TryCreate(vtz, id);
            if (z is not null)
            {
                return z;
            }

            // RFC 7808 §5.2: TZID-ALIAS-OF names the zone this one stands for.
            if (vtz.GetText("TZID-ALIAS-OF") is { } target && depth < 4 && !string.Equals(target.Trim(), id, StringComparison.Ordinal))
            {
                IcsTimeZone? aliased = ResolveCore(target.Trim(), depth + 1);
                if (aliased is not null)
                {
                    return aliased;
                }
            }

            _report.Add(RecurrenceDefectKind.InvalidTimeZone, id);
        }

        if (IsUtcName(id))
        {
            return UtcTimeZone.Instance;
        }

        if (!_options.UseRuntimeDatabase)
        {
            return null;
        }

        if (TryRuntime(id, ZoneSource.Iana) is { } iana)
        {
            return iana;
        }

        if (id.StartsWith('/'))
        {
            string[] segments = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int take = Math.Min(3, segments.Length); take >= 1; take--)
            {
                string tail = string.Join('/', segments[^take..]);
                if (TryRuntime(tail, ZoneSource.Iana) is { } stripped)
                {
                    _report.Add(RecurrenceDefectKind.AmbiguousZoneName, id);
                    return stripped;
                }
            }
        }

        if (_options.UseWindowsNames && WindowsZones.TryGetIana(id, out string? mapped) && TryRuntime(mapped, ZoneSource.WindowsName) is { } win)
        {
            _report.Add(RecurrenceDefectKind.AmbiguousWindowsZone, id);
            return win;
        }

        return null;
    }

    private static bool IsUtcName(string id) =>
        id.Equals("UTC", StringComparison.OrdinalIgnoreCase) || id.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase)
        || id.Equals("GMT", StringComparison.OrdinalIgnoreCase) || id.Equals("Etc/GMT", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Z", StringComparison.OrdinalIgnoreCase) || id.Equals("Etc/Zulu", StringComparison.OrdinalIgnoreCase);

    // Zone names reach the file system on some platforms, so only the characters real names use are passed on.
    private static bool LooksLikeZoneName(string name)
    {
        if (name.Length is 0 or > 100 || name.Contains("..", StringComparison.Ordinal) || name[0] == '/')
        {
            return false;
        }

        // Files in the database directory that name the machine's own settings rather than a zone: results would depend on the host.
        if (name.Equals("localtime", StringComparison.OrdinalIgnoreCase) || name.Equals("posixrules", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("posix/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("right/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+' or ' ' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static RuntimeTimeZone? TryRuntime(string name, ZoneSource source)
    {
        if (!LooksLikeZoneName(name))
        {
            return null;
        }

        try
        {
            return new RuntimeTimeZone(name, source, TimeZoneInfo.FindSystemTimeZoneById(name));
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
