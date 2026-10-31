#!/usr/bin/env python3
"""Differential runner: runs the oracle drivers over a corpus, compares their dumps pairwise and reports.

    diffrun.py run     --corpus NAME --root DIR --out DIR --bin DIR [--drivers mimekit,python,go] [--chunk 1000] [--jobs 8]
                       [--max-seconds N] [--driver NAME=COMMAND ...] [--limit N] [--sample N --seed S]
    diffrun.py compare --corpus NAME --out DIR [--drivers ...]     (re-compare stored dumps, e.g. after adding a driver)
    diffrun.py report  --corpus NAME --out DIR                      (merge chunk summaries into report.md / report.json)
    diffrun.py cases   --corpus NAME --out DIR                      (per-message digest table, for small corpora)

The work is resumable: a chunk whose dump files and summary exist is skipped. `run` exits 75 when `--max-seconds` ran out
before every chunk was done; run it again to continue. The dump form and the driver contract are in README.md.
"""
import argparse
import concurrent.futures as cf
import gzip
import json
import os
import random
import re
import shlex
import subprocess
import sys
import time
from collections import Counter, defaultdict
from itertools import combinations

CHUNK_TIMEOUT = 900
HERE = os.path.dirname(os.path.abspath(__file__))


# ---- drivers -----------------------------------------------------------------------------------------------------

def default_commands(bin_dir):
    pydump = os.path.join(HERE, "python", "pydump.py")
    return {
        "mimekit": [os.path.join(bin_dir, "mimekit", "MimeKitDump"), "--root", "{root}"],
        "python": [sys.executable, pydump, "--root", "{root}"],
        "python-compat32": [sys.executable, pydump, "--policy", "compat32", "--root", "{root}"],
        "go": [os.path.join(bin_dir, "godump"), "-root", "{root}"],
        "onemail": [sys.executable, os.path.join(HERE, "onemail", "onemail_stub.py"), "--root", "{root}"],
    }


def run_driver(cmd, root, files):
    """Runs one driver over `files`; returns {id: record}. A crash is bisected down to the file that caused it."""
    argv = [a.replace("{root}", root) for a in cmd]
    try:
        p = subprocess.run(argv, input="\n".join(files).encode("utf-8", "surrogateescape") + b"\n",
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=CHUNK_TIMEOUT)
        out, rc, err = p.stdout, p.returncode, p.stderr
    except subprocess.TimeoutExpired:
        out, rc, err = b"", -9, b"timeout"
    recs = {}
    for line in out.split(b"\n"):
        if line:
            try:
                r = json.loads(line)
                recs[r["id"]] = r
            except (ValueError, KeyError):
                break
    missing = [f for f in files if f not in recs]
    if missing:
        if len(missing) == 1:
            recs[missing[0]] = {"schema": "onemail-dump/1", "id": missing[0], "oracle": {"name": "?", "version": "?", "notes": ""},
                                "error": "driver-crash rc=%s %s" % (rc, err[-200:].decode("utf-8", "replace")), "defects": None, "crash": True}
        else:
            half = len(missing) // 2
            recs.update(run_driver(cmd, root, missing[:half]))
            recs.update(run_driver(cmd, root, missing[half:]))
    return recs


# ---- normalisation and comparison ----------------------------------------------------------------------------------

FOLD = re.compile(r"[ \t]*\r?\n[ \t]+")


def unfold(raw):
    """Header value normalisation: a fold (line break plus following whitespace) becomes one space; the ends are trimmed."""
    return FOLD.sub(" ", raw).strip(" \t\r\n")


