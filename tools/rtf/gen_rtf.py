#!/usr/bin/env python3
"""Generate RTF documents that exercise each feature of the RTF to HTML converter, the way Outlook and Word write them.

    gen_rtf.py <out-dir> [seed]

Writes <out-dir>/gen__<name>.rtf for the feature documents and gen__mix<N>.rtf for 40 random mixes of the features (the seed
decides them; default 1). Output is deterministic. Nothing here is a real person's mail.
"""
import random
import struct
import sys
import zlib
from pathlib import Path

HEAD = (r"{\rtf1\ansi\ansicpg1252\deff0\nouicompat\deflang1033"
        r"{\fonttbl{\f0\fswiss\fcharset0 Calibri;}{\f1\froman\fcharset0 Times New Roman;}{\f2\fmodern\fcharset0 Courier New;}"
        r"{\f3\fswiss\fcharset204 Arial;}{\f4\fnil\fcharset128 MS Gothic;}{\f5\fnil\fcharset238 Arial;}}"
        r"{\colortbl ;\red255\green0\blue0;\red0\green128\blue0;\red0\green0\blue255;\red255\green255\blue0;\red128\green128\blue128;}")
BODY = r"\pard\sa200\sl276\slmult1\f0\fs22\lang9 "
TAIL = "}"


def doc(body, head=HEAD):
    return head + "\r\n" + BODY + body + "\r\n" + TAIL


def png(w=4, h=3, color=(200, 30, 30)):
    raw = b"".join(b"\x00" + bytes(color) * w for _ in range(h))

    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


# The smallest valid baseline JPEG (a 1x1 grey pixel).
JPEG = bytes.fromhex(
    "ffd8ffe000104a46494600010100000100010000ffdb004300080606070605080707070909080a0c140d0c0b0b0c1912130f141d1a1f1e1d1a1c1c20242e2720"
    "222c231c1c2837292c30313434341f27393d38323c2e333432ffc0000b080001000101011100ffc4001f0000010501010101010100000000000000000102030405"
    "060708090a0bffc400b5100002010303020403050504040000017d01020300041105122131410613516107227114328191a1082342b1c11552d1f02433627282"
    "090a161718191a25262728292a3435363738393a434445464748494a535455565758595a636465666768696a737475767778797a838485868788898a92939495"
    "969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9faffda0008"
    "01010000003f00fbd0ffd9")


def hexs(b, width=64):
    h = b.hex()
    return "\r\n".join(h[i:i + width] for i in range(0, len(h), width))


def pict_png(w=4, h=3, goal=(1500, 1125), scale=(100, 100), color=(200, 30, 30)):
    return (r"{\pict\pngblip\picw%d\pich%d\picwgoal%d\pichgoal%d\picscalex%d\picscaley%d" % (w, h, goal[0], goal[1], scale[0], scale[1])
            + "\r\n" + hexs(png(w, h, color)) + "}")


LISTS = (r"{\*\listtable"
         r"{\list\listtemplateid101{\listlevel\levelnfc23\leveljc0\levelstartat1{\leveltext\'01\u-3913 ?;}{\levelnumbers;}\f2\li720\fi-360}"
         r"{\listlevel\levelnfc23\levelstartat1{\leveltext\'01o;}{\levelnumbers;}\li1440\fi-360}"
         r"{\listlevel\levelnfc23\levelstartat1{\leveltext\'01\u-3929 ?;}{\levelnumbers;}\li2160\fi-360}\listid1001}"
         r"{\list\listtemplateid102{\listlevel\levelnfc0\levelstartat1{\leveltext\'02\'00.;}{\levelnumbers\'01;}\li720\fi-360}"
         r"{\listlevel\levelnfc4\levelstartat1{\leveltext\'02\'01.;}{\levelnumbers\'01;}\li1440\fi-360}"
         r"{\listlevel\levelnfc2\levelstartat1{\leveltext\'02\'02.;}{\levelnumbers\'01;}\li2160\fi-360}\listid1002}"
         r"{\list\listtemplateid103{\listlevel\levelnfc3\levelstartat3{\leveltext\'02\'00);}{\levelnumbers\'01;}\li720\fi-360}"
         r"{\listlevel\levelnfc1\levelstartat1{\leveltext\'02\'01.;}{\levelnumbers\'01;}\li1440\fi-360}\listid1003}}"
         r"{\*\listoverridetable{\listoverride\listid1001\listoverridecount0\ls1}{\listoverride\listid1002\listoverridecount0\ls2}"
         r"{\listoverride\listid1002\listoverridecount1{\lfolevel\listoverridestartat\levelstartat5}\ls3}{\listoverride\listid1003\listoverridecount0\ls4}}")
