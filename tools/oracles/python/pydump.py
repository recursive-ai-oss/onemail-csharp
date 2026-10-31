#!/usr/bin/env python3
"""Python `email` oracle driver: prints the normalised dump form (see tools/oracles/README.md).

Reads relative file names on standard input (one per line, under --root) and writes one JSON line per input.
Only the standard library's public API is used. `--policy default` (the default) uses email.policy.default;
`--policy compat32` uses the legacy policy, with decoded values rebuilt through email.header.
"""
import argparse
import email
import email.header
import email.policy
import email.utils
import hashlib
import json
import re
import sys
from datetime import timezone

UNSTRUCTURED = {"subject", "comments", "keywords", "content-description", "thread-topic"}
ADDRESS_HEADERS = ["from", "sender", "reply-to", "to", "cc", "bcc"]
UU = {"x-uuencode", "uuencode", "uue", "x-uue"}
SURROGATES = re.compile("[\ud800-\udfff]")


def clean(s):
    return SURROGATES.sub("\ufffd", s)


def latin1(s):
    # The parser decodes the file as ASCII with surrogate escapes for other octets; put the octets back.
    return s.encode("ascii", "surrogateescape").decode("latin-1")


def hashes(body):
    eol = body.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    trim = eol.rstrip(b"\n")
    h = lambda b: hashlib.sha256(b).hexdigest()
    return {"len": len(body), "sha256": h(body), "eolSha256": h(eol), "trimSha256": h(trim)}


def has_text(s):
    return s is not None and any(not c.isspace() for c in s)


class Dumper:
    def __init__(self, policy_name):
        self.policy_name = policy_name
        self.default = policy_name == "default"
        self.policy = email.policy.default if self.default else email.policy.compat32

    def decoded(self, msg, name, index):
        try:
            if self.default:
                values = msg.get_all(name)
                return clean(str(values[index]))
            raw = [v for k, v in msg.raw_items() if k.lower() == name][index]
            return clean(str(email.header.make_header(email.header.decode_header(re.sub(r"\r?\n(?=[ \t])", "", raw)))))
        except Exception:
            return None

    def headers(self, msg):
        out = []
        seen = {}
        for k, v in msg.raw_items():
            lk = k.lower()
            idx = seen.get(lk, 0)
            seen[lk] = idx + 1
            out.append({
                "name": latin1(k),
                "raw": latin1(v) if isinstance(v, str) else str(v),
                "value": self.decoded(msg, lk, idx) if lk in UNSTRUCTURED else None,
            })
        return out

    def params(self, msg):
        if self.default:
            h = msg["content-type"]
            if h is None:
                return {}
            try:
                return {k.lower(): clean(str(v)) for k, v in h.params.items()}
            except Exception:
                return None
        out = {}
        for k, v in msg.get_params(failobj=[])[1:]:
            if isinstance(v, tuple):
                v = email.utils.collapse_rfc2231_value(v)
            out[k.lower()] = clean(str(v))
        return out

    def cte(self, msg):
        c = str(msg.get("content-transfer-encoding", "")).lower()
        # get_payload(decode=True) acts on these names only.
        c = c.strip()
        if c == "base64":
            return "base64"
        if c == "quoted-printable":
            return "quoted-printable"
        if c in UU:
            return "uuencode"
        return "none"

    def entity(self, msg):
        e = {"headers": self.headers(msg)}
        ctype = msg.get_content_type()
        e["contentType"] = ctype
        e["params"] = self.params(msg)
        disp = msg.get_content_disposition() if self.default else (msg.get("content-disposition") and msg.get_content_disposition())
        e["disposition"] = disp.lower() if disp else None
        fn = msg.get_filename()
        e["filename"] = clean(fn) if fn is not None else None
        e["cteApplied"] = self.cte(msg)
        e["preamble"] = e["epilogue"] = None
        e["body"] = None
        e["children"] = []
        e["message"] = None
        payload = msg.get_payload()
        if isinstance(payload, list):
            if msg.get_content_maintype() == "multipart":
                e["preamble"] = has_text(msg.preamble)
                e["epilogue"] = has_text(msg.epilogue)
                e["children"] = [self.entity(p) for p in payload]
            elif ctype == "message/rfc822" and payload:
                e["message"] = self.entity(payload[0])
            # Other message/* types (delivery-status groups) have no byte content in the dump.
        else:
            body = msg.get_payload(decode=True)
            if isinstance(body, str):
                body = body.encode("utf-8", "surrogateescape")
            e["body"] = hashes(body or b"")
        return e

    def addresses(self, msg):
        out = {}
        for name in ADDRESS_HEADERS:
            if msg.get(name) is None:
                continue
            lst = []
            try:
                if self.default:
                    for a in msg[name].addresses:
                        lst.append({"name": clean(a.display_name), "addr": clean(a.addr_spec)})
                else:
                    for n, a in email.utils.getaddresses(msg.get_all(name)):
                        lst.append({"name": clean(n), "addr": clean(a)})
            except Exception:
                pass
            out[name] = lst
        return out

    def date(self, msg):
        if msg.get("date") is None:
            return None
        try:
            if self.default:
                d = msg["date"].datetime
            else:
                d = email.utils.parsedate_to_datetime(str(msg["date"]))
        except Exception:
            return None
        if d is None:
            return None
        if d.tzinfo is None:
            return {"utc": d.strftime("%Y-%m-%dT%H:%M:%SZ"), "offsetMinutes": None}
        off = d.utcoffset()
        return {"utc": d.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "offsetMinutes": int(off.total_seconds() // 60)}

    def defects(self, msg):
        out = []
        for i, part in enumerate(msg.walk()):
            for d in part.defects:
                out.append("part%d:%s" % (i, type(d).__name__))
        return out

    def dump(self, id_, path, version):
        rec = {"schema": "onemail-dump/1", "id": id_,
               "oracle": {"name": "python" if self.default else "python-compat32", "version": version,
                          "notes": "email.message_from_bytes, policy=" + self.policy_name, "headersOrdered": True}}
        try:
            # message_from_bytes keeps line endings; message_from_binary_file would translate CRLF to LF while reading.
            with open(path, "rb") as f:
                msg = email.message_from_bytes(f.read(), policy=self.policy)
            rec["error"] = None
            rec["defects"] = self.defects(msg)
            rec["addresses"] = self.addresses(msg)
            rec["date"] = self.date(msg)
            rec["root"] = self.entity(msg)
        except Exception as ex:  # a driver crash on one input must not stop the run
            rec = {k: rec[k] for k in ("schema", "id", "oracle")}
            rec["error"] = clean("%s: %s" % (type(ex).__name__, ex))
            rec["defects"] = None
        return rec


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=".")
    ap.add_argument("--policy", choices=["default", "compat32"], default="default")
    ap.add_argument("--version", action="store_true")
    a = ap.parse_args()
    version = "%d.%d.%d" % sys.version_info[:3]
    if a.version:
        print("python " + version)
        return
    d = Dumper(a.policy)
    out = sys.stdout.buffer
    for line in sys.stdin:
        line = line.rstrip("\n")
        if not line:
            continue
        rec = d.dump(line, a.root.rstrip("/") + "/" + line, version)
        out.write(json.dumps(rec, ensure_ascii=True, separators=(",", ":")).encode("ascii") + b"\n")
    out.flush()


if __name__ == "__main__":
    main()
