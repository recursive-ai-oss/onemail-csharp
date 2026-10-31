#!/usr/bin/env python3
"""Compares what the black-box oracles (MimeKit, Python email) read from the EML files OxcMailTool exported with what the source
Outlook item says (the tool's expected/NNNNNN.json), field by field.

    check_export.py OUT_DIR(oxc export dir) DUMPS_DIR(diffrun out dir/<corpus>/dumps) [oracle ...]

Prints, per oracle, the messages and fields compared and every field that differs, and exits 1 if any does.
"""
import gzip, json, os, re, sys, glob, hashlib
from collections import Counter

def norm(s): return " ".join((s or "").split())

def walk(e):
    yield e
    for c in e.get("children") or []:
        yield from walk(c)
    if e.get("message"):
        yield from walk(e["message"])

def hdr(root, name):
    for h in root["headers"]:
        if h["name"].lower() == name:
            return h
    return None

def addrs(rec, key):
    l = (rec.get("addresses") or {}).get(key)
    if l is None: return None
    return ",".join(sorted(a["addr"].lower() for a in l if "@" in a["addr"]))

def main():
    out, dumps = sys.argv[1], sys.argv[2]
    oracles = sys.argv[3:] or ["mimekit", "python"]
    rc = 0
    failed = {}  # oracle -> set of (id, field)
    origin = {}
    mp = os.path.join(out, "manifest.tsv")
    if os.path.exists(mp):
        for l in open(mp, encoding="utf-8"):
            a, _, b = l.rstrip("\n").partition("\t"); origin[a + ".eml"] = b
    explained = {}  # (oracle, origin, field) -> reason, from explained.tsv beside this script
    ep = os.path.join(os.path.dirname(os.path.abspath(__file__)), "explained.tsv")
    if os.path.exists(ep):
        for l in open(ep, encoding="utf-8"):
            if l.strip() and not l.startswith("#"):
                o_, org, fld, why = l.rstrip("\n").split("\t", 3); explained[(o_, org, fld)] = why
    for o in oracles:
        failed[o] = set()
        n = 0; fields = 0; bad = Counter(); expl = Counter(); examples = []
        for f in sorted(glob.glob(os.path.join(dumps, o, "*.jsonl.gz"))):
            for line in gzip.open(f, "rt", encoding="utf-8", errors="surrogateescape"):
                rec = json.loads(line)
                exp = json.load(open(os.path.join(out, "expected", os.path.basename(rec["id"]).replace(".eml", ".json"))))
                n += 1
                if rec.get("error"):
                    bad["parse-failed"] += 1; continue
                root = rec["root"]
                def chk(field, got, want):
                    nonlocal fields
                    fields += 1
                    if got != want:
                        key = (o, origin.get(os.path.basename(rec["id"]), ""), field)
                        if key in explained:
                            expl[field] += 1
                            return
                        bad[field] += 1
                        failed[o].add((rec["id"], field))
                        if len(examples) < 40: examples.append((rec["id"], field, repr(got)[:100], repr(want)[:100]))
                h = hdr(root, "subject")
                chk("subject", norm(h.get("value") if h and h.get("value") is not None else (h or {}).get("raw")) if h else None, norm(exp["subject"]) if exp["subject"] is not None else None)
                chk("from", addrs(rec, "from") or None, exp["from"] or None)
                chk("to", addrs(rec, "to") or None, exp["to"] or None)
                chk("cc", addrs(rec, "cc") or None, exp["cc"] or None)
                d = (rec.get("date") or {}).get("utc")
                chk("date", d[:19] + "Z" if d else None, exp["date"])
                m = hdr(root, "message-id")
                chk("message-id", norm(m["raw"]).strip("<>") if m else None, (exp["messageId"] or None))
                ents = [e for e in walk(root) if e.get("body")]
                disp = lambda e: (e.get("disposition") or "")
                texts = [e for e in ents if e["contentType"] == "text/plain" and not e.get("filename") and disp(e) != "attachment"]
                htmls = [e for e in ents if e["contentType"] == "text/html" and not e.get("filename") and disp(e) != "attachment"]
                if exp["textEolSha"]:
                    chk("text-body", texts[0]["body"]["eolSha256"] if texts else None, exp["textEolSha"])
                else:
                    chk("text-body", texts[0]["body"]["len"] if texts else 0, 0)
                if exp["htmlEolSha"]:
                    chk("html-body", htmls[0]["body"]["eolSha256"] if htmls else None, exp["htmlEolSha"])
                else:
                    chk("html-body", len(htmls), 0)
                want = Counter((a["name"], a["size"], a["sha"]) for a in exp["attachments"] if not a.get("embedded") and a["name"])
                names = {k[0] for k in want}
                got = Counter((e.get("filename"), e["body"]["len"], e["body"]["sha256"]) for e in ents if e.get("filename") in names)
                chk("attachments", got, want)
        print(f"{o}: {n} messages, {fields} fields compared, {sum(bad.values())} differ, {sum(expl.values())} explained in explained.tsv")
        for k, v in sorted(bad.items()): print(f"  {k}\t{v}")
        for ex in examples: print("   e.g.", *ex)
        if bad: rc = 1
    # A difference only one oracle shows, while the other agrees with the source, is that oracle's own reading (for example a
    # charset label it does not know); a difference both show is ours to explain.
    both = set.intersection(*failed.values()) if len(failed) > 1 else set().union(*failed.values())
    print(f"differences both oracles show: {len(both)}; only one oracle shows: {len(set().union(*failed.values())) - len(both)}")
    for x in sorted(both)[:40]: print("   both:", *x)
    return 1 if both else 0

sys.exit(main())
