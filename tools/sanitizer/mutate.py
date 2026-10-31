#!/usr/bin/env python3
"""Writes random tag soup for the browser structure check: tools/sanitizer/mutate.py SEED COUNT OUT.json

Inputs are built from a vocabulary of the pieces mutation attacks on sanitisers are made of (nesting-sensitive elements, raw-text and RCDATA
elements, foreign content, comments, CDATA, bogus comments, quotes and entities). The same seed gives the same file."""
import json, random, sys
seed, count, out = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
r = random.Random(seed)
open_tags = ("a b i u s em strong font nobr big small code tt p div span table tbody thead tr td th caption colgroup col select option optgroup "
             "button form fieldset label ul ol li dl dt dd h1 h2 h3 pre listing textarea title style script noscript template xmp iframe noembed noframes "
             "plaintext svg math mi mo mn mtext ms mglyph malignmark annotation-xml foreignObject desc image img br hr input object embed video audio "
             "source picture details summary dialog frameset frame body html head base link meta area map isindex center marquee sup sub ruby rt rp "
             "bdo bdi wbr q cite kbd samp var abbr time mark article section nav aside header footer main figure figcaption address blockquote").split()
attrs = [' title="', ' title=\'', ' alt="', ' class="a b"', ' id=x', ' id="y z"', ' style="color:red"', ' style="background:url(http://h.test/b)"',
         ' style="font-family:\'', ' href="http://h.test/l"', ' href="javascript:alert(1)"', ' src="http://h.test/i"', ' src=x', ' onerror=alert(1)',
         ' onclick="alert(1)"', ' srcset="http://h.test/s 1x, http://h.test/t 2x"', ' background="http://h.test/g"', ' encoding="text/html"',
         ' encoding="application/xhtml+xml"', ' xlink:href="javascript:alert(1)"', ' xmlns="http://www.w3.org/1999/xhtml"', ' size=3', ' width=1',
         ' colspan=2', ' type=a', ' name=n', ' dir=rtl', ' open', ' nowrap', ' bgcolor=red', ' face="a b"']
pieces = ["&lt;", "&gt;", "&amp;", "&quot;", "&#0;", "&#x3c;", "&#60;script&#62;", "&#x27;", "&amp;lt;", "&nbsp;", "\n", "\r\n", "\t", " ", "x", "text",
          "<!--", "-->", "--!>", "<!-->", "<!--->", "<![CDATA[", "]]>", "<?", "?>", "<!", "<!x ", "</", "<", ">", "/>", '"', "'", "`", "=", "\\", "\x00", "\x1b",
          "/*", "*/", "{", "}", ";", "p{color:red}", "a{background:url(http://h.test/c)}", "@import url(http://h.test/m);", "@media screen{", "url(", "expression(",
          "<![endif]-->", "<!--[if x]>", "&#xd800;", "&#x10ffff;", "\u2028", "\ufeff"]
def tag():
    t = r.choice(open_tags)
    k = r.random()
    if k < 0.45:
        return "<" + t + ">"
    if k < 0.75:
        return "</" + t + ">"
    s = "<" + t
    for _ in range(r.randint(1, 3)):
        s += r.choice(attrs)
    return s + r.choice([">", "/>", " ", "", ">>"])
def doc():
    return "".join(tag() if r.random() < 0.6 else r.choice(pieces) for _ in range(r.randint(3, 45)))
json.dump([doc() for _ in range(count)], open(out, "w"))
