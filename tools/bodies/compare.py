#!/usr/bin/env python3
"""Compares OneMail's HTML-to-text output with w3m, lynx and html2text on a normalised form.

For each <name>.html in the directory (with <name>.ours.txt and <name>.hidden.txt from BodiesTool) it runs the three oracles, reduces every
output to the sequence of its words (links, image text, bullets, quote and table marks removed), aligns the sequences, and classifies
each difference. A difference with no class is "unexplained". It also compares link addresses and counts list items.

usage: compare.py <dir> [--show N]   (HTMLTEXT oracle paths come from the environment set by oracles.sh)
"""
import difflib
import unicodedata
from urllib.parse import unquote
import json
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict

INVISIBLE = dict.fromkeys(map(ord, "\u00ad\u200b\u200c\u200d\u200e\u200f\u2060\ufeff\u034f\u180e"), None)
URL_RE = re.compile(r"\b[a-z][a-z0-9+.-]*://\S+|mailto:\S+", re.I)


MARK_RE = re.compile(r"^[ \t>|]*(?:[*\u2022o+-]|\d+\\?[.)])[ \t]+", re.M)


def words(text):
    text = unicodedata.normalize("NFKC", text).translate(INVISIBLE).replace("\u00a0", " ")
    text = MARK_RE.sub("", text)  # list markers: bullets and numbers differ by program, the items are compared as words
    text = re.sub(r"<[a-z][a-z0-9+.-]*:[^>\s]+>", " ", text, flags=re.I)  # OneMail writes an address as <address>
    text = re.sub(r"\[[^\]\n]*\]", " ", text)  # image text and link numbers
    text = URL_RE.sub(" ", text)
    return re.findall(r"[^\W_]+", text)


def oracle_copy(path):
    """The oracles read the file without meta tags: the text was decoded already, and a stale charset or refresh meta would make them differ."""
    src = open(path, encoding="utf-8").read()
    src = re.sub(r"<meta\b[^>]*>", "", src, flags=re.I)
    out = path[:-5] + ".oracle.html"
    open(out, "w", encoding="utf-8").write('<meta charset="utf-8">' + src)
    return out


def run_w3m(path):
    r = subprocess.run([os.environ["W3M"], "-dump", "-cols", "100000", "-T", "text/html", "-I", "utf-8", "-O", "utf-8", path], capture_output=True, timeout=120)
    return r.stdout.decode("utf-8", "replace")


def run_lynx(path):
    r = subprocess.run([os.environ["LYNX"], "-dump", "-width=100000", "-force_html", "-nocolor", "-display_charset=utf-8", "-assume_charset=utf-8", "-nomargins", path],
                       capture_output=True, timeout=120, env={**os.environ, "LYNX_CFG": os.environ.get("LYNX_CFG", "")})
    out = r.stdout.decode("utf-8", "replace")
    urls = []
    m = re.search(r"\n\s*(?:Visible links|References|Hidden links)\s*\n", out)
    if m:
        refs = out[m.start():]
        out = out[: m.start()]
        urls = re.findall(r"^\s*\d+\.\s+(\S+)", refs, re.M)
    return out, urls


H2T = None


def strip_markdown_links(text):
    """Removes the brackets and addresses of markdown links (which may nest and may wrap the address over lines) and keeps their text;
    brackets that are not part of a link stay, as the document's own text."""
    drop = []  # (start, end) spans to delete
    stack = []
    i = 0
    while i < len(text):
        c = text[i]
        if c == "[":
            stack.append(i)
        elif c == "]":
            opener = stack.pop() if stack else None
            if text.startswith("](", i):
                close = text.find(")", i)
                if close > 0 and opener is not None:
                    drop.append((opener, opener + 1))
                    drop.append((i, close + 1))
                    i = close
        i += 1
    out = []
    last = 0
    for a, b in sorted(drop):
        out.append(text[last:a])
        out.append(" ")
        last = b
    out.append(text[last:])
    return "".join(out)


