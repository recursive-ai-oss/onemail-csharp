#!/usr/bin/env python3
"""Compares what the black-box oracles (MimeKit, Python email, Go net/mail) read from the replies and forwards CompositionTool wrote with what
the tool says it wrote (expected/NNNNNN.json): subject, From, To, Cc, In-Reply-To and References, field by field.

    check_compose.py OUT_DIR(CompositionTool output) DUMPS_DIR(diffrun out dir/<corpus>/dumps) [oracle ...]

Prints, per oracle, the messages and fields compared and every field that differs, and exits 1 if any differs for the oracles that agree
with each other (a difference only one oracle shows is that oracle's own reading and is listed separately).
"""
import gzip, json, os, re, sys, glob
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "oracles"))
from diffrun import obsolete_local  # addresses with a doubled or edge dot in the local part: MimeKit, Go and Python leave them out or cut them (ambiguity A15, A16)
from collections import Counter


def norm(s):
    return " ".join((s or "").split())


def hdr(root, name):
    for h in root["headers"]:
        if h["name"].lower() == name:
            return h
    return None


def addrs(rec, key):
    l = (rec.get("addresses") or {}).get(key)
    if l is None:
        return []
    return sorted(a["addr"].lower() for a in l if "@" in a["addr"] and not obsolete_local(a["addr"]))


def ids(root, name):
    h = hdr(root, name)
    return re.findall(r"<([^<>]*)>", h["raw"]) if h else []


def main():
    out, dumps = sys.argv[1], sys.argv[2]
    oracles = sys.argv[3:] or ["mimekit", "python", "go"]
    failed = {}
    for o in oracles:
        failed[o] = set()
        n = fields = 0
        bad = Counter()
        examples = []
        for f in sorted(glob.glob(os.path.join(dumps, o, "*.jsonl.gz"))):
            for line in gzip.open(f, "rt", encoding="utf-8", errors="surrogateescape"):
                rec = json.loads(line)
                exp = json.load(open(os.path.join(out, "expected", os.path.basename(rec["id"]).replace(".eml", ".json"))))
                n += 1
                if rec.get("error"):
                    bad["parse-failed"] += 1
                    failed[o].add((rec["id"], "parse-failed"))
                    continue
                root = rec["root"]

                def chk(field, got, want):
                    nonlocal fields
                    fields += 1
                    if got != want:
                        bad[field] += 1
                        failed[o].add((rec["id"], field))
                        if len(examples) < 20:
                            examples.append((rec["id"], field, "got-only " + repr(sorted(set(got) - set(want)))[:160] if isinstance(got, list) else repr(got)[:100], "want-only " + repr(sorted(set(want) - set(got)))[:160] if isinstance(want, list) else repr(want)[:100]))

                h = hdr(root, "subject")
                chk("subject", norm(h.get("value") if h and h.get("value") is not None else (h or {}).get("raw")) if h else None, norm(exp["subject"]))
                chk("from", addrs(rec, "from"), [exp["from"]])
                for fld in ("to", "cc"):
                    got = addrs(rec, fld)
                    # A parser that rejects the whole list over one address it cannot read (Go) returns nothing for it.
                    if not got and any(obsolete_local(a) for a in exp[fld]):
                        continue
                    chk(fld, got, [a for a in exp[fld] if not obsolete_local(a)])
                chk("in-reply-to", ids(root, "in-reply-to"), exp["inReplyTo"])
                chk("references", ids(root, "references"), exp["references"])
        print(f"{o}: {n} messages, {fields} fields compared, {sum(bad.values())} differ")
        for k, v in sorted(bad.items()):
            print(f"  {k}\t{v}")
        for ex in examples:
            print("   e.g.", *ex)
    both = set.intersection(*failed.values()) if len(failed) > 1 else set().union(*failed.values())
    print(f"differences all oracles show: {len(both)}; only some oracles show: {len(set().union(*failed.values())) - len(both)}")
    for x in sorted(both)[:40]:
        print("   all:", *x)
    return 1 if both else 0


sys.exit(main())
