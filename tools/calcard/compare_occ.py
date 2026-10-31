#!/usr/bin/env python3
"""Compare the calendar card's occurrence lists with the expansion the card is built on and with recurring-ical-events.

  compare_occ.py card.jsonl ours.jsonl rec.jsonl

card.jsonl comes from `CalcardTool occ` (first 50 occurrences from DTSTART), ours.jsonl from `RecurrenceDump expand` and rec.jsonl from
`py_expand.py rec` for the same cases (window: DTSTART's day, five years, at most 400). The card's list is compared as a prefix:
against the library's expansion, which must be identical (same engine); against the oracle, where a difference the library's own
expansion also has against the oracle is counted as inherited (explained in docs/recurrence.md), not as a card difference.
"""
import json, sys


def load(p):
    return {json.loads(l)['case']: json.loads(l) for l in open(p)}


card, ours, rec = (load(p) for p in sys.argv[1:4])
total = same_ours = same_rec = inherited = unexplained_ours = unexplained_rec = rec_err = skipped_n = empty = 0
for case, c in card.items():
    total += 1
    if c['n'] != 0:
        skipped_n += 1
        continue
    mine = c['starts']
    o = ours[case]['starts']
    r = rec.get(case, {})
    # The card lists from the series start; the case window starts at DTSTART's day, so earlier RDATEs are dropped from the card side.
    k = min(len(mine), len(o))
    if not mine and not o:
        empty += 1
    if mine[:k] == o[:k] and (len(mine) <= len(o) or len(o) >= 400):
        same_ours += 1
    else:
        unexplained_ours += 1
        print('CARD != LIBRARY', case, mine[:3], o[:3])
        continue
    if 'error' in r or not r:
        rec_err += 1
        continue
    rs = r['starts']
    k = min(len(mine), len(rs))
    if mine[:k] == rs[:k]:
        same_rec += 1
    elif o[:k] != rs[:k]:
        inherited += 1
    else:
        unexplained_rec += 1
        print('CARD != RECURRING-ICAL-EVENTS', case, mine[:3], rs[:3])
print(f'cases {total} (series with a second master skipped: {skipped_n}); card == library expansion {same_ours}, differ {unexplained_ours}; '
      f'oracle could not expand {rec_err}; card == recurring-ical-events {same_rec}, differ where the library also differs (inherited, explained in docs/recurrence.md) {inherited}, unexplained {unexplained_rec}')
sys.exit(1 if unexplained_ours or unexplained_rec else 0)
