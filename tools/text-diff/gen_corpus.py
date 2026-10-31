#!/usr/bin/env python3
"""Generates the encoded-word and RFC 2231 differential corpus (JSON lines on stdout or --out).

Deterministic: the same --seed gives the same file. Every case is one of

  {"id": n, "kind": "ew",    "group": g, "hex": "<bytes of a header value>"}
  {"id": n, "kind": "param", "group": g, "hdr": "attachment; filename=...", "params": [["filename*0*", "<hex of unquoted value>"], ...], "want": "filename"}

`group` names what the case exercises; compare.py uses it to attribute differences to documented resolutions.
"""
import argparse
import base64
import json
import random
import sys
import urllib.parse

SAMPLES = [
    "Héllo wörld", "Привет мир", "日本語のテキスト", "中文测试文本", "한국어 텍스트", "€uro price list",
    "Ünïcödé façade", "Zażółć gęślą jaźń", "Ça va très bien", "ASCII only subject", "Łódź i Kraków", "Ελληνικά κείμενο",
    "naïve café", "Größe", "emoji 😀 test", "Å ä ö", "Тест 1 2 3", "résumé", "日本語 and English", "ñandú",
]
CHARSETS = ["utf-8", "iso-8859-1", "iso-8859-2", "iso-8859-15", "windows-1252", "windows-1251", "koi8-r",
            "shift_jis", "euc-jp", "iso-2022-jp", "gb2312", "gbk", "big5", "euc-kr", "iso-8859-7", "windows-1250"]
