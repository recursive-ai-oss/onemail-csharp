#!/usr/bin/env python3
"""chrome_compare.py DOCS_DIR...: for every <id>.html/<id>.pdf in the sweep output, print the same display HTML with headless Chrome and
compare page counts with ours (pdfinfo), and score page 1 (mean absolute grey difference at 48 dpi, 0 = identical, 255 = opposite).
Reported, not gating: the engines use different fonts and line breaking."""
import glob
import os
import re
import subprocess
import sys
import tempfile

PAGE_RULE = "<style>@page{size:A4;margin:48px}</style>"


def pages(pdf):
    out = subprocess.run(["pdfinfo", pdf], capture_output=True, text=True).stdout
    m = re.search(r"Pages:\s+(\d+)", out)
    return int(m.group(1)) if m else -1


def ppm(pdf, tmp, tag):
    base = os.path.join(tmp, tag)
    subprocess.run(["pdftoppm", "-r", "48", "-f", "1", "-l", "1", "-gray", pdf, base], check=False)
    for f in glob.glob(base + "*.pgm"):
        return f
    return None


def read_pgm(path):
    data = open(path, "rb").read()
    m = re.match(rb"P5\s+(\d+)\s+(\d+)\s+(\d+)\s", data)
    w, h = int(m.group(1)), int(m.group(2))
    return w, h, data[m.end():]


def score(a, b):
    wa, ha, da = a
    wb, hb, db = b
    w, h = min(wa, wb), min(ha, hb)
    if w == 0 or h == 0:
        return -1
    total = 0
    for y in range(h):
        ra = da[y * wa:y * wa + w]
        rb = db[y * wb:y * wb + w]
        total += sum(abs(x - y2) for x, y2 in zip(ra, rb))
    return total / (w * h)


same = diff = 0
scores = []
deltas = []
with tempfile.TemporaryDirectory() as tmp:
    for d in sys.argv[1:]:
        for html in sorted(glob.glob(os.path.join(d, "*.html")), key=lambda p: int(os.path.basename(p)[:-5])):
            ours = html[:-5] + ".pdf"
            if not os.path.exists(ours):
                continue
            theirs = os.path.join(tmp, "chrome.pdf")
            page = os.path.join(tmp, "page.html")
            src = open(html, encoding="utf-8").read().replace("</head>", PAGE_RULE + "</head>", 1)
            open(page, "w", encoding="utf-8").write(src)
            subprocess.run(["google-chrome", "--headless=new", "--no-sandbox", "--disable-gpu", "--no-pdf-header-footer",
                            "--disable-background-networking", "--host-resolver-rules=MAP * ~NOTFOUND",
                            "--print-to-pdf=" + theirs, "file://" + page], capture_output=True, timeout=120)
            if not os.path.exists(theirs):
                print(html, "chrome produced nothing")
                continue
            po, pc = pages(ours), pages(theirs)
            if po == pc:
                same += 1
            else:
                diff += 1
                deltas.append((html, po, pc))
            a, b = ppm(ours, tmp, "o"), ppm(theirs, tmp, "c")
            if a and b:
                scores.append(score(read_pgm(a), read_pgm(b)))
            for f in glob.glob(os.path.join(tmp, "*.pgm")):
                os.remove(f)
            os.remove(theirs)
print("page count equal", same, "different", diff)
for h, po, pc in deltas[:40]:
    print("  ", h, "ours", po, "chrome", pc)
if scores:
    scores.sort()
    print("page-1 mean abs grey difference: n", len(scores), "median %.1f" % scores[len(scores) // 2], "p90 %.1f" % scores[int(len(scores) * .9)], "max %.1f" % scores[-1])
