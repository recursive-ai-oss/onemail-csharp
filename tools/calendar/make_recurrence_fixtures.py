#!/usr/bin/env python3
"""Generate the recurrence fixtures in testdata/calendar/recurrence/generated (seeded; same output every run).

  make_recurrence_fixtures.py <venv-python-with-dateutil>   (run with the pinned venv from oracles/setup_python_expand.sh)

grid-<FREQ>.ics    every subset of {BYMONTH, BYWEEKNO, BYYEARDAY, BYMONTHDAY, BYDAY, BYHOUR, BYMINUTE, BYSECOND, BYSETPOS} (512 subsets)
                   for the frequency, with seeded values, INTERVAL, WKST and a COUNT or UNTIL, in America/New_York. DTSTART is moved
                   to the rule's first instance with python-dateutil so that the series starts in step with its own rule; rules with
                   no instance within a few years are left out.
frames.ics         a seeded sample of those rules on each kind of start: DATE, floating, UTC, Europe/Berlin, Australia/Sydney, Pacific/Auckland.
dst.ics            rules at 01:30, 02:30 and 03:30 around the clock changes of New York, Berlin, Sydney and Auckland, with DTEND and DURATION across a change.
sets.ics           RDATE (dates, date-times, periods), EXDATE (date-time and date), EXRULE, RECURRENCE-ID overrides, THISANDFUTURE.
windows.ics        Outlook-style Windows zone names with a VTIMEZONE, and the same names without one.
unsynced.ics       a DTSTART that does not match its rule, UNTIL of every value type, invalid dates, and other documented ambiguities.
"""
import datetime as dt, itertools, os, random, signal, sys
from dateutil import rrule as R
from dateutil.tz import gettz

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '../../testdata/calendar/recurrence/generated')
os.makedirs(OUT, exist_ok=True)
rng = random.Random(20261031)
FREQS = ['YEARLY', 'MONTHLY', 'WEEKLY', 'DAILY', 'HOURLY', 'MINUTELY', 'SECONDLY']
DAYS = ['MO', 'TU', 'WE', 'TH', 'FR', 'SA', 'SU']
PARTS = ['BYMONTH', 'BYWEEKNO', 'BYYEARDAY', 'BYMONTHDAY', 'BYDAY', 'BYHOUR', 'BYMINUTE', 'BYSECOND', 'BYSETPOS']


class Timeout(Exception):
    pass


def _alarm(*_):
    raise Timeout()


signal.signal(signal.SIGALRM, _alarm)


def values(part, freq):
    r = rng
    if part == 'BYMONTH':
        return ','.join(str(x) for x in sorted(r.sample(range(1, 13), r.choice([1, 2, 3]))))
    if part == 'BYWEEKNO':
        return ','.join(str(x) for x in r.sample([1, 2, 10, 20, 30, 52, 53, -1, -2], r.choice([1, 2])))
    if part == 'BYYEARDAY':
        return ','.join(str(x) for x in r.sample([1, 32, 100, 200, 365, 366, -1, -100], r.choice([1, 2])))
    if part == 'BYMONTHDAY':
        return ','.join(str(x) for x in r.sample([1, 15, 28, 29, 30, 31, -1, -2, -15], r.choice([1, 2])))
    if part == 'BYDAY':
        days = r.sample(DAYS, r.choice([1, 2, 3]))
        out = []
        for d in days:
            if freq in ('MONTHLY', 'YEARLY') and r.random() < 0.5:
                out.append(str(r.choice([1, 2, 3, -1, -2, 5])) + d)
            else:
                out.append(d)
        return ','.join(out)
    if part == 'BYHOUR':
        return ','.join(str(x) for x in sorted(r.sample(range(0, 24), r.choice([1, 2]))))
    if part == 'BYMINUTE':
        return ','.join(str(x) for x in sorted(r.sample([0, 15, 30, 45], r.choice([1, 2]))))
    if part == 'BYSECOND':
        return ','.join(str(x) for x in sorted(r.sample([0, 30], r.choice([1, 2]))))
    if part == 'BYSETPOS':
        return ','.join(str(x) for x in r.sample([1, 2, 3, -1, -2], r.choice([1, 2])))


def fold(line):
    out, first = [], True
    while len(line.encode()) > 74:
        out.append(line[:74]); line = ' ' + line[74:]
    out.append(line)
    return '\r\n'.join(out)


def rule_text(freq, subset, terminator):
    parts = ['FREQ=' + freq]
    iv = rng.choice([1, 1, 1, 2, 3])
    if iv != 1:
        parts.append('INTERVAL=%d' % iv)
    for p in PARTS:
        if p in subset:
            parts.append('%s=%s' % (p, values(p, freq)))
    if rng.random() < 0.3:
        parts.append('WKST=' + rng.choice(DAYS))
    return parts, terminator


