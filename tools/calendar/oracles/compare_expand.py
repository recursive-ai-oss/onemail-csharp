#!/usr/bin/env python3
"""Compare expansion outputs case by case.

  compare_expand.py ours.jsonl oracle.jsonl [-v] [--explained explained_expand.tsv]

Prints components compared, identical, differing, oracle errors (an oracle that could not expand: counted, not compared) and,
for each difference, the first point where the lists diverge. With --explained, each difference is looked up in the TSV
(columns: case-glob, oracle, id, note) and only unexplained ones are listed; the exit status is 1 if any remain.
"""
import fnmatch, json, sys

args = [a for a in sys.argv[1:] if not a.startswith('-')]
verbose = '-v' in sys.argv
explained = []
if '--explained' in sys.argv:
    path = sys.argv[sys.argv.index('--explained') + 1]
    args = [a for a in args if a != path]
    for line in open(path):
        if line.strip() and not line.startswith('#'):
            explained.append(line.rstrip('\n').split('\t'))
ours = {}
for l in open(args[0]):
    r = json.loads(l); ours[r['case']] = r
oracle_name = args[2] if len(args) > 2 else args[1]
same = diff = err = 0
unexplained = []
by_id = {}
for l in open(args[1]):
    o = json.loads(l)
    m = ours.get(o['case'])
    if m is None:
        continue
    if 'error' in o:
        err += 1
        if verbose:
            print('ERROR', o['case'], o['error'])
        by_id.setdefault('oracle-error: ' + o['error'].split(':')[0], 0)
        by_id['oracle-error: ' + o['error'].split(':')[0]] += 1
        continue
    a, b = sorted(m['starts']), sorted(o['starts'])  # inside a skipped local hour, generation order can differ from instant order
    if a == b:
        same += 1
        continue
    diff += 1
    i = 0
    while i < min(len(a), len(b)) and a[i] == b[i]:
        i += 1
    note = 'ours=%d oracle=%d first-diff@%d ours=%s oracle=%s defects=%s' % (len(a), len(b), i, a[i] if i < len(a) else None, b[i] if i < len(b) else None, m.get('defects'))
    ident = None
    for g, orc, eid, _ in [e + [''] * (4 - len(e)) for e in explained]:
        if fnmatch.fnmatch(o['case'], g) and (orc in ('*', oracle_name)):
            ident = eid
            break
    if ident is None:
        unexplained.append((o['case'], note))
    else:
        by_id[ident] = by_id.get(ident, 0) + 1
print('%s: compared %d, identical %d, differ %d (explained %d, unexplained %d), oracle could not expand %d' % (oracle_name, same + diff, same, diff, diff - len(unexplained), len(unexplained), err))
for k, v in sorted(by_id.items()):
    print('   %-40s %d' % (k, v))
for c, n in unexplained[:200 if verbose else 25]:
    print('UNEXPLAINED', c, n)
sys.exit(1 if unexplained else 0)
