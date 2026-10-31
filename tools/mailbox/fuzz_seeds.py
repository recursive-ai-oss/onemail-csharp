#!/usr/bin/env python3
"""Makes fuzz seed corpora from generated stores: mbox seeds (one selector byte, then the file) and emlx seeds.

usage: fuzz_seeds.py <gen dir from run.sh> <out dir>   (writes <out>/mbox and <out>/emlx)
"""
import glob
import os
import sys

gen, out = sys.argv[1], sys.argv[2]
os.makedirs(out + "/mbox", exist_ok=True)
os.makedirs(out + "/emlx", exist_ok=True)
for i, p in enumerate(sorted(glob.glob(gen + "/mbox/*.mbox"))):
    data = open(p, "rb").read()[:30000]
    for pick in (0x00, 0x02, 0x13, 0x14, 0x11):
        open("%s/mbox/s%d-%02x" % (out, i, pick), "wb").write(bytes([pick]) + data)
for i, p in enumerate(sorted(glob.glob(gen + "/stores/emlx/**/*.emlx", recursive=True))):
    open("%s/emlx/e%d" % (out, i), "wb").write(open(p, "rb").read()[:16000])
for i, p in enumerate(sorted(glob.glob(gen + "/stores/maildir/**/cur/*", recursive=True))[:20]):
    open("%s/emlx/n%d" % (out, i), "wb").write(os.path.basename(p).encode())