LHEAD = HEAD.replace(r"{\colortbl", LISTS + r"{\colortbl", 1)


def item(ls, lvl, text, mark=r"\'b7"):
    return (r"\pard\plain\ls%d\ilvl%d\li%d\fi-360{\listtext\f2" % (ls, lvl, 720 * (lvl + 1)) + mark + r"\tab}\f0\fs22 " + text + r"\par" + "\r\n")


def cell(text, extra=""):
    return r"\pard\intbl " + extra + text + r"\cell"


def row(defs, cells, header=False):
    return (r"\trowd\trgaph108\trleft-108" + (r"\trhdr" if header else "") + defs + "\r\n" + "".join(cells) + r"\row" + "\r\n")


def simple_defs(widths, border=True):
    out, x = "", 0
    for w in widths:
        x += w
        b = r"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10" if border else ""
        out += b + r"\cellx%d" % x
    return out


def features():
    f = {}
    f["chars"] = doc(r"Plain {\b bold} {\i italic} {\ul underline} {\strike struck} {\b\i\ul all three} x{\super 2} H{\sub 2}O {\cf1 red} {\cf2\b green bold} "
                     r"{\highlight4 yellow marked} {\caps shouting} {\fs36 big} {\fs16 small} {\f2 monospace} {\f1 serif}\par" + "\r\n"
                     r"{\v hidden words}after hidden {\ulnone no} {\b0 off} {\plain reset}\par")
    f["paragraphs"] = doc(r"\pard\qc centred\par\pard\qr right\par\pard\qj justified text that is long enough to wrap around a line or two in a browser\par"
                          r"\pard\li720\ri720 indented both sides\par\pard\fi720 first line indent\par\pard\li720\fi-360 hanging\par"
                          r"\pard\sb240\sa480 spaced out\par\pard\sl480\slmult1 double spaced\par\pard\sl-300\slmult0 exact line height\par\pard\par\pard after blanks\par\par\par")
    f["unicode"] = doc(r"caf\'e9 na\'efve \u8364? \u8212? \uc2\u26085\'93\'fa\u26412\'96\'7b \uc0\u1055\u1088 \uc1\u-10179?\u-8704? emoji \u233? \u-4?\par"
                       r"{\f3 \'cf\'f0\'e8\'e2\'e5\'f2 \'ec\'e8\'f0} {\f4 \'82\'b1\'82\'f1\'82\'c9\'82\'bf\'82\'cd} {\f5 \'9c\'e6\'bf\'f3}\par")
    f["unicode-cp1250"] = doc(r"\'9e\'e8\'f8\'ec\'e1\'ed \'8e\'c8\'d8\'cc\par", HEAD.replace(r"\ansicpg1252", r"\ansicpg1250"))
    f["specials"] = doc(r"a\~b non\_breaking soft\-hyphen \{braces\} back\\slash tab\tab end\line next\line\emdash \endash \bullet \lquote \rquote \ldblquote \rdblquote \enspace\emspace x\par"
                        r"\pard line one\par line two\par")
    f["lists-bullets"] = (LHEAD + "\r\n" + BODY + "Intro\\par\r\n" + item(1, 0, "First") + item(1, 1, "Nested a", "o") + item(1, 1, "Nested b", "o")
                                                    + item(1, 2, "Deep", r"\'a7") + item(1, 0, "Second") + r"\pard After\par" + "\r\n" + TAIL)
    f["lists-numbered"] = (LHEAD + "\r\n" + BODY + item(2, 0, "One", "1.") + item(2, 1, "sub a", "a.") + item(2, 1, "sub b", "b.") + item(2, 2, "deep i", "i.")
                           + item(2, 0, "Two", "2.") + r"\pard Interruption\par" + item(2, 0, "Three continues", "3.") + item(3, 0, "Restarted at five", "5.")
                           + item(4, 0, "Letters from c", "c)") + item(4, 1, "Roman", "I.") + r"\pard End\par" + TAIL)
    f["lists-old-pn"] = doc(r"{\pntext\f2\'b7\tab}{\*\pn\pnlvlblt\pnf2\pnindent360{\pntxtb\'b7}}\fi-360\li720 bullet one\par"
                            r"{\pntext\f2\'b7\tab}\fi-360\li720 bullet two\par"
                            r"\pard{\pntext 1.\tab}{\*\pn\pnlvlbody\pndec\pnstart1\pnindent360{\pntxta.}}\fi-360\li720 number one\par"
                            r"{\pntext 2.\tab}\fi-360\li720 number two\par\pard{\pntext a.\tab}{\*\pn\pnlvlbody\pnlcltr\pnstart1\pnindent360{\pntxta.}}\fi-360\li720 letter\par\pard end\par")
    f["table-simple"] = doc(r"Before\par" + "\r\n" + row(simple_defs([2000, 3000, 2500]), [cell("A1"), cell("B1"), cell("C1")], True)
                            + row(simple_defs([2000, 3000, 2500]), [cell("A2"), cell(r"{\b B2} bold"), cell("C2")])
                            + row(simple_defs([2000, 3000, 2500]), [cell(""), cell("B3 first" + r"\par" + "second paragraph"), cell("C3")]) + r"\pard After\par")
    f["table-merged"] = doc(row(r"\clmgf\cellx3000\clmrg\cellx6000\cellx9000", [cell("wide"), cell(""), cell("right")])
                            + row(r"\clvmgf\cellx3000\cellx6000\cellx9000", [cell("tall"), cell("m"), cell("r")])
                            + row(r"\clvmrg\cellx3000\cellx6000\clvertalc\clcbpat3\cellx9000", [cell(""), cell("n"), cell("centred shaded")]) + r"\pard End\par")
    f["table-borderless"] = doc(row(simple_defs([4000, 4000], False), [cell("left"), cell("right")]) + row(simple_defs([4000, 4000], False), [cell("a"), cell("b")]))
    f["tables-two"] = doc(row(simple_defs([3000, 3000]), [cell("t1a"), cell("t1b")]) + r"\pard between\par" + row(simple_defs([3000, 3000]), [cell("t2a"), cell("t2b")]) + r"\pard end\par")
    f["table-in-list-text"] = doc(r"\pard Intro paragraph with {\b formatting}.\par" + row(simple_defs([5000, 5000]), [cell(r"{\i italic cell}"), cell(r"{\ul linked}")]) + r"\pard Closing paragraph.\par")
    q = '"'
    f["links"] = doc(r"Visit {\field{\*\fldinst{HYPERLINK " + q + r"http://example.com/path?a=1&b=2" + q + r"}}{\fldrslt{\cf3\ul example site}}} or write "
                     r"{\field{\*\fldinst HYPERLINK " + q + r"mailto:someone@example.org" + q + r"}{\fldrslt{\cf3\ul someone@example.org}}}. "
                     r"Jump to {\field{\*\fldinst{HYPERLINK \\l " + q + r"section1" + q + r"}}{\fldrslt{\cf3 anchor}}} or "
                     r"{\field{\*\fldinst HYPERLINK " + q + r"http://example.com/x" + q + r" \\l " + q + r"frag" + q + r"}{\fldrslt both}}. "
                     r"Page {\field{\*\fldinst PAGE}{\fldrslt 1}} of {\field{\*\fldinst NUMPAGES}{\fldrslt 3}}.\par")
    f["pictures"] = doc(r"Inline: " + pict_png() + r" and scaled: " + pict_png(8, 2, (1200, 300), (50, 200), (30, 30, 200)) + r"\par"
                        r"Pair: {\*\shppict" + pict_png(2, 2, (600, 600)) + "}{\\nonshppict{\\pict\\wmetafile8\\picw100\\pich100 d7cdc69a0000" + "0" * 40 + "}}\\par"
                        r"JPEG: {\pict\jpegblip\picwgoal600\pichgoal600 " + hexs(JPEG) + r"}\par"
                        r"Metafile: {\pict\emfblip\picw100\pich100 01000000" + "00" * 36 + "20454d46" + "00" * 8 + r"}\par")
    f["attachments"] = doc(r"Please see \objattph \'20the attached file and \objattph \'20this one.\par"
                           r"{\object\objemb{\*\objclass Package}{\*\objdata 0102}{\result{\pict\wmetafile8\picw100\pich100 d7cdc69a0000" + "00" * 20 + r"}}}\par")
    f["destinations"] = doc(r"{\header This is the header\par}{\footer footer text}{\footnote A footnote}{\*\unknowndest should not show}{\*\bkmkstart mark}Visible{\*\bkmkend mark} text"
                            r"{\info{\title Secret Title}{\author Someone}}{\private hidden private}{\*\generator Msftedit 5.41.21.2510;}{\xyzzy unknown but not ignorable}\par")
    f["headings"] = doc(r"\pard\s1\b\fs32 Main title\par\pard\s2\b\fs26 Section\par\pard\s0 Body text under the headings.\par",
                        HEAD.replace(r"{\colortbl", r"{\stylesheet{\s0 Normal;}{\s1\sbasedon0\snext0 heading 1;}{\s2\sbasedon0 heading 2;}}{\colortbl", 1))
    f["shape-textbox"] = doc(r"Before {\shp{\*\shpinst\shpleft0\shptop0{\sp{\sn shapeType}{\sv 202}}{\shptxt \pard text in a box\par}}{\shprslt fallback should not repeat}} after.\par")
    f["horizontal-rule"] = doc(r"above\par\pard{\pict{\*\picprop{\sp{\sn fHorizRule}{\sv 1}}{\sp{\sn fStandardHR}{\sv 1}}}\wmetafile8\picw1\pich1 d7cdc69a0000}\par\pard below\par")
    f["fonts-nested"] = doc(r"{\f2 mono {\f1 serif {\b bold serif} back to serif} back to mono} body\par")
    f["bare-cell"] = doc(r"Fax / Phone Station\cell Fax / Phone #\cell \par")
    return f


