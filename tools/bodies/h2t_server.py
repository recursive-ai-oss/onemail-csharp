"""Keeps html2text loaded: reads a file path per line, answers with the byte length and the markdown-ish text. Used by compare.py."""
import sys

import html2text

h = html2text.HTML2Text()
h.body_width = 0
h.ignore_images = True
h.ignore_emphasis = True
h.unicode_snob = True
h.protect_links = False
for line in sys.stdin:
    p = line.rstrip("\n")
    try:
        out = h.handle(open(p, encoding="utf-8", errors="replace").read()).encode("utf-8")
    except Exception as e:  # an oracle crash is reported as empty output
        out = ("<<error %s>>" % type(e).__name__).encode()
    sys.stdout.buffer.write(b"%d\n" % len(out) + out)
    sys.stdout.buffer.flush()
