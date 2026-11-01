using System.Globalization;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendaring;
using RecursiveAI.OneMail.Conversion;
using RecursiveAI.OneMail.Conversion.OxcIcal;
using RecursiveAI.OneMail.Mapi;
using Xunit;

namespace RecursiveAI.OneMail.Tests.Calendaring;

/// <summary>
/// Outlook series to iCalendar and back, over a grid of every pattern type (MS-OXOCAL 2.2.1.44.1), end kind, interval, weekday set, zone and
/// start day: the days the iCalendar expands to are the days the pattern walks, and importing the iCalendar gives the same pattern again.
/// </summary>
public sealed class OxcIcalGridTests
{
    private static readonly DateTime From = new(2024, 1, 1);
    private static readonly DateTime To = new(2031, 1, 1);

    private static readonly (string Name, TzRule Rule)[] Zones =
    [
        ("UTC", CalendarFixtures.UtcRule),
        ("Pacific Standard Time", CalendarFixtures.Pacific),
        ("AUS Eastern Standard Time", CalendarFixtures.Sydney),
    ];

    private static readonly DateTime[] Starts = [new(2025, 3, 9), new(2026, 1, 31), new(2024, 2, 29), new(2026, 11, 1)];

    private static readonly uint[] Masks =
    [
        0x02,   // Monday
        0x3E,   // Monday to Friday
        0x41,   // Saturday and Sunday
        0x7F,   // every day
        0x28,   // Tuesday and Thursday
    ];

    private static IEnumerable<(string Name, RecurrencePattern Pattern, bool ShortMonth)> Patterns(DateTime start)
    {
        (string, uint, uint, DateTime?)[] ends =
        [
            ("never", RecurrencePattern.EndNever, 0, null),
            ("count5", RecurrencePattern.EndAfterCount, 5, null),
            ("count13", RecurrencePattern.EndAfterCount, 13, null),
            ("until", RecurrencePattern.EndAfterDate, 0, start.AddDays(400)),
        ];
        foreach ((string en, uint et, uint count, DateTime? until) in ends)
        {
            foreach (int every in new[] { 1, 2, 3, 10 })
            {
                yield return ($"daily/{every}/{en}", CalendarFixtures.Daily(start, every, et, count, until), false);
            }

            foreach (int every in new[] { 1, 2, 5 })
            {
                foreach (uint mask in Masks)
                {
                    foreach (uint dow in new uint[] { 0, 1 })
                    {
                        // The first day of the week only matters for an interval above 1.
                        if (every == 1 && dow == 1)
                        {
                            continue;
                        }

                        yield return ($"weekly/{every}/{mask:X}/wk{dow}/{en}", CalendarFixtures.Weekly(start, every, mask, dow, et, count, until), false);
                    }
                }
            }

            foreach (int every in new[] { 1, 2, 3, 12 })
            {
                foreach (uint day in new uint[] { 1, 15, 28, 29, 30, 31 })
                {
                    yield return ($"monthly/{every}/day{day}/{en}", CalendarFixtures.Monthly(start, every, day, et, count, until), day is 29 or 30);
                }

                foreach (uint mask in new uint[] { 0x01, 0x02, 0x3E, 0x41, 0x7F })
                {
                    foreach (uint nth in new uint[] { 1, 2, 3, 4, 5 })
                    {
                        yield return ($"monthnth/{every}/{mask:X}/{nth}/{en}", CalendarFixtures.MonthlyNth(start, every, mask, nth, et, count, until), false);
                    }
                }
            }

            foreach (uint day in new uint[] { 1, 15, 28, 29, 31 })
            {
                // Outlook's yearly series sits in the month of its first day; the anchor is the start's own month.
                yield return ($"yearly/day{day}/{en}", CalendarFixtures.Yearly(start, day, et, count, until), day is 29 or 30 && day > DateTime.DaysInMonth(2001, start.Month));
            }

            foreach (uint mask in new uint[] { 0x01, 0x3E, 0x41 })
            {
                foreach (uint nth in new uint[] { 1, 3, 5 })
                {
                    yield return ($"yearnth/{mask:X}/{nth}/{en}", CalendarFixtures.YearlyNth(start, mask, nth, et, count, until), false);
                }
            }
        }
    }

