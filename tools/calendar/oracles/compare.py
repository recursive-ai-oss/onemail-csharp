#!/usr/bin/env python3
"""Compare two facts files (CalendarDump / py_facts.py / the .NET oracle drivers).

  compare.py ours.jsonl oracle.jsonl [--show N] [--only-prefix DIR] [--ignore NAME,NAME...]

A file counts as compared when both sides parsed it. Facts are compared as multisets of (path, name, params, value).
Prints counts and the differences grouped by property name and kind (only-ours / only-oracle).
"""
import argparse, collections, json


def load(p):
    d = {}
    for line in open(p, encoding="utf-8"):
        r = json.loads(line)
        d[r["file"]] = r
    return d


def key(f):
    return (f[0], f[1], "[]" if A_NOPARAMS else json.dumps(f[2], sort_keys=True), f[3])


A_NOPARAMS = False
A_SKIPZONED = False


def strip_untyped(ours_facts, orc_facts):
    """An oracle that returns a typed property as plain text (t:<raw>) has not parsed it; when that text is our raw
    value, treat the two as agreeing on everything we can compare and count the property as 'oracle-untyped'."""
    raw = collections.Counter((x[0], x[1], json.dumps(x[2], sort_keys=True), "t:" + x[4]) for x in ours_facts if len(x) > 4 and x[4] and not x[3].startswith("t:"))
    n = 0
    out = []
    for x in orc_facts:
        k = key(x)
        if k[3].startswith("t:") and raw[k] > 0:
            raw[k] -= 1
            n += 1
            continue
        out.append(x)
    return out, n


ap = argparse.ArgumentParser()
ap.add_argument("ours")
ap.add_argument("oracle")
ap.add_argument("--show", type=int, default=0)
ap.add_argument("--only-prefix", default="")
ap.add_argument("--ignore", default="")
ap.add_argument("--by", default="name", choices=["name", "value"])
ap.add_argument("--only", default="", help="compare only these property names (components always compared)")
ap.add_argument("--no-params", action="store_true", help="ignore parameters (an oracle that drops vCard 2.1 bare types)")
ap.add_argument("--skip-zoned", action="store_true", help="ignore properties with a TZID parameter (an oracle that converts zones)")
a = ap.parse_args()
A_NOPARAMS, A_SKIPZONED = a.no_params, a.skip_zoned
ignore = set(x.upper() for x in a.ignore.split(",") if x)
only = set(x.upper() for x in a.only.split(",") if x)
ours, orc = load(a.ours), load(a.oracle)
files = [f for f in ours if f in orc and f.startswith(a.only_prefix)]
oracle_err = [f for f in files if "error" in orc[f]]
ours_err = [f for f in files if "error" in ours[f]]
cmp = [f for f in files if "error" not in orc[f] and "error" not in ours[f]]
same, groups, shown, untyped = 0, collections.Counter(), 0, 0
examples = {}
diff_files = []
for f in cmp:
    ofacts, orcfacts = ours[f]["facts"], orc[f]["facts"]
    if A_SKIPZONED:
        ofacts = [x for x in ofacts if not any(p[0] == "TZID" for p in x[2])]
        orcfacts = [x for x in orcfacts if not any(p[0] in ("TZID", "X-VOBJ-ORIGINAL-TZID") for p in x[2])]
    orcfacts2, nu = strip_untyped(ofacts, orcfacts)
    untyped += nu
    # drop our typed counterpart of an untyped oracle property
    drop = collections.Counter(key(x)[:3] for x in orcfacts if x not in orcfacts2)
    ofacts2 = []
    for x in ofacts:
        k3 = key(x)[:3]
        if drop[k3] > 0 and not x[3].startswith("t:"):
            drop[k3] -= 1
            continue
        ofacts2.append(x)
    def keep(x):
        n = x[1].upper()
        return n not in ignore and (not only or n == "" or n in only)
    A = collections.Counter(key(x) for x in ofacts2 if keep(x))
    B = collections.Counter(key(x) for x in orcfacts2 if keep(x))
    oa, ob = A - B, B - A
    if not oa and not ob:
        same += 1
        continue
    diff_files.append(f)
    for k in oa:
        g = ("only-ours", k[1] if a.by == "name" else k[1] + " " + k[3].split(":")[0])
        groups[g] += oa[k]
        examples.setdefault(g, (f, k))
    for k in ob:
        g = ("only-oracle", k[1] if a.by == "name" else k[1] + " " + k[3].split(":")[0])
        groups[g] += ob[k]
        examples.setdefault(g, (f, k))
print(f"oracle returned {untyped} typed properties as plain text (not compared)")
print(f"files in both: {len(files)}; oracle rejected/crashed: {len(oracle_err)}; ours crashed: {len(ours_err)}; compared: {len(cmp)}; identical facts: {same}; differing files: {len(diff_files)}")
if oracle_err:
    c = collections.Counter(orc[f]["error"] for f in oracle_err)
    print("  oracle errors:", dict(c))
for (side, name), n in groups.most_common(a.show or 40):
    f, k = examples[(side, name)]
    print(f"  {side:12} {name:28} {n:6}  e.g. {f}: {k[0]} {k[1]} {k[2]} {k[3][:90]!r}")