def header_diffs(a, b, ordered, out, prefix):
    ha = [(h["name"].lower(), unfold(h["raw"]), h.get("value")) for h in a or []]
    hb = [(h["name"].lower(), unfold(h["raw"]), h.get("value")) for h in b or []]
    # Some libraries keep Content-* fields apart from the others, so their relative order is not comparable.
    oa = [h[0] for h in ha if not h[0].startswith("content-")]
    ob = [h[0] for h in hb if not h[0].startswith("content-")]
    ca, cb = Counter(h[0] for h in ha), Counter(h[0] for h in hb)
    if ca != cb:
        for n in sorted((ca - cb).keys() | (cb - ca).keys()):
            side = "a" if ca[n] > cb[n] else "b"
            out.append((prefix + "header.names", n + (" (more in %s)" % side), "%s: %d vs %d" % (n, ca[n], cb[n])))
    if ordered and ca == cb and oa != ob:
        out.append((prefix + "header.order", "", ",".join(oa[:12]) + " | " + ",".join(ob[:12])))
    for n in sorted(ca.keys() & cb.keys()):
        va = [h for h in ha if h[0] == n]
        vb = [h for h in hb if h[0] == n]
        for x, y in zip(va, vb):
            if x[1] != y[1]:
                out.append((prefix + "header.raw", n, "%s: %r vs %r" % (n, x[1][:80], y[1][:80])))
                break
        for x, y in zip(va, vb):
            if x[2] is not None and y[2] is not None and x[2] != y[2]:
                out.append((prefix + "header.decoded", n, "%s: %r vs %r" % (n, x[2][:80], y[2][:80])))
                break


def entity_diffs(a, b, ordered, out, depth):
    prefix = "part." if depth else ""
    header_diffs(a.get("headers"), b.get("headers"), ordered, out, prefix)
    if a["contentType"] != b["contentType"]:
        out.append(("entity.type", "%s -> %s" % (a["contentType"], b["contentType"]), "%s vs %s" % (a["contentType"], b["contentType"])))
    pa, pb = a.get("params"), b.get("params")
    if pa is not None and pb is not None and pa != pb:
        keys = sorted(k for k in pa.keys() | pb.keys() if pa.get(k) != pb.get(k))
        out.append(("entity.params", ",".join(keys), "; ".join("%s=%r vs %r" % (k, pa.get(k), pb.get(k)) for k in keys[:3])))
    for f, cls in (("disposition", "entity.disposition"), ("filename", "entity.filename"), ("cteApplied", "entity.cte")):
        if a.get(f) != b.get(f):
            out.append((cls, "", "%r vs %r" % (a.get(f), b.get(f))))
    for f in ("preamble", "epilogue"):
        if a.get(f) is not None and b.get(f) is not None and a[f] != b[f]:
            out.append(("entity." + f, "", "%r vs %r" % (a[f], b[f])))
    ba, bb = a.get("body"), b.get("body")
    if (ba is None) != (bb is None):
        out.append(("body.presence", "", "a=%s b=%s" % (ba is not None, bb is not None)))
    elif ba is not None and ba["sha256"] != bb["sha256"]:
        if ba["eolSha256"] == bb["eolSha256"]:
            out.append(("body.eol", "", "len %d vs %d" % (ba["len"], bb["len"])))
        elif ba["trimSha256"] == bb["trimSha256"]:
            out.append(("body.final-newline", "", "len %d vs %d" % (ba["len"], bb["len"])))
        else:
            out.append(("body.content", "", "len %d vs %d" % (ba["len"], bb["len"])))
    ca, cb = a.get("children") or [], b.get("children") or []
    if len(ca) != len(cb):
        out.append(("entity.children", "%d vs %d" % (min(len(ca), 9), min(len(cb), 9)), "%d vs %d parts" % (len(ca), len(cb))))
    for x, y in zip(ca, cb):
        entity_diffs(x, y, ordered, out, depth + 1)
    ma, mb = a.get("message"), b.get("message")
    if (ma is None) != (mb is None):
        out.append(("entity.message", "", "a=%s b=%s" % (ma is not None, mb is not None)))
    elif ma is not None:
        entity_diffs(ma, mb, ordered, out, depth + 1)


