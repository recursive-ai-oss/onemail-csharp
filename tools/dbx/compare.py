#!/usr/bin/env python3
"""Compares DbxTool's extraction of a store with undbx's.

    compare.py <DbxTool extract dir> <undbx output dir>

DbxTool writes list.tsv (folder path, position, length, sha256, defects) and one file per message. undbx writes one directory per
folder, named after the folder (not its path), one .eml per message (names built from the header fields). The messages of a folder
are compared as a multiset of SHA-256 hashes, so file names and order do not matter. A folder both sides know must hold the same
messages; folders only one side reports are differences. Exit status is the number of differing folders.
"""
import collections
import hashlib
import os
import sys


def main(ours, theirs):
    mine = collections.defaultdict(collections.Counter)
    names = set()
    with open(os.path.join(ours, "list.tsv"), encoding="utf-8") as f:
        next(f)
        for line in f:
            c = line.rstrip("\n").split("\t")
            if c[0] == "#folder":
                if c[2] and c[2] != "None":
                    names.add(c[1].split("/")[-1])
                continue
            mine[c[0].split("/")[-1]][c[3]] += 1
            names.add(c[0].split("/")[-1])
    other = collections.defaultdict(collections.Counter)
    for d in sorted(os.listdir(theirs)):
        p = os.path.join(theirs, d)
        if not os.path.isdir(p):
            continue
        for fn in os.listdir(p):
            fp = os.path.join(p, fn)
            if os.path.isfile(fp):
                with open(fp, "rb") as g:
                    other[d][hashlib.sha256(g.read()).hexdigest()] += 1
    bad = 0
    total = 0
    for folder in sorted(set(mine) | set(other)):
        a, b = mine.get(folder, collections.Counter()), other.get(folder, collections.Counter())
        total += sum(a.values())
        if a != b:
            bad += 1
            print(f"DIFF {folder}: ours {sum(a.values())} undbx {sum(b.values())} only-ours {sum((a - b).values())} only-undbx {sum((b - a).values())}")
    print(f"folders compared {len(set(mine) | set(other))}, messages (ours) {total}, differing folders {bad}")
    return bad


if __name__ == "__main__":
    sys.exit(min(main(sys.argv[1], sys.argv[2]), 255))
