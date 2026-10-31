#!/usr/bin/env python3
"""Compare expansion outputs case by case and explain every difference.

  compare_expand.py ours.jsonl oracle.jsonl <oracle name> [--peer other.jsonl]... [-v]

Prints components compared, identical, differing (explained / unexplained) and the components the oracle could not expand
(counted, never compared). Every difference is given an id from `explain` below, each defined in docs/recurrence.md; the exit
status is 1 if any difference has none. --peer names the other oracles' outputs, which the Ical.Net rule uses.
"""
import json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rulecls

args = [a for a in sys.argv[1:] if not a.startswith('-')]
verbose = '-v' in sys.argv
peers_paths = [sys.argv[i + 1] for i, a in enumerate(sys.argv) if a == '--peer']
args = [a for a in args if a not in peers_paths]
ours_path, oracle_path, name = args[0], args[1], args[2]


def load(path):
    out = {}
    for l in open(path):
        r = json.loads(l)
        out[r['case']] = r
    return out


ours = load(ours_path)
cases = load(os.path.join(os.path.dirname(os.path.abspath(ours_path)), 'cases.jsonl'))
peers = [load(p) for p in peers_paths]


def explain(case, mine, theirs):
    """-> an id from docs/recurrence.md, or None"""
    ics = cases[case]['ics']
    defects = ' '.join(mine.get('defects', []))
    rules = rulecls.rules(ics)
    # R3: DTSTART that does not match its own rule. RFC 5545 section 3.8.5.3 leaves the set undefined.
    if 'AmbiguousUnsynchronizedStart' in defects:
        return 'R3-unsynchronised-start'
    # R4: UNTIL in a different value type from DTSTART.
    if 'AmbiguousUntilType' in defects:
        return 'R4-until-type'
    if 'AmbiguousExdateType' in defects:
        return 'R6-exdate-date'
    # R2: parts the table in section 3.3.10 marks N/A for the frequency; the result is implementation-defined.
    if any(rulecls.not_applicable(r[1]) for r in rules):
        return 'R2-not-applicable-part'
    # R1: BYWEEKNO with no day part. We take the weekday from DTSTART (RFC 5545 section 3.3.10; Ical.Net does too);
    # python-dateutil takes every day of the week.
    if name in ('rec', 'du') and any(rulecls.weekno_without_day(r[1]) for r in rules):
        return 'R1-weekno-default-day'
    # R5: wall-clock stepping of rules across a clock change.
    if 'AmbiguousSkippedLocalTime' in defects or 'AmbiguousRepeatedLocalTime' in defects:
        return 'R5-clock-change'
    # R7: python-dateutil positions BYSETPOS inside the part of the first week that follows DTSTART; RFC 5545 section 3.3.10 counts in the whole week.
    if name in ('rec', 'du') and any(r[0] == 'RRULE' and rulecls.parse(r[1]).get('FREQ') == 'WEEKLY' and 'BYSETPOS' in rulecls.parse(r[1]) for r in rules):
        return 'R7-weekly-setpos-first-week'
    # Limits of the oracles themselves.
    if name == 'du' and 'RECURRENCE-ID' in ics:
        return 'L-du-glue-has-no-overrides'
    if name in ('rec', 'du') and any(r[0] == 'EXRULE' for r in rules):
        return 'L-oracle-ignores-exrule'
    # Two of three agree with us: the third is the odd one out (the convention of the ambiguity table).
    if any(case in p and 'error' not in p[case] and sorted(p[case]['starts']) == sorted(mine['starts']) for p in peers):
        return 'L-outvoted-by-the-other-two'
    return None


same = diff = err = 0
unexplained = []
by_id = {}
for case, o in load(oracle_path).items():
    m = ours.get(case)
    if m is None:
        continue
    if 'error' in o:
        err += 1
        key = 'oracle-could-not-expand: ' + o['error'].split(':')[0]
        by_id[key] = by_id.get(key, 0) + 1
        if verbose:
            print('ERROR', case, o['error'])
        continue
    a, b = sorted(m['starts']), sorted(o['starts'])  # inside a skipped local hour, generation order can differ from instant order
    if a == b:
        same += 1
        continue
    diff += 1
    ident = explain(case, m, o)
    if ident is None:
        i = 0
        while i < min(len(a), len(b)) and a[i] == b[i]:
            i += 1
        unexplained.append((case, 'ours=%d oracle=%d first-diff@%d ours=%s oracle=%s defects=%s' % (len(a), len(b), i, a[i] if i < len(a) else None, b[i] if i < len(b) else None, m.get('defects'))))
    else:
        by_id[ident] = by_id.get(ident, 0) + 1
print('%s: compared %d, identical %d, differ %d (explained %d, unexplained %d), oracle could not expand %d' % (name, same + diff, same, diff, diff - len(unexplained), len(unexplained), err))
for k, v in sorted(by_id.items()):
    print('   %-48s %d' % (k, v))
for c, n in unexplained[:300 if verbose else 30]:
    print('UNEXPLAINED', c, n)
sys.exit(1 if unexplained else 0)
