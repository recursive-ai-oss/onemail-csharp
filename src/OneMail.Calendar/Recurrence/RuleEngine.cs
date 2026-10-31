using RecursiveAI.OneMail.Calendar.Ics;

namespace RecursiveAI.OneMail.Calendar.Recurrence;

/// <summary>
/// Generates the wall-clock instants of one recurrence rule (RFC 5545 §3.3.10) in ascending order, period by period.
/// It knows nothing about zones: a period is a year, month, week, day, hour, minute or second counted from DTSTART in
/// steps of INTERVAL, the BYxxx parts expand or limit the period as the table in §3.3.10 says, and BYSETPOS picks from what
/// is left. Invalid dates (February 30, a fifth Monday in a four-Monday month) are never generated, so they are never counted.
/// </summary>
internal sealed class RuleEngine
{
    private const long TicksPerDay = TimeSpan.TicksPerDay;
    private const long TicksPerHour = TimeSpan.TicksPerHour;
    private const long TicksPerMinute = TimeSpan.TicksPerMinute;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    // Last tick the engine will generate from: it stops a week short of 9999-12-31 so period arithmetic never overflows.
    private static readonly long MaxTicks = DateTime.MaxValue.Ticks - (8 * TicksPerDay);

    private readonly RecurrenceFrequency _freq;
    private readonly int _interval;
    private readonly int _wkst;
    private readonly DateTime _dt;
    private readonly long _dtTicks;
    private readonly long _maxEmptyPeriods;

    private readonly bool[] _months = new bool[13];
    private readonly bool _hasMonths;
    private readonly int[] _monthDays;
    private readonly int[] _yearDays;
    private readonly int[] _weekNos;
    private readonly bool[] _plainDays = new bool[7];
    private readonly (int Ordinal, int Day)[] _ordinalDays;
    private readonly bool _hasWeekdays;
    private readonly bool _ordinalInMonth;
    private readonly int[] _setPos;
    private readonly long[] _timesOfDay = [];
    private readonly int[] _hours;
    private readonly int[] _minutes;
    private readonly int[] _seconds;
    private readonly bool _limitHour;
    private readonly bool _limitMinute;
    private readonly bool _limitSecond;
    private readonly bool _isDate;

    private readonly List<long> _buf = new(64);
    private readonly List<long> _tmp = new(64);
    private readonly List<long> _days = new(64);
    private long _n;
    private int _pos;
    private long _emptyRun;

    // Week-numbering cache (BYWEEKNO): the day number on which week 1 starts, for the year before, of and after _wkYear.
    private int _wkYear = -1;
    private long _w1Prev;
    private long _w1Cur;
    private long _w1Next;

