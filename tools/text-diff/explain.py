"""Reasons a difference between OneMail and an oracle is a documented resolution rather than a bug.

explain(oracle, case, ours, theirs) -> reason string or None. Each reason names the rule in docs/text-encodings.md it comes from.
Rules are deliberately narrow: they look at the generated group and at what the two outputs actually are, so a new kind of
difference still shows up as unexplained.
"""
import re


def _text(r):
    return bytes.fromhex(r["r"]).decode("utf-8", "replace") if r and "r" in r else None


def _case_text(c):
    return bytes.fromhex(c["hex"]).decode("latin-1") if c["kind"] == "ew" else c["hdr"]


def _is_c1_only_difference(a, b):
    """True if a and b differ only where b has a C1 control (U+0080..U+009F) and a has a printable character."""
    if a is None or b is None or len(a) != len(b):
        return False
    diff = False
    for x, y in zip(a, b):
        if x != y:
            if not ("\u0080" <= y <= "\u009f"):
                return False
            diff = True
    return diff


def _same_ascii_positions(a, b):
    """Same length, and every ASCII character is where the other has it: only non-ASCII characters differ."""
    return len(a) == len(b) and all((x == y) or (not x.isascii() and not y.isascii()) for x, y in zip(a, b))


def _declared(c, *names):
    t = _case_text(c).lower()
    return any(n in t for n in names)


def explain(oracle, c, o, t):
    g = c["group"]
    if c["kind"] == "cs":
        return _explain_cs(oracle, c, o, t)
    ours, theirs = _text(o), _text(t)
    issues = o.get("i", "")
    ct = _case_text(c).lower()

    # --- charset-table differences, any oracle -------------------------------------------------------------------
    if ours is not None and theirs is not None:
        # Folds: the oracle may keep or drop the white space of a fold; compare without white space.
        if re.search(r"\r\n", _case_text(c)) and "\r\n" in _case_text(c) and re.sub(r"\s+", "", ours) == re.sub(r"\s+", "", theirs):
            return "fold-white-space-kept-or-dropped-by-oracle"
        if re.search(r"\r\n", _case_text(c)) and "big5" in ct and _same_ascii_positions(re.sub(r"\s+", "", ours), re.sub(r"\s+", "", theirs)):
            return "big5-table (Encoding Standard Big5 vs the oracle's)"
        if oracle == "mimekit" and any(0xE000 <= ord(ch) <= 0xF8FF for ch in theirs) and not any(0xE000 <= ord(ch) <= 0xF8FF for ch in ours):
            return "iso-8859-7-table (2003 euro/drachma vs the oracle's older table)"
        if _declared(c, "iso-8859-1", "us-ascii") and _is_c1_only_difference(ours, theirs):
            return "latin1-label-is-windows-1252"
        if _declared(c, "iso-8859-7", "iso-8859-15", "iso-8859-2", "windows-125", "koi8") and _is_c1_only_difference(ours, theirs):
            return "single-byte-label-table"
        if "big5" in ct and _same_ascii_positions(ours, theirs):
            return "big5-table (Encoding Standard Big5 vs the oracle's)"
        if "euc-jp" in ct and any(ch in theirs for ch in "?\u30fb\ufffd") and not any(ch in ours for ch in "\ufffd"):
            return "euc-jp-jis-x-0212 (oracle lacks the three-byte set)"
        if "\r\n" in theirs and ours == theirs.replace("\r\n", ""):
            return "fold-not-unfolded-by-oracle"
        if oracle == "python" and "\r\n" in ct and ours.replace(" ", "") == theirs.replace(" ", ""):
            return "python-drops-white-space-around-fold"

    # --- parameters ----------------------------------------------------------------------------------------------
    if c["kind"] == "param":
        if "AmbiguousEncodedWordInQuotedString" in issues and theirs is not None and "=?" in theirs:
            return "encoded-word-in-parameter-left-literal (AmbiguousEncodedWordInQuotedString)"
        if "MalformedExtendedValue" in issues or g in ("no-quotes", "start1", "empty-charset", "unknown-charset"):
            return "extended-value-without-charset (UTF-8 if valid else windows-1252)"
        if "AmbiguousParameterForms" in issues:
            return "plain-and-extended-both-present (extended wins)"
        if g == "cont-plain-mix":
            return "literal-segment-after-extended-appended-as-bytes"
        if g in ("star-only", "bad-pct", "trailing-pct") and oracle in ("mimekit", "go", "python"):
            return "bad-percent-escape-or-empty-value"
        if oracle == "go":
            m = re.search(r"\*[^=]*=\s*([^'; ]*)'", c["hdr"])
            cs = m.group(1).lower() if m else ""
            if cs not in ("utf-8", "us-ascii", ""):
                return "go-extended-charset-unsupported"
            if g in ("dup-plain", "dup-seg", "gap", "single", "cont", "cont-split", "upper", "pct-lower", "ew-plain", "ew-quoted"):
                return "go-strict-parameter-parsing"
        if g == "dup-plain":
            return "duplicate-plain-parameter"
        if g in ("gap", "dup-seg") and ours is not None and theirs is not None:
            return "segment-gap-or-duplicate"
        if g in ("ew-plain", "ew-quoted") and oracle in ("python", "go"):
            return "encoded-word-in-parameter-left-literal (AmbiguousEncodedWordInQuotedString)"

    # --- encoded words -------------------------------------------------------------------------------------------
    if c["kind"] == "ew":
        if oracle == "python":
            if t.get("e") in ("HeaderParseError", "LookupError"):
                return "python-raises-on-bad-base64-or-unknown-charset"
            if g in ("charset-space", "empty-charset"):
                return "python-accepts-empty-or-spaced-charset"
        if oracle == "go":
            if g in ("unknown-charset", "lang-tag", "charset-space", "empty-charset") and (theirs == "" or theirs is None):
                return "go-fails-whole-header-on-unknown-charset"
            if theirs is not None and "=?" in theirs and "=?" not in (ours or ""):
                return "go-leaves-undecodable-word-literal"
            if g.startswith("split-multibyte") and theirs is not None and "\ufffd" in theirs:
                return "go-does-not-join-split-multibyte (AmbiguousSplitMultibyte)"
        if oracle in ("mimekit", "go") and g.startswith("split-multibyte"):
            return "oracle-does-not-join-split-multibyte (AmbiguousSplitMultibyte)"
        if oracle == "mimekit" and re.search(r"=\?[^?]*\.[^?]*\?[bq]\?", ct) and theirs is not None and "=?" in theirs:
            return "mimekit-rejects-charset-with-dot (IANA alias ANSI_X3.4-1968; '.' is an especial in RFC 2047)"
        if oracle == "mimekit":
            if "InvalidQuotedPrintable" in issues:
                return "mimekit-drops-invalid-q-escape"
            if "InvalidBase64" in issues:
                return "mimekit-differs-on-invalid-base64"
            if g == "two-eq":
                return "oracle-does-not-retry-inside-failed-word"
        if g == "two-eq":
            return "oracle-does-not-retry-inside-failed-word"
        if g in ("b64-one-char-tail", "b64-no-pad", "bad-b64-char", "extra-q") and ("InvalidBase64" in issues):
            return "invalid-base64-partial-group"
    return None


def _explain_cs(oracle, c, o, t):
    g = c["group"]
    if g.endswith(("-truncated", "-flipped", "-no-reset")):
        # Damaged input: each decoder repairs it its own way. Python's "replace" is not a client behaviour.
        return "damaged-input-repair-differs (reported as an issue by OneMail)"
    return None
