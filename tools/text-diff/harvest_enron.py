#!/usr/bin/env python3
"""Harvests real header values from the CMU Enron maildir into differential cases (JSON lines, ids are renumbered by run.sh).

  harvest_enron.py <maildir root> <out.jsonl> [--stride 20] [--max 6000]

Takes Subject, From, To, Cc and X-FileName values (folded lines kept folded, as the file has them) from every
<stride>th message, plus every value found in that sample that contains an encoded word. Only the values are kept; the corpus is
fetched, never committed, and the generated case file is not committed either.
"""
import argparse
import json
import os
import random

HEADERS = (b"subject:", b"from:", b"to:", b"cc:", b"x-filename:")


def headers_of(path):
    with open(path, "rb") as f:
        head = f.read(8192).split(b"\n\n", 1)[0]
    values, cur = [], None
    for line in (l.rstrip(b"\r") for l in head.split(b"\n")):
        if line[:1] in (b" ", b"\t") and cur is not None:
            cur[1].append(line)
            continue
        if cur:
            values.append(cur)
        cur = None
        low = line.lower()
        for h in HEADERS:
            if low.startswith(h):
                cur = [h, [line[len(h):].lstrip()]]
    if cur:
        values.append(cur)
    return [(h.decode().rstrip(":"), b"\r\n".join(parts)) for h, parts in values]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("out")
    ap.add_argument("--stride", type=int, default=20)
    ap.add_argument("--max", type=int, default=6000)
    a = ap.parse_args()
    rng = random.Random(7)
    seen, cases, encoded = set(), [], []
    n = 0
    for dirpath, _, files in os.walk(a.root):
        for name in sorted(files):
            n += 1
            if n % a.stride:
                continue
            try:
                hs = headers_of(os.path.join(dirpath, name))
            except OSError:
                continue
            for h, v in hs:
                if v in seen or not v:
                    continue
                seen.add(v)
                (encoded if b"=?" in v else cases).append((h, v))
    rng.shuffle(cases)
    picked = encoded + cases[: max(0, a.max - len(encoded))]
    with open(a.out, "w") as out:
        for h, v in picked:
            if h == "x-filename":
                # A file name is a parameter value: quote it the way a mail client would.
                q = '"' + v.decode("latin-1").replace("\\", "\\\\").replace('"', '\\"').replace("\r\n", " ") + '"'
                out.write(json.dumps({"kind": "param", "group": "enron-filename", "hdr": "attachment; filename=" + q,
                                      "params": [["filename", v.replace(b"\r\n", b" ").hex()]], "want": "filename"}) + "\n")
            else:
                out.write(json.dumps({"kind": "ew", "group": "enron-" + h + ("-encoded" if b"=?" in v else ""), "hex": v.hex()}) + "\n")
    print("scanned %d files, %d unique values, %d with encoded words, wrote %d" % (n, len(seen), len(encoded), len(picked)))


if __name__ == "__main__":
    main()