    [Fact]
    public void EveryPatternKeepsItsDaysThroughTheICalendar()
    {
        int cases = 0, shortMonth = 0;
        List<string> failures = [];
        foreach ((string zn, TzRule rule) in Zones)
        {
            foreach (DateTime start in Starts)
            {
                foreach ((string name, RecurrencePattern p, bool short29) in Patterns(start))
                {
                    // A series whose start day the pattern does not allow starts on its first allowed day (reported as AmbiguousFirstInstance elsewhere).
                    if (OutlookRecurrence.FirstDay(p) is not { } first || first != start.Date)
                    {
                        continue;
                    }

                    cases++;
                    string id = $"{zn} {start:yyyy-MM-dd} {name}";
                    MapiMessage item = CalendarFixtures.Series(p, 9 * 60 + 30, 10 * 60 + 30, rule, zn, id);
                    var cal = CalendarFixtures.ToIcs(item, out LossReport report);
                    List<DateTime> expected = OutlookRecurrence.Days(p).TakeWhile(d => d <= To).Where(d => d >= From).ToList();
                    List<DateTime> actual = CalendarFixtures.Occurrences(cal, From, To.AddDays(1)).Select(d => d.Date).Where(d => d <= To).ToList();
                    if (short29)
                    {
                        shortMonth++;
                        // RFC 5545 skips the months that lack the day (and COUNT counts only the instances it keeps); Outlook uses their last day.
                        // The days differ by design, and the export says so.
                        if (!report.Has("PidLidAppointmentRecur"))
                        {
                            failures.Add(id + ": short-month case is not reported");
                        }

                        continue;
                    }

                    if (!expected.SequenceEqual(actual))
                    {
                        failures.Add($"{id}: days differ ({expected.Count} vs {actual.Count}; first {Describe(expected, actual)})");
                        continue;
                    }

                    // And back: the same pattern from the iCalendar.
                    MapiMessage again = new IcsToMapiConverter().Convert(cal).Value;
                    AppointmentRecurrencePattern? r2 = OutlookAppointment.TryCreate(again)?.Recurrence;
                    if (r2 is null)
                    {
                        failures.Add(id + ": import lost the recurrence");
                        continue;
                    }

                    string a = Shape(p), b = Shape(r2.Pattern);
                    if (a != b)
                    {
                        failures.Add($"{id}: pattern {a} came back as {b}");
                    }
                }
            }
        }

        Assert.True(cases > 1000, $"only {cases} cases");
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} cases failed (short-month cases {shortMonth}); first: " + string.Join(" | ", failures.Take(8)));
    }

    private static string Describe(List<DateTime> a, List<DateTime> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (a[i] != b[i])
            {
                return $"{i}: {a[i]:yyyy-MM-dd} vs {b[i]:yyyy-MM-dd}";
            }
        }

        return $"count {a.Count} vs {b.Count}";
    }

    // What a pattern says, without how the blob spells it.
    private static string Shape(RecurrencePattern p) => string.Create(CultureInfo.InvariantCulture,
        $"{p.PatternType}/{p.Period}/{(p.PatternType is 1 or 3 ? p.WeekdayMask : 0)}/{(p.PatternType == 2 ? p.DayOfMonth : 0)}/{(p.PatternType == 3 ? p.Nth : 0)}/{p.EndType}/{(p.EndType == RecurrencePattern.EndAfterCount ? p.OccurrenceCount : 0)}/{(p.PatternType == 1 && p.Period > 1 ? p.FirstDayOfWeek : 0)}");
}