RUNS = [r"{\b %s}", r"{\i %s}", r"{\ul %s}", r"{\strike %s}", r"{\cf1 %s}", r"{\f2 %s}", r"{\fs32 %s}", r"{\super %s}", r"{\v %s}", r"%s", r"%s", r"%s"]
WORDS = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon".split()


def sentence(rng, n):
    out = []
    for _ in range(n):
        w = rng.choice(WORDS)
        r = rng.choice(RUNS)
        if "\\v" in r and rng.random() < 0.6:
            r = "%s"
        out.append(r % w)
    return " ".join(out)


def mix(rng):
    parts = []
    for _ in range(rng.randint(3, 9)):
        kind = rng.choice(["p", "p", "p", "qc", "li", "table", "link", "pict", "heading"])
        if kind == "p":
            parts.append(r"\pard\sa%d " % rng.choice([0, 120, 200]) + sentence(rng, rng.randint(2, 10)) + r"\par")
        elif kind == "qc":
            parts.append(r"\pard\qc " + sentence(rng, 4) + r"\par")
        elif kind == "li":
            ls = rng.choice([1, 2, 4])
            for _ in range(rng.randint(2, 4)):
                parts.append(item(ls, rng.randint(0, 1), sentence(rng, 3)).rstrip())
        elif kind == "table":
            widths = [rng.choice([1500, 2500, 3000]) for _ in range(rng.randint(2, 4))]
            for _ in range(rng.randint(1, 3)):
                parts.append(row(simple_defs(widths, rng.random() < 0.6), [cell(sentence(rng, 2)) for _ in widths]))
            parts.append(r"\pard\par")
        elif kind == "link":
            parts.append(r"\pard See {\field{\*\fldinst{HYPERLINK \"http://example.com/%s\"}}{\fldrslt{\cf3\ul %s}}} now.\par" % (rng.choice(WORDS), rng.choice(WORDS)))
        elif kind == "pict":
            parts.append(r"\pard " + pict_png(2, 2, (rng.randrange(256), rng.randrange(256), rng.randrange(256))) + r"\par")
        else:
            parts.append(r"\pard\s1\b\fs32 " + sentence(rng, 2) + r"\par")
    return (LHEAD.replace(r"{\colortbl", r"{\stylesheet{\s0 Normal;}{\s1 heading 1;}}{\colortbl", 1) + "\r\n" + BODY + "\r\n".join(parts) + "\r\n" + TAIL)


def main():
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    seed = int(sys.argv[2]) if len(sys.argv) > 2 else 1
    for name, text in features().items():
        (out / ("gen__%s.rtf" % name)).write_bytes(text.encode("latin-1"))
    # The binary-picture document needs raw bytes after \bin.
    p = png(2, 2)
    raw = (HEAD + "\r\n" + BODY + r"Binary picture: {\pict\pngblip\picwgoal600\pichgoal450\bin%d " % len(p)).encode("latin-1") + p + (r"}\par" + "\r\n" + TAIL).encode("latin-1")
    (out / "gen__pictures-bin.rtf").write_bytes(raw)
    rng = random.Random(seed)
    for i in range(40):
        (out / ("gen__mix%02d.rtf" % i)).write_bytes(mix(rng).encode("latin-1"))


if __name__ == "__main__":
    main()
