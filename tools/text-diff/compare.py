#!/usr/bin/env python3
"""Compares OneMail's output with each oracle's.

  compare.py cases.jsonl ours.jsonl py.jsonl go.jsonl mimekit.jsonl [--diffs out.jsonl] [--unexplained]

Each oracle file has one line per case: {"id", "r": hex} | {"n":1} | {"e": err} | {"s":1} (skipped).
A difference is "explained" when explain.py's RULES give it a reason (a documented resolution); the report lists counts per
oracle: compared, same, explained (by reason), unexplained. Exit status 1 when anything is unexplained.
"""
import json
import sys
from collections import Counter, defaultdict

try:
    from explain import explain
except ImportError:  # first run, before any rule exists
    def explain(oracle, case, ours, theirs):
        return None


def load(path):
    d = {}
    for line in open(path):
        r = json.loads(line)
        d[r["id"]] = r
    return d


def show(r):
    if r is None:
        return "<missing>"
    if "r" in r:
        return bytes.fromhex(r["r"]).decode("utf-8", "replace")
    if "n" in r:
        return "<absent>"
    if "s" in r:
        return "<skipped>"
    return "<error " + r.get("e", "?") + ">"


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    diffs_path = None
    if "--diffs" in sys.argv:
        diffs_path = sys.argv[sys.argv.index("--diffs") + 1]
        args.remove(diffs_path)
    cases = [json.loads(l) for l in open(args[0])]
    ours = load(args[1])
    oracles = {"python": load(args[2]), "go": load(args[3]), "mimekit": load(args[4])}
    out = open(diffs_path, "w") if diffs_path else None
    unexplained_total = 0
    for name, res in oracles.items():
        counts = Counter()
        reasons = Counter()
        by_group = defaultdict(Counter)
        for c in cases:
            o, t = ours[c["id"]], res.get(c["id"])
            if t is None or "s" in t:
                counts["skipped"] += 1
                continue
            counts["compared"] += 1
            same = (o.get("r"), "n" in o, "e" in o) == (t.get("r"), "n" in t, "e" in t) and "e" not in o
            if same:
                counts["same"] += 1
                continue
            why = explain(name, c, o, t)
            if why:
                counts["explained"] += 1
                reasons[why] += 1
            else:
                counts["unexplained"] += 1
                by_group[c["group"]]["n"] += 1
                if out:
                    out.write(json.dumps({"oracle": name, "id": c["id"], "group": c["group"], "case": c.get("hex") and bytes.fromhex(c["hex"]).decode("latin-1") or c.get("hdr"),
                                          "ours": show(o), "theirs": show(t), "issues": o.get("i", "")}, ensure_ascii=False) + "\n")
        unexplained_total += counts["unexplained"]
        print("== %s: compared %d, same %d, explained %d, unexplained %d, skipped %d" % (name, counts["compared"], counts["same"], counts["explained"], counts["unexplained"], counts["skipped"]))
        for why, n in sorted(reasons.items(), key=lambda kv: -kv[1]):
            print("     explained %-44s %d" % (why, n))
        for g, c2 in sorted(by_group.items(), key=lambda kv: -kv[1]["n"]):
            print("     unexplained group %-28s %d" % (g, c2["n"]))
    return 1 if unexplained_total else 0


if __name__ == "__main__":
    sys.exit(main())