def compare_records(a, b):
    """Returns a list of (class, sub, detail) differences between two dumps of the same message."""
    out = []
    na, nb = a["oracle"]["name"], b["oracle"]["name"]
    ea, eb = a.get("error"), b.get("error")
    if ea or eb:
        if bool(ea) != bool(eb):
            out.append(("parse-failed", na if ea else nb, (ea or eb)[:120]))
        return out
    ordered = a["oracle"].get("headersOrdered", True) and b["oracle"].get("headersOrdered", True)
    ada, adb = a.get("addresses") or {}, b.get("addresses") or {}
    for k in sorted(ada.keys() | adb.keys()):
        x, y = ada.get(k, "absent"), adb.get(k, "absent")
        if isinstance(x, str) or isinstance(y, str):
            out.append(("address.presence", k, k))
        elif x is None or y is None:
            out.append(("address.parse-failed", k + ":" + (na if x is None else nb), k))
        elif x != y:
            if len(x) != len(y):
                out.append(("address.count", k, "%s: %d vs %d" % (k, len(x), len(y))))
            elif [i["addr"] for i in x] != [i["addr"] for i in y]:
                out.append(("address.addr", k, "%s: %r vs %r" % (k, [i["addr"] for i in x][:3], [i["addr"] for i in y][:3])))
            else:
                out.append(("address.name", k, "%s: %r vs %r" % (k, [i["name"] for i in x][:3], [i["name"] for i in y][:3])))
    da, db = a.get("date"), b.get("date")
    if (da is None) != (db is None):
        out.append(("date.parse-failed", na if da is None else nb, "%r vs %r" % (da, db)))
    elif da is not None:
        if da["utc"] != db["utc"]:
            out.append(("date.instant", "", "%s vs %s" % (da["utc"], db["utc"])))
        elif da.get("offsetMinutes") is not None and db.get("offsetMinutes") is not None and da["offsetMinutes"] != db["offsetMinutes"]:
            out.append(("date.offset", "", "%s vs %s" % (da["offsetMinutes"], db["offsetMinutes"])))
    entity_diffs(a["root"], b["root"], ordered, out, 0)
    return out


def digest(rec):
    """A one-line view of a dump, for the case table."""
    if rec.get("error"):
        return "ERROR " + rec["error"][:60]
    parts = []

    def walk(e, depth):
        b = e.get("body")
        parts.append("%s%s%s%s%s" % ("  " * depth, e["contentType"],
                                      "" if not e.get("params") else " " + ",".join("%s=%s" % (k, v[:20]) for k, v in sorted(e["params"].items())),
                                      "" if not b else " [%d:%s]" % (b["len"], b["sha256"][:6]),
                                      "" if not e.get("filename") else " fn=%r" % e["filename"]))
        for c in e.get("children") or []:
            walk(c, depth + 1)
        if e.get("message"):
            walk(e["message"], depth + 1)

    walk(rec["root"], 0)
    text = " / ".join(p.strip() for p in parts[:6])
    for k, v in sorted((rec.get("addresses") or {}).items()):
        if k != "from" or v != [{"name": "Alice", "addr": "alice@example.com"}]:
            text += " | %s=%s" % (k, "FAIL" if v is None else [(i["name"], i["addr"]) for i in v])
    if rec.get("date"):
        text += " | date=%s%+d" % (rec["date"]["utc"], rec["date"]["offsetMinutes"] if rec["date"].get("offsetMinutes") is not None else 0)
    for h in rec["root"].get("headers") or []:
        if h.get("value") is not None:
            text += " | %s=%r" % (h["name"].lower(), h["value"])
    return text


# ---- storage -------------------------------------------------------------------------------------------------------

def dump_path(out, corpus, driver, chunk):
    return os.path.join(out, corpus, "dumps", driver, "%06d.jsonl.gz" % chunk)


def summary_path(out, corpus, chunk):
    return os.path.join(out, corpus, "compare", "%06d.json" % chunk)


