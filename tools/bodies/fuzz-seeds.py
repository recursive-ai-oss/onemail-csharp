#!/usr/bin/env python3
"""Writes seed inputs for the `htmltext` fuzz target from the generated HTML cases: each file is two option bytes (mode, flags) and the document.

usage: fuzz-seeds.py <cases-dir> <seed-dir>
"""
import glob
import os
import random
import sys

src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
r = random.Random(28)
n = 0
for f in sorted(glob.glob(os.path.join(src, "*.html"))):
    data = open(f, "rb").read()[:3000]
    for mode in range(4):
        open(os.path.join(out, "s%05d" % n), "wb").write(bytes([mode, r.randrange(256)]) + data)
        n += 1
print(n, "seeds")
