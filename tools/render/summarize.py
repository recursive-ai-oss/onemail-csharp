#!/usr/bin/env python3
"""Summaries of RenderTool results.jsonl: summarize.py text|unserved FILE"""
import collections
import json
import sys

mode, path = sys.argv[1], sys.argv[2]
rows = [json.loads(l) for l in open(path, encoding="utf-8")]

if mode == "text":
    c = collections.Counter(r.get("text", "none") if "exception" not in r else "exception:" + r["exception"].split(":")[0] for r in rows)
    print("items", len(rows), dict(c))
    pdf = sorted(r["pdfMs"] for r in rows if "pdfMs" in r)
    png = sorted(r["pngMs"] for r in rows if "pngMs" in r)
    if pdf:
        pick = lambda a, q: a[min(len(a) - 1, int(len(a) * q))]
        print("pdf ms p50/p90/max", pick(pdf, .5), pick(pdf, .9), pdf[-1], " png ms p50/p90/max", pick(png, .5), pick(png, .9), png[-1])
        print("pages max", max(r.get("pages", 0) for r in rows), "defects", dict(collections.Counter(d for r in rows for d in r.get("defects", "").split(",") if d)))
    bad = [r for r in rows if r.get("text") in ("missing", "extra-only", "reordered") or "exception" in r]
    for r in bad[:40]:
        print(" ", r["item"], r.get("text"), r.get("exception", ""), "| miss", r.get("missing", "")[:50], "| extra", r.get("extra", "")[:50], "|", repr(r.get("expectedAt", ""))[:50], "|", repr(r.get("gotAt", ""))[:50])
elif mode == "unserved":
    n = sum(r.get("unserved", 0) for r in rows)
    print("items", len(rows), "items with requests", sum(1 for r in rows if r.get("unserved", 0)), "requests listed", n, "(nothing was fetched: the renderer has no loader beyond data: URIs and the caller's callback)")
