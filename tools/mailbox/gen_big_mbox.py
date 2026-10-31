#!/usr/bin/env python3
"""Writes a large mbox (mboxrd quoting) for the constant-memory check. Messages are 1 to 200 KiB, with quoted and
unquoted-looking "From" lines in the bodies. Fixed seed.

usage: gen_big_mbox.py <out file> <size in MiB>
"""
import random
import sys

out, target = sys.argv[1], int(sys.argv[2]) * 1024 * 1024
rng = random.Random(1)
lines = [b"The quick brown fox jumps over the lazy dog %d" % i for i in range(64)]
lines += [b">From quoted line", b">>From twice quoted", b"From the desk of nobody", b""]
written = 0
n = 0
with open(out, "wb", buffering=1 << 20) as f:
    while written < target:
        body = b"\n".join(rng.choice(lines) for _ in range(rng.randrange(20, 3000)))
        msg = b"From sender@example.com Mon Jan  1 00:00:00 2024\nSubject: big %d\nMessage-ID: <big-%d@example.com>\n\n" % (n, n) + body + b"\n\n"
        f.write(msg)
        written += len(msg)
        n += 1
print(n, "messages", written, "bytes")
