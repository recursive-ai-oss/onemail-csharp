#!/usr/bin/env python3
"""Prints a few unexplained differences per group: show.py diffs.jsonl <oracle> [per-group=2]"""
import json
import sys
from collections import defaultdict

seen = defaultdict(int)
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 2
for line in open(sys.argv[1]):
    d = json.loads(line)
    if d["oracle"] != sys.argv[2]:
        continue
    if seen[d["group"]] >= limit:
        continue
    seen[d["group"]] += 1
    print(d["group"], "|", repr(d["case"])[:110], "| ours=", repr(d["ours"])[:55], "| theirs=", repr(d["theirs"])[:55], "|", d["issues"])