SEEDS = [dt.datetime(2026, 1, 15, 9, 30, 0), dt.datetime(2025, 7, 4, 22, 45, 10), dt.datetime(2027, 12, 31, 0, 0, 0), dt.datetime(2026, 3, 8, 1, 30, 0)]


def synced_start(rule_line, seed, tz):
    """the first instance of the rule at or after seed, found with dateutil (a fixture-building step, not part of the library)"""
    signal.setitimer(signal.ITIMER_REAL, 1.5)
    try:
        base = seed.replace(tzinfo=tz) if tz else seed
        r = R.rrulestr(rule_line, dtstart=base)
        first = next(iter(r), None)
        if first is None:
            return None
        if first - base > dt.timedelta(days=3 * 366):
            return None
        return first.replace(tzinfo=None)
    except (Timeout, ValueError, OverflowError):
        return None
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)


def make_rule(freq, subset):
    parts, _ = rule_text(freq, subset, None)
    t = rng.random()
    return parts, t


def finish(parts, t, start, kind):
    """append COUNT or UNTIL in the form the start calls for"""
    if t < 0.6:
        parts = parts + ['COUNT=%d' % rng.randint(3, 12)]
    elif t < 0.8:
        u = start + dt.timedelta(days=rng.randint(30, 900))
        if kind == 'date':
            parts = parts + ['UNTIL=' + u.strftime('%Y%m%d')]
        elif kind == 'float':
            parts = parts + ['UNTIL=' + u.strftime('%Y%m%dT%H%M%S')]
        else:
            parts = parts + ['UNTIL=' + u.strftime('%Y%m%dT%H%M%SZ')]
    return ';'.join(parts)


def vevent(uid, dtstart_line, *lines):
    return fold('BEGIN:VEVENT') + '\r\nUID:%s@example.test\r\nDTSTAMP:20260101T000000Z\r\n%s\r\n%s\r\nEND:VEVENT\r\n' % (uid, dtstart_line, '\r\n'.join(fold(l) for l in lines))


def calendar(body, tz=''):
    return 'BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//OneMail//recurrence fixtures//EN\r\n' + tz + body + 'END:VCALENDAR\r\n'


def vtz_us(tzid, std_off, dst_off, std_name, dst_name, dst_rule, std_rule):
    return ('BEGIN:VTIMEZONE\r\nTZID:%s\r\nBEGIN:DAYLIGHT\r\nDTSTART:19700308T020000\r\nTZOFFSETFROM:%s\r\nTZOFFSETTO:%s\r\nTZNAME:%s\r\nRRULE:%s\r\nEND:DAYLIGHT\r\n'
            'BEGIN:STANDARD\r\nDTSTART:19701101T020000\r\nTZOFFSETFROM:%s\r\nTZOFFSETTO:%s\r\nTZNAME:%s\r\nRRULE:%s\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n'
            % (tzid, std_off, dst_off, dst_name, dst_rule, dst_off, std_off, std_name, std_rule))


def write(name, text):
    with open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='') as f:
        f.write(text)
    print(name, len(text))


# ---- the grid ----
NY = gettz('America/New_York')
grid_rules = {}
for freq in FREQS:
    body, n, kept = '', 0, []
    for k in range(len(PARTS) + 1):
        for subset in itertools.combinations(PARTS, k):
            if freq in ('HOURLY', 'MINUTELY', 'SECONDLY') and ('BYWEEKNO' in subset or 'BYYEARDAY' in subset) and rng.random() < 0.5:
                continue  # keep the sub-day grid light: half of the week-number and year-day subsets
            parts, t = make_rule(freq, set(subset))
            seed = rng.choice(SEEDS)
            probe = ';'.join(parts)
            start = synced_start(probe, seed, NY)
            if start is None:
                continue
            rule = finish(parts, t, start, 'z')
            n += 1
            kept.append((rule, start))
            body += vevent('g%s-%d' % (freq.lower(), n), 'DTSTART;TZID=America/New_York:' + start.strftime('%Y%m%dT%H%M%S'), 'RRULE:' + rule)
    grid_rules[freq] = kept
    write('grid-%s.ics' % freq, calendar(body))

