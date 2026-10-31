#!/usr/bin/env python3
"""Expand recurring components with released Python packages, for the expansion differential (black-box: only the packages are run).

  py_expand.py rec|du <cases.jsonl> <out.jsonl>

  rec  recurring-ical-events over icalendar: the library's own calendar-level expansion (RRULE, RDATE, EXDATE, overrides)
  du   python-dateutil rrule/rruleset with our glue: DTSTART, RRULE, RDATE and EXDATE read through icalendar, no overrides

The cases come from `RecurrenceDump cases`, so every oracle sees the same components and windows. Output: one JSON line per
case with the start times as Z (UTC), F (floating) or D (date) strings, or an "error" (an oracle limit, never silently dropped).
"""
import datetime as dt, json, signal, sys, traceback
import icalendar
from dateutil import rrule as du_rrule
from dateutil.tz import UTC

mode, cases_path, out_path = sys.argv[1:4]
CAP = 400


def fmt(v, force=None):
    if isinstance(v, dt.datetime):
        if v.tzinfo is not None:
            return v.astimezone(UTC).strftime('%Y%m%dT%H%M%SZ'), 'Z'
        return v.strftime('%Y%m%dT%H%M%S'), 'F'
    return v.strftime('%Y%m%d'), 'D'


def parse_win(s):
    return dt.datetime.strptime(s, '%Y%m%dT%H%M%S')


def masters(cal, uid):
    out = []
    for c in cal.walk():
        if c.name in ('VEVENT', 'VTODO', 'VJOURNAL') and str(c.get('UID')) == uid and 'RECURRENCE-ID' not in c:
            out.append(c)
    return out


def values(prop):
    """the datetime/date values of a possibly repeated RDATE/EXDATE property"""
    if prop is None:
        return []
    props = prop if isinstance(prop, list) else [prop]
    res = []
    for p in props:
        for d in p.dts:
            v = d.dt
            if isinstance(v, tuple):  # PERIOD
                v = v[0]
            res.append(v)
    return res


def run_rec(case, cal):
    import recurring_ical_events
    ws, we = parse_win(case['start']), parse_win(case['end'])
    uid = case['uid']
    q = recurring_ical_events.of(cal, components=['VEVENT', 'VTODO', 'VJOURNAL'])
    starts, kinds = [], set()
    # between() selects what overlaps the window; we keep what starts in it, like the other oracles
    for c in q.between(ws.replace(tzinfo=UTC), we.replace(tzinfo=UTC)):
        if str(c.get('UID')) != uid:
            continue
        v = c['DTSTART'].dt
        s, k = fmt(v)
        if (k == 'Z' and v.astimezone(UTC).replace(tzinfo=None) < ws) or (k == 'F' and v < ws) or (k == 'D' and v < ws.date()):
            continue  # overlapping the window is not starting in it
        kinds.add(k)
        starts.append((v, s))
    return starts, kinds, ws, we


def run_du(case, cal):
    ws, we = parse_win(case['start']), parse_win(case['end'])
    uid, n = case['uid'], case['n']
    c = masters(cal, uid)[n]
    start = c['DTSTART'].dt
    is_date = not isinstance(start, dt.datetime)
    base = dt.datetime(start.year, start.month, start.day) if is_date else start
    rs = du_rrule.rruleset()
    rules = c.get('RRULE')
    rules = rules if isinstance(rules, list) else ([rules] if rules is not None else [])
    for r in rules:
        text = r.to_ical().decode()
        parts = dict(p.split('=', 1) for p in text.split(';'))
        until = parts.get('UNTIL')
        if until is not None:
            # dateutil insists on the UNTIL matching DTSTART's awareness; this is glue, not a rule interpretation
            if base.tzinfo is not None:
                u = r['UNTIL'][0]
                if not isinstance(u, dt.datetime):
                    u = dt.datetime(u.year, u.month, u.day)
                if u.tzinfo is None:
                    u = u.replace(tzinfo=base.tzinfo)
                parts['UNTIL'] = u.astimezone(UTC).strftime('%Y%m%dT%H%M%SZ')
            else:
                u = r['UNTIL'][0]
                if isinstance(u, dt.datetime):
                    u = u.replace(tzinfo=None)
                else:
                    u = dt.datetime(u.year, u.month, u.day)
                parts['UNTIL'] = u.strftime('%Y%m%dT%H%M%S')
        text = ';'.join('%s=%s' % kv for kv in parts.items())
        rs.rrule(du_rrule.rrulestr(text, dtstart=base))
    for v in values(c.get('RDATE')):
        rs.rdate(dt.datetime(v.year, v.month, v.day) if not isinstance(v, dt.datetime) else v)
    for v in values(c.get('EXDATE')):
        rs.exdate(dt.datetime(v.year, v.month, v.day) if not isinstance(v, dt.datetime) else v)
    rs.rdate(base)
    starts, kinds = [], set()
    lo = ws.replace(tzinfo=UTC) if base.tzinfo is not None else ws
    hi = we.replace(tzinfo=UTC) if base.tzinfo is not None else we
    for v in rs:
        if v >= hi:
            break
        if v < lo:
            continue
        out = v.date() if is_date else v
        s, k = fmt(out)
        kinds.add(k)
        starts.append((out, s))
        if len(starts) >= CAP * 4:
            break
    return starts, kinds, ws, we


class Timeout(Exception):
    pass


def _alarm(*_):
    raise Timeout('no answer in 20 seconds')


signal.signal(signal.SIGALRM, _alarm)
cache = {}
with open(out_path, 'w') as out:
    for line in open(cases_path):
        case = json.loads(line)
        rec = {'case': case['case']}
        signal.alarm(20)
        try:
            cal = icalendar.Calendar.from_ical(case['ics'])
            starts, kinds, ws, we = (run_rec if mode == 'rec' else run_du)(case, cal)
            starts.sort(key=lambda t: t[1])
            rec['kind'] = sorted(kinds)[0] if kinds else 'F'
            rec['starts'] = [s for _, s in starts][:case['cap']]
        except Exception as e:  # an oracle that cannot expand a component is a documented limit, not a pass
            rec['error'] = '%s: %s' % (type(e).__name__, str(e)[:200])
        signal.alarm(0)
        out.write(json.dumps(rec) + '\n')
