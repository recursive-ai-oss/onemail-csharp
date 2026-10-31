#!/usr/bin/env python3
"""Compare OneMail's RTF results with black-box oracles, over the bodies RtfTool extracted.

    compare.py <dir>        <dir> is RtfTool's output directory (after `RtfTool run`)

Oracles (run only, released packages in a uv venv; see README.md):
  compressed-rtf 1.0.7 (MIT)  decompress / compress
  RTFDE 0.1.2.2 (LGPL-3.0)    de-encapsulation
and the message's own PidTagHtml / PidTagBody streams.

Writes <dir>/<id>.oracle.rtfz (the oracle's compression, for `RtfTool verify`), <dir>/<id>.rtfde.* and
<dir>/compare.txt. Prints a summary. Never prints message content beyond the first differing context of a few characters.
"""
import csv
import hashlib
import os
import re
import sys

import compressed_rtf
from RTFDE.deencapsulate import DeEncapsulator


def read(path):
    with open(path, "rb") as f:
        return f.read()


def decode_body(data, kind, cp):
    if kind.endswith(".u"):
        return data.decode("utf-16-le", errors="replace")
    for enc in ("utf-8",):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            pass
    try:
        return data.decode("cp%s" % cp)
    except (LookupError, UnicodeDecodeError):
        return data.decode("cp1252", errors="replace")


def crlf(s):
    return s.replace("\r\n", "\n").replace("\r", "\n")


def squash(s):
    return re.sub(r"\s+", " ", s).strip()


def meta_free(s):
    return re.sub(r"""<meta\s+http-equiv=["']?content-type[^>]*>""", "", s, flags=re.I)


def serialisation(s):
    return squash(meta_free(s).lower().replace("&nbsp;", "\u00a0").replace("\u00a0", " "))


def classify(a, b):
    if a == b:
        return "exact"
    if crlf(a) == crlf(b):
        return "line-endings"
    if crlf(a).rstrip() == crlf(b).rstrip():
        return "trailing-whitespace"
    if squash(a) == squash(b):
        return "whitespace"
    if squash(meta_free(a)) == squash(meta_free(b)):
        return "content-type meta added by the store"
    if crlf(a).replace("\u00a0", "&nbsp;") == crlf(b):
        return "nbsp-as-entity"
    if serialisation(a) == serialisation(b):
        return "re-serialised (case, &nbsp;, whitespace)"
    if text_words(a) == text_words(b):
        return "same text, markup re-serialised"
    return "different"


def text_words(s):
    return re.findall(r"\w+", re.sub(r"<[^>]*>", " ", s).replace("&nbsp;", " "))


def first_diff(a, b):
    n = min(len(a), len(b))
    i = next((k for k in range(n) if a[k] != b[k]), n)
    return i, a[max(0, i - 20): i + 20], b[max(0, i - 20): i + 20]


