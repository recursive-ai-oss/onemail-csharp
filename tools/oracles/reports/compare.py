#!/usr/bin/env python3
"""Runs the report drivers over a directory of messages and compares OneMail with each oracle.

    compare.py run --root DIR --out OUT --bin BIN --python PY [--files-from LIST]
    compare.py urljoin --pairs FILE --out OUT --bin BIN --python PY

Drivers (tools/oracles/reports/README.md): OneMailReports, MimeKitReports (both .NET) and pyreports.py. Every difference
between OneMail and an oracle must match a line of explained.tsv or the run fails (exit 1).
"""
import argparse
import collections
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def drivers(a):
    dn = os.path.join(os.environ.get("DOTNET_ROOT", os.path.expanduser("~/dotnet10")), "dotnet")
    return {
        "onemail": [dn, os.path.join(a.bin, "onemail", "OneMailReports.dll")],
        "mimekit": [dn, os.path.join(a.bin, "mimekit", "MimeKitReports.dll")],
        "python": [a.python, os.path.join(HERE, "pyreports.py")],
    }


def run_driver(cmd, root, names, extra=()):
    p = subprocess.run(cmd + list(extra) + ["--root", root], input="\n".join(names) + "\n", capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = []
    for line in p.stdout.splitlines():
        if line.strip():
            out.append(json.loads(line))
    if len(out) != len(names):
        sys.stderr.write("driver %s returned %d of %d records\n%s\n" % (cmd[-1], len(out), len(names), p.stderr[-2000:]))
    return out


FOLD = re.compile(r"\r?\n[ \t]+")


def norm(s):
    # The common ground: a fold becomes one space and both ends are trimmed.
    return FOLD.sub(" ", s).strip(" \t\r\n") if s is not None else None


def lower_name(s):
    return s.lower()


def diffs(file, ours, theirs, oracle):
    """Yields (class, subclass, detail)."""
    if theirs.get("error"):
        yield ("parse-failed", oracle, theirs["error"])
        return
    if ours.get("error"):
        yield ("parse-failed", "onemail", ours["error"])
        return
    a, b = ours.get("reports") or [], theirs.get("reports")
    if b is not None:
        if [r["type"].lower() for r in a] != [r["type"].lower() for r in b]:
            yield ("report.types", "", "%s vs %s" % ([r["type"] for r in a], [r["type"] for r in b]))
        else:
            for i, (ra, rb) in enumerate(zip(a, b)):
                if rb["blocks"] is None:
                    continue
                if len(ra["blocks"]) != len(rb["blocks"]):
                    yield ("report.blocks", ra["type"], "%d vs %d" % (len(ra["blocks"]), len(rb["blocks"])))
                    continue
                for j, (ba, bb) in enumerate(zip(ra["blocks"], rb["blocks"])):
                    na = [(lower_name(k), norm(v)) for k, v in ba]
                    nb = [(lower_name(k), norm(v)) for k, v in bb]
                    if [k for k, _ in na] != [k for k, _ in nb]:
                        yield ("report.names", ra["type"], "block %d: %s vs %s" % (j, [k for k, _ in na], [k for k, _ in nb]))
                    elif na != nb:
                        for (k, va), (_, vb) in zip(na, nb):
                            if va != vb:
                                yield ("report.value", k, "block %d: %r vs %r" % (j, va, vb))
    a, b = ours.get("auth"), theirs.get("auth")
    if a is not None and b is not None:
        if len(a) != len(b):
            yield ("auth.count", "", "%d vs %d" % (len(a), len(b)))
        else:
            for i, (xa, xb) in enumerate(zip(a, b)):
                if xb.get("failed"):
                    kinds = sorted(set(xa.get("defects") or []))
                    yield ("auth.parse-failed", oracle, "field %d: ours defects=%s" % (i, "|".join(kinds) or "none"))
                    continue
                if (norm(xa["authserv"]) or "") != (norm(xb["authserv"]) or ""):
                    yield ("auth.authserv", "", "%r vs %r" % (xa["authserv"], xb["authserv"]))
                if xa["version"] != xb["version"]:
                    yield ("auth.version", "", "%r vs %r" % (xa["version"], xb["version"]))
                ra, rb = xa["results"], xb["results"]
                if len(ra) != len(rb):
                    yield ("auth.results", "count", "field %d: %s vs %s" % (i, [r["method"] for r in ra], [r["method"] for r in rb]))
                    continue
                for k, (ya, yb) in enumerate(zip(ra, rb)):
                    for key in ("method", "result"):
                        if (ya[key] or "").lower() != (yb[key] or "").lower():
                            yield ("auth.result", key, "%r vs %r" % (ya[key], yb[key]))
                    if ya["mv"] != yb["mv"]:
                        yield ("auth.result", "mv", "%r vs %r" % (ya["mv"], yb["mv"]))
                    if norm(ya["comment"]) != norm(yb["comment"]):
                        yield ("auth.result", "comment", "%r vs %r" % (ya["comment"], yb["comment"]))
                    if norm(ya["reason"]) != norm(yb["reason"]):
                        yield ("auth.result", "reason", "%r vs %r" % (ya["reason"], yb["reason"]))
                    pa = [(t.lower(), n.lower(), norm(v)) for t, n, v in ya["props"]]
                    pb = [(t.lower(), n.lower(), norm(v)) for t, n, v in yb["props"]]
                    if pa != pb:
                        yield ("auth.props", ya["method"], "%s vs %s" % (pa, pb))
    a, b = ours.get("mhtml"), theirs.get("mhtml")
    if a is not None and b is not None:
        if a["parts"] != b["parts"]:
            yield ("mhtml.parts", "", "%d vs %d" % (a["parts"], b["parts"]))
        if a["root"] != b["root"]:
            yield ("mhtml.root", "", "%d vs %d" % (a["root"], b["root"]))
        for ra, rb in zip(a["refs"], b["refs"]):
            if rb["hit"] is None:
                continue
            if ra["hit"] != rb["hit"]:
                yield ("mhtml.hit", ra["ref"], "%d vs %d" % (ra["hit"], rb["hit"]))


def load_rules():
    rules = []
    with open(os.path.join(HERE, "explained.tsv"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            p = line.rstrip("\n").split("\t")
            while len(p) < 6:
                p.append("")
            rules.append((p[0], p[1], re.compile(p[2] or ".*"), re.compile(p[3] or ".*"), re.compile(p[4] or ".*"), re.compile(p[5] or ".*")))
    return rules


def explain(rules, oracle, file, cls, sub, detail):
    for rid, orc, rf, rc, rs, rd in rules:
        if orc in ("*", oracle) and rf.search(file) and rc.fullmatch(cls) and rs.search(sub) and rd.search(detail):
            return rid
    return None


def cmd_run(a):
    names = sorted(n for n in os.listdir(a.root) if n.endswith(".eml") or n.endswith(".msg") or n.endswith(".txt") or "." not in n)
    if a.files_from:
        names = [l.strip() for l in open(a.files_from) if l.strip()]
    d = drivers(a)
    os.makedirs(a.out, exist_ok=True)
    dumps = {}
    for k, cmd in d.items():
        dumps[k] = {r["id"]: r for r in run_driver(cmd, a.root, names)}
        with open(os.path.join(a.out, k + ".jsonl"), "w", encoding="utf-8") as f:
            for r in dumps[k].values():
                f.write(json.dumps(r) + "\n")
    rules = load_rules()
    counts = collections.defaultdict(lambda: collections.Counter())
    compared = collections.Counter()
    unexplained = []
    for n in names:
        ours = dumps["onemail"].get(n)
        if ours is None:
            continue
        for oracle in ("mimekit", "python"):
            theirs = dumps[oracle].get(n)
            if theirs is None:
                continue
            compared[oracle] += 1
            file_diff = False
            for cls, sub, detail in diffs(n, ours, theirs, oracle):
                file_diff = True
                rid = explain(rules, oracle, n, cls, sub, detail)
                if rid is None:
                    unexplained.append((oracle, n, cls, sub, detail))
                else:
                    counts[oracle][rid] += 1
            counts[oracle]["_files_with_differences"] += 1 if file_diff else 0
    lines = ["# report differential", ""]
    reports = {k: sum(len(r.get("reports") or []) for r in dumps[k].values()) for k in dumps}
    auth = {k: sum(len(r.get("auth") or []) for r in dumps[k].values()) for k in dumps}
    rt = [r.get("roundtrip") for r in dumps["onemail"].values()]
    lines.append("round trip byte-identical: %d of %d" % (sum(1 for x in rt if x), len(rt)))
    lines.append("files: %d; report parts found: %s; Authentication-Results fields found: %s" % (len(names), reports, auth))
    for oracle in ("mimekit", "python"):
        lines.append("")
        lines.append("## OneMail vs %s: %d files compared, %d with differences, %d unexplained differences" % (oracle, compared[oracle], counts[oracle]["_files_with_differences"], sum(1 for u in unexplained if u[0] == oracle)))
        for rid, c in sorted(counts[oracle].items()):
            if not rid.startswith("_"):
                lines.append("- %s: %d" % (rid, c))
    if unexplained:
        lines.append("")
        lines.append("## unexplained")
        for u in unexplained:
            lines.append("- %s %s %s %s: %s" % u)
    open(os.path.join(a.out, "report.md"), "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print("\n".join(lines))
    return 1 if unexplained else 0


def cmd_urljoin(a):
    d = drivers(a)
    pairs = [l.rstrip("\n") for l in open(a.pairs, encoding="utf-8") if l.strip()]
    ours = run_driver(d["onemail"], ".", pairs, ["--urljoin"])
    theirs = run_driver(d["python"], ".", pairs, ["--urljoin"])
    rules = load_rules()
    unexplained = []
    same = 0
    counts = collections.Counter()
    for o, t in zip(ours, theirs):
        if o["result"] == t["result"]:
            same += 1
            continue
        rid = explain(rules, "python", o["base"] + " + " + o["ref"], "urljoin", "", "%s vs %s" % (o["result"], t["result"]))
        if rid is None:
            unexplained.append((o["base"], o["ref"], o["result"], t["result"]))
        else:
            counts[rid] += 1
    print("urljoin: %d pairs, %d identical, %d explained %s, %d unexplained" % (len(pairs), same, sum(counts.values()), dict(counts), len(unexplained)))
    for u in unexplained[:50]:
        print("  base=%r ref=%r ours=%r python=%r" % u)
    return 1 if unexplained else 0


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("run", "urljoin"):
        p = sub.add_parser(name)
        p.add_argument("--out", required=True)
        p.add_argument("--bin", required=True)
        p.add_argument("--python", default=os.path.expanduser("~/onemail/oracles/py313/bin/python"))
        if name == "run":
            p.add_argument("--root", required=True)
            p.add_argument("--files-from")
        else:
            p.add_argument("--pairs", required=True)
    a = ap.parse_args()
    sys.exit(cmd_run(a) if a.cmd == "run" else cmd_urljoin(a))


if __name__ == "__main__":
    main()