    private RuleEngine(RecurrenceRule rule, DateTime dtstart, bool isDate, long maxEmptyPeriods, ExpansionReport report, string ruleText)
    {
        _freq = rule.Frequency;
        _interval = rule.Interval;
        _wkst = (int)(rule.WeekStart ?? DayOfWeek.Monday);
        _dt = dtstart;
        _dtTicks = dtstart.Ticks;
        _isDate = isDate;
        _maxEmptyPeriods = maxEmptyPeriods;

        foreach (int m in rule.ByMonth)
        {
            _months[m] = true;
            _hasMonths = true;
        }

        _monthDays = Sorted(rule.ByMonthDay);
        _yearDays = Sorted(rule.ByYearDay);
        _weekNos = Sorted(rule.ByWeekNo);
        _setPos = Sorted(rule.BySetPos);

        bool ordinalsAllowed = _freq == RecurrenceFrequency.Monthly || (_freq == RecurrenceFrequency.Yearly && rule.ByWeekNo.Count == 0);
        _ordinalInMonth = _freq == RecurrenceFrequency.Monthly || (_freq == RecurrenceFrequency.Yearly && _hasMonths);
        List<(int, int)> ordinals = [];
        bool ignored = false;
        foreach (WeekdayNumber wd in rule.ByDay)
        {
            if (wd.Ordinal is { } o)
            {
                if (ordinalsAllowed)
                {
                    ordinals.Add((o, (int)wd.Day));
                    continue;
                }

                ignored = true;
            }

            _plainDays[(int)wd.Day] = true;
        }

        if (ignored)
        {
            report.Add(RecurrenceDefectKind.OrdinalIgnored, ruleText);
        }

        _ordinalDays = [.. ordinals];
        _hasWeekdays = rule.ByDay.Count > 0;

        // RFC 5545 §3.3.10: what the rule leaves out is taken from DTSTART.
        bool daySpec = rule.ByWeekNo.Count > 0 || rule.ByYearDay.Count > 0 || rule.ByMonthDay.Count > 0 || rule.ByDay.Count > 0;
        if (!daySpec)
        {
            switch (_freq)
            {
                case RecurrenceFrequency.Yearly:
                    if (!_hasMonths)
                    {
                        _months[dtstart.Month] = true;
                        _hasMonths = true;
                    }

                    _monthDays = [dtstart.Day];
                    break;
                case RecurrenceFrequency.Monthly:
                    _monthDays = [dtstart.Day];
                    break;
                case RecurrenceFrequency.Weekly:
                    _plainDays[(int)dtstart.DayOfWeek] = true;
                    _hasWeekdays = true;
                    break;
                default:
                    break;
            }
        }
        else if (rule.ByWeekNo.Count > 0 && rule.ByYearDay.Count == 0 && rule.ByMonthDay.Count == 0 && rule.ByDay.Count == 0)
        {
            _plainDays[(int)dtstart.DayOfWeek] = true;
            _hasWeekdays = true;
        }

        bool timeRuleOnDate = isDate && (rule.ByHour.Count > 0 || rule.ByMinute.Count > 0 || rule.BySecond.Count > 0 || _freq <= RecurrenceFrequency.Hourly);
        if (timeRuleOnDate)
        {
            report.Add(RecurrenceDefectKind.TimeRuleOnDate, ruleText);
        }

        _hours = isDate || rule.ByHour.Count == 0 ? [dtstart.Hour] : Sorted(rule.ByHour);
        _minutes = isDate || rule.ByMinute.Count == 0 ? [dtstart.Minute] : Sorted(rule.ByMinute);
        _seconds = isDate || rule.BySecond.Count == 0 ? [dtstart.Second] : Sorted(rule.BySecond);
        _limitHour = !isDate && rule.ByHour.Count > 0 && _freq <= RecurrenceFrequency.Hourly;
        _limitMinute = !isDate && rule.ByMinute.Count > 0 && _freq <= RecurrenceFrequency.Minutely;
        _limitSecond = !isDate && rule.BySecond.Count > 0 && _freq <= RecurrenceFrequency.Secondly;

        if (_freq >= RecurrenceFrequency.Daily)
        {
            List<long> times = [];
            foreach (int h in _hours)
            {
                foreach (int mi in _minutes)
                {
                    foreach (int s in _seconds)
                    {
                        times.Add((h * TicksPerHour) + (mi * TicksPerMinute) + (s * TicksPerSecond));
                    }
                }
            }

            times.Sort();
            _timesOfDay = [.. times];
        }
    }

    /// <summary>Set when the rule stopped because it went too many periods without producing an instance.</summary>
    internal bool HitIterationLimit { get; private set; }

