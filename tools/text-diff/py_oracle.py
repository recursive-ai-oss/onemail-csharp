#!/usr/bin/env python3
"""Runs the corpus through Python's email package and prints one JSON line per case: {"id", "r": <hex of UTF-8 result>} | {"id", "n": 1} (parameter absent) | {"id", "e": "<error>"} | {"id", "s": 1} (skipped).

  ew:    the chunks of decode_header(value), joined
  cs:    bytes.decode(<python codec>, "replace")  (IMAP modified UTF-7: the generator's reference result)
  param: collapse_rfc2231_value(Message.get_param(want, header="content-disposition"))
"""
import json
import sys
from email.header import decode_header
from email.message import Message
from email.utils import collapse_rfc2231_value


def run_ew(raw):
    try:
        s = raw.decode("ascii")
    except UnicodeDecodeError:
        return {"s": 1}
    # make_header() would put a space between a decoded word and its neighbours (Header.__str__), which is Python's
    # display rule and not part of decoding; the decoded chunks are joined as they are.
    out = []
    for chunk, charset in decode_header(s):
        out.append(chunk if isinstance(chunk, str) else chunk.decode(charset or "latin-1", "replace"))
    return {"r": "".join(out).encode("utf-8", "surrogatepass").hex()}


def run_param(case):
    m = Message()
    m["Content-Disposition"] = case["hdr"]
    v = m.get_param(case["want"], header="content-disposition")
    if v is None:
        return {"n": 1}
    return {"r": collapse_rfc2231_value(v).encode("utf-8", "surrogatepass").hex()}


def run_cs(c):
    if c["codec"] in ("imap", "imap-encode"):
        # Python has no IMAP codec: the generator's independent RFC 3501 reference result is the expectation.
        return {"r": c["want"]}
    return {"r": bytes.fromhex(c["hex"]).decode(c["codec"], "replace").encode("utf-8", "surrogatepass").hex()}


def main():
    for line in open(sys.argv[1]):
        c = json.loads(line)
        try:
            r = run_ew(bytes.fromhex(c["hex"])) if c["kind"] == "ew" else run_cs(c) if c["kind"] == "cs" else run_param(c)
        except Exception as e:  # noqa: BLE001 - an oracle failure is a result
            r = {"e": type(e).__name__}
        r["id"] = c["id"]
        print(json.dumps(r, separators=(",", ":")))


if __name__ == "__main__":
    main()
