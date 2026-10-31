#!/usr/bin/env python3
"""Lists a Maildir the way Python's `mailbox.Maildir` sees it, one TSV row per message:
folder, subdir, unique key, flag letters after "2,", SHA-256 of the file, size. Folders are Maildir++ names ("" is the root).

usage: py_maildir.py <maildir root>
"""
import hashlib
import mailbox
import sys

root = sys.argv[1]
box = mailbox.Maildir(root, factory=None, create=False)
rows = []


def collect(b, name):
    for key in b.iterkeys():
        m = b.get_message(key)
        raw = b.get_bytes(key)
        rows.append((name, m.get_subdir(), key, m.get_flags(), hashlib.sha256(raw).hexdigest(), len(raw)))


# Maildir++ folders are flat in Python's API: ".Archive.2024" is the folder "Archive.2024".
collect(box, "")
for f in box.list_folders():
    collect(box.get_folder(f), f)
for r in sorted(rows):
    print("\t".join(str(x) for x in r))
