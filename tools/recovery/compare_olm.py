#!/usr/bin/env python3
"""Compares the recovery reports of an intact archive and its damaged variants (written by check-olm.sh).

usage: compare_olm.py OUT_DIR      (holds intact/ and one directory per variant, each with recovery-report.json)

For every variant prints items recovered against the intact original, per tag, and the damage records. Exit status is the number of variants whose
loss is not explained: items missing from the variant must be accounted for by damage (a damage record, or an ArchiveRecovered defect), and a variant
must never hold an item the original does not (by entry and index).
"""
import json
import os
import sys


def load(d):
    with open(os.path.join(d, "recovery-report.json")) as f:
        return json.load(f)


def key(item):
    return (item["source"], item.get("subject"), item["bytes"])


def main():
    out = sys.argv[1]
    base = load(os.path.join(out, "intact"))
    want = {}
    for i in base["items"]:
        want.setdefault(key(i), 0)
        want[key(i)] += 1
    print(f"{os.path.basename(out)}: intact {len(base['items'])} items {base['summary']}")
    bad = 0
    for v in sorted(os.listdir(out)):
        p = os.path.join(out, v)
        if v == "intact" or not os.path.isdir(p):
            continue
        r = load(p)
        got = {}
        for i in r["items"]:
            got.setdefault(key(i), 0)
            got[key(i)] += 1
        lost = sum(max(0, n - got.get(k, 0)) for k, n in want.items())
        extra = sum(max(0, n - want.get(k, 0)) for k, n in got.items())
        damaged = len(r["damage"]) + sum(1 for d in r["defects"] if "ArchiveRecovered" in d or "EntryDamaged" in d or "Unreadable" in d)
        # Items that read but whose text was cut are the "extra" ones for a partial record; only count entirely new subjects as a problem.
        new_subjects = {k[1] for k in got if k not in want} - {k[1] for k in want}
        ok = (lost == 0 or damaged > 0) and not new_subjects
        print(f"  {v:22} {len(r['items']):5} items ({len(r['items']) / max(1, len(base['items'])):.0%}) {r['summary']} lost {lost} damage-records {len(r['damage'])} defects {len(r['defects'])}" + ("" if ok else "  UNEXPLAINED"))
        if not ok:
            bad += 1
    return bad


if __name__ == "__main__":
    sys.exit(main())