def run_html2text(path):
    global H2T
    if H2T is None:
        H2T = subprocess.Popen([os.environ["HTML2TEXT_PY"], os.path.join(os.path.dirname(os.path.abspath(__file__)), "h2t_server.py")],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    H2T.stdin.write((path + "\n").encode())
    H2T.stdin.flush()
    n = int(H2T.stdout.readline())
    out = H2T.stdout.read(n).decode("utf-8", "replace")
    urls = re.findall(r"\]\(([^)\s]+)", out) + re.findall(r"<((?:https?|ftp|mailto):[^>\s]+)>", out)
    out = re.sub(r"<(?:https?|ftp|mailto):[^>\s]+>", " ", out)
    # markdown links, possibly nested or with the address wrapped over lines: drop the "](address)" parts and the brackets, keep the text
    out = strip_markdown_links(out)
    return out, urls


def list_items(text):
    return len(re.findall(r"^\s*(?:[*\u2022o+-]|\d+\.)\s+\S", text, re.M))


DECORATION = {"lynx": {"BUTTON", "IMAGE", "INLINE", "SUBMIT", "FORM", "LINK", "IMG"}, "w3m": set(), "html2text": set()}


SMALL_INT = re.compile(r"\d{1,2}$")


def explain_run(tool, a, b, sets):
    """The class of one differing run (ours a, oracle b), or None."""
    b = [w for w in b if w not in DECORATION[tool]]
    if not a and not b:
        return "decoration"
    if a and b and "".join(a) == "".join(b):
        return "word-boundary"
    if not a:
        cb = Counter(b)
        for name in ("hidden", "skipped", "alt", "link-unsupported"):
            if cb <= sets[name]:
                return name
        if cb <= (sets["hidden"] + sets["skipped"] + sets["alt"]):
            return "hidden+skipped+alt"
    if a and not b and Counter(a) <= sets["link-unsupported"]:
        return "link-unsupported"
    if a and not b and Counter(a) <= sets["link-ws"]:
        return "oracle-markdown-link"  # html2text loses the text of a link whose address has a line break in it
    if a and b and sorted("".join(a)) == sorted("".join(b)):
        return "same-letters"  # the same characters in another order or grouping (text in a mangled charset, a glued word shifted by the aligner)
    if b:
        # an oracle word glued to an alt word ("facebookfind"): the oracle writes alt text without a space
        joined = "".join(b)
        for w in sorted(sets["alt"], key=len, reverse=True):
            joined = joined.replace(w, "")
        if joined == "".join(a):
            return "alt-glued"
    return None


def classify(tool, ours, oracle, sets, has_table=False, has_ol=False, has_c1=False):
    """Returns (explained, unexplained) lists of (ours_run, oracle_run, reason).

    A run only the oracle has is explained when every word of it is text OneMail leaves out on purpose: elements a screen hides, head/script/
    style/title/form controls, or image alt text (OneMail writes alt text in brackets, which the normal form drops, and html2text is run
    without images). Other classes: word boundaries (same letters, different spaces), an oracle word glued to an alt word, links of schemes
    the oracle cannot follow, table cells set side by side (w3m, html2text: compared as a multiset), small list numbers, and the
    Windows-1252 numeric references (&#128;-&#159;) that the oracles drop and WHATWG maps (the oracle's words lose the symbol)."""
    if has_ol:
        # Programs number ordered lists in different places (a table cell may put the number mid-line); small numbers are not compared.
        ours = [w for w in ours if not SMALL_INT.match(w)]
        oracle = [w for w in oracle if not SMALL_INT.match(w)]
    if has_c1:
        ours = [re.sub(r"(TM|SM|EUR)$", "", w) or w for w in ours]
        oracle = [re.sub(r"(TM|SM|EUR)$", "", w) or w for w in oracle]
    plain = [w for w in oracle if w not in DECORATION[tool]]
    if "".join(ours) == "".join(plain):
        return ([(ours, oracle, "word-boundary")] if ours != plain else []), []
    if tool in ("w3m", "html2text") and has_table:
        extra = Counter(plain) - Counter(ours)
        missing = Counter(ours) - Counter(plain)
        allowed = Counter("".join(w for name in ("hidden", "skipped", "alt", "link-unsupported") for w in sets[name].elements()))
        cextra = Counter("".join(plain)) - Counter("".join(ours))
        cmissing = Counter("".join(ours)) - Counter("".join(plain))
        if not cmissing and cextra <= allowed:
            return [(ours, oracle, "table-layout")], []
    sm = difflib.SequenceMatcher(None, ours, oracle, autojunk=False)
    ops = [o for o in sm.get_opcodes() if o[0] != "equal"]
    results = []  # (i1, i2, j1, j2, reason)
    for _, i1, i2, j1, j2 in ops:
        results.append([i1, i2, j1, j2, explain_run(tool, ours[i1:i2], oracle[j1:j2], sets)])
    # unexplained neighbours with at most three equal words between them are one stretch: a glued word shifts the alignment
    k = 0
    while k < len(results):
        if results[k][4] is None:
            m = k
            while m + 1 < len(results) and results[m + 1][4] is None and results[m + 1][0] - results[m][1] <= 3 and results[m + 1][2] - results[m][3] <= 3:
                m += 1
            if m > k:
                i1, j1, i2, j2 = results[k][0], results[k][2], results[m][1], results[m][3]
                reason = explain_run(tool, ours[i1:i2], oracle[j1:j2], sets)
                if reason:
                    results[k:m + 1] = [[i1, i2, j1, j2, reason]]
            k += 1
        else:
            k += 1
    explained = [(ours[r[0]:r[1]], oracle[r[2]:r[3]], r[4]) for r in results if r[4]]
    bad = [(ours[r[0]:r[1]], oracle[r[2]:r[3]], "") for r in results if not r[4]]
    return explained, bad


def main():
    d = sys.argv[1]
    show = int(sys.argv[sys.argv.index("--show") + 1]) if "--show" in sys.argv else 5
    names = sorted(f[:-5] for f in os.listdir(d) if f.endswith(".html") and not f.endswith(".oracle.html"))
    if "--only" in sys.argv:
        names = [n for n in names if re.search(sys.argv[sys.argv.index("--only") + 1], n)]
    stats = defaultdict(Counter)
    samples = defaultdict(list)
    url_stats = defaultdict(Counter)
    per_group = defaultdict(Counter)
    for n in names:
        path = os.path.join(d, n + ".html")
        ours_text = open(os.path.join(d, n + ".ours.txt"), encoding="utf-8").read()
        hidden_path = os.path.join(d, n + ".hidden.txt")
        hidden = Counter(words(open(hidden_path, encoding="utf-8").read())) if os.path.exists(hidden_path) else Counter()
        def load(ext):
            p = os.path.join(d, n + ext)
            return Counter(words(open(p, encoding="utf-8").read())) if os.path.exists(p) else Counter()

        sets = {"hidden": hidden, "skipped": load(".skipped.txt"), "alt": load(".alt.txt")}
        ours = words(ours_text)
        ours_urls = set(re.findall(r"<((?:https?|ftp|mailto):[^>\s]+)>", ours_text))
        opath = oracle_copy(path)
        src = open(path, encoding="utf-8").read()
        unsupported = " ".join(re.findall(r"<a\b[^>]*href\s*=\s*[\"']?(?:news|nntp|snews):[^>]*>(.*?)</a>", src, re.I | re.S))
        spaced = " ".join(re.findall(r"<a\b[^>]*href\s*=\s*[\"'][^\"']*\s[^\"']*[\"'][^>]*>(.*?)</a>", src, re.I | re.S))
        sets["link-ws"] = Counter(words(re.sub(r"<[^>]*>", " ", spaced)))
        sets["link-unsupported"] = Counter(words(re.sub(r"<[^>]*>", " ", unsupported)))
        has_table = bool(re.search(r"<table", src, re.I))
        has_c1 = bool(re.search(r"&#(?:1[2-5]\d|x[89][0-9a-f]);", src, re.I))
        has_ol = bool(re.search(r"<ol\b", src, re.I))
        area_urls = {unquote(x) for x in re.findall(r"<area\b[^>]*?href\s*=\s*[\"']?([^\"'\s>]+)", src, re.I)}
        outs = {"w3m": (run_w3m(opath), None)}
        outs["lynx"] = run_lynx(opath)
        outs["html2text"] = run_html2text(opath)
        for tool, (text, urls) in outs.items():
            ex, bad = classify(tool, ours, words(text), sets, has_table, has_ol, has_c1)
            st = stats[tool]
            st["files"] += 1
            if not ex and not bad:
                st["identical"] += 1
            elif not bad:
                st["explained-only"] += 1
            else:
                st["unexplained-files"] += 1
                if len(samples[tool]) < show:
                    samples[tool].append((n, bad[:3]))
            for _, _, reason in ex:
                st["class:" + reason] += 1
            st["explained-diffs"] += len(ex)
            group = re.match(r"[a-z]+", n).group(0) if not n.startswith(("c-", "g-")) else n[0] + "-"
            per_group[tool][group + (" identical" if not ex and not bad else " differing-explained" if not bad else " UNEXPLAINED")] += 1
            st["unexplained-diffs"] += len(bad)
            st["ours-list-items"] += list_items(ours_text)
            st["oracle-list-items"] += list_items(text)
            if urls is not None:
                u = url_stats[tool]
                urls = [unquote(x) for x in urls]
                ours_urls_u = {unquote(x) for x in ours_urls}
                for url in urls:
                    if url.startswith("#") or re.match(r"(javascript|data|cid|file|lynximgmap):", url, re.I):
                        u["skipped-scheme"] += 1
                    elif url in area_urls:
                        u["image-map-area"] += 1
                    elif url in ours_urls_u or url.rstrip("/") in {x.rstrip("/") for x in ours_urls_u} or re.sub(r"^(https?://|mailto:)(www\.)?", "", url.lower()).rstrip("/") in unquote(ours_text).lower():
                        u["found"] += 1
                    else:
                        u["missing"] += 1
                        if len(samples[tool + "-url"]) < show:
                            samples[tool + "-url"].append((n, url))
                extra = [x for x in ours_urls_u if x not in set(urls) and x.rstrip("/") not in {y.rstrip("/") for y in urls}
                         and not any(x.startswith(y) for y in urls)]
                u["ours-not-in-oracle"] += len(extra)
                if extra and len(samples[tool + "-extra"]) < show:
                    samples[tool + "-extra"].append((n, extra[:2]))
    print(json.dumps({"files": len(names), "words": {k: dict(v) for k, v in stats.items()}, "urls": {k: dict(v) for k, v in url_stats.items()}, "per-corpus": {k: dict(sorted(v.items())) for k, v in per_group.items()}}, indent=1))
    for k, v in samples.items():
        print("---", k)
        for s in v:
            print(str(s)[:300])


if __name__ == "__main__":
    main()
