#!/usr/bin/env python3
"""Writes generated HTML cases for the HTML-to-text comparison: one small file per construct, then seeded random mixtures.

usage: gen_cases.py <out-dir> [count] [seed]   (defaults 300, 28)
"""
import os
import random
import sys

CONSTRUCTS = {
    "p": "<p>First paragraph with some words.</p><p>Second   paragraph\nwith   odd spacing.</p>",
    "br": "<p>line one<br>line two<br><br>after a gap</p>",
    "headings": "<h1>Big title</h1><h2>Sub title</h2><p>text</p><h3>Third</h3>",
    "inline": "<p>Some <b>bold</b>, <i>italic</i>, <u>under</u>, <code>code</code> and <span style='color:red'>red</span> words.</p>",
    "ul": "<ul><li>alpha</li><li>beta</li><li>gamma</li></ul>",
    "ol": "<ol><li>first</li><li>second</li><li>third</li></ol>",
    "nested-lists": "<ul><li>one<ul><li>one a</li><li>one b<ol><li>deep x</li><li>deep y</li></ol></li></ul></li><li>two</li></ul>",
    "dl": "<dl><dt>Term one</dt><dd>Definition one</dd><dt>Term two</dt><dd>Definition two</dd></dl>",
    "table": "<table border=1><tr><th>Name</th><th>Qty</th></tr><tr><td>apple</td><td>3</td></tr><tr><td>pear</td><td>12</td></tr></table>",
    "layout-table": "<table width=600><tr><td><table><tr><td><p>Header text</p></td></tr><tr><td>Body text here</td></tr></table></td></tr></table>",
    "table-colspan": "<table><tr><td colspan=2>wide cell</td></tr><tr><td>left</td><td>right</td></tr></table>",
    "blockquote": "<p>Before</p><blockquote><p>Quoted line one</p><blockquote>Nested quote</blockquote></blockquote><p>After</p>",
    "cite": "<div>New text</div><blockquote type=\"cite\"><div>Old text</div></blockquote>",
    "pre": "<p>Code:</p><pre>  indented line\n\n    more   spaces\nlast</pre><p>done</p>",
    "link-text": "<p>Visit <a href='https://example.com/page'>our page</a> now.</p>",
    "link-same": "<p><a href='https://example.com'>https://example.com</a> and <a href='mailto:me@example.com'>me@example.com</a></p>",
    "link-skip": "<p><a href='#top'>top</a> <a href='javascript:void(0)'>js</a> <a name=x>anchor</a></p>",
    "image-alt": "<p>Logo: <img src='a.png' alt='Company logo'> end</p>",
    "image-noalt": "<p>pic <img src='a.png'> <img src='b.png' alt=''> end</p>",
    "pixel": "<p>text<img src='t.gif' width=1 height=1 alt='tracker'></p>",
    "entities": "<p>Fish &amp; chips &lt;3 &copy; 2026 &eacute;t&eacute; &#8364;5 &nbsp;&nbsp;spaced &quot;quoted&quot;</p>",
    "hr": "<p>above</p><hr><p>below</p>",
    "hidden-inline": "<p>shown</p><div style='display:none'>secret one</div><span style='visibility:hidden'>secret two</span><p>also shown</p>",
    "hidden-class": "<style>.pre{display:none;max-height:0}.vis{color:red}</style><div class='pre'>preheader text</div><p class='vis'>real body</p>",
    "hidden-media": "<style>@media screen and (max-width:480px){.m{display:none}}.d{display:none}@media (min-width:2px){.s{display:none}}</style><p class='m'>kept on wide screens</p><p class='d'>hidden desktop</p><p class='s'>hidden screen</p>",
    "hidden-attr": "<p>shown</p><p hidden>attr hidden</p><div style='mso-hide:all'>outlook hidden</div>",
    "head": "<html><head><title>The title</title><meta charset=utf-8><style>p{color:red}</style><script>var a='script text';</script></head><body><p>body only</p></body></html>",
    "script-body": "<p>before</p><script>document.write('x')</script><noscript>no script fallback</noscript><p>after</p>",
    "comments": "<p>one<!-- hidden comment --> two</p><!--[if mso]><p>conditional</p><![endif]-->",
    "form": "<form><p>Name <input type=text value=v> <select><option>opt a</option><option>opt b</option></select> <button>Go</button></p><textarea>area text</textarea></form>",
    "center-font": "<center><font size=2 face=Arial>Old style <b>markup</b></font></center>",
    "div-soup": "<div><div><div>a</div><div>b</div></div><div>c</div></div>",
    "span-soup": "<span>one</span><span>two</span> <span>three</span>",
    "nbsp-pad": "<p>padding&nbsp;&nbsp;&nbsp;&nbsp;here</p><p>&nbsp;</p><p>after</p>",
    "zero-width": "<p>pre&zwnj;&nbsp;&zwnj;&nbsp;header&#8203;text&shy;break</p>",
    "unclosed": "<p>one<p>two<ul><li>a<li>b</ul><table><tr><td>x<td>y",
    "fragment": "just text <b>with</b> a fragment",
    "preformatted-css": "<div style='white-space:pre'>keep   this\n   spacing</div><div style='white-space:pre-line'>fold   spaces\nkeep lines</div>",
    "figure": "<figure><img src=x alt='A chart'><figcaption>Chart caption</figcaption></figure>",
    "address": "<address>1 Main St<br>Springfield</address>",
    "unicode": "<p>日本語のテキスト, Ελληνικά, العربية, emoji 😀 and café</p>",
    "inline-block-mix": "<p>text<div>block inside p</div>tail</p>",
    "button-link": "<table><tr><td><a href='https://shop.example/buy?x=1&amp;y=2' style='display:block'>Buy now</a></td></tr></table>",
}

