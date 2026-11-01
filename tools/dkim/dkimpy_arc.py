#!/usr/bin/env python3
"""dkimpy as a black-box ARC verifier: dkimpy_arc.py verify <dir> <subdir> prints the chain status of arc/<subdir>/*.eml."""
import json
import sys
from pathlib import Path

import dkim

d = Path(sys.argv[2]) / "arc"
dns = json.loads((d / "dns.json").read_text())


def dnsfunc(name, timeout=5):
    n = name.decode() if isinstance(name, bytes) else name
    v = dns.get(n.rstrip("."))
    return v[0].encode() if v else None


for f in sorted((d / sys.argv[3]).glob("*.eml")):
    try:
        cv, results, comment = dkim.arc_verify(f.read_bytes(), dnsfunc=dnsfunc)
        cv = cv.decode() if isinstance(cv, bytes) else str(cv)
    except Exception as e:
        cv = "error: " + type(e).__name__
    print(f"  dkimpy verify {sys.argv[3]}/{f.stem}: {cv}")