def main(d):
    rows = list(csv.DictReader(open(os.path.join(d, "report.tsv")), delimiter="\t"))
    out = []
    counts = {}

    def bump(k):
        counts[k] = counts.get(k, 0) + 1

    seen = set()
    for r in rows:
        i = r["id"]
        z = read(os.path.join(d, i + ".rtfz"))
        ours = read(os.path.join(d, i + ".ours.rtf"))
        digest = hashlib.sha256(z).hexdigest()
        dup = digest in seen
        seen.add(digest)
        # decompression
        try:
            theirs = compressed_rtf.decompress(z)
        except Exception as e:  # the oracle is allowed to refuse
            theirs = None
            out.append("%s decompress: oracle error %s" % (i, type(e).__name__))
        if theirs is not None:
            k = "decompress identical" if theirs == ours else "decompress DIFFERENT"
            bump(k)
            if theirs != ours:
                out.append("%s decompress differs (ours %d bytes, oracle %d)" % (i, len(ours), len(theirs)))
        # compression
        packed = compressed_rtf.compress(ours)
        with open(os.path.join(d, i + ".oracle.rtfz"), "wb") as f:
            f.write(packed)
        mine = read(os.path.join(d, i + ".ours.rtfz"))
        try:
            back = compressed_rtf.decompress(mine)
            bump("oracle reads our compression: " + ("identical" if back == ours else "DIFFERENT"))
            if back != ours:
                out.append("%s oracle decompresses our output to different bytes" % i)
        except Exception as e:
            bump("oracle reads our compression: ERROR")
            out.append("%s oracle rejects our compression: %s" % (i, type(e).__name__))
        bump("our compression equals the original bytes" if mine == z else "our compression differs from the original bytes")
        bump("oracle compression equals the original bytes" if packed == z else "oracle compression differs from the original bytes")
        if not dup:
            bump("unique original streams")
        # de-encapsulation
        kind = r["deencap"]
        if kind == "None":
            try:
                de = DeEncapsulator(ours)
                de.deencapsulate()
                bump("not encapsulated here, RTFDE agrees" if not (getattr(de, "html", None) or getattr(de, "text", None)) else "not encapsulated here, RTFDE extracted something")
            except Exception:
                bump("not encapsulated here, RTFDE refuses too")
            continue
        ext = "html" if kind == "Html" else "txt"
        mine_text = open(os.path.join(d, i + ".ours.deencap." + ext), encoding="utf-8", newline="").read()
        try:
            de = DeEncapsulator(ours)
            de.deencapsulate()
            theirs_text = de.html if kind == "Html" else de.text
            if isinstance(theirs_text, bytes):
                theirs_text = theirs_text.decode("utf-8", errors="replace")
        except Exception as e:
            theirs_text = None
            bump("RTFDE error on encapsulated " + kind)
            out.append("%s RTFDE error %s" % (i, type(e).__name__))
        if theirs_text is not None:
            c = classify(mine_text, theirs_text)
            bump("deencap vs RTFDE " + kind + ": " + c)
            if c != "exact":
                pos, a, b = first_diff(mine_text, theirs_text)
                out.append("%s deencap vs RTFDE %s: %s at %d ours=%r oracle=%r" % (i, kind, c, pos, a, b))
        # the message's own body
        body = None
        for suffix in (("html", "html.u") if kind == "Html" else ("txt.u", "txt.a")):
            p = os.path.join(d, i + "." + suffix)
            if os.path.exists(p):
                body = decode_body(read(p), suffix, r["default_cp"])
                break
        if body is None:
            bump("no own body stream to compare (" + kind + ")")
        else:
            c = classify(mine_text, body)
            bump("deencap vs own body " + kind + ": " + c)
            if c not in ("exact",):
                pos, a, b = first_diff(mine_text, body)
                out.append("%s deencap vs own body %s: %s at %d ours=%r own=%r" % (i, kind, c, pos, a, b))
    explained = {
        "exact": "-",
        "line-endings": "R1 (CRLF per MS-OXRTFEX; RTFDE writes LF)",
        "nbsp-as-entity": "R2 (U+00A0 kept as a character; RTFDE writes &nbsp;)",
        "content-type meta added by the store": "R3 (own body carries an injected content-type meta)",
        "same text, markup re-serialised": "R4 (own body is a re-serialised copy: same text, markup differs)",
    }
    unexplained = 0
    for k in sorted(counts):
        m = re.match(r"deencap vs (RTFDE|own body) (\w+): (.*)$", k)
        if m and m.group(3) not in explained:
            unexplained += counts[k]
    for k in list(counts):
        if k.startswith("deencap vs own body Html: different") and "RTFDE" not in k:
            pass
    with open(os.path.join(d, "compare.txt"), "w") as f:
        f.write("\n".join(out) + "\n")
    print("bodies: %d" % len(rows))
    for k in sorted(counts):
        print("%5d  %s" % (counts[k], k))
    print("differences not mapped to R1-R4 (each is listed in compare.txt and needs a reason): %d" % unexplained)
    print("details: %s" % os.path.join(d, "compare.txt"))


if __name__ == "__main__":
    main(sys.argv[1])
