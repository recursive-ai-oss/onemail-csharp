#!/usr/bin/env python3
"""Compare OneMail's RTF to HTML conversion with black-box converters on a normalised model.

    htmlcompare.py <dir>

<dir> holds <id>.rtf and, per converter, <id>.ours.html, <id>.pandoc.html, <id>.lo.html and <id>.unrtf.html (see run-html-oracles.sh).
The model is not HTML bytes but what a reader sees: the words, which words are bold/italic/underlined/struck/super-/subscript,
list items with their kind, table cell text, link targets, heading levels, and the image count. For each document and metric
the result for OneMail is compared with each converter; a metric a converter cannot produce (it has no such element in any
document) is reported as n/a for that converter. Writes <dir>/compare.tsv (one row per document and metric) and prints totals.
"""
import collections
import fnmatch
import html
import json
import re
import sys
import unicodedata
from html.parser import HTMLParser
from pathlib import Path

BLOCK = {"p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "tr", "td", "th", "table", "ul", "ol", "br", "hr", "blockquote", "pre",
         "body", "title", "center", "dd", "dt", "dl", "caption", "thead", "tbody"}
VOID = {"br", "hr", "img", "meta", "link", "col", "area", "base", "input", "wbr", "param", "source", "track"}
SKIP = {"script", "style", "head", "title"}


def style_flags(style):
    f = set()
    s = (style or "").lower().replace(" ", "")
    if "font-weight:bold" in s or "font-weight:700" in s or "font-weight:600" in s:
        f.add("b")
    if "font-style:italic" in s:
        f.add("i")
    if "text-decoration:underline" in s or "text-decoration:overline,underline" in s:
        f.add("u")
    if "line-through" in s:
        f.add("s")
    if "vertical-align:super" in s:
        f.add("sup")
    if "vertical-align:sub" in s:
        f.add("sub")
    if "display:none" in s or "visibility:hidden" in s:
        f.add("hidden")
    return f


TAG_FLAG = {"b": "b", "strong": "b", "i": "i", "em": "i", "u": "u", "ins": "u", "s": "s", "strike": "s", "del": "s", "sup": "sup", "sub": "sub"}


