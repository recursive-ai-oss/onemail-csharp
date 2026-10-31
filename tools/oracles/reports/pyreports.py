#!/usr/bin/env python3
"""Python `email` driver for the report differential (tools/oracles/reports/README.md).

Reads relative file names on standard input (under --root) and writes one JSON line per file: the header blocks of every
message/delivery-status, message/disposition-notification and message/feedback-report part (and their "global" forms), as
the standard library's feed parser reads them (`policy.compat32`, raw field values). With --urljoin it reads
"base<TAB>reference" lines and writes urllib.parse.urljoin's result, the RFC 3986 reference for MHTML resolution.
Only the standard library's public API is used.
"""
import argparse
import email
import email.policy
import json
import sys
from urllib.parse import urljoin

TYPES = {
    "message/delivery-status", "message/global-delivery-status", "message/disposition-notification",
    "message/global-disposition-notification", "message/feedback-report",
}


def latin1(s):
    # The parser decodes the file as ASCII with surrogate escapes for other octets; put the octets back.
    return s.encode("ascii", "surrogateescape").decode("latin-1")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=".")
    ap.add_argument("--version", action="store_true")
    ap.add_argument("--urljoin", action="store_true")
    a = ap.parse_args()
    if a.version:
        print("python " + sys.version.split()[0])
        return
    ver = sys.version.split()[0]
    for line in sys.stdin:
        line = line.rstrip("\n")
        if not line:
            continue
        if a.urljoin:
            f = line.split("\t")
            ref = f[1] if len(f) > 1 else ""
            print(json.dumps({"base": f[0], "ref": ref, "result": urljoin(f[0], ref)}), flush=True)
            continue
        rec = {"schema": "onemail-reports/1", "id": line, "oracle": {"name": "python", "version": ver}, "error": None,
               "auth": None, "mhtml": None}
        try:
            with open(a.root + "/" + line, "rb") as fh:
                msg = email.message_from_bytes(fh.read(), policy=email.policy.compat32)
            reports = []
            for part in msg.walk():
                ct = part.get_content_type()
                if ct not in TYPES:
                    continue
                payload = part.get_payload()
                blocks = []
                if isinstance(payload, list):
                    for m in payload:
                        blocks.append([[k, latin1(v) if isinstance(v, str) else str(v)] for k, v in m.raw_items()])
                reports.append({"type": ct, "blocks": blocks})
            rec["reports"] = reports
        except Exception as e:  # noqa: BLE001 - the oracle must never stop the run
            rec["error"] = type(e).__name__ + ": " + str(e)
        print(json.dumps(rec), flush=True)


if __name__ == "__main__":
    main()
