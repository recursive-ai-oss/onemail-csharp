#!/usr/bin/env python3
"""Writes tools/oracles/reports/urljoin-pairs.tsv: "base<TAB>reference" pairs for the MHTML resolution differential.

The RFC 3986 section 5.4 examples come first, then every combination of a set of bases and a set of references that appear in
saved web pages (relative paths with dot segments, queries, fragments, scheme-relative URLs, file: URLs, thismessage:/).
Deterministic.
"""
import itertools
import os

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "oracles", "reports", "urljoin-pairs.tsv")
BASE = "http://a/b/c/d;p?q"
rfc = [
    "g:h", "g", "./g", "g/", "/g", "//g", "?y", "g?y", "#s", "g#s", "g?y#s", ";x", "g;x", "g;x?y#s", "", ".", "./", "..", "../", "../g", "../..", "../../", "../../g",
    "../../../g", "../../../../g", "/./g", "/../g", "g.", ".g", "g..", "..g", "./../g", "./g/.", "g/./h", "g/../h", "g;x=1/./y", "g;x=1/../y", "g?y/./x", "g?y/../x", "g#s/./x", "g#s/../x",
    "http:g",
]
pairs = [(BASE, r) for r in rfc]
bases = [
    "http://www.example.org/dir/page.html",
    "http://www.example.org/",
    "http://www.example.org",
    "https://Example.ORG:8443/a/b/",
    "file:///C:/Users/me/page.htm",
    "thismessage:/",
    "thismessage:/x/y.html",
    "http://x.example/a/b/c.html?q=1#f",
]
refs = [
    "a.png", "./a.png", "../a.png", "../../a.png", "/a.png", "//other.example/a.png", "a/b/../c.png", "a/./b.png", "?x=1", "#top", "a.png?x=1#y",
    "http://y.example/z.png", "HTTP://Y.EXAMPLE/z.png", "cid:foo@bar", "mailto:a@b.example", "data:image/png;base64,AAAA", "page_files/a b.png", "a%20b.png", "a/%2e%2e/b.png",
    "C:\\dir\\a.png", "file:///D:/x.png", "images/",
]
for b, r in itertools.product(bases, refs):
    pairs.append((b, r))
with open(out, "w", encoding="utf-8", newline="\n") as f:
    for b, r in pairs:
        f.write("%s\t%s\n" % (b, r))
print("wrote %d pairs" % len(pairs))
