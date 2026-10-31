#!/usr/bin/env python3
"""Damaged-stream comparison for compressed RTF.

    mutants.py make <rtf-dir> <mut-dir> [seed]   write damaged copies of a few extracted streams, plus the oracle's answer for each
    mutants.py compare <mut-dir>                 compare with OneMail's answers (after `RtfTool decompress-all <mut-dir>`)

Oracle: compressed-rtf 1.0.7 (MIT), run only. A mutant is a truncation, a flipped byte, or an edited header size field.
The classes: same bytes; oracle refuses (an exception) and OneMail reports a defect; oracle accepts and OneMail's output is
the same bytes with a defect (a check the oracle does not make: CRC, sizes); and anything else, which is a finding.
"""
import csv
import glob
import hashlib
import os
import random
import sys

import compressed_rtf


def make(src, out, seed):
    rng = random.Random(seed)
    os.makedirs(out, exist_ok=True)
    files = sorted(f for f in glob.glob(os.path.join(src, "*.rtfz")) if ".ours." not in f and ".oracle." not in f)
    rng.shuffle(files)
    n = 0
    for f in files[:12]:
        data = open(f, "rb").read()
        for k in range(250):
            b = bytearray(data)
            kind = rng.choice(["trunc", "flip", "flip", "hdr", "ins"])
            if kind == "trunc":
                b = b[: rng.randrange(0, len(b))]
            elif kind == "flip":
                for _ in range(rng.choice([1, 1, 2, 3])):
                    b[rng.randrange(0, len(b))] ^= 1 << rng.randrange(8)
            elif kind == "hdr":
                i = rng.choice([0, 4, 12])
                b[i + rng.randrange(0, 4)] = rng.randrange(256)
            else:
                i = rng.randrange(16, len(b))
                b[i:i] = bytes(rng.randrange(256) for _ in range(rng.randrange(1, 4)))
            name = "m%05d" % n
            n += 1
            open(os.path.join(out, name + ".rtfz"), "wb").write(bytes(b))
            try:
                res = compressed_rtf.decompress(bytes(b))
                open(os.path.join(out, name + ".oracle.rtf"), "wb").write(res)
            except Exception as e:
                open(os.path.join(out, name + ".oracle.err"), "w").write(type(e).__name__)
    print(n, "mutants")


def compare(out):
    rows = list(csv.DictReader(open(os.path.join(out, "ours.tsv")), delimiter="\t"))
    counts = {}
    bad = []
    for r in rows:
        stem = r["name"][:-5]
        defects = r["defects"]
        oracle_ok = os.path.exists(os.path.join(out, stem + ".oracle.rtf"))
        if oracle_ok:
            theirs = hashlib.sha256(open(os.path.join(out, stem + ".oracle.rtf"), "rb").read()).hexdigest()
            if theirs == r["sha256"]:
                k = "same bytes, no defect" if not defects else "same bytes, OneMail also reports: " + "+".join(sorted(set(defects.split(",")))) 
            elif "ExpansionRatioExceeded" in defects or "OutputLimitExceeded" in defects:
                k = "oracle decodes; OneMail refused by its size or ratio limit"  # RT14: the limits are the point
            else:
                k = "DIFFERENT bytes" if defects else "DIFFERENT bytes and no defect"
                bad.append((stem, k, defects))
        else:
            k = "oracle refuses; OneMail reports a defect" if defects else "oracle refuses; OneMail reports NOTHING"
            if not defects:
                bad.append((stem, k, defects))
        # normalise the per-kind detail so the table stays short
        key = k.split(":")[0]
        counts[key] = counts.get(key, 0) + 1
        if "DIFFERENT" in k:
            pass
    print(len(rows), "mutants")
    for k in sorted(counts):
        print("%6d  %s" % (counts[k], k))
    with open(os.path.join(out, "mutants-different.txt"), "w") as f:
        for b in bad:
            f.write("\t".join(b) + "\n")
    print("findings:", len(bad), "->", os.path.join(out, "mutants-different.txt"))


if __name__ == "__main__":
    if sys.argv[1] == "make":
        make(sys.argv[2], sys.argv[3], int(sys.argv[4]) if len(sys.argv) > 4 else 12)
    else:
        compare(sys.argv[2])