def write_atomic(path, data, gz=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp%d" % os.getpid()
    with (gzip.open(tmp, "wb") if gz else open(tmp, "wb")) as f:
        f.write(data)
    os.replace(tmp, path)


def read_dump(path):
    recs = {}
    with gzip.open(path, "rb") as f:
        for line in f:
            r = json.loads(line)
            recs[r["id"]] = r
    return recs


def compare_chunk(out, corpus, drivers, chunk, files):
    dumps = {d: read_dump(dump_path(out, corpus, d, chunk)) for d in drivers}
    s = {"messages": len(files), "crashes": Counter(), "oracleErrors": Counter(), "versions": {}, "defects": defaultdict(Counter), "pairs": {}}
    for d in drivers:
        for r in dumps[d].values():
            if r["oracle"]["version"] != "?":
                s["versions"][d] = r["oracle"]["version"]
            if r.get("crash"):
                s["crashes"][d] += 1
            elif r.get("error") and not r.get("skipped"):
                s["oracleErrors"][d] += 1
            for name in r.get("defects") or []:
                s["defects"][d][re.sub(r"^part\d+:", "", name)] += 1
    for d1, d2 in combinations(drivers, 2):
        p = {"compared": 0, "agree": 0, "differ": 0, "bothFailed": 0, "classes": Counter(), "subs": defaultdict(Counter), "examples": {}}
        for f in files:
            a, b = dumps[d1].get(f), dumps[d2].get(f)
            if not a or not b or a.get("skipped") or b.get("skipped") or a.get("crash") or b.get("crash"):
                continue
            p["compared"] += 1
            if a.get("error") and b.get("error"):
                p["bothFailed"] += 1
                p["agree"] += 1
                continue
            diffs = compare_records(a, b)
            if not diffs:
                p["agree"] += 1
                continue
            p["differ"] += 1
            seen = set()
            for cls, sub, detail in diffs:
                if cls not in seen:
                    seen.add(cls)
                    p["classes"][cls] += 1
                    p["examples"].setdefault(cls, {"id": f, "sub": sub, "detail": detail})
                p["subs"][cls][sub] += 1
        s["pairs"]["%s~%s" % (d1, d2)] = p
    return s


def jsonable(o):
    if isinstance(o, dict):
        return {k: jsonable(v) for k, v in o.items()}
    return o


# ---- commands ---------------------------------------------------------------------------------------------------------

def chunk_task(args):
    out, corpus, root, drivers, cmds, chunk, files = args
    for d in drivers:
        p = dump_path(out, corpus, d, chunk)
        if not os.path.exists(p):
            recs = run_driver(cmds[d], root, files)
            for f in files:
                if recs[f]["oracle"]["name"] == "?":
                    recs[f]["oracle"]["name"] = d
            write_atomic(p, b"".join(json.dumps(recs[f], separators=(",", ":")).encode("utf-8") + b"\n" for f in files), gz=True)
    s = compare_chunk(out, corpus, drivers, chunk, files)
    write_atomic(summary_path(out, corpus, chunk), json.dumps(jsonable(s)).encode())
    return chunk


def list_files(root, out, corpus, limit=0, sample=0, seed=1):
    lp = os.path.join(out, corpus, "files.txt")
    if os.path.exists(lp):
        with open(lp, encoding="utf-8", errors="surrogateescape") as f:
            return [l.rstrip("\n") for l in f if l.strip()]
    files = []
    for dp, dn, fn in os.walk(root):
        dn.sort()
        for n in sorted(fn):
            if not n.startswith(".") and not n.endswith((".tsv", ".md")):
                files.append(os.path.relpath(os.path.join(dp, n), root))
    files.sort()
    if sample and sample < len(files):
        files = sorted(random.Random(seed).sample(files, sample))
    if limit:
        files = files[:limit]
    os.makedirs(os.path.dirname(lp), exist_ok=True)
    with open(lp, "w", encoding="utf-8", errors="surrogateescape") as f:
        f.write("\n".join(files) + "\n")
    return files


def cmd_run(a):
    cmds = default_commands(a.bin)
    for spec in a.driver or []:
        n, c = spec.split("=", 1)
        cmds[n] = shlex.split(c)
    drivers = a.drivers.split(",")
    files = list_files(a.root, a.out, a.corpus, a.limit, a.sample, a.seed)
    chunks = [files[i:i + a.chunk] for i in range(0, len(files), a.chunk)]
    for d in drivers:  # fail fast when a driver cannot start at all
        probe = run_driver(cmds[d], a.root, files[:1])
        if any(x.get("crash") for x in probe.values()):
            sys.exit("driver %s does not start: %s" % (d, [x["error"] for x in probe.values()][0][:300]))
    todo = [i for i in range(len(chunks)) if not os.path.exists(summary_path(a.out, a.corpus, i))]
    print("%s: %d files, %d chunks, %d to do" % (a.corpus, len(files), len(chunks), len(todo)), flush=True)
    t0 = time.time()
    done = 0
    pending = list(todo)
    with cf.ProcessPoolExecutor(a.jobs) as ex:
        running = set()
        while pending or running:
            while pending and len(running) < a.jobs and (not a.max_seconds or time.time() - t0 < a.max_seconds):
                i = pending.pop(0)
                running.add(ex.submit(chunk_task, (a.out, a.corpus, a.root, drivers, cmds, i, chunks[i])))
            if not running:
                break
            fin, running = cf.wait(running, return_when=cf.FIRST_COMPLETED)
            for f in fin:
                f.result()
                done += 1
                if done % 25 == 0:
                    print("  %d/%d chunks, %.0fs" % (done, len(todo), time.time() - t0), flush=True)
    left = [i for i in range(len(chunks)) if not os.path.exists(summary_path(a.out, a.corpus, i))]
    print("done %d chunks in %.0fs; %d left" % (done, time.time() - t0, len(left)), flush=True)
    return 75 if left else 0


def cmd_compare(a):
    drivers = a.drivers.split(",")
    files = list_files("", a.out, a.corpus)
    n = 0
    for i in range(0, len(files), a.chunk):
        s = compare_chunk(a.out, a.corpus, drivers, i // a.chunk, files[i:i + a.chunk])
        write_atomic(summary_path(a.out, a.corpus, i // a.chunk), json.dumps(jsonable(s)).encode())
        n += 1
    print("re-compared %d chunks" % n)
    return 0


def merge(out, corpus):
    total = {"messages": 0, "crashes": Counter(), "oracleErrors": Counter(), "versions": {}, "defects": defaultdict(Counter), "pairs": {}}
    cdir = os.path.join(out, corpus, "compare")
    for n in sorted(os.listdir(cdir)):
        if not n.endswith(".json"):
            continue
        with open(os.path.join(cdir, n)) as f:
            s = json.load(f)
        total["messages"] += s["messages"]
        total["crashes"].update(s["crashes"])
        total["oracleErrors"].update(s["oracleErrors"])
        total["versions"].update(s["versions"])
        for d, c in s["defects"].items():
            total["defects"][d].update(c)
        for k, p in s["pairs"].items():
            t = total["pairs"].setdefault(k, {"compared": 0, "agree": 0, "differ": 0, "bothFailed": 0, "classes": Counter(),
                                              "subs": defaultdict(Counter), "examples": {}})
            for f in ("compared", "agree", "differ", "bothFailed"):
                t[f] += p[f]
            t["classes"].update(p["classes"])
            for c, subs in p["subs"].items():
                t["subs"][c].update(subs)
            for c, ex in p["examples"].items():
                t["examples"].setdefault(c, ex)
    return total


def cmd_report(a):
    # Corpora of real people's mail never have message text in a report: only counts, classes and file names.
    private = a.private or a.corpus.startswith(("enron", "edrm"))
    t = merge(a.out, a.corpus)
    if private:
        # A sub-class can be a malformed header's "name", which is message text: keep only name-like ones.
        safe = re.compile(r"^[a-z0-9_.-]{1,40}( \(more in [ab]\))?$")
        for p in t["pairs"].values():
            for ex in p["examples"].values():
                ex["detail"] = "withheld"
                ex["sub"] = ex["sub"] if safe.match(ex["sub"]) else "(withheld)"
            for subs in p["subs"].values():
                for k in [k for k in subs if k and not safe.match(k) and " -> " not in k and " vs " not in k]:
                    subs["(withheld)"] += subs.pop(k)
    lines = ["# Differential report: %s" % a.corpus, "", "Messages: %d" % t["messages"], "",
             "Oracle versions: " + ", ".join("%s %s" % kv for kv in sorted(t["versions"].items())), "",
             "Driver crashes: %s" % (dict(t["crashes"]) or "none"), "",
             "Messages an oracle itself failed to parse: %s" % (dict(t["oracleErrors"]) or "none"), ""]
    for k, p in sorted(t["pairs"].items()):
        lines += ["## %s" % k, "", "compared %d, agree %d, differ %d (both failed: %d)" % (p["compared"], p["agree"], p["differ"], p["bothFailed"]), "",
                  "| class | messages | top sub-classes | example |", "|---|---|---|---|"]
        for cls, n in p["classes"].most_common():
            subs = ", ".join("%s×%d" % (s or "-", c) for s, c in p["subs"][cls].most_common(4))
            ex = p["examples"][cls]
            detail = "(withheld: private corpus)" if private else ex["detail"].replace("|", "/").replace("\n", " ")[:90]
            lines.append("| %s | %d | %s | %s: %s |" % (cls, n, subs.replace("|", "/"), ex["id"], detail))
        lines.append("")
    lines += ["## Oracle defect counts", ""]
    for d, c in sorted(t["defects"].items()):
        lines.append("- %s: %s" % (d, ", ".join("%s×%d" % kv for kv in c.most_common(12)) or "none"))
    text = "\n".join(lines) + "\n"
    with open(os.path.join(a.out, a.corpus, "report.md"), "w") as f:
        f.write(text)
    with open(os.path.join(a.out, a.corpus, "report.json"), "w") as f:
        json.dump(jsonable(t), f, indent=1)
    print(text)
    return 0


def cmd_cases(a):
    drivers = a.drivers.split(",")
    files = list_files("", a.out, a.corpus)
    dumps = {d: read_dump(dump_path(a.out, a.corpus, d, 0)) for d in drivers}
    for f in files:
        print("## " + f)
        for d in drivers:
            print("  %-16s %s" % (d, digest(dumps[d][f])))
        for d1, d2 in combinations(drivers, 2):
            diffs = compare_records(dumps[d1][f], dumps[d2][f])
            if diffs:
                print("  %s~%s: %s" % (d1, d2, "; ".join(sorted({c + (":" + s if s else "") for c, s, _ in diffs}))))
    return 0


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("run", "compare", "report", "cases"):
        p = sub.add_parser(name)
        p.add_argument("--corpus", required=True)
        p.add_argument("--out", required=True)
        p.add_argument("--drivers", default="mimekit,python,go")
        p.add_argument("--chunk", type=int, default=1000)
        if name == "run":
            p.add_argument("--root", required=True)
            p.add_argument("--bin", required=True)
            p.add_argument("--jobs", type=int, default=8)
            p.add_argument("--max-seconds", type=int, default=0)
            p.add_argument("--driver", action="append")
            p.add_argument("--limit", type=int, default=0)
            p.add_argument("--sample", type=int, default=0)
            p.add_argument("--seed", type=int, default=1)
        if name == "report":
            p.add_argument("--private", action="store_true", help="withhold message text from the report (automatic for enron*/edrm*)")
    a = ap.parse_args()
    sys.exit({"run": cmd_run, "compare": cmd_compare, "report": cmd_report, "cases": cmd_cases}[a.cmd](a))


if __name__ == "__main__":
    main()
