#!/usr/bin/env python3
"""Compares what the black-box oracles (MimeKit, Python email, Go net/mail) read from the messages `CompositionTool names` wrote with what the tool
says it wrote (expected/NNNNNN.json): the decoded display names and addresses of From, To and Cc, the decoded subject and the file names of the
attachments, with white space normalised.

    check_names.py OUT_DIR(CompositionTool names output) DUMPS_DIR(diffrun out dir/<corpus>/dumps) [oracle ...]

Prints, per oracle, the messages and fields compared and every field that differs, and exits 1 if a field differs in every oracle (a difference
only some oracles show is that oracle's own reading and is listed, with its explanation in docs/composition.md).
"""
import gzip, json, os, sys, glob
from collections import Counter


def norm(s):
    return " ".join((s or "").split())


def hdr(root, name):
    for h in root["headers"]:
        if h["name"].lower() == name:
            return h
    return None


def walk(e):
    yield e
    for c in e.get("children") or []:
        yield from walk(c)


def replaced(v):
    """What Python's bytes parser makes of raw UTF-8: one U+FFFD per byte outside ASCII."""
    def r(t):
        return "".join(c if ord(c) < 128 else "\ufffd" * len(c.encode("utf-8")) for c in t)
    if isinstance(v, list):
        return [(r(x[0]), x[1]) if isinstance(x, tuple) else r(x) for x in v]
    return r(v) if isinstance(v, str) else v


def people(rec, key):
    l = (rec.get("addresses") or {}).get(key)
    if l is None:
        return None
    return [(norm(a.get("name")), a["addr"]) for a in l]


def main():
    out, dumps = sys.argv[1], sys.argv[2]
    oracles = sys.argv[3:] or ["mimekit", "python", "go"]
    failed = {}
    for o in oracles:
        failed[o] = set()
        n = fields = 0
        bad = Counter()
        explained = Counter()
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
                    if got != want and o == "python" and exp["utf8"] and got == replaced(want):
                        # Ambiguity L6: Python's parser turns every byte of raw UTF-8 in a header into U+FFFD (it reads RFC 6532 headers only from text).
                        explained[field] += 1
                    elif got != want:
                        bad[field] += 1
                        failed[o].add((rec["id"], field))
                        if len(examples) < 12:
                            examples.append((rec["id"], field, "got " + repr(got)[:140], "want " + repr(want)[:140]))

                h = hdr(root, "subject")
                chk("subject", norm(h.get("value") if h and h.get("value") is not None else (h or {}).get("raw")) if h else None, exp["subject"])
                chk("from", people(rec, "from"), [(exp["from"]["name"], exp["from"]["addr"])])
                chk("to", people(rec, "to"), [(p["name"], p["addr"]) for p in exp["to"]])
                chk("cc", people(rec, "cc") or [], [(p["name"], p["addr"]) for p in exp["cc"]])
                files = [norm(e.get("filename")) for e in walk(root) if e is not root and e.get("filename") is not None]
                chk("filenames", files, exp["filenames"])
        print(f"{o}: {n} messages, {fields} fields compared, {sum(bad.values())} differ, {sum(explained.values())} explained {dict(explained)}")
        for k, v in sorted(bad.items()):
            print(f"  {k}\t{v}")
        for ex in examples:
            print("   e.g.", *ex)
    everyone = set.intersection(*failed.values()) if len(failed) > 1 else set().union(*failed.values())
    print(f"differences every oracle shows: {len(everyone)}; only some oracles show: {len(set().union(*failed.values())) - len(everyone)}")
    for x in sorted(everyone)[:40]:
        print("   all:", *x)
    return 1 if everyone else 0


sys.exit(main())