WORDS = "alpha beta gamma delta epsilon invoice meeting tomorrow update report schedule review contract price total 2026 42 7th quarter north south east west".split()
BLOCKS = ["p", "div", "h2", "blockquote", "ul", "ol", "table", "pre", "hr", "dl"]


def words(r, lo=1, hi=8):
    return " ".join(r.choice(WORDS) for _ in range(r.randint(lo, hi)))


def inline(r, depth=0):
    parts = []
    for _ in range(r.randint(1, 4)):
        k = r.random()
        if k < 0.45:
            parts.append(words(r, 1, 6))
        elif k < 0.55:
            t = r.choice(["b", "i", "span", "u", "font"])
            parts.append("<%s>%s</%s>" % (t, words(r, 1, 3), t))
        elif k < 0.7:
            parts.append("<a href='https://x%d.example/%s?a=1&amp;b=2'>%s</a>" % (r.randint(1, 9), r.choice(WORDS), words(r, 1, 3)))
        elif k < 0.75:
            u = "https://y%d.example" % r.randint(1, 9)
            parts.append("<a href='%s'>%s</a>" % (u, u))
        elif k < 0.8:
            parts.append("<img src=i.png alt='%s'>" % words(r, 1, 2))
        elif k < 0.85:
            parts.append("<br>")
        elif k < 0.9:
            parts.append(r.choice(["&amp;", "&lt;", "&copy;", "&nbsp;", "&eacute;", "&#8364;"]))
        elif k < 0.95 and depth == 0:
            parts.append("<span style='display:none'>%s</span>" % words(r, 1, 3))
        else:
            parts.append("<em>%s</em>" % words(r, 1, 2))
    return " ".join(parts)


def block(r, depth=0):
    kind = r.choice(BLOCKS if depth < 3 else ["p", "div"])
    if kind in ("p", "div"):
        return "<%s>%s</%s>" % (kind, inline(r), kind)
    if kind == "h2":
        return "<h2>%s</h2>" % words(r, 1, 4)
    if kind == "blockquote":
        return "<blockquote>%s</blockquote>" % "".join(block(r, depth + 1) for _ in range(r.randint(1, 3)))
    if kind in ("ul", "ol"):
        items = []
        for _ in range(r.randint(1, 4)):
            inner = inline(r)
            if depth < 2 and r.random() < 0.3:
                inner += block(r, depth + 1) if r.random() < 0.5 else "<%s><li>%s</li></%s>" % (kind, words(r, 1, 3), kind)
            items.append("<li>%s</li>" % inner)
        return "<%s>%s</%s>" % (kind, "".join(items), kind)
    if kind == "table":
        rows = []
        for _ in range(r.randint(1, 4)):
            cells = "".join("<td>%s</td>" % (inline(r) if r.random() < 0.8 else block(r, depth + 1)) for _ in range(r.randint(1, 4)))
            rows.append("<tr>%s</tr>" % cells)
        return "<table>%s</table>" % "".join(rows)
    if kind == "pre":
        return "<pre>%s\n  %s</pre>" % (words(r, 1, 4), words(r, 1, 4))
    if kind == "hr":
        return "<hr>"
    return "<dl><dt>%s</dt><dd>%s</dd></dl>" % (words(r, 1, 2), words(r, 1, 5))


def doc(r):
    head = ""
    if r.random() < 0.5:
        head = "<head><title>%s</title><style>.h{display:none}%s</style></head>" % (words(r, 1, 3), "@media (max-width:1px){.m{display:none}}" if r.random() < 0.3 else "")
    body = "".join(block(r) for _ in range(r.randint(1, 6)))
    if r.random() < 0.3:
        body = "<div class='h'>%s</div>" % words(r, 1, 4) + body
    return "<html>%s<body>%s</body></html>" % (head, body)


def main():
    out = sys.argv[1]
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 300
    seed = int(sys.argv[3]) if len(sys.argv) > 3 else 28
    os.makedirs(out, exist_ok=True)
    for k, v in CONSTRUCTS.items():
        open(os.path.join(out, "c-%s.html" % k), "w", encoding="utf-8").write(v)
    r = random.Random(seed)
    for i in range(count):
        open(os.path.join(out, "g-%04d.html" % i), "w", encoding="utf-8").write(doc(r))


if __name__ == "__main__":
    main()