    /// <summary>Checks a rule's parts and builds the generator, or reports why it cannot.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="dtstart">The series start as a wall-clock value.</param>
    /// <param name="isDate">True when the start is a DATE.</param>
    /// <param name="maxEmptyPeriods">The iteration limit.</param>
    /// <param name="report">Receives defects.</param>
    /// <param name="ruleText">The rule as written, for defect details.</param>
    /// <returns>The generator, or null when the rule is invalid.</returns>
    internal static RuleEngine? Create(RecurrenceRule rule, DateTime dtstart, bool isDate, long maxEmptyPeriods, ExpansionReport report, string ruleText)
    {
        if (rule.RScale is not null && !string.Equals(rule.RScale, "GREGORIAN", StringComparison.OrdinalIgnoreCase))
        {
            report.Add(RecurrenceDefectKind.UnsupportedRScale, ruleText);
            return null;
        }

        if (!Valid(rule.BySecond, 0, 59, false) || !Valid(rule.ByMinute, 0, 59, false) || !Valid(rule.ByHour, 0, 23, false)
            || !Valid(rule.ByMonth, 1, 12, false) || !Valid(rule.ByMonthDay, 1, 31, true) || !Valid(rule.ByYearDay, 1, 366, true)
            || !Valid(rule.ByWeekNo, 1, 53, true) || !Valid(rule.BySetPos, 1, 366, true) || rule.Count is 0
            || rule.ByDay.Any(d => d.Ordinal is { } o && (o == 0 || Math.Abs(o) > 53)))
        {
            report.Add(RecurrenceDefectKind.InvalidRule, ruleText);
            return null;
        }

        return new RuleEngine(rule, dtstart, isDate, maxEmptyPeriods, report, ruleText);
    }

    private static bool Valid(IReadOnlyList<int> values, int min, int max, bool signed)
    {
        foreach (int v in values)
        {
            int a = signed ? Math.Abs(v) : v;
            if (a < min || a > max)
            {
                return false;
            }
        }

        return true;
    }

    private static int[] Sorted(IReadOnlyList<int> values)
    {
        int[] a = [.. values.Distinct()];
        Array.Sort(a);
        return a;
    }

    /// <summary>Jumps close to the period holding <paramref name="wall"/>. Only valid before the first <see cref="TryNext"/>, and only when COUNT does not need the skipped instances.</summary>
    /// <param name="wall">The wall-clock time to approach.</param>
    internal void SeekNear(DateTime wall)
    {
        if (wall.Ticks <= _dtTicks)
        {
            return;
        }

        long idx;
        switch (_freq)
        {
            case RecurrenceFrequency.Yearly:
                idx = (wall.Year - _dt.Year) / _interval;
                break;
            case RecurrenceFrequency.Monthly:
                idx = (((wall.Year * 12L) + wall.Month) - ((_dt.Year * 12L) + _dt.Month)) / _interval;
                break;
            case RecurrenceFrequency.Weekly:
                idx = ((wall.Ticks / TicksPerDay) - WeekStartDay(_dt)) / (7L * _interval);
                break;
            case RecurrenceFrequency.Daily:
                idx = ((wall.Ticks / TicksPerDay) - (_dtTicks / TicksPerDay)) / _interval;
                break;
            default:
                idx = (wall.Ticks - Base()) / Step();
                break;
        }

        _n = Math.Max(_n, idx - 1);
    }

    /// <summary>Produces the next instant, or false when the rule is exhausted (range or iteration limit).</summary>
    /// <param name="wall">The instant as a wall-clock value.</param>
    /// <returns>False at the end.</returns>
    internal bool TryNext(out DateTime wall)
    {
        while (true)
        {
            while (_pos < _buf.Count)
            {
                long t = _buf[_pos++];
                if (t >= _dtTicks)
                {
                    _emptyRun = 0;
                    wall = new DateTime(t, DateTimeKind.Unspecified);
                    return true;
                }
            }

            if (!FillNext())
            {
                wall = default;
                return false;
            }
        }
    }