# ---- other kinds of start ----
body = ''
n = 0
for kind, tzid in [('date', None), ('float', None), ('utc', None), ('zoned', 'Europe/Berlin'), ('zoned', 'Australia/Sydney'), ('zoned', 'Pacific/Auckland')]:
    pool = [(f, r, s) for f in FREQS for (r, s) in grid_rules[f]]
    if kind == 'date':
        pool = [p for p in pool if p[0] in ('YEARLY', 'MONTHLY', 'WEEKLY', 'DAILY')]
    for f, r, s in rng.sample(pool, 120):
        if kind in ('utc', 'zoned') and 'UNTIL=' in r and not r.split('UNTIL=')[1].split(';')[0].endswith('Z'):
            r = r.replace(r.split('UNTIL=')[1].split(';')[0], r.split('UNTIL=')[1].split(';')[0] + 'Z')
        if kind == 'date' and 'UNTIL=' in r:
            u = r.split('UNTIL=')[1].split(';')[0]
            r = r.replace(u, u[:8])
        if kind == 'float' and 'UNTIL=' in r:
            u = r.split('UNTIL=')[1].split(';')[0].rstrip('Z')
            r = r.replace(r.split('UNTIL=')[1].split(';')[0], u)
        n += 1
        if kind == 'date':
            line = 'DTSTART;VALUE=DATE:' + s.strftime('%Y%m%d')
            r = ';'.join(p for p in r.split(';') if not p.startswith(('BYHOUR', 'BYMINUTE', 'BYSECOND')))
        elif kind == 'float':
            line = 'DTSTART:' + s.strftime('%Y%m%dT%H%M%S')
        elif kind == 'utc':
            line = 'DTSTART:' + s.strftime('%Y%m%dT%H%M%SZ')
        else:
            line = 'DTSTART;TZID=%s:%s' % (tzid, s.strftime('%Y%m%dT%H%M%S'))
        body += vevent('f%d' % n, line, 'RRULE:' + r)
write('frames.ics', calendar(body))

# ---- DST edges ----
body = ''
n = 0
edges = [
    ('America/New_York', ['20260307T023000', '20260308T013000', '20261031T013000', '20261101T013000', '20261101T023000', '20260308T023000']),
    ('Europe/Berlin', ['20260328T023000', '20261024T023000', '20260329T023000', '20261025T023000']),
    ('Australia/Sydney', ['20260404T023000', '20261003T023000', '20260405T023000', '20261004T023000']),
    ('Pacific/Auckland', ['20260405T023000', '20260927T023000', '20260404T023000', '20260926T023000']),
]
rules = ['FREQ=DAILY;COUNT=6', 'FREQ=WEEKLY;COUNT=4', 'FREQ=MONTHLY;COUNT=3', 'FREQ=YEARLY;COUNT=3', 'FREQ=HOURLY;COUNT=30', 'FREQ=DAILY;INTERVAL=7;COUNT=6;BYHOUR=1,2,3;BYMINUTE=30']
for tzid, starts in edges:
    for s in starts:
        for r in rules:
            n += 1
            body += vevent('d%d' % n, 'DTSTART;TZID=%s:%s' % (tzid, s), 'RRULE:' + r, 'DTEND;TZID=%s:%s' % (tzid, (dt.datetime.strptime(s, '%Y%m%dT%H%M%S') + dt.timedelta(hours=2)).strftime('%Y%m%dT%H%M%S')))
write('dst.ics', calendar(body))

