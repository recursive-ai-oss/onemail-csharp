#!/usr/bin/env python3
"""Reduces an oracle's output for the CR CR LF probes to one line per probe: the folded and following properties it saw.

  summarize.py facts <oracle name> <facts.jsonl>        ({file, facts: [[path, name, params, value, raw]]} rows)
  summarize.py json <oracle name> <dir with *.ics.json / *.vcf.json>   (jCal / jCard)
  summarize.py plain <oracle name> <lines>              (oracle<TAB>file<TAB>text, passed through)

Prints: oracle <TAB> file <TAB> SUMMARY|FN=<value> <TAB> following properties <TAB> property count.
"""
import json, os, sys

WANT = ("SUMMARY", "FN", "DESCRIPTION", "LOCATION", "NOTE", "N", "EMAIL")


def emit(oracle, f, props):
    names = {}
    for n, v in props:
        names.setdefault(n.upper(), []).append(v)
    head = next((names[k][0] for k in ("SUMMARY", "FN") if k in names), "-")
    rest = ";".join(f"{k}={names[k][0]}" for k in WANT[2:] if k in names) or "-"
    esc = lambda t: t.replace("\r", "\\r").replace("\n", "\\n")
    print("\t".join([oracle, f, esc(head), esc(rest), str(len(props))]))


def jcal_props(comp, out):
    name, props, subs = comp
    for p in props:
        v = p[3] if len(p) > 3 else ""
        out.append((p[0], v if isinstance(v, str) else json.dumps(v, separators=(",", ":"))))
    for s in subs:
        jcal_props(s, out)


mode, oracle, src = sys.argv[1:4]
if mode == "facts":
    for line in open(src, encoding="utf-8"):
        d = json.loads(line)
        if "error" in d:
            print("\t".join([oracle, d["file"], "ERROR", d["error"], "0"]))
            continue
        props = [(r[1], r[3][2:] if r[3].startswith("t:") else r[3]) for r in d["facts"] if not r[1].startswith(("BEGIN", "END"))]
        emit(oracle, d["file"], props)
elif mode == "json":
    for f in sorted(os.listdir(src)):
        text = open(os.path.join(src, f), encoding="utf-8").read()
        if text.startswith("ERROR"):
            print("\t".join([oracle, f.rsplit(".json", 1)[0], "ERROR", text[:60], "0"]))
            continue
        j = json.loads(text)
        props = []
        for c in (j if j and isinstance(j[0], list) and isinstance(j[0][0], str) is False else [j]):
            if c and c[0] == "vcard":
                props += [(p[0], p[3] if isinstance(p[3], str) else json.dumps(p[3], separators=(",", ":"))) for p in c[1]]
            else:
                jcal_props(c, props)
        emit(oracle, f.rsplit(".json", 1)[0], props)
