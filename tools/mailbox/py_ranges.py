#!/usr/bin/env python3
"""Prints the message boundaries Python's `mailbox.mbox` finds, in the format of the other range tools:
"<path>\t<count>" then per message "<start>\t<stop>" (start is the envelope line, stop excludes the separator).
Uses the standard library's table of contents (the offsets its own get_bytes() would read)."""
import mailbox
import sys

for path in sys.argv[1:]:
    try:
        box = mailbox.mbox(path)
        box._generate_toc()
        rows = [box._toc[k] for k in sorted(box._toc)]
        box.close()
    except Exception as e:  # noqa: BLE001 - the oracle's failure is a result, not a crash
        print(f"{path}\terror\t{type(e).__name__}")
        continue
    print(f"{path}\t{len(rows)}")
    for start, stop in rows:
        print(f"{start}\t{stop}")
