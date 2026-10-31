# Recurrence expansion and time zones

Normative notes for `RecursiveAI.OneMail.Calendar.Recurrence` and `.TimeZones`. The entry points are
`CalendarRecurrence.Expand(calendar, window, options, report)` (a VCALENDAR with its events, VTIMEZONEs and overrides) and
`RecurrenceSet` (one master component plus its overrides). Expansion reads the component trees and never changes them, so
untouched ICS still writes back byte-identical. It never throws on content: every repair, guess or cut-off is a typed
`RecurrenceDefect` collected in an `ExpansionReport`.

Sources: RFC 5545 §3.3.5, §3.3.10, §3.8.5.1–3.8.5.3, §3.6.5 (VTIMEZONE); RFC 7808 (`TZID-ALIAS-OF`); RFC 7529 (RSCALE, refused).
Windows zone names come from the Unicode CLDR `windowsZones.xml` (see THIRD-PARTY-NOTICES).

## What an occurrence is

An `Occurrence` has the start and end as the component writes them (DATE, floating, UTC, or local time in its zone), the UTC
instants where there is one (`StartUtc`/`EndUtc` are null for dates, floating times and unresolved zones), the origin (`Start`,
`Rule`, `Date`, `Period`, `Override`, `FutureOverride`) and the component it came from. Occurrences come in start order.

## The rule engine

The engine follows the expansion table of RFC 5545 §3.3.10 period by period (year, month, week, day, hour, minute, second, in
steps of `INTERVAL`): a BYxxx part expands where the table says *Expand* and filters where it says *Limit*. BYSETPOS selects
within the instants of one period after the other parts. `WKST` fixes the week for `WEEKLY` and `BYWEEKNO`. Negative BYxxx
values count from the end. `COUNT` counts rule instants before EXDATE/EXRULE removal, so `COUNT=4` with one EXDATE gives three
occurrences. `UNTIL` is inclusive: a UTC UNTIL is an instant; a DATE UNTIL covers its whole day; a local UNTIL is a wall-clock
value (so a UTC UNTIL on a floating series is compared as if the wall clock were UTC).

Defaults and choices (each is provisional until a client is observed; the ones where real implementations disagree
have an entry in `ambiguities.md`, section "Recurrence and time zones"):

- **DTSTART is always the first instance**, even when it does not match its rule (RFC 5545 §3.8.5.3 leaves that undefined).
  Defect `AmbiguousUnsynchronizedStart` (R3). It counts toward `COUNT`.
- **Parts that the table marks N/A** for the frequency (for example `BYWEEKNO` with `MONTHLY`) filter, not expand (R2).
- **A BYDAY ordinal where it is not allowed** (not MONTHLY/YEARLY, or with BYWEEKNO) is ignored: `OrdinalIgnored`.
- **`BYWEEKNO` with no day part** takes the weekday of DTSTART (R1).
- **Weekly `BYSETPOS`** counts in the whole week, including the part before DTSTART in the first week (R7).
- **`EXDATE` as a DATE on a date-time series** removes every occurrence on that day (R6, `AmbiguousExdateType`).
- **`UNTIL` of another value type than DTSTART** is compared as described above (R4, `AmbiguousUntilType`).
- **Sub-day frequencies** (`HOURLY`, `MINUTELY`, `SECONDLY`) step in wall-clock time of the series' zone. Two wall times that
  land on one instant (the repeated hour) stay two occurrences.
- **A rule with a time-of-day frequency, or BYHOUR/BYMINUTE/BYSECOND, on a DATE start** keeps dates and collapses repeats:
  `TimeRuleOnDate`.
- **`RSCALE` other than GREGORIAN** is not expanded: `UnsupportedRScale`. An unparseable rule or an out-of-range part
  (`BYMONTHDAY=0`) ignores that rule: `InvalidRule`.
- **EXRULE** (obsolete, RFC 2445) is honoured, with RDATE and EXDATE, in the order RFC 5545 §3.8.5.3 gives.
- **Overrides** (`RECURRENCE-ID`) replace the occurrence they name; `RANGE=THISANDFUTURE` moves the later ones by the same
  shift (`FutureOverride`). An override whose master is missing is yielded alone (`OrphanOverride`); two overrides for one
  instance: the later in the file wins (`DuplicateOverride`).
- **Durations** follow §3.8.5.3: an exact length from DTEND/DUE; nominal days plus an exact time part from DURATION, so a
  one-day event across a clock change ends at the same wall time.

## Daylight-saving transitions

For a zoned series (`DTSTART;TZID=…`) the local time of each instance is converted to an instant through the zone:

| Local time | Policy |
|---|---|
| Exists once | converted as is |
| Skipped (the hour clocks jump over) | **Shift** (default): read with the offset before the gap, so 02:30 becomes 03:30 (RFC 5545 §3.3.5). `SkippedTimePolicy.Skip` drops it without counting it against COUNT (the strict reading of §3.3.10). Defect `AmbiguousSkippedLocalTime` per occurrence (R5) |
| Happens twice | the first one is used (RFC 5545 §3.3.5). Defect `AmbiguousRepeatedLocalTime` per occurrence (R5) |

Inside a skipped hour, occurrences generated from different wall times may map out of instant order; the output is ordered
by the instant it computes, ties by generation order.

## Windows

`OccurrenceWindow` is half-open `[Start, End)` in UTC. `WindowMode.Overlapping` (default) returns occurrences that start before
the end and finish after the start (a zero-length one must start inside); `StartingIn` returns those that start inside.
Floating times and DATE values have no instant, so they are compared by their wall-clock value as if it were UTC.
Rules without `COUNT` jump to the window instead of walking from DTSTART (the result is the same); rules with `COUNT` must
generate the instances before the window.