    private bool FillNext()
    {
        _buf.Clear();
        _pos = 0;
        if (_emptyRun++ >= _maxEmptyPeriods)
        {
            HitIterationLimit = true;
            return false;
        }

        long n = _n;
        bool ok = _freq switch
        {
            RecurrenceFrequency.Yearly => FillYear(n),
            RecurrenceFrequency.Monthly => FillMonth(n),
            RecurrenceFrequency.Weekly => FillWeek(n),
            RecurrenceFrequency.Daily => FillDay(n),
            _ => FillTimePeriod(n),
        };
        if (!ok)
        {
            return false;
        }

        if (_n == n)
        {
            _n = n + 1;
        }

        if (_setPos.Length > 0 && _buf.Count > 0)
        {
            ApplySetPos();
        }

        return true;
    }

    // ---- Periods that hold whole days ----

    private bool FillYear(long n)
    {
        long year = _dt.Year + (n * _interval);
        if (year > 9998)
        {
            return false;
        }

        int y = (int)year;
        bool simple = _weekNos.Length == 0 && _yearDays.Length == 0 && !_hasWeekdays;
        _days.Clear();
        if (simple)
        {
            for (int m = 1; m <= 12; m++)
            {
                if (!_hasMonths || _months[m])
                {
                    AddMonthDays(y, m);
                }
            }
        }
        else if (_hasMonths && _weekNos.Length == 0 && _yearDays.Length == 0)
        {
            // BYMONTH with BYDAY: DayMatches decides each day alone, so only the selected months need scanning (about a twelfth of the year).
            for (int m = 1; m <= 12; m++)
            {
                if (_months[m])
                {
                    ScanDays(new DateTime(y, m, 1).Ticks / TicksPerDay, DateTime.DaysInMonth(y, m));
                }
            }
        }
        else
        {
            ScanDays(new DateTime(y, 1, 1).Ticks / TicksPerDay, DateTime.IsLeapYear(y) ? 366 : 365);
        }

        CrossTimes();
        return true;
    }

    private bool FillMonth(long n)
    {
        long mi = (_dt.Year * 12L) + (_dt.Month - 1) + (n * _interval);
        long year = mi / 12;
        if (year > 9998)
        {
            return false;
        }

        int y = (int)year;
        int m = (int)(mi % 12) + 1;
        _days.Clear();
        if (_hasMonths && !_months[m])
        {
            return true;
        }

        if (_weekNos.Length == 0 && _yearDays.Length == 0 && !_hasWeekdays)
        {
            AddMonthDays(y, m);
        }
        else
        {
            ScanDays(new DateTime(y, m, 1).Ticks / TicksPerDay, DateTime.DaysInMonth(y, m));
        }

        CrossTimes();
        return true;
    }

    private bool FillWeek(long n)
    {
        long start = WeekStartDay(_dt) + (7L * _interval * n);
        if (start * TicksPerDay > MaxTicks)
        {
            return false;
        }

        _days.Clear();
        ScanDays(start, 7);
        CrossTimes();
        return true;
    }

    private bool FillDay(long n)
    {
        long day = (_dtTicks / TicksPerDay) + (_interval * n);
        if (day * TicksPerDay > MaxTicks)
        {
            return false;
        }

        _days.Clear();
        ScanDays(day, 1);
        CrossTimes();
        return true;
    }

    // Months of a simple rule (no weekday, week number or year day): the BYMONTHDAY days, or DTSTART's day through the defaults.
    private void AddMonthDays(int y, int m)
    {
        int dim = DateTime.DaysInMonth(y, m);
        long first = new DateTime(y, m, 1).Ticks;
        int before = _days.Count;
        if (_monthDays.Length == 0)
        {
            for (int d = 1; d <= dim; d++)
            {
                _days.Add(first + ((d - 1) * TicksPerDay));
            }

            return;
        }

        foreach (int md in _monthDays)
        {
            int d = md > 0 ? md : dim + md + 1;
            if (d >= 1 && d <= dim)
            {
                _days.Add(first + ((d - 1) * TicksPerDay));
            }
        }

        if (_days.Count - before > 1)
        {
            _days.Sort(before, _days.Count - before, null);
        }
    }

