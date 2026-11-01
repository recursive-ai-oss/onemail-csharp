#!/usr/bin/env python3
"""Runs the ARC test suite's scenarios (suite.json from arc_suite_to_json.py) through dkimpy; prints name -> chain status.

  arc_suite_oracles.py dkimpy <suite.json> <out.json>
"""
import json
import re
import sys

import dkim  # noqa

suite = json.load(open(sys.argv[2]))
out = {}


def crlf(s):
    return re.sub(r"\r?\n", "\r\n", s).encode()


def hv(name, value):
    return name + ": " + "\r\n ".join(value.rstrip("\n").split("\n")) + "\r\n"


for t in suite:
    msg = t["message"]
    if t["kind"] == "sign":
        if not t.get("AS", "").strip():
            continue
        msg = hv("ARC-Seal", t["AS"]) + hv("ARC-Message-Signature", t["AMS"]) + hv("ARC-Authentication-Results", t["AAR"]) + msg
    txt = t["txt"]

    def dnsfunc(name, timeout=5, txt=txt):
        n = name.decode() if isinstance(name, bytes) else name
        v = txt.get(n.rstrip("."))
        return v.encode() if v else None

    try:
        cv, results, comment = dkim.arc_verify(crlf(msg), dnsfunc=dnsfunc)
        cv = cv.decode() if isinstance(cv, bytes) else str(cv)
    except Exception as e:
        cv = "error: " + type(e).__name__
    out[t["kind"] + "/" + t["name"]] = cv.lower()
json.dump(out, open(sys.argv[3], "w"), indent=1)
print(len(out), "scenarios")
