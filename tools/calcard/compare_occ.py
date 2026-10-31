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
cases = load(__import__('os').path.join(__import__('os').path.dirname(__import__('os').path.abspath(sys.argv[2])), 'cases.jsonl'))
bounded = total = same_ours = same_rec = inherited = unexplained_ours = unexplained_rec = rec_err = skipped_n = empty = 0
for case, c in card.items():
    total += 1
    if c['n'] != 0:
        skipped_n += 1
        continue
    ws, we = cases[case]['start'], cases[case]['end']
    # The card lists from the series start without a window; the case window is [DTSTART's day, five years later), so the card's list is cut to it.
    full = c['starts']
    mine = [x for x in full if ws[:len(x)] <= x < we[:len(x)] or (len(x) == 8 and ws[:8] <= x < we[:8])]
    capped = len(full) >= 50 and len(mine) == len(full)
    o = ours[case]['starts']
    r = rec.get(case, {})
    if not mine and not o:
        empty += 1
    k = len(mine)
    if mine == o[:k] and (capped or len(o) == k):
        same_ours += 1
    elif 'ExpansionLimited' in c.get('defects', []) and mine == o[:k]:
        bounded += 1  # the card stops earlier than the library by design (200,000 instances scanned) and says so with a defect
        continue
    else:
        unexplained_ours += 1
        print('CARD != LIBRARY', case, mine[:3], o[:3], len(mine), len(o))
        continue
    if 'error' in r or not r:
        rec_err += 1
        continue
    rs = r['starts']
    if mine == rs[:k]:
        same_rec += 1
    elif o[:k] != rs[:k]:
        inherited += 1
    else:
        unexplained_rec += 1
        print('CARD != RECURRING-ICAL-EVENTS', case, mine[:3], rs[:3])
print(f'cases {total} (series with a second master skipped: {skipped_n}; empty in both: {empty}); card == library expansion {same_ours}, stopped early by the card's own scan limit (defect ExpansionLimited, a prefix of the library's list) {bounded}, differ {unexplained_ours}; '
      f'oracle could not expand {rec_err}; card == recurring-ical-events {same_rec}, differ where the library also differs (inherited, explained in docs/recurrence.md) {inherited}, unexplained {unexplained_rec}')
sys.exit(1 if unexplained_ours or unexplained_rec else 0)
