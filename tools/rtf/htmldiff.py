#!/usr/bin/env python3
"""Show how OneMail's normalised model differs from one converter's, as word-level edits.

    htmldiff.py <dir> <oracle: pandoc|lo|unrtf> <metric> [id substring]

Reads <dir>/models.json written by htmlcompare.py. The metric is text, fmt:b, fmt:i, fmt:u, fmt:s, fmt:sup, fmt:sub, lists, tables, links,
headings or images. Prints one line per document that differs: both lengths and the first edits (ours | theirs).
"""
import difflib
import json
import re
import sys

MARK = re.compile(r"^(?:[\u2022\u25cf\u25cb\u25aa\u25a0\u00b7o\-\*\uf0b7\uf0a7\uf0d8]|\(?[0-9ivxlcdmIVXLCDM]{1,4}[.)]|\(?[a-zA-Z][.)])$")


def strip(words):
    return [w for w in words if not MARK.match(w)]


def series(m, metric):
    if metric == "text":
        return strip(m["text"])
    if metric.startswith("fmt:"):
        return strip(m["fmt"][metric[4:]])
    if metric == "images":
        return [str(m["images"])]
    return [str(x) for x in m[metric]]


def main():
    d, oracle, metric = sys.argv[1:4]
    only = sys.argv[4] if len(sys.argv) > 4 else None
    models = json.load(open(d + "/models.json"))
    for doc, ms in sorted(models.items()):
        if oracle not in ms or (only and only not in doc):
            continue
        x, y = series(ms["ours"], metric), series(ms[oracle], metric)
        if x == y:
            continue
        ops = [(t, " ".join(x[i1:i2])[:90], " ".join(y[j1:j2])[:90]) for t, i1, i2, j1, j2 in difflib.SequenceMatcher(None, x, y, autojunk=False).get_opcodes() if t != "equal"]
        print(doc, len(x), len(y), ops[:3], "..." if len(ops) > 3 else "")


if __name__ == "__main__":
    main()