# ---- sets: RDATE / EXDATE / EXRULE / overrides ----
body = ''
body += vevent('s1', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=10', 'EXDATE;TZID=America/New_York:20260107T090000,20260109T090000', 'RDATE;TZID=America/New_York:20260120T100000')
body += vevent('s2', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=10', 'EXDATE:20260107T140000Z')
body += vevent('s3', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=10', 'EXDATE;VALUE=DATE:20260108')
body += vevent('s4', 'DTSTART;VALUE=DATE:20260105', 'RRULE:FREQ=WEEKLY;COUNT=6', 'EXDATE;VALUE=DATE:20260112', 'RDATE;VALUE=DATE:20260114,20260121')
body += vevent('s5', 'DTSTART:20260105T090000Z', 'RDATE:20260106T090000Z,20260110T120000Z', 'RDATE;VALUE=PERIOD:20260111T080000Z/PT3H,20260112T080000Z/20260112T100000Z')
body += vevent('s6', 'DTSTART:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=8', 'EXRULE:FREQ=WEEKLY;BYDAY=SA,SU')
body += vevent('s7', 'DTSTART;TZID=Europe/Berlin:20260105T090000', 'RRULE:FREQ=WEEKLY;COUNT=5;BYDAY=MO', 'EXDATE;TZID=Europe/Berlin:20260112T090000', 'EXDATE;TZID=America/New_York:20260119T030000')
body += vevent('s8', 'DTSTART:20260105T090000Z', 'RRULE:FREQ=DAILY;COUNT=5', 'RDATE:20260105T090000Z,20260106T090000Z')
write('sets.ics', calendar(body))

# overrides live in their own calendar so each series stays one component per UID for the single-component oracles
ov = ''
ov += vevent('o1', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=6', 'SUMMARY:series') + vevent('o1', 'DTSTART;TZID=America/New_York:20260107T140000', 'RECURRENCE-ID;TZID=America/New_York:20260107T090000', 'SUMMARY:moved')
ov += vevent('o2', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=6') + vevent('o2', 'DTSTART;TZID=America/New_York:20260108T100000', 'RECURRENCE-ID;TZID=America/New_York:20260107T090000;RANGE=THISANDFUTURE', 'SUMMARY:future')
ov += vevent('o3', 'DTSTART:20260105T090000Z', 'RRULE:FREQ=WEEKLY;COUNT=4') + vevent('o3', 'DTSTART:20260120T090000Z', 'RECURRENCE-ID:20260112T090000Z')
write('overrides.ics', calendar(ov))

# ---- windows names ----
ol_berlin = ('BEGIN:VTIMEZONE\r\nTZID:W. Europe Standard Time\r\nBEGIN:STANDARD\r\nDTSTART:16011028T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nRRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=10\r\nEND:STANDARD\r\n'
             'BEGIN:DAYLIGHT\r\nDTSTART:16010325T020000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nRRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=3\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n')
body = ''
for i, s in enumerate(['20260328T023000', '20260329T090000', '20261025T023000', '20261024T090000']):
    body += vevent('w%d' % i, 'DTSTART;TZID=W. Europe Standard Time:' + s, 'RRULE:FREQ=DAILY;COUNT=4')
write('windows.ics', calendar(body, ol_berlin))
body = ''
for i, (z, s) in enumerate([('W. Europe Standard Time', '20260328T023000'), ('Eastern Standard Time', '20260307T023000'), ('AUS Eastern Standard Time', '20260404T023000'), ('New Zealand Standard Time', '20260404T023000'), ('Pacific Standard Time', '20261031T013000')]):
    body += vevent('n%d' % i, 'DTSTART;TZID=%s:%s' % (z, s), 'RRULE:FREQ=DAILY;COUNT=4')
write('windows-novtimezone.ics', calendar(body))

# ---- documented ambiguities ----
body = ''
body += vevent('u1', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=WEEKLY;COUNT=4;BYDAY=TU')
body += vevent('u2', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;UNTIL=20260108')
body += vevent('u3', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;UNTIL=20260108T090000Z')
body += vevent('u4', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;UNTIL=20260108T140000Z')
body += vevent('u5', 'DTSTART;VALUE=DATE:20260105', 'RRULE:FREQ=DAILY;UNTIL=20260108T000000Z')
body += vevent('u6', 'DTSTART:20260105T090000', 'RRULE:FREQ=DAILY;UNTIL=20260108T090000Z')
body += vevent('u7', 'DTSTART;TZID=America/New_York:20260130T090000', 'RRULE:FREQ=MONTHLY;COUNT=5')
body += vevent('u8', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=MONTHLY;COUNT=6;BYDAY=5MO')
body += vevent('u9', 'DTSTART;TZID=America/New_York:20240229T090000', 'RRULE:FREQ=YEARLY;COUNT=3')
body += vevent('u10', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=YEARLY;COUNT=3;BYWEEKNO=2')
body += vevent('u11', 'DTSTART;TZID=America/New_York:20261228T090000', 'RRULE:FREQ=YEARLY;COUNT=3;BYWEEKNO=1;BYDAY=MO')
body += vevent('u12', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=WEEKLY;COUNT=4;BYMONTHDAY=5,12')
body += vevent('u13', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=MONTHLY;COUNT=4;BYYEARDAY=5,40')
body += vevent('u14', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=4;BYSETPOS=1')
body += vevent('u15', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=3', 'RRULE:FREQ=WEEKLY;COUNT=3;BYDAY=FR')
body += vevent('u16', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=0')
body += vevent('u17', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=MONTHLY;COUNT=4;BYMONTHDAY=31')
body += vevent('u18', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=HOURLY;INTERVAL=5;COUNT=8;WKST=SU')
body += vevent('u19', 'DTSTART;TZID=America/New_York:20261103T093000', 'RRULE:FREQ=YEARLY;COUNT=3;BYMONTH=11;BYDAY=1TU')
body += vevent('u20', 'DTSTART;TZID=America/New_York:20260105T090000', 'RRULE:FREQ=DAILY;COUNT=5;BYHOUR=9,17;BYMINUTE=0,30;BYSECOND=0,45')
write('unsynced.ics', calendar(body))
