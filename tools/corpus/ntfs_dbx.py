#!/usr/bin/env python3
"""Copies every *.dbx file under one directory of an NTFS image to an output folder, using ntfsls and ntfscat (read-only, no root).

    ntfs_dbx.py <ntfs bin dir> <image> <start path> <out dir>

The output keeps the directory structure below the start path, with spaces and braces replaced so the names are shell-friendly.
"""
import os
import re
import subprocess
import sys


def main(bindir, image, start, out):
    ls = os.path.join(bindir, "ntfsls")
    cat = os.path.join(bindir, "ntfscat")
    n = 0
    stack = [start]
    while stack:
        d = stack.pop()
        r = subprocess.run([ls, "-f", "-F", "-p", d, image], capture_output=True, text=True, errors="replace")
        for name in r.stdout.splitlines():
            if name in (".", "..", ""):
                continue
            if name.endswith("/"):
                stack.append(d.rstrip("/") + "/" + name[:-1])
            elif name.lower().endswith(".dbx"):
                rel = os.path.relpath(d, start)
                dest = os.path.join(out, re.sub(r"[^A-Za-z0-9._-]", "_", rel))
                os.makedirs(dest, exist_ok=True)
                with open(os.path.join(dest, name), "wb") as f:
                    subprocess.run([cat, "-f", image, d.rstrip("/") + "/" + name], stdout=f, check=True)
                n += 1
    print(f"{n} dbx files copied to {out}")


if __name__ == "__main__":
    main(*sys.argv[1:5])
