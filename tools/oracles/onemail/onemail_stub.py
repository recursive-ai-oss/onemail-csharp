#!/usr/bin/env python3
"""Placeholder for OneMail's own driver. It reports every message as skipped, so the runner leaves it out of
the comparisons until a real driver that emits the same dump form replaces it (see README.md)."""
import json
import sys

root = "."
args = sys.argv[1:]
if "--version" in args:
    print("onemail stub")
    sys.exit(0)
for line in sys.stdin:
    line = line.rstrip("\n")
    if line:
        rec = {"schema": "onemail-dump/1", "id": line, "oracle": {"name": "onemail", "version": "stub", "notes": "not implemented", "headersOrdered": True},
               "error": "not implemented", "defects": None, "skipped": True}
        sys.stdout.write(json.dumps(rec) + "\n")