class Model(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack = []  # (tag, flags added)
        self.flags = collections.Counter()
        self.chars = []        # (character, frozenset(flags)); block boundaries are spaces
        self.skip = 0
        self.lists = []        # (kind, depth, text)
        self.list_stack = []
        self.item_text = []
        self.item_open = []
        self.tables = []
        self.table_stack = []
        self.cell = None
        self.links = []
        self.link_open = []
        self.headings = []
        self.heading = None
        self.images = 0
        self.in_link = 0
        self.block_breaks = 0

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        extra = ()
        if tag in SKIP:
            self.skip += 1
            self.stack.append((tag, (), ("skip",)))
            return
        added = []
        if tag in TAG_FLAG:
            added.append(TAG_FLAG[tag])
        added.extend(style_flags(a.get("style")))
        for f in added:
            self.flags[f] += 1
        if tag in ("ul", "ol"):
            self.list_stack.append(tag)
        if tag == "li":
            self.item_open.append(len(self.lists))
            self.lists.append([self.list_stack[-1] if self.list_stack else "ul", len(self.list_stack), []])
        if tag == "table":
            self.table_stack.append([])
        if tag == "tr" and self.table_stack:
            self.table_stack[-1].append([])
        if tag in ("td", "th") and self.table_stack and self.table_stack[-1]:
            self.cell = []
            self.table_stack[-1][-1].append(self.cell)
        if tag == "a" and a.get("href") is not None:
            self.links.append(a["href"])
            self.in_link += 1
            extra = ("link",)
        if tag == "span" and "rtf-picture" in (a.get("class") or ""):
            self.images += 1
            self.skip += 1
            extra = ("skip",)
        if tag in ("h1", "h2", "h3", "h4", "h5", "h6"):
            self.heading = [int(tag[1]), []]
        if tag == "img":
            self.images += 1
        if tag in BLOCK:
            self.chars.append((" ", frozenset()))
        if tag not in VOID:
            self.stack.append((tag, tuple(added), extra))

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in VOID:
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        if tag in VOID:
            return
        # pop to the matching open tag (the parsers here are lenient about nesting)
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                for t, added, extra in reversed(self.stack[i:]):
                    if "skip" in extra:
                        self.skip -= 1
                    if "link" in extra:
                        self.in_link -= 1
                    for f in added:
                        self.flags[f] -= 1
                    if t in ("ul", "ol") and self.list_stack:
                        self.list_stack.pop()
                    if t == "li" and self.item_open:
                        self.item_open.pop()
                    if t == "table" and self.table_stack:
                        self.tables.append(self.table_stack.pop())
                    if t in ("td", "th"):
                        self.cell = None
                    if t.startswith("h") and len(t) == 2 and t[1] in "123456" and self.heading:
                        self.headings.append((self.heading[0], norm("".join(self.heading[1]))))
                        self.heading = None
                del self.stack[i:]
                break
        if tag in BLOCK:
            self.chars.append((" ", frozenset()))

    def handle_data(self, data):
        if self.skip:
            return
        # A link is underlined by default, so an explicit underline inside one says nothing.
        flags = frozenset(f for f, n in self.flags.items() if n > 0 and not (f == "u" and self.in_link > 0))
        if "hidden" in flags:
            return
        text = norm_keep(data)
        for ch in text:
            self.chars.append((ch, flags))
        if self.cell is not None:
            self.cell.append(text)
        if self.item_open:
            self.lists[self.item_open[-1]][2].append(text)
        if self.heading is not None:
            self.heading[1].append(text)

    def words(self):
        """Words from the character stream: split at white space and block boundaries only, so tags inside a word do not split it."""
        out, cur = [], []
        for ch, flags in self.chars + [(" ", frozenset())]:
            if ch.isspace():
                if cur:
                    word = "".join(c for c, _ in cur)
                    core = [f for c, f in cur if c.isalnum()] or [f for _, f in cur]
                    common = frozenset.union(*core) if core else frozenset()
                    out.append((word, common))
                    cur = []
            else:
                cur.append((ch, flags))
        return out


def norm_keep(s):
    """Like norm but keeps leading and trailing white space (as one space)."""
    lead = s[:1].isspace()
    trail = s[-1:].isspace()
    n = norm(s)
    if not n:
        return " " if s.isspace() else ""
    return (" " if lead and n else "") + n + (" " if trail and n else "")


def norm(s):
    s = unicodedata.normalize("NFC", s)
    s = s.replace("\u00a0", " ").replace("\u00ad", "").replace("\u2011", "-").replace("\u200b", "").replace("\ufeff", "")
    s = re.sub(r"[\u2018\u2019]", "'", s)
    s = re.sub(r"[\u201c\u201d]", '"', s)
    s = re.sub(r"[\u2013\u2014]", "-", s)
    s = re.sub(r"\s+", " ", s)
    return s.strip()


MARKER = re.compile(r"^(?:[\u2022\u25cf\u25cb\u25aa\u25a0\u00b7o\-\*\uf0b7\uf0a7\uf0d8]|\(?[0-9ivxlcdmIVXLCDM]{1,4}[.)]|\(?[a-zA-Z][.)])$")


def load(path):
    raw = Path(path).read_bytes()
    m = re.search(rb"charset\s*=\s*[\"']?([A-Za-z0-9_\-]+)", raw[:4096])
    enc = m.group(1).decode("ascii") if m else "utf-8"
    try:
        text = raw.decode(enc)
    except (LookupError, UnicodeDecodeError):
        text = raw.decode("utf-8", errors="replace")
    p = Model()
    p.feed(text)
    p.close()
    return p


def metrics(m):
    ws = m.words()
    words = [w for w, _ in ws]
    # Converters differ in whether they write a list marker as text; drop a leading marker word of each list item and any lone marker.
    out = {}
    out["text"] = [w for w in words]
    fmt = {}
    for flag in ("b", "i", "u", "s", "sup", "sub"):
        fmt[flag] = sorted(w for w, f in ws if flag in f)
    out["fmt"] = fmt
    out["lists"] = [(k, d, norm("".join(t))) for k, d, t in m.lists]
    out["tables"] = [[[norm("".join(c)) for c in r] for r in t] for t in m.tables]
    out["links"] = sorted(m.links)
    out["headings"] = m.headings
    out["images"] = m.images
    return out


def strip_markers(words):
    return [w for w in words if not MARKER.match(w)]


def compare(ours, theirs, metric):
    if metric == "text":
        return strip_markers(ours["text"]) == strip_markers(theirs["text"])
    if metric.startswith("fmt:"):
        flag = metric[4:]
        return strip_markers(ours["fmt"][flag]) == strip_markers(theirs["fmt"][flag])
    if metric == "lists":
        a = [(k, strip_markers(t.split())) for k, d, t in ours["lists"]]
        b = [(k, strip_markers(t.split())) for k, d, t in theirs["lists"]]
        return a == b
    if metric == "tables":
        def flat(ts):
            return [[[strip_markers(c.split()) for c in r] for r in t] for t in ts]
        return flat(ours["tables"]) == flat(theirs["tables"])
    if metric == "links":
        return ours["links"] == theirs["links"]
    if metric == "headings":
        return [h[1:] for h in ours["headings"]] == [h[1:] for h in theirs["headings"]]
    if metric == "images":
        return ours["images"] == theirs["images"]
    raise KeyError(metric)


METRICS = ["text", "fmt:b", "fmt:i", "fmt:u", "fmt:s", "fmt:sup", "fmt:sub", "lists", "tables", "links", "headings", "images"]
ORACLES = ["pandoc", "lo", "unrtf"]


def load_rules(d):
    # explained-html.tsv: oracle<TAB>id-pattern<TAB>metric-pattern<TAB>reason (patterns are shell globs). A difference from that oracle on a
    # matching document and metric is its documented limitation or behaviour, not a defect of OneMail.
    out = []
    for f in (d / "explained.tsv", Path(__file__).with_name("explained-html.tsv")):
        if f.exists():
            for line in f.read_text().splitlines():
                parts = line.split("\t")
                if line and not line.startswith("#") and len(parts) >= 4:
                    out.append((parts[0], parts[1], parts[2], parts[3]))
    return out


def load_explained(d):
    # <dir>/explained.tsv or tools/rtf/explained-html.tsv: id<TAB>metric<TAB>reason; id * covers every document
    out = {}
    for f in (d / "explained.tsv", Path(__file__).with_name("explained-html.tsv")):
        if f.exists():
            for line in f.read_text().splitlines():
                if line and not line.startswith("#"):
                    parts = line.split("\t")
                    if len(parts) >= 3:
                        out[parts[0] + "\t" + parts[1]] = parts[2]
    return out


def main():
    d = Path(sys.argv[1])
    ids = sorted(p.name[:-len(".ours.html")] for p in d.glob("*.ours.html"))
    rows = []
    totals = {o: collections.Counter() for o in ORACLES}
    models = {}
    for i in ids:
        ours = metrics(load(d / (i + ".ours.html")))
        models[i] = {"ours": ours}
        for o in ORACLES:
            f = d / (i + "." + o + ".html")
            if not f.exists():
                continue
            theirs = metrics(load(f))
            models[i][o] = theirs
            for metric in METRICS:
                ok = compare(ours, theirs, metric)
                # A metric neither side has anything for says nothing about the converter.
                rows.append((i, o, metric, "agree" if ok else "differ"))
                totals[o][(metric, ok)] += 1
    # An oracle that never produces a kind of content in any document of the set cannot be asked about it: that metric is n/a for it.
    support = {o: set() for o in ORACLES}
    for i, ms in models.items():
        for o in ORACLES:
            if o not in ms:
                continue
            t = ms[o]
            for metric in METRICS:
                v = (t["fmt"][metric[4:]] if metric.startswith("fmt:") else t[metric])
                if metric != "text" and v:
                    support[o].add(metric)
    support_by = {o: support[o] | {"text"} for o in ORACLES}
    # The gate: for each oracle, every document and metric where it differs from OneMail must be explained by a rule in explained-html.tsv
    # (a documented limitation or behaviour of that oracle). An oracle with no unexplained difference is "validated"; two must be.
    rules = load_rules(d)
    uses = collections.Counter()
    per = {o: collections.Counter() for o in ORACLES}
    unexplained = []
    for i, o, metric, result in rows:
        if metric not in support_by[o]:
            per[o]["n/a"] += 1
            continue
        if result == "agree":
            per[o]["agree"] += 1
            continue
        hit = [n for n, (op, ip, mp, why) in enumerate(rules) if op == o and fnmatch.fnmatchcase(i, ip) and fnmatch.fnmatchcase(metric, mp)]
        if hit:
            per[o]["explained"] += 1
            uses[hit[0]] += 1
        else:
            per[o]["UNEXPLAINED"] += 1
            unexplained.append((i, o, metric))
    unused = [r for n, r in enumerate(rules) if uses[n] == 0]
    validated = [o for o in ORACLES if per[o]["UNEXPLAINED"] == 0]
    with open(d / "compare.tsv", "w") as f:
        f.write("id\toracle\tmetric\tresult\n")
        for r in rows:
            f.write("\t".join(r) + "\n")
    (d / "models.json").write_text(json.dumps(models, ensure_ascii=False))
    print("documents", len(ids))
    for o in ORACLES:
        print(o, dict(per[o]), "never produces:", sorted(set(METRICS) - support_by[o]))
    print("oracles with no unexplained difference:", validated)
    for i, o, metric in unexplained:
        print("  UNEXPLAINED", o, i, metric)
    for r in unused:
        print("  unused rule", r[:3])


if __name__ == "__main__":
    main()