## Limits (on by default, in `ExpansionOptions`)

| Limit | Default | When exceeded |
|---|---|---|
| `MaxOccurrences` | 100,000 | enumeration ends, `OccurrenceLimit` |
| `MaxIterationsWithoutHit` | 1,000,000 | that rule ends, `IterationLimit` (rules that never match, like `BYMONTH=2;BYMONTHDAY=30`, end here or at year 9999) |
| `MaxInstancesScanned` | 5,000,000 | that rule ends, `ScanLimit` (instances generated before the window, for rules with COUNT) |

A rule that can never match still yields DTSTART (R3) and then ends at a limit.

## Time zones

`TimeZoneResolver.Resolve(tzid)` takes the first hit of this list, and records `UnknownTimeZone` once per TZID if there is none:

1. **A VTIMEZONE in the file** with that `TZID` (`ZoneSource.VTimeZone`). Its STANDARD/DAYLIGHT observances (with their own
   RRULE/RDATE) are expanded, as are fixed ones. A VTIMEZONE without usable observances is `InvalidTimeZone`; then
   `TZID-ALIAS-OF` (RFC 7808) is tried.
2. **UTC names** (`UTC`, `Etc/UTC`, `GMT`, `Z`, …).
3. **The runtime's IANA database** (`ZoneSource.Iana`), when `UseRuntimeDatabase` is true (default).
4. **A vendor-prefixed id** (`/softwarestudio.org/Olson_20011030_5/America/New_York`): the longest tail of up to three segments
   found in the runtime database. Defect `AmbiguousZoneName`.
5. **A Windows zone name** (`Eastern Standard Time`), through the committed CLDR table to the default-territory IANA zone
   (`ZoneSource.WindowsName`). Defect `AmbiguousWindowsZone`: the same name can mean different IANA zones by region, and the
   table keeps one. Off with `UseWindowsNames = false`. This step runs *before* step 3 for any name in the table, so the result
   is the same whether the runtime knows Windows ids (Windows) or not (Linux, macOS).

Every runtime lookup (steps 3 to 5) asks `TimeZoneInfo` for the name first. If the runtime does not know an IANA name, as on
Windows without ICU (and `InvariantGlobalization` removes ICU), it asks again under the Windows zone id CLDR assigns that IANA name
(the default-territory row first, then a territory row; 444 IANA names). The zone keeps its IANA `Id` and `ZoneSource.Iana`, with no
defect. A name known to neither side is `UnknownTimeZone` (Z4), never an exception. Names that are IANA aliases CLDR does not list
(`US/Eastern`) resolve on Windows only if the runtime itself knows them.

**Windows zone data is thinner than IANA's.** The Windows registry carries the current rules and a few years of history, not IANA's
full past: US dates before 2007 use today's rules (1997's changes fall on the wrong days), and zones that changed rules long ago
differ similarly. Expansion of a recurring event over such dates on Windows can therefore differ in UTC instants (never in wall-clock
values) from the same file on Linux or macOS. A VTIMEZONE in the file always wins and gives the file's own history; use it, or run
without the runtime database (`UseRuntimeDatabase = false`), where results must not depend on the host.

An unresolvable TZID leaves the series floating (no instants are claimed). Names that would reach the machine's own settings
(`localtime`, `posixrules`, `posix/…`, `right/…`), paths and names with other characters than real zone names use are refused
before any lookup.

A VTIMEZONE in the file beats the database: the file says what its author meant, and a Windows-style custom zone may differ
from IANA. If the file has none, expansion falls back to the database and says so with the defects above.

**Browser (WASM).** The runtime's database may be absent in the browser. Lookups are guarded by try/catch and return "unknown",
never an exception. Set `TimeZones = new TimeZoneResolverOptions { UseRuntimeDatabase = false }` there and rely on the file's
VTIMEZONEs; the AOT smoke app exercises that configuration. The browser smoke app also expands a rule through the runtime's database (`America/New_York` across the spring-forward day)
and through a VTIMEZONE; both give the same instants in headless Chromium, so the database is present in the browser runtime as built.

## Method of the differential check

`tools/calendar/run_expand_diff.sh` expands every recurring component of the calendar corpus (see `tools/calendar/README.md`) and of
the committed generated fixtures in a fixed window, with ours (`RecurrenceDump expand`) and three oracles run as black-box
processes, and compares the sorted start lists (`compare_expand.py`):

- Python `recurring-ical-events` (on `icalendar`; the one that applies RECURRENCE-ID and EXDATE/RDATE like a calendar client),
- Python `python-dateutil` `rrule` driven by our glue (`py_expand.py du`: a rule engine with no overrides or EXRULE),
- Ical.Net, from its NuGet package (`tools/calendar/oracles/OracleExpand`), public API only.

Versions and licences are in THIRD-PARTY-NOTICES. Components an oracle cannot expand (errors, timeouts) are counted and never
compared. Every remaining difference must get an id from `compare_expand.py`, defined in the section "Recurrence and time zones"
of `ambiguities.md` (R1–R7 resolve a documented ambiguity; `L-*` are limits of one oracle). Anything left unexplained is a bug.
The generated grid (`tools/calendar/make_recurrence_fixtures.py`, fixed seed) holds every subset of BYxxx parts per frequency,
DST edges, window and override cases; RFC 5545 §3.8.5.3's examples run as unit tests (`RecurrenceRfcTests`).

`RecurrenceDump rate` and `RecurrenceDump ratepure` measure throughput and allocations per occurrence; `fuzz/run.sh recurrence`
fuzzes rule parsing and expansion with small limits.
