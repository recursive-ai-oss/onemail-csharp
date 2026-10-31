"""Runs the PyPI package tnefparse (a black-box oracle, never imported by the library) over TNEF files and prints one JSON object per file:
the attribute list, the message-level MAPI properties it exposes, the attachments with size and SHA-256, and the bodies."""
import hashlib, json, sys
from tnefparse import TNEF


def raw_of(p):
    r = p.raw_data
    if isinstance(r, list):
        r = r[0] if r else b""
    if isinstance(r, str):
        r = r.encode("latin-1", "replace")
    return bytes(r)


def main(paths):
    for path in paths:
        out = {"file": path.rsplit("/", 1)[-1]}
        try:
            t = TNEF(open(path, "rb").read())
            out["codepage"] = t.codepage
            out["key"] = t.key
            out["objects"] = [[o.level, o.name & 0xFFFF] for o in t.objects]
            props = []
            for p in t.mapiprops:
                d = p.data
                props.append({"id": p.name, "type": p.attr_type, "raw": raw_of(p).hex(),
                              "text": d if isinstance(d, str) else None,
                              "guid": str(p.guid) if p.guid else None})
            out["props"] = props
            atts = []
            for a in t.attachments:
                data = bytes(a.data)
                atts.append({"name": a.long_filename() or a.name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            out["attachments"] = atts
            out["body"] = t.body if isinstance(t.body, str) else None
            out["htmlbody"] = None if t.htmlbody is None else (t.htmlbody if isinstance(t.htmlbody, str) else repr(t.htmlbody))
            out["hasRtf"] = t.rtfbody is not None
        except Exception as e:  # an oracle failing is a result, not a crash
            out["error"] = type(e).__name__ + ": " + str(e)[:200]
        print(json.dumps(out))


main(sys.argv[1:])