PY_CODEC = {"iso-2022-jp": "iso2022_jp", "gb2312": "gb2312"}
PREFIXES = ["", "", "", "Re: ", "Fwd: [list] ", "Invoice #42 - ", "(", "\"", "x"]
SUFFIXES = ["", "", "", " done", " (ok)", ".", " <a@b.c>", "\""]
QSAFE = set(b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!*+-/")


def encode_text(text, cs):
    try:
        return text.encode(PY_CODEC.get(cs, cs))
    except (UnicodeEncodeError, LookupError):
        return None


def b_word(cs, raw, enc="B"):
    return "=?%s?%s?%s?=" % (cs, enc, base64.b64encode(raw).decode())


def q_word(cs, raw, enc="Q", lower=False):
    out = []
    for b in raw:
        if b == 0x20:
            out.append("_")
        elif b in QSAFE:
            out.append(chr(b))
        else:
            out.append(("=%02x" if lower else "=%02X") % b)
    return "=?%s?%s?%s?=" % (cs, enc, "".join(out))


def word(rng, cs, raw):
    return rng.choice([b_word, q_word])(cs, raw)


def pick(rng):
    for _ in range(50):
        cs = rng.choice(CHARSETS)
        text = rng.choice(SAMPLES)
        raw = encode_text(text, cs)
        if raw is not None:
            return cs, text, raw
    return "utf-8", SAMPLES[0], SAMPLES[0].encode()


def split_utf8(raw, rng):
    """Cut bytes at an arbitrary point (possibly inside a multibyte character)."""
    if len(raw) < 2:
        return raw, b""
    k = rng.randrange(1, len(raw))
    return raw[:k], raw[k:]


def ew_valid(rng):
    cs, _, raw = pick(rng)
    return rng.choice(PREFIXES) + word(rng, cs, raw) + rng.choice(SUFFIXES), "valid"


def ew_adjacent(rng):
    cs, _, raw = pick(rng)
    ws = rng.choice([" ", "  ", "\t", "\r\n ", "\r\n\t", " \r\n  ", ""])
    a, b = split_utf8(raw, rng)
    group = rng.choice(["adjacent-split", "adjacent-whole"])
    if group == "adjacent-whole":
        cs2, _, raw2 = pick(rng)
        return word(rng, cs, raw) + ws + word(rng, cs2, raw2), "adjacent" + ("-nows" if ws == "" else "")
    return word(rng, cs, a) + ws + word(rng, cs, b), "split-multibyte" + ("-nows" if ws == "" else "")


def ew_plain_between(rng):
    cs, _, raw = pick(rng)
    cs2, _, raw2 = pick(rng)
    return word(rng, cs, raw) + rng.choice([" and ", "  x  ", " - ", "\r\n and "]) + word(rng, cs2, raw2), "plain-between"


def ew_malformed(rng):
    cs, _, raw = pick(rng)
    w = word(rng, cs, raw)
    kinds = ["no-terminator", "extra-q", "bad-b64-char", "b64-no-pad", "b64-one-char-tail", "q-bad-hex", "q-trailing-eq", "q-short-hex",
             "unknown-charset", "empty-charset", "empty-text", "space-inside", "glued-text", "glued-words", "bad-encoding",
             "lang-tag", "upper-enc", "lower-enc", "long-word", "only-prefix", "nested", "charset-space", "two-eq"]
    k = rng.choice(kinds)
    if k == "no-terminator":
        return "Subject " + w[:-2], k
    if k == "extra-q":
        return w.replace("?=", "??=", 1), k
    if k == "bad-b64-char":
        return b_word(cs, raw)[:-4] + "!?=", k
    if k == "b64-no-pad":
        return "=?%s?B?%s?=" % (cs, base64.b64encode(raw).decode().rstrip("=")), k + "-open" if False else k
    if k == "b64-one-char-tail":
        return "=?%s?B?%s?=" % (cs, base64.b64encode(raw).decode().rstrip("=") + "A"), k
    if k == "q-bad-hex":
        return "=?%s?Q?ab=ZZcd=4?=" % cs, k
    if k == "q-trailing-eq":
        return "=?%s?Q?abc=?=" % cs, k
    if k == "q-short-hex":
        return "=?%s?Q?abc=4?=" % cs, k
    if k == "unknown-charset":
        return "=?x-no-such-charset?" + rng.choice("BQ") + "?aGVsbG8=?=", k
    if k == "empty-charset":
        return "=??Q?abc?=", k
    if k == "empty-text":
        return "a =?utf-8?Q??= b", k
    if k == "space-inside":
        return "=?utf-8?Q?hello world?=", k
    if k == "glued-text":
        return "pre" + w + "post", k
    if k == "glued-words":
        return w + word(rng, cs, raw), k
    if k == "bad-encoding":
        return "=?utf-8?X?abc?=", k
    if k == "lang-tag":
        return w.replace("?" + cs + "?", "?" + cs + "*en?", 1) if ("?" + cs + "?") in w else w, k
    if k == "upper-enc":
        return w.replace("?B?", "?b?").replace("?Q?", "?q?"), k
    if k == "lower-enc":
        return w.replace("?b?", "?B?").replace("?q?", "?Q?"), k
    if k == "long-word":
        long_raw = ("Привет мир " * 12).encode()
        return b_word("utf-8", long_raw), k
    if k == "only-prefix":
        return "=?utf-8", k
    if k == "nested":
        return "=?utf-8?Q?=3D=3Futf-8=3FQ=3Fabc=3F=3D?=", k
    if k == "charset-space":
        return "=?utf 8?Q?abc?=", k
    return "=?=?utf-8?Q?abc?=?=", k


def ew_plain(rng):
    s = rng.choice(["plain subject", "Re: Re: hello (world)", "a=b c=?d", "http://x.y/?a=b", "=? not a word", "?= stray", "100% sure", ""])
    return s, "plain"


def ew_raw8(rng):
    text = rng.choice(SAMPLES)
    if rng.random() < 0.5:
        raw = text.encode("utf-8")
    else:
        raw = text.encode("latin-1", "replace")
    raw = raw + b" " + word(rng, "utf-8", "é".encode()).encode()
    return raw, "raw8"


def pct(raw, safe_upper=True):
    return "".join(chr(b) if (b in QSAFE and b not in b"*'%") else "%%%02X" % b for b in raw)


def hexs(s):
    return s.encode("latin-1").hex() if isinstance(s, str) else s.hex()


def quote(v):
    return '"' + v.replace("\\", "\\\\").replace('"', '\\"') + '"'


def hdr_from(params):
    parts = ["attachment"]
    for name, val, q in params:
        parts.append("%s=%s" % (name, quote(val) if q else val))
    return "; ".join(parts)


def mk_param(group, params, want="filename"):
    """params: list of (name, unquoted-ascii-value, quoted?)"""
    return {"kind": "param", "group": group, "hdr": hdr_from(params), "params": [[n, v.encode("latin-1").hex()] for n, v, _ in params], "want": want}


def param_case(rng):
    cs, text, raw = pick(rng)
    ext_cs = rng.choice([cs, "utf-8", "utf-8", "iso-8859-1", "us-ascii"])
    if ext_cs == "us-ascii":
        raw = rng.choice(SAMPLES[9:10] + ["plain name"]).encode()
    elif ext_cs != cs:
        raw2 = encode_text(text, ext_cs)
        raw = raw2 if raw2 is not None else raw
        cs = ext_cs if raw2 is not None else cs
    else:
        ext_cs = cs
    cs = ext_cs
    lang = rng.choice(["", "", "en", "de-DE"])
    kinds = ["token", "quoted", "single", "single", "cont", "cont", "cont-split", "cont-plain-mix", "plain-cont", "gap", "start1", "dup-seg",
             "bad-pct", "no-quotes", "empty-charset", "unknown-charset", "both-forms", "dup-plain", "upper", "ew-plain", "ew-quoted", "trailing-pct", "pct-lower",
             "ctl-in-quoted", "star-only"]
    k = rng.choice(kinds)
    ext = lambda r: cs + "'" + lang + "'" + pct(r)
    if k == "token":
        return mk_param(k, [("filename", "report.pdf", False)])
    if k == "quoted":
        return mk_param(k, [("filename", 'my "file" \\ name.txt', True)])
    if k == "single":
        return mk_param(k, [("filename*", ext(raw), False)])
    if k in ("cont", "cont-split"):
        pieces = []
        if k == "cont":
            cut = max(1, len(raw) // 2)
            pieces = [raw[:cut], raw[cut:]]
        else:
            a, b = split_utf8(raw, rng)
            pieces = [a, b]
        params = [("filename*0*", ext(pieces[0]), False), ("filename*1*", pct(pieces[1]), False)]
        return mk_param(k, params)
    if k == "cont-plain-mix":
        a, b = split_utf8(raw, rng)
        return mk_param(k, [("filename*0*", ext(a), False), ("filename*1", "tail.txt", True)])
    if k == "plain-cont":
        return mk_param(k, [("filename*0", "very-long-", False), ("filename*1", "file-name.txt", False)])
    if k == "gap":
        return mk_param(k, [("filename*0*", ext(raw[:3]), False), ("filename*2*", pct(raw[3:]), False)])
    if k == "start1":
        return mk_param(k, [("filename*1*", pct(raw), False), ("filename*2*", pct(b"x"), False)])
    if k == "dup-seg":
        return mk_param(k, [("filename*0*", ext(raw), False), ("filename*1*", pct(b"AA"), False), ("filename*1*", pct(b"BB"), False)])
    if k == "bad-pct":
        return mk_param(k, [("filename*", cs + "''ab%ZZcd%4", False)])
    if k == "trailing-pct":
        return mk_param(k, [("filename*", cs + "''abc%", False)])
    if k == "no-quotes":
        return mk_param(k, [("filename*", pct(raw), False)])
    if k == "empty-charset":
        return mk_param(k, [("filename*", "''" + pct(raw), False)])
    if k == "unknown-charset":
        return mk_param(k, [("filename*", "x-nosuch''" + pct(raw), False)])
    if k == "both-forms":
        return mk_param(k, [("filename", "plain.txt", True), ("filename*", ext(raw), False)])
    if k == "dup-plain":
        return mk_param(k, [("filename", "first.txt", True), ("filename", "second.txt", True)])
    if k == "upper":
        return mk_param(k, [("FILENAME*", ext(raw), False)])
    if k == "pct-lower":
        return mk_param(k, [("filename*", cs + "''" + "".join("%%%02x" % b for b in raw), False)])
    if k == "ew-plain":
        return mk_param(k, [("filename", word(rng, cs, raw), False)])
    if k == "ew-quoted":
        return mk_param(k, [("filename", word(rng, cs, raw), True)])
    if k == "ctl-in-quoted":
        return mk_param(k, [("filename", "a\tb c.txt", True)])
    return mk_param(k, [("filename*", "", False)])


JP = ["日本語のテキスト", "こんにちは世界", "漢字とかなカタカナ", "東京タワー 2024", "半角ｱｲｳｴｵ", "ABC 日本 def", "丈夫 ① ㈱"]
JP1 = ["日本語 Ǎ À ı", "¡Hola", "café"]  # JIS X 0212 and Latin-1 letters
JP2 = ["한국어 텍스트 日本語", "中文 日本語", "Ελληνικά ñ", "naïve café 日本"]
JP3 = ["日本語", "俱𠀋"]
MIX = ["Hello 日本語 world", "x+y=z ~ \\ ok", "A&B", "héllo wörld", "Привет мир", "😀 emoji", "1 + 2 = 3", "~tilde", "日本語", "", "a-b+c"]
IMAP = ["INBOX", "Entwürfe", "日本語", "台北", "Sent & Trash", "a&b", "~peter/mail/台北/日本語", "😀", "Ünï/cödé", "&", "", "x😀y"]


def imap_encode(s):
    """RFC 3501 5.1.3 reference encoder, written from the RFC: independent of the C# implementation."""
    out, run = [], ""

    def flush():
        nonlocal run
        if run:
            b64 = base64.b64encode(run.encode("utf-16-be")).decode().rstrip("=").replace("/", ",")
            out.append("&" + b64 + "-")
            run = ""
    for ch in s:
        if " " <= ch <= "~":
            flush()
            out.append("&-" if ch == "&" else ch)
        else:
            run += ch
    flush()
    return "".join(out)


def cs_cases(rng, n):
    """Charset decode cases: bytes made by Python's own codec, decoded by Python (the expected text) and by us."""
    specs = [("iso-2022-jp", "iso2022_jp", JP[:4] + MIX[:1]), ("iso-2022-jp-1", "iso2022_jp_1", JP + JP1),
             ("iso-2022-jp-2", "iso2022_jp_2", JP + JP1 + JP2), ("cp50221", "iso2022_jp_ext", JP), ("iso-2022-jp-3", "iso2022_jp_3", JP[:4]),
             ("iso-2022-kr", "iso2022_kr", ["한국어 텍스트", "가나다 abc 라마"]), ("hz-gb-2312", "hz", ["中文测试 abc", "你好~世界"]),
             ("utf-7", "utf_7", MIX)]
    for _ in range(n):
        label, codec, pool = rng.choice(specs)
        text = rng.choice(pool)
        if rng.random() < 0.4:
            text = text + rng.choice([" ", "\n", " end", "\r\n"]) + rng.choice(pool)
        try:
            raw = text.encode(codec)
        except (UnicodeEncodeError, LookupError):
            continue
        kind = rng.choice(["valid"] * 6 + ["truncated", "flipped", "no-reset"])
        if kind == "truncated" and len(raw) > 2:
            raw = raw[: rng.randrange(1, len(raw))]
        elif kind == "flipped" and raw:
            i = rng.randrange(len(raw))
            raw = raw[:i] + bytes([raw[i] ^ (1 << rng.randrange(8))]) + raw[i + 1:]
        elif kind == "no-reset":
            raw = raw.replace(b"\x1b(B", b"")
        yield {"kind": "cs", "group": "cs-" + label + "-" + kind, "label": label, "codec": codec, "hex": raw.hex()}
    for _ in range(max(1, n // 10)):
        text = rng.choice(IMAP)
        enc = imap_encode(text)
        yield {"kind": "cs", "group": "cs-imap-utf-7-valid", "label": "x-imap-utf-7", "codec": "imap", "hex": enc.encode().hex(), "want": text.encode("utf-8").hex()}
    for text in IMAP:
        # Round trip checked by the driver: encode(text) must equal the reference encoder.
        yield {"kind": "cs", "group": "cs-imap-utf-7-encode", "label": "x-imap-utf-7", "codec": "imap-encode", "hex": text.encode("utf-8").hex(), "want": imap_encode(text).encode().hex()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=20261030)
    ap.add_argument("--ew", type=int, default=12000)
    ap.add_argument("--param", type=int, default=4000)
    ap.add_argument("--cs", type=int, default=2500)
    ap.add_argument("--out", default="-")
    a = ap.parse_args()
    rng = random.Random(a.seed)
    out = sys.stdout if a.out == "-" else open(a.out, "w")
    n = 0

    def emit(case):
        nonlocal n
        n += 1
        case["id"] = n
        out.write(json.dumps(case, separators=(",", ":")) + "\n")

    makers = [(ew_valid, 30), (ew_adjacent, 15), (ew_plain_between, 5), (ew_malformed, 40), (ew_plain, 3), (ew_raw8, 7)]
    funcs = [f for f, w in makers for _ in range(w)]
    for _ in range(a.ew):
        v, g = rng.choice(funcs)(rng)
        raw = v if isinstance(v, bytes) else v.encode("utf-8" if g == "raw8" else "ascii", "strict")
        emit({"kind": "ew", "group": g, "hex": raw.hex()})
    for _ in range(a.param):
        emit(param_case(rng))
    for case in cs_cases(rng, a.cs):
        emit(case)
    if out is not sys.stdout:
        out.close()


if __name__ == "__main__":
    main()
