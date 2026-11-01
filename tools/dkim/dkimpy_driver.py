#!/usr/bin/env python3
"""dkimpy as a black-box DKIM oracle (run only; its source is not read).

  dkimpy_driver.py verify <dir>   verify <dir>/ours-signed/*.eml -> <dir>/dkimpy-verifies-ours.json
  dkimpy_driver.py sign <dir>     sign <dir>/unsigned/*.eml per cases.json -> <dir>/dkimpy-signed/*.eml
"""
import json
import re
import sys
from pathlib import Path

import dkim

d = Path(sys.argv[2])
dns = json.loads((d / "dns.json").read_text())


def dnsfunc(name, timeout=5):
    n = name.decode() if isinstance(name, bytes) else name
    recs = dns.get(n.rstrip("."))
    return recs[0].encode() if recs else None


def crlf(b):
    return re.sub(rb"\r?\n", b"\r\n", b)


if sys.argv[1] == "verify":
    out = {}
    for f in sorted((d / "ours-signed").glob("*.eml")):
        msg = f.read_bytes()
        head = msg.split(b"\r\n\r\n", 1)[0] if b"\r\n\r\n" in msg else msg.split(b"\n\n", 1)[0]
        count = len(re.findall(rb"(?im)^DKIM-Signature:", head))
        res = []
        for i in range(count):
            try:
                ok = dkim.DKIM(msg).verify(idx=i, dnsfunc=dnsfunc)
                res.append({"status": "pass" if ok else "fail"})
            except Exception as e:  # dkimpy raises on malformed input
                res.append({"status": "error", "detail": f"{type(e).__name__}: {e}"})
        out[f.stem] = res
    (d / "dkimpy-verifies-ours.json").write_text(json.dumps(out, indent=1))
    print("dkimpy verified", len(out))
else:
    cases = json.loads((d / "cases.json").read_text())
    (d / "dkimpy-signed").mkdir(exist_ok=True)
    errors = 0
    for c in cases:
        msg = (d / "unsigned" / (c["id"] + ".eml")).read_bytes()
        hc, bc = c["canon"].split("/")
        key = (d / "keys" / "rsa.pem").read_bytes() if c["alg"] == "rsa" else (d / "keys" / "ed.seed").read_bytes()
        try:
            sig = dkim.sign(msg, c["alg"].encode(), b"example.com", key,
                            canonicalize=(hc.encode(), bc.encode()),
                            signature_algorithm=b"rsa-sha256" if c["alg"] == "rsa" else b"ed25519-sha256",
                            length=c["length"],
                            linesep=b"\r\n")
            (d / "dkimpy-signed" / (c["id"] + ".eml")).write_bytes(sig + msg)
        except Exception as e:
            errors += 1
            print("sign failed", c["id"], type(e).__name__, e)
    print("dkimpy signed", len(cases) - errors, "errors", errors)