    private void ScanDays(long startDay, int count)
    {
        for (int i = 0; i < count; i++)
        {
            long dayNumber = startDay + i;
            if (dayNumber < 0)
            {
                continue;
            }

            if (dayNumber * TicksPerDay > MaxTicks)
            {
                return;
            }

            if (DayMatches(dayNumber))
            {
                _days.Add(dayNumber * TicksPerDay);
            }
        }
    }

    private bool DayMatches(long dayNumber)
    {
        DateTime date = new(dayNumber * TicksPerDay);
        int month = date.Month;
        if (_hasMonths && !_months[month])
        {
            return false;
        }

        int day = date.Day;
        int dow = (int)((dayNumber + 1) % 7);
        int doy = date.DayOfYear;
        int dim = 0;
        int diy = 0;

        if (_monthDays.Length > 0)
        {
            dim = DateTime.DaysInMonth(date.Year, month);
            if (!Contains(_monthDays, day, day - dim - 1))
            {
                return false;
            }
        }

        if (_yearDays.Length > 0)
        {
            diy = DateTime.IsLeapYear(date.Year) ? 366 : 365;
            if (!Contains(_yearDays, doy, doy - diy - 1))
            {
                return false;
            }
        }

        if (_weekNos.Length > 0)
        {
            (int week, int weeks) = WeekOf(date.Year, dayNumber);
            if (!Contains(_weekNos, week, week - weeks - 1))
            {
                return false;
            }
        }

        if (_hasWeekdays)
        {
            if (_plainDays[dow])
            {
                return true;
            }

            foreach ((int ordinal, int ordDay) in _ordinalDays)
            {
                if (ordDay != dow)
                {
                    continue;
                }

                // RFC 5545 §3.3.10: within MONTHLY, or YEARLY with BYMONTH, the ordinal counts in the month; otherwise in the year.
                int from;
                int to;
                if (_ordinalInMonth)
                {
                    dim = dim == 0 ? DateTime.DaysInMonth(date.Year, month) : dim;
                    from = ((day - 1) / 7) + 1;
                    to = -(((dim - day) / 7) + 1);
                }
                else
                {
                    diy = diy == 0 ? (DateTime.IsLeapYear(date.Year) ? 366 : 365) : diy;
                    from = ((doy - 1) / 7) + 1;
                    to = -(((diy - doy) / 7) + 1);
                }

                if (ordinal == from || ordinal == to)
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    // True when the sorted list holds the value or its negative-form twin.
    private static bool Contains(int[] sorted, int positive, int negative)
    {
        foreach (int v in sorted)
        {
            if (v == positive || v == negative)
            {
                return true;
            }
        }

        return false;
    }

    // ISO 8601 style week numbering with the rule's WKST (RFC 5545 §3.3.10, BYWEEKNO): week 1 is the first week with at least four days in the year.
    private (int Week, int Weeks) WeekOf(int year, long dayNumber)
    {
        if (_wkYear != year)
        {
            _wkYear = year;
            _w1Prev = Week1Start(year - 1);
            _w1Cur = Week1Start(year);
            _w1Next = Week1Start(year + 1);
        }

        if (dayNumber < _w1Cur)
        {
            return ((int)((dayNumber - _w1Prev) / 7) + 1, (int)((_w1Cur - _w1Prev) / 7));
        }

        if (dayNumber >= _w1Next)
        {
            return ((int)((dayNumber - _w1Next) / 7) + 1, (int)((Week1Start(year + 2) - _w1Next) / 7));
        }

        return ((int)((dayNumber - _w1Cur) / 7) + 1, (int)((_w1Next - _w1Cur) / 7));
    }

    private long Week1Start(int year)
    {
        int y = Math.Clamp(year, 1, 9999);
        long jan4 = (new DateTime(y, 1, 4).Ticks / TicksPerDay) + ((year - y) * 365L);
        int dow = (int)((jan4 + 1) % 7);
        return jan4 - ((dow - _wkst + 7) % 7);
    }

    private long WeekStartDay(DateTime d)
    {
        long day = d.Ticks / TicksPerDay;
        return day - ((((int)((day + 1) % 7)) - _wkst + 7) % 7);
    }

    private void CrossTimes()
    {
        long previous = -1;
        foreach (long day in _days)
        {
            if (day == previous)
            {
                continue; // BYMONTHDAY=31,-1 names the same day twice; an instance counts once
            }

            previous = day;
            foreach (long t in _timesOfDay)
            {
                _buf.Add(day + t);
            }
        }

        if (_isDate)
        {
            // A DATE start has one instant per day, whatever the time parts said.
            for (int i = _buf.Count - 1; i > 0; i--)
            {
                if (_buf[i] / TicksPerDay == _buf[i - 1] / TicksPerDay)
                {
                    _buf.RemoveAt(i);
                }
            }
        }
    }

    // ---- Periods shorter than a day ----

    private long Step() => (_freq switch
    {
        RecurrenceFrequency.Hourly => TicksPerHour,
        RecurrenceFrequency.Minutely => TicksPerMinute,
        _ => TicksPerSecond,
    }) * _interval;

    private long Base() => _dtTicks - (_dtTicks % (_freq switch
    {
        RecurrenceFrequency.Hourly => TicksPerHour,
        RecurrenceFrequency.Minutely => TicksPerMinute,
        _ => TicksPerSecond,
    }));

    private bool FillTimePeriod(long n)
    {
        long step = Step();
        long start = Base() + (n * step);
        if (start > MaxTicks || n > (MaxTicks / step))
        {
            return false;
        }

        long dayNumber = start / TicksPerDay;
        if (!DayMatches(dayNumber))
        {
            // Nothing on this day can match: jump to the first period of the next day instead of stepping through all of it.
            long nextDay = (dayNumber + 1) * TicksPerDay;
            _n = Math.Max(n + 1, ((nextDay - Base()) + step - 1) / step);
            return true;
        }

        long inDay = start % TicksPerDay;
        int hour = (int)(inDay / TicksPerHour);
        int minute = (int)(inDay % TicksPerHour / TicksPerMinute);
        int second = (int)(inDay % TicksPerMinute / TicksPerSecond);
        if ((_limitHour && Array.IndexOf(_hours, hour) < 0) || (_limitMinute && Array.IndexOf(_minutes, minute) < 0)
            || (_limitSecond && Array.IndexOf(_seconds, second) < 0))
        {
            return true;
        }

        switch (_freq)
        {
            case RecurrenceFrequency.Hourly:
                foreach (int mi in _minutes)
                {
                    foreach (int s in _seconds)
                    {
                        _buf.Add(start + (mi * TicksPerMinute) + (s * TicksPerSecond));
                    }
                }

                break;
            case RecurrenceFrequency.Minutely:
                foreach (int s in _seconds)
                {
                    _buf.Add(start + (s * TicksPerSecond));
                }

                break;
            default:
                _buf.Add(start);
                break;
        }

        _buf.Sort();
        if (_isDate)
        {
            for (int i = _buf.Count - 1; i > 0; i--)
            {
                if (_buf[i] / TicksPerDay == _buf[i - 1] / TicksPerDay)
                {
                    _buf.RemoveAt(i);
                }
            }
        }

        return true;
    }

    // RFC 5545 §3.3.10, BYSETPOS: 1 is the first instant of the period's sorted set, -1 the last.
    private void ApplySetPos()
    {
        _tmp.Clear();
        int count = _buf.Count;
        foreach (int p in _setPos)
        {
            int i = p > 0 ? p - 1 : count + p;
            if (i >= 0 && i < count)
            {
                _tmp.Add(_buf[i]);
            }
        }

        _tmp.Sort();
        _buf.Clear();
        long last = -1;
        foreach (long t in _tmp)
        {
            if (t != last)
            {
                _buf.Add(t);
                last = t;
            }
        }
    }
}
