#!/usr/bin/env python3
"""Generates the parser-differential corpus: small hand-written messages, one per known ambiguity.

Usage: python3 tools/corpus/make_differential.py [output-dir]   (default: testdata/differential)

Every case is a (name, description, bytes) triple. Names are `<area>-<nn>-<slug>`; docs/ambiguities.md refers to them.
The files are plain octets, so CR, LF, NUL and 8-bit cases are exact. The manifest `cases.tsv` lists name and description.
The messages are invented; none of them contains real mail.
"""
import os
import sys

CRLF = "\r\n"


def crlf(text):
    return text.replace("\n", CRLF).encode("latin-1")


def lf(text):
    return text.encode("latin-1")


BASE = "From: Alice <alice@example.com>\nTo: Bob <bob@example.com>\nSubject: probe\nDate: Mon, 14 May 2001 23:39:00 +0000\nMessage-ID: <probe@example.com>\n"
MIME = "MIME-Version: 1.0\n"


def simple(extra="", body="hello\n", base=BASE):
    return crlf(base + extra + "\n" + body)


def multi(boundary_hdr, body, extra="", ctype="multipart/mixed"):
    return crlf(BASE + MIME + extra + "Content-Type: %s; %s\n\n" % (ctype, boundary_hdr) + body)


def part(ctype="text/plain", body="part\n", extra=""):
    return "Content-Type: %s\n%s\n%s" % (ctype, extra, body)


cases = []


def case(name, desc, data):
    cases.append((name, desc, data))


# ---- boundaries -----------------------------------------------------------------------------------------------
P1 = part(body="one\n")
P2 = part(body="two\n")
case("bnd-01-plain", "Well-formed two-part multipart (control).", multi('boundary="abc"', "preamble\n--abc\n%s--abc\n%s--abc--\nepilogue\n" % (P1, P2)))
case("bnd-02-longer-boundary-prefix-line", "A body line starts with the boundary and more text (`--abcdef`): a delimiter or content?", multi('boundary="abc"', "--abc\n" + part(body="line\n--abcdef\nafter\n") + "--abc--\n"))
case("bnd-03-trailing-whitespace", "Delimiter lines carry trailing spaces and a tab (transport padding, RFC 2046 5.1.1).", multi('boundary="abc"', "--abc \t \n%s--abc  \n%s--abc-- \t\n" % (P1, P2)))
case("bnd-04-garbage-after-boundary", "Delimiter followed by non-whitespace text (`--abc xyz`).", multi('boundary="abc"', "--abc xyz\n%s--abc\n%s--abc--\n" % (P1, P2)))
case("bnd-05-missing-final", "No closing delimiter; the message ends inside the last part.", multi('boundary="abc"', "--abc\n%s--abc\n%s" % (P1, P2)))
case("bnd-06-epilogue-and-reopen", "Text after the closing delimiter, then another delimiter line.", multi('boundary="abc"', "--abc\n%s--abc--\nepilogue text\n--abc\n%s--abc--\n" % (P1, P2)))
case("bnd-07-outer-prefix-of-inner", "Outer boundary `out` is a prefix of the inner boundary `out-in`.",
     multi('boundary="out"', "--out\nContent-Type: multipart/mixed; boundary=\"out-in\"\n\n--out-in\n%s--out-in--\n--out\n%s--out--\n" % (P1, P2)))
case("bnd-08-inner-prefix-of-outer", "Inner boundary `in` is a prefix of the outer boundary `in-out`.",
     multi('boundary="in-out"', "--in-out\nContent-Type: multipart/mixed; boundary=\"in\"\n\n--in\n%s--in--\n--in-out\n%s--in-out--\n" % (P1, P2)))
case("bnd-09-no-boundary-param", "multipart/mixed with no boundary parameter.", crlf(BASE + MIME + "Content-Type: multipart/mixed\n\n--abc\n%s--abc--\n" % P1))
case("bnd-10-empty-boundary-param", "multipart/mixed with `boundary=\"\"`.", multi('boundary=""', "--abc\n%s--abc--\n" % P1))
case("bnd-11-boundary-with-space", "Quoted boundary containing a space and a trailing space.", multi('boundary="a b "', "--a b \n%s--a b \n%s--a b --\n" % (P1, P2)))
case("bnd-12-unquoted-special-chars", "Unquoted boundary containing `/` and `=` (needs quoting per RFC 2045 tspecials).", multi("boundary=a/b=c", "--a/b=c\n%s--a/b=c--\n" % P1))
case("bnd-13-duplicate-boundary-param", "Two boundary parameters with different values.", multi('boundary="one"; boundary="two"', "--one\n%s--one--\n--two\n%s--two--\n" % (P1, P2)))
case("bnd-14-case-sensitive", "Delimiter case differs from the parameter (`--ABC` vs `abc`).", multi('boundary="abc"', "--ABC\n%s--abc\n%s--abc--\n" % (P1, P2)))
case("bnd-15-zero-parts", "Only the closing delimiter.", multi('boundary="abc"', "--abc--\n"))
case("bnd-16-no-preamble-line", "First delimiter is the first body line, no preamble, no blank line between header and body.", crlf(BASE + MIME + 'Content-Type: multipart/mixed; boundary="abc"\n--abc\n%s--abc--\n' % P1))
case("bnd-17-lf-delimiters-in-crlf", "CRLF message whose delimiter lines end in bare LF.", crlf(BASE + MIME + 'Content-Type: multipart/mixed; boundary="abc"\n\n--abc\n') + crlf(P1) + b"--abc\n" + crlf(P2) + b"--abc--\n")
case("bnd-18-part-without-blank-line", "A part with headers and no blank line before the next delimiter.", multi('boundary="abc"', "--abc\nContent-Type: text/plain\n--abc\n%s--abc--\n" % P2))
case("bnd-19-part-empty-headers", "A part that starts with an empty line (no headers; defaults apply).", multi('boundary="abc"', "--abc\n\nno headers here\n--abc--\n"))
case("bnd-20-final-newline-before-delim", "Part body ends with one blank line before the delimiter (is the last line break part of the body?).", multi('boundary="abc"', "--abc\n" + part(body="text\n\n") + "--abc--\n"))
case("bnd-21-delimiter-at-eof-no-newline", "Closing delimiter is the last bytes of the file, no line break.", crlf(BASE + MIME + 'Content-Type: multipart/mixed; boundary="abc"\n\n--abc\n%s--abc--' % P1))
case("bnd-22-preamble-only-whitespace", "Preamble is blank lines only; epilogue is blank lines only.", multi('boundary="abc"', "\n\n--abc\n%s--abc--\n\n\n" % P1))
case("bnd-23-bare-cr-delimiters", "Delimiter lines separated by bare CR only.", crlf(BASE + MIME + 'Content-Type: multipart/mixed; boundary="abc"\n\n') + b"--abc\rContent-Type: text/plain\r\rone\r--abc--\r")
case("bnd-24-long-boundary", "Boundary of 80 characters (RFC 2046 limit is 70).", multi('boundary="%s"' % ("x" * 80), "--%s\n%s--%s--\n" % ("x" * 80, P1, "x" * 80)))
case("bnd-25-multipart-nonmultipart-body", "multipart type whose body has no delimiters at all.", multi('boundary="abc"', "just text, no delimiter\n"))
case("bnd-26-nested-same-boundary", "Inner multipart reuses the outer boundary string.",
     multi('boundary="abc"', "--abc\nContent-Type: multipart/mixed; boundary=\"abc\"\n\n--abc\n%s--abc--\n--abc\n%s--abc--\n" % (P1, P2)))

# ---- line endings and raw octets ------------------------------------------------------------------------------
case("eol-01-lf-only", "Whole message with bare LF line endings.", lf((BASE + "\nbody line\n")))
case("eol-02-cr-only", "Whole message with bare CR line endings.", (BASE + "\nbody line\n").replace("\n", "\r").encode("latin-1"))
case("eol-03-mixed-headers", "Headers mix CRLF and LF endings.", b"From: a@example.com\r\nTo: b@example.com\nSubject: mixed\r\nDate: Mon, 14 May 2001 23:39:00 +0000\n\r\nbody\r\n")
case("eol-04-crlf-headers-lf-body", "CRLF headers, bare LF body.", crlf(BASE + "\n") + b"line1\nline2\nline3\n")
case("eol-05-bare-cr-in-header", "Bare CR in the middle of a header value (not a line end).", b"From: a@example.com\r\nSubject: one\rtwo\r\nTo: b@example.com\r\n\r\nbody\r\n")
case("eol-06-no-final-newline", "Body without a final line break.", crlf(BASE + "\n") + b"no newline at end")
case("eol-07-headers-eof-no-newline", "Headers end at EOF with no line break and no blank line.", crlf(BASE.rstrip("\n")))
case("eol-08-blank-line-lf-then-crlf-body", "Header block ends with a bare LF blank line; body is CRLF.", b"From: a@example.com\nSubject: s\n\r\nbody\r\n")
case("eol-09-double-cr-lf", "Header ends `CR CR LF` (a stray CR before the line end).", b"From: a@example.com\r\r\nSubject: s\r\n\r\nbody\r\n")
case("raw-01-nul-in-header-value", "NUL byte inside a header value.", b"From: a@example.com\r\nSubject: a\x00b\r\nTo: b@example.com\r\n\r\nbody\r\n")
case("raw-02-nul-in-header-name", "NUL byte inside a header name.", b"From: a@example.com\r\nX-A\x00B: v\r\nSubject: s\r\n\r\nbody\r\n")
case("raw-03-nul-in-body", "NUL bytes in a text body with no transfer encoding.", b"From: a@example.com\r\nSubject: s\r\n\r\nab\x00cd\r\n\x00\r\n")
case("raw-04-utf8-in-header", "Raw UTF-8 octets in Subject, From display name and a custom header (RFC 6532 without SMTPUTF8 negotiation).",
     "From: Zoë <zoe@example.com>\r\nSubject: café ☕\r\nX-Note: naïve\r\nTo: b@example.com\r\n\r\nbody\r\n".encode("utf-8"))
case("raw-05-latin1-in-header", "Raw ISO-8859-1 octets in Subject and a display name (invalid UTF-8).", b"From: Zo\xeb <zoe@example.com>\r\nSubject: caf\xe9\r\nTo: b@example.com\r\n\r\nbody\r\n")
case("raw-06-8bit-body-7bit-cte", "High-bit octets in a body declared 7bit.", b"From: a@example.com\r\nContent-Transfer-Encoding: 7bit\r\nContent-Type: text/plain; charset=us-ascii\r\nSubject: s\r\n\r\ncaf\xe9 \xff\r\n")
case("raw-07-overlong-header", "A 3000-character header line (RFC 5322 limit 998).", ("From: a@example.com\r\nSubject: " + "x" * 3000 + "\r\n\r\nbody\r\n").encode("ascii"))
case("raw-08-overlong-body-line", "A 20000-character body line.", ("From: a@example.com\r\nSubject: s\r\n\r\n" + "y" * 20000 + "\r\n").encode("ascii"))
case("raw-09-form-feed-vt-in-header", "Form feed and vertical tab in a header value.", b"From: a@example.com\r\nSubject: a\x0cb\x0bc\r\n\r\nbody\r\n")
case("raw-10-utf8-bom", "A UTF-8 byte order mark before the first header.", b"\xef\xbb\xbfFrom: a@example.com\r\nSubject: s\r\n\r\nbody\r\n")
case("raw-11-del-and-c1", "DEL and C1 control octets in a header value.", b"From: a@example.com\r\nSubject: a\x7fb\x85c\r\n\r\nbody\r\n")

# ---- headers --------------------------------------------------------------------------------------------------
case("hdr-01-duplicate-content-type", "Two Content-Type headers (text/plain then text/html).", simple("Content-Type: text/plain; charset=us-ascii\nContent-Type: text/html; charset=utf-8\n", "<b>x</b>\n"))
case("hdr-02-duplicate-mime-version", "Two MIME-Version headers (1.0, 2.0).", simple("MIME-Version: 1.0\nMIME-Version: 2.0\n"))
case("hdr-03-duplicate-cte", "Two Content-Transfer-Encoding headers (base64 then 7bit), body is base64.", simple("Content-Transfer-Encoding: base64\nContent-Transfer-Encoding: 7bit\n", "aGVsbG8gd29ybGQ=\n"))
case("hdr-04-duplicate-singletons", "Two Subject, From, To and Date headers each.",
     crlf("From: a@example.com\nFrom: c@example.com\nTo: b@example.com\nTo: d@example.com\nSubject: first\nSubject: second\nDate: Mon, 14 May 2001 23:39:00 +0000\nDate: Tue, 15 May 2001 01:00:00 +0000\n\nbody\n"))
case("hdr-05-line-without-colon", "A header line without a colon between two valid headers.", crlf("From: a@example.com\nthis line has no colon\nSubject: s\n\nbody\n"))
case("hdr-06-first-line-no-colon", "The first line is not a header (no colon), followed by headers.", crlf("this is not a header\nFrom: a@example.com\nSubject: s\n\nbody\n"))
case("hdr-07-space-before-colon", "Whitespace before the colon (`Subject : x`, obsolete syntax allowed by RFC 5322 4.2).", crlf("From: a@example.com\nSubject : spaced\nX-Tab\t: tabbed\n\nbody\n"))
case("hdr-08-continuation-first", "A folded continuation line before any header.", crlf(" continued from nowhere\nFrom: a@example.com\nSubject: s\n\nbody\n"))
case("hdr-09-no-blank-line", "No blank line between the headers and a body that does not look like a header.", crlf("From: a@example.com\nSubject: s\nThis body starts right after the headers\nsecond line\n"))
case("hdr-10-body-looks-like-header", "No blank line, and the first body line looks like a header (`Note: x`).", crlf("From: a@example.com\nSubject: s\nNote: this is actually body\nmore body\n"))
case("hdr-11-mbox-from-line", "A leading `From ` envelope line (mbox) before the headers.", crlf("From alice@example.com Mon May 14 23:39:00 2001\n" + BASE + "\nbody\n"))
case("hdr-12-leading-blank-lines", "Blank lines before the first header.", crlf("\n\n" + BASE + "\nbody\n"))
case("hdr-13-empty-value", "Headers with empty values (`Subject:`, `X-Empty:`) and value of spaces.", crlf("From: a@example.com\nSubject:\nX-Empty:\nX-Spaces:    \nTo: b@example.com\n\nbody\n"))
case("hdr-14-space-in-name", "A header name containing a space (`X Bad: v`).", crlf("From: a@example.com\nX Bad: v\nSubject: s\n\nbody\n"))
case("hdr-15-blank-fold-line", "Folded header whose continuation is whitespace only.", crlf("From: a@example.com\nSubject: a\n \n b\n\nbody\n"))
case("hdr-16-tab-fold", "Folding with a tab and with several spaces.", crlf("From: a@example.com\nSubject: one\n\ttwo\n   three\n\nbody\n"))
case("hdr-17-no-mime-version", "Content-Type present but no MIME-Version.", crlf(BASE + 'Content-Type: multipart/mixed; boundary="abc"\n\n--abc\n%s--abc--\n' % P1))
case("hdr-18-empty-file", "A zero-length file.", b"")
case("hdr-19-whitespace-only", "A file with only a blank line.", b"\r\n")
case("hdr-20-headers-only", "Header block, blank line, no body.", crlf(BASE + "\n"))
case("hdr-21-long-fold", "A 5-line folded Subject with encoded-word on the last line.", crlf("From: a@example.com\nSubject: one\n two\n three\n four\n =?utf-8?q?five?=\n\nbody\n"))
case("hdr-22-colon-in-value", "Colons in the value (`Subject: a: b: c`).", crlf("From: a@example.com\nSubject: a: b: c\n\nbody\n"))
case("hdr-23-case-insensitive-names", "Header names in odd case (`sUbJeCt`, `CONTENT-TYPE`).", crlf("from: a@example.com\nsUbJeCt: cased\nCONTENT-TYPE: Text/Plain; CHARSET=us-ascii\n\nbody\n"))
case("hdr-24-many-headers", "2000 distinct X- headers.", ("From: a@example.com\r\n" + "".join("X-H%d: v%d\r\n" % (i, i) for i in range(2000)) + "\r\nbody\r\n").encode("ascii"))
case("hdr-25-trailing-ws-value", "Trailing spaces and tabs on header values.", b"From: a@example.com  \t\r\nSubject: s \t \r\n\r\nbody\r\n")
case("hdr-26-received-order", "Several Received headers and a trace block (order matters).", crlf("Received: from a by b; Mon, 14 May 2001 23:39:00 +0000\nReceived: from c by d; Mon, 14 May 2001 23:38:00 +0000\nFrom: a@example.com\nReceived: from e by f; Mon, 14 May 2001 23:37:00 +0000\nSubject: s\n\nbody\n"))

# ---- content-type -----------------------------------------------------------------------------------------------
case("ct-01-case-and-space", "Odd case and spacing: `TEXT/Plain ; Charset = \"UTF-8\"`.", simple('MIME-Version: 1.0\nContent-Type: TEXT/Plain ; Charset = "UTF-8"\n'))
case("ct-02-no-subtype", "Content-Type `text` with no subtype.", simple("MIME-Version: 1.0\nContent-Type: text\n"))
case("ct-03-empty-subtype", "Content-Type `text/`.", simple("MIME-Version: 1.0\nContent-Type: text/\n"))
case("ct-04-garbage", "Content-Type with no slash and garbage.", simple("MIME-Version: 1.0\nContent-Type: garbage here;;;\n"))
case("ct-05-param-no-equals", "A parameter without `=` (`text/plain; charset`).", simple("MIME-Version: 1.0\nContent-Type: text/plain; charset\n"))
case("ct-06-unterminated-quote", "Unterminated quoted parameter value.", simple('MIME-Version: 1.0\nContent-Type: text/plain; charset="utf-8; format=flowed\n'))
case("ct-07-comment-in-type", "Comments in the Content-Type value.", simple("MIME-Version: 1.0\nContent-Type: text/plain (a comment); charset=us-ascii (another)\n"))
case("ct-08-duplicate-charset", "Duplicate charset parameters.", simple("MIME-Version: 1.0\nContent-Type: text/plain; charset=us-ascii; charset=utf-8\n"))
case("ct-09-empty-value", "Empty Content-Type header.", simple("MIME-Version: 1.0\nContent-Type:\n"))
case("ct-10-semicolons", "Leading, trailing and doubled semicolons.", simple("MIME-Version: 1.0\nContent-Type: text/plain;; charset=us-ascii;;\n"))
case("ct-11-unknown-charset", "Unknown charset name.", simple("MIME-Version: 1.0\nContent-Type: text/plain; charset=x-no-such-charset\n"))
case("ct-12-nonascii-param", "Raw UTF-8 octets in a parameter value.", "From: a@example.com\r\nMIME-Version: 1.0\r\nContent-Type: application/octet-stream; name=\"café.bin\"\r\n\r\nbody\r\n".encode("utf-8"))
case("ct-13-multipart-in-no-version", "Content-Type message/rfc822 at top level with a nested message.", crlf(BASE + MIME + "Content-Type: message/rfc822\n\nFrom: n@example.com\nSubject: inner\n\ninner body\n"))
case("ct-14-text-no-ct-8bit", "No Content-Type, 8bit CTE, UTF-8 body (default charset question).", b"From: a@example.com\r\nContent-Transfer-Encoding: 8bit\r\nSubject: s\r\n\r\ncaf\xc3\xa9\r\n")

# ---- transfer encodings -----------------------------------------------------------------------------------------
B64 = "aGVsbG8gd29ybGQsIHRoaXMgaXMgYmFzZTY0IHRleHQ="
case("cte-01-unknown", "Unknown Content-Transfer-Encoding (`x-foo`).", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: x-foo\n", "plain text\n"))
case("cte-02-multipart-base64", "Multipart declared base64 (illegal for composite types).", multi('boundary="abc"', "--abc\n%s--abc--\n" % P1, extra="Content-Transfer-Encoding: base64\n"))
case("cte-03-multipart-8bit", "Multipart declared 8bit with a high-bit preamble.", crlf(BASE + MIME + 'Content-Transfer-Encoding: 8bit\nContent-Type: multipart/mixed; boundary="abc"\n\n') + b"caf\xe9\r\n" + crlf("--abc\n%s--abc--\n" % P1))
case("cte-04-case-and-space", "`Content-Transfer-Encoding:  Base64 ` in odd case and spacing.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding:  Base64 \n", B64 + "\n"))
case("cte-05-b64-invalid-chars", "Base64 body with characters outside the alphabet (`!`, `*`).", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: base64\n", "aGVsbG8!d29y*bGQ=\n"))
case("cte-06-b64-no-padding", "Base64 body with the padding removed.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: base64\n", "aGVsbG8gd29ybGQ\n"))
case("cte-07-b64-whitespace", "Base64 body with embedded spaces, tabs and blank lines.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: base64\n", "aGVs bG8g\td29y\n\nbGQ=\n"))
case("cte-08-b64-data-after-pad", "Base64 body with data after the padding.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: base64\n", "aGVsbG8=\nd29ybGQ=\n"))
case("cte-09-b64-long-lines", "Base64 body in one 200-column line.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: base64\n", "QUJD" * 50 + "\n"))
case("cte-10-qp-soft-breaks", "Quoted-printable with soft line breaks and escapes.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: quoted-printable\n", "caf=C3=A9 and a long=\n line that continues=20\nend\n"))
case("cte-11-qp-invalid-escape", "Quoted-printable with invalid escapes (`=ZZ`, `=` at end of line before text, lone `=`).", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: quoted-printable\n", "bad =ZZ escape and = alone\nand end=\n"))
case("cte-12-qp-lowercase-hex", "Quoted-printable with lower-case hex (`=e9`).", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: quoted-printable\n", "caf=e9\n"))
case("cte-13-qp-trailing-ws", "Quoted-printable with trailing spaces before the line end.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: quoted-printable\n", "trailing   \nspace\t\n"))
case("cte-14-qp-8bit-literal", "Quoted-printable body containing raw high-bit octets.", crlf(BASE + MIME + "Content-Transfer-Encoding: quoted-printable\n\n") + b"raw caf\xe9 here\r\n")
case("cte-15-message-base64", "message/rfc822 part declared base64 (inner message encoded).", multi('boundary="abc"', "--abc\nContent-Type: message/rfc822\nContent-Transfer-Encoding: base64\n\nRnJvbTogaW5uZXJAZXhhbXBsZS5jb20KU3ViamVjdDogaW5uZXIKCmlubmVyIGJvZHkK\n--abc--\n"))
case("cte-16-uuencode", "uuencode transfer encoding (legacy).", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: x-uuencode\n", "begin 644 a.txt\n+:&5L;&\\@=V]R;&0*\n`\nend\n"))
case("cte-17-binary", "`binary` CTE with CR, LF and NUL bytes.", crlf(BASE + MIME + "Content-Transfer-Encoding: binary\n\n") + b"a\x00b\rc\nd\r\ne")
case("cte-18-comment", "A comment after the encoding name (`base64 (note)`).", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: base64 (note)\n", B64 + "\n"))
case("cte-19-empty", "Empty CTE header value.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding:\n", "plain\n"))
case("cte-20-b64-crlf-in-quad", "Base64 line break in the middle of a 4-character group.", simple("MIME-Version: 1.0\nContent-Transfer-Encoding: base64\n", "aGV\nsbG8=\n"))
case("cte-21-8bit-in-part-7bit-top", "Multipart with a part that declares 8bit and holds UTF-8.", crlf(BASE + MIME + 'Content-Type: multipart/mixed; boundary="abc"\n\n--abc\nContent-Type: text/plain; charset=utf-8\nContent-Transfer-Encoding: 8bit\n\n') + "café\r\n".encode("utf-8") + b"--abc--\r\n")

# ---- encoded words, parameters, filenames -----------------------------------------------------------------------
def attach(disp_params, ct_params='name="ct.bin"'):
    return multi('boundary="abc"', "--abc\n%s--abc--\n" % ("Content-Type: application/octet-stream" + ("; " + ct_params if ct_params else "") + "\nContent-Disposition: attachment" + ("; " + disp_params if disp_params else "") + "\n\nDATA\n"))


case("enc-01-2047-in-filename", "RFC 2047 encoded word inside a quoted filename parameter.", attach('filename="=?utf-8?q?caf=C3=A9.txt?="', ct_params=""))
case("enc-02-2231-filename", "RFC 2231 `filename*=utf-8''...`.", attach("filename*=utf-8''caf%C3%A9.txt", ct_params=""))
case("enc-03-2231-continuations", "RFC 2231 continuations `filename*0*`, `filename*1*`, `filename*2`.", attach("filename*0*=utf-8''long%20na; filename*1*=me%20%C3%A9; filename*2=.txt", ct_params=""))
case("enc-04-both-plain-and-2231", "Both `filename=` and `filename*=` present.", attach("filename=plain.txt; filename*=utf-8''ext%C3%A9.txt", ct_params=""))
case("enc-05-2231-and-2047", "`filename*=` whose value is itself an encoded word.", attach("filename*=utf-8''%3D%3Futf-8%3Fq%3Fx%3F%3D", ct_params=""))
case("enc-06-2231-bad-charset", "RFC 2231 with an unknown charset.", attach("filename*=x-nonsense''a%E9b.txt", ct_params=""))
case("enc-07-2231-no-quote-marks", "`filename*=` without the charset and language quote marks.", attach("filename*=caf%C3%A9.txt", ct_params=""))
case("enc-08-2231-missing-first", "Continuations starting at `*1` (no `*0`).", attach("filename*1=b.txt; filename*2=c", ct_params=""))
case("enc-09-2231-out-of-order", "Continuations in the order `*1`, `*0`.", attach("filename*1=world.txt; filename*0=hello-", ct_params=""))
case("enc-10-name-vs-filename", "Content-Type `name` differs from Content-Disposition `filename`.", attach('filename="disp.txt"', ct_params='name="type.txt"'))
case("enc-11-name-only", "Only Content-Type `name`, no disposition filename.", multi('boundary="abc"', '--abc\nContent-Type: application/octet-stream; name="only-name.bin"\nContent-Disposition: attachment\n\nDATA\n--abc--\n'))
case("enc-12-path-in-filename", "Filename with directory separators and `..`.", attach('filename="../../etc/passwd"', ct_params=""))
case("enc-13-backslash-filename", "Windows path and backslash escapes in a quoted filename.", attach('filename="C:\\\\dir\\\\a\\"b.txt"', ct_params=""))
case("enc-14-duplicate-filename", "Two `filename` parameters.", attach('filename="one.txt"; filename="two.txt"', ct_params=""))
case("enc-15-unquoted-space-filename", "Unquoted filename containing spaces.", attach("filename=has space.txt", ct_params=""))
case("enc-16-subject-2047-q", "Subject with Q-encoded word, underscore as space.", simple("").replace(b"Subject: probe", b"Subject: =?utf-8?q?caf=C3=A9_au_lait?="))
case("enc-17-subject-2047-b", "Subject with B-encoded word.", simple("").replace(b"Subject: probe", b"Subject: =?UTF-8?B?Y2Fmw6k=?="))
case("enc-18-adjacent-words", "Adjacent encoded words separated by whitespace, and by a fold.", simple("").replace(b"Subject: probe", b"Subject: =?utf-8?q?a?= =?utf-8?q?b?=\r\n =?utf-8?q?c?=   =?utf-8?q?d?="))
case("enc-19-word-text-mix", "Encoded words mixed with plain text without separating whitespace.", simple("").replace(b"Subject: probe", b"Subject: pre=?utf-8?q?mid?=post and =?utf-8?q?x?=y"))
case("enc-20-unknown-charset", "Encoded word with an unknown charset.", simple("").replace(b"Subject: probe", b"Subject: =?x-nonsense?q?abc?="))
case("enc-21-bad-base64-word", "Encoded word with invalid base64 payload.", simple("").replace(b"Subject: probe", b"Subject: =?utf-8?b?@@@@?= tail"))
case("enc-22-long-word", "Encoded word over 75 characters.", simple("").replace(b"Subject: probe", ("Subject: =?utf-8?q?" + "a" * 90 + "?=").encode("ascii")))
case("enc-23-word-lowercase-encoding", "Lower-case encoding letter and trailing `?=` oddities.", simple("").replace(b"Subject: probe", b"Subject: =?utf-8?q?a=3Db?= =?utf-8?Q?c?="))
case("enc-24-word-split-by-fold", "One encoded word split across a fold (invalid).", simple("").replace(b"Subject: probe", b"Subject: =?utf-8?q?ab\r\n cd?="))
case("enc-25-word-in-quoted-string", "Encoded word inside a quoted display name (disallowed by RFC 2047 5).", simple("").replace(b"From: Alice <alice@example.com>", b'From: "=?utf-8?q?Zo=C3=AB?=" <zoe@example.com>'))
case("enc-26-word-in-comment", "Encoded word in a comment and in the address phrase.", simple("").replace(b"From: Alice <alice@example.com>", b"From: =?utf-8?q?Zo=C3=AB?= <zoe@example.com> (=?utf-8?q?c=C3=A9?=)"))
case("enc-27-iso2022jp", "ISO-2022-JP encoded word.", simple("").replace(b"Subject: probe", b"Subject: =?ISO-2022-JP?B?GyRCRnxLXDhsGyhC?="))
case("enc-28-empty-word", "Empty encoded text `=?utf-8?q??=`.", simple("").replace(b"Subject: probe", b"Subject: a =?utf-8?q??= b"))
case("enc-29-comments-subject", "Comments and Keywords with encoded words.", simple("Comments: =?utf-8?q?c=C3=A9?=\nKeywords: =?utf-8?q?k?=, plain\n"))
case("enc-30-2047-in-ct-name", "Encoded word in the Content-Type `name` parameter, unquoted token form.", multi('boundary="abc"', '--abc\nContent-Type: application/octet-stream; name==?utf-8?q?a=C3=A9.bin?=\n\nDATA\n--abc--\n'))

# ---- addresses -------------------------------------------------------------------------------------------------
def addr(value, header="To"):
    return crlf("From: Alice <alice@example.com>\n%s: %s\nSubject: s\nDate: Mon, 14 May 2001 23:39:00 +0000\n\nbody\n" % (header, value))


ADDRS = [
    ("01-group-empty", "Empty group `undisclosed-recipients:;`.", "undisclosed-recipients:;"),
    ("02-group-members", "Group with members and a trailing mailbox.", "Team: a@x.example, B <b@x.example>;, c@x.example"),
    ("03-unquoted-comma", "Unquoted comma in a display name (`Doe, John <j@x>`).", "Doe, John <john@x.example>"),
    ("04-no-angle", "Bare address, and display text followed by a bare address.", "john@x.example, John Doe jane@x.example"),
    ("05-comment-name", "Name given as a trailing comment (`j@x (John Doe)`).", "john@x.example (John Doe)"),
    ("06-nested-comments", "Nested comments and comments between tokens.", "John (a (nested) comment) <(c)john(d)@(e)x.example(f)>"),
    ("07-obs-route", "Obsolete source route `<@a.example,@b.example:j@x>`.", "<@a.example,@b.example:john@x.example>"),
    ("08-quoted-local", "Quoted local part with a space and an escaped quote.", '"john q"@x.example, "a\\"b"@x.example'),
    ("09-empty-angle", "Empty addr-spec `<>` and name with empty angle.", "Bounce <>, <>"),
    ("10-no-domain", "Address without a domain.", "john, mary@"),
    ("11-ip-literal", "Domain as an IP literal and an IPv6 literal.", "j@[192.0.2.1], k@[IPv6:2001:db8::1]"),
    ("12-utf8-local", "Raw UTF-8 in the local part and the domain.", "jöhn@exämple.example"),
    ("13-empty-elements", "Empty list elements: `a@x,,b@y,`.", "a@x.example,,b@y.example,"),
    ("14-unclosed-angle", "Unclosed angle bracket `<j@x`.", "John <john@x.example"),
    ("15-unclosed-quote", "Unclosed quoted display name.", '"John <john@x.example>'),
    ("16-dots-in-name", "Unquoted display name with dots (`J. Q. Doe`).", "J. Q. Doe <john@x.example>"),
    ("17-name-with-at", "Display name containing `@` unquoted.", "john@home <john@x.example>"),
    ("18-multiple-angle", "Two address-specs in one mailbox `<a@x><b@y>`.", "<a@x.example><b@y.example>"),
    ("19-semicolon-separator", "Semicolons as separators (`a@x; b@y`).", "a@x.example; b@y.example"),
    ("20-case-and-spaces", "Spaces inside the addr-spec (`a @ x . example`).", "a @ x . example"),
    ("21-long-list", "Fifty mailboxes folded over several lines.", ",\n ".join("User%d <u%d@x.example>" % (i, i) for i in range(50))),
    ("22-quoted-name-escapes", "Quoted display name with escaped specials.", '"Doe\\, \\"JD\\" John" <john@x.example>'),
    ("23-trailing-dot-local", "Local part with leading, trailing and doubled dots.", ".a@x.example, b.@x.example, c..d@x.example"),
    ("24-8bit-name", "Raw 8-bit (Latin-1) display name.", None),
]
for n, d, v in ADDRS:
    if v is None:
        case("adr-" + n, d, b"From: Alice <alice@example.com>\r\nTo: Zo\xeb <zoe@x.example>\r\nSubject: s\r\n\r\nbody\r\n")
    else:
        case("adr-" + n, d, addr(v))
case("adr-25-duplicate-to", "Two To headers with different addresses.", crlf("From: a@example.com\nTo: one@x.example\nTo: two@x.example\nSubject: s\n\nbody\n"))
case("adr-26-from-multiple", "From with two mailboxes and a Sender.", crlf("From: a@example.com, b@example.com\nSender: s@example.com\nSubject: s\n\nbody\n"))
case("adr-27-reply-to-group", "Reply-To as a group.", addr("Staff: a@x.example, b@x.example;", "Reply-To"))
case("adr-28-bcc-empty", "Empty Bcc and empty Cc.", crlf("From: a@example.com\nBcc:\nCc:\nTo: t@example.com\nSubject: s\n\nbody\n"))

# ---- dates ------------------------------------------------------------------------------------------------------
def date(v):
    return crlf("From: a@example.com\nTo: b@example.com\nSubject: s\nDate: %s\n\nbody\n" % v)


DATES = [
    ("01-standard", "Mon, 14 May 2001 23:39:00 +0000"),
    ("02-no-seconds", "Mon, 14 May 2001 23:39 +0000"),
    ("03-two-digit-year-99", "Thu, 4 Jan 99 08:00:00 +0000"),
    ("04-two-digit-year-49", "Fri, 4 Jan 49 08:00:00 +0000"),
    ("05-two-digit-year-50", "Wed, 4 Jan 50 08:00:00 +0000"),
    ("06-no-day-name", "14 May 2001 23:39:00 +0000"),
    ("07-wrong-day-name", "Tue, 14 May 2001 23:39:00 +0000"),
    ("08-zone-est", "Mon, 14 May 2001 23:39:00 EST"),
    ("09-zone-gmt-ut", "Mon, 14 May 2001 23:39:00 GMT"),
    ("10-zone-minus-zero", "Mon, 14 May 2001 23:39:00 -0000"),
    ("11-zone-comment", "Mon, 14 May 2001 23:39:00 +0200 (CEST)"),
    ("12-zone-military", "Mon, 14 May 2001 23:39:00 Z"),
    ("13-lower-month", "Mon, 14 may 2001 23:39:00 +0000"),
    ("14-three-digit-year", "Mon, 14 May 101 23:39:00 +0000"),
    ("15-garbage", "sometime last Tuesday"),
    ("16-second-60", "Mon, 14 May 2001 23:59:60 +0000"),
    ("17-offset-2400", "Mon, 14 May 2001 23:39:00 +2400"),
    ("18-offset-1400", "Mon, 14 May 2001 23:39:00 +1400"),
    ("19-iso", "2001-05-14 23:39:00"),
    ("20-iso-t-z", "2001-05-14T23:39:00Z"),
    ("21-gmt-plus", "Mon, 14 May 2001 23:39:00 GMT+2"),
    ("22-hour-24", "Mon, 14 May 2001 24:00:00 +0000"),
    ("23-day-31-in-30-day-month", "Tue, 31 Apr 2001 10:00:00 +0000"),
    ("24-extra-spaces", "Mon,   14  May   2001   23:39:00    +0000"),
    ("25-missing-zone", "Mon, 14 May 2001 23:39:00"),
    ("26-year-1969", "Wed, 31 Dec 1969 23:59:59 +0000"),
    ("27-year-1899", "Sun, 1 Jan 1899 00:00:00 +0000"),
    ("28-year-2100", "Fri, 1 Jan 2100 00:00:00 +0000"),
    ("29-pdt-name", "Mon, 14 May 2001 23:39:00 PDT"),
    ("30-fractional", "Mon, 14 May 2001 23:39:00.123 +0000"),
    ("31-empty", ""),
    ("32-day-zero", "Mon, 0 May 2001 23:39:00 +0000"),
    ("34-two-digit-year-68", "Mon, 4 Jan 68 08:00:00 +0000"),
    ("35-two-digit-year-69", "Sat, 4 Jan 69 08:00:00 +0000"),
    ("36-two-digit-year-70", "Sun, 4 Jan 70 08:00:00 +0000"),
    ("37-year-00", "Tue, 4 Jan 00 08:00:00 +0000"),
    ("38-year-0002", "Fri, 4 Jan 0002 08:00:00 +0000"),
]
for n, v in DATES:
    case("date-" + n, "Date: %s" % (v if v else "(empty)"), date(v))
case("date-33-duplicate", "Two Date headers.", crlf("From: a@example.com\nDate: Mon, 14 May 2001 23:39:00 +0000\nDate: Tue, 15 May 2001 01:00:00 +0200\nSubject: s\n\nbody\n"))

# ---- nested messages and structure --------------------------------------------------------------------------------
INNER = "From: inner@example.com\nSubject: inner\nDate: Tue, 15 May 2001 01:00:00 +0000\n\ninner body\n"
case("nest-01-rfc822-attached", "A message/rfc822 part holding a plain message.", multi('boundary="abc"', "--abc\nContent-Type: message/rfc822\n\n%s--abc--\n" % INNER))
case("nest-02-rfc822-multipart-inside", "message/rfc822 whose inner message is multipart.",
     multi('boundary="abc"', "--abc\nContent-Type: message/rfc822\n\nFrom: i@example.com\nMIME-Version: 1.0\nContent-Type: multipart/alternative; boundary=\"in\"\n\n--in\nContent-Type: text/plain\n\nplain\n--in\nContent-Type: text/html\n\n<p>html</p>\n--in--\n--abc--\n"))
case("nest-03-rfc822-empty", "message/rfc822 part with an empty body.", multi('boundary="abc"', "--abc\nContent-Type: message/rfc822\n\n--abc--\n"))
case("nest-04-rfc822-no-headers", "message/rfc822 whose body has no headers, only text.", multi('boundary="abc"', "--abc\nContent-Type: message/rfc822\n\njust text and no headers\n--abc--\n"))
case("nest-05-rfc822-8bit", "message/rfc822 with CTE 8bit and a UTF-8 inner message.", crlf(BASE + MIME + 'Content-Type: multipart/mixed; boundary="abc"\n\n--abc\nContent-Type: message/rfc822\nContent-Transfer-Encoding: 8bit\n\nFrom: i@example.com\nSubject: ') + "café".encode("utf-8") + crlf("\n\nbody\n--abc--\n"))
case("nest-06-digest-default", "multipart/digest: children default to message/rfc822 (RFC 2046 5.1.5).", multi('boundary="abc"', "--abc\n\n%s--abc--\n" % INNER, ctype="multipart/digest"))
case("nest-07-digest-explicit-text", "multipart/digest with an explicit text/plain child.", multi('boundary="abc"', "--abc\nContent-Type: text/plain\n\ntext in digest\n--abc--\n", ctype="multipart/digest"))
case("nest-08-report-dsn", "multipart/report with delivery-status and returned headers.",
     multi('boundary="abc"; report-type=delivery-status', "--abc\nContent-Type: text/plain\n\nCould not deliver.\n--abc\nContent-Type: message/delivery-status\n\nReporting-MTA: dns; mx.example.com\n\nFinal-Recipient: rfc822; bob@example.com\nAction: failed\nStatus: 5.1.1\n\n--abc\nContent-Type: text/rfc822-headers\n\nFrom: alice@example.com\nSubject: probe\n\n--abc--\n", ctype="multipart/report"))
case("nest-09-partial", "message/partial fragment.", simple("MIME-Version: 1.0\nContent-Type: message/partial; id=\"x@y\"; number=1; total=2\n", "fragment data\n"))
case("nest-10-alternative", "multipart/alternative with text, html and an inline image.", multi('boundary="abc"', "--abc\nContent-Type: text/plain\n\nplain\n--abc\nContent-Type: multipart/related; boundary=\"rel\"\n\n--rel\nContent-Type: text/html\n\n<img src=\"cid:i1\">\n--rel\nContent-Type: image/png\nContent-ID: <i1>\nContent-Transfer-Encoding: base64\n\niVBORw0KGgo=\n--rel--\n--abc--\n", ctype="multipart/alternative"))
case("nest-11-signed", "multipart/signed with a detached signature part.", multi('boundary="abc"; protocol="application/pgp-signature"; micalg=pgp-sha256', "--abc\nContent-Type: text/plain\n\nsigned text\n--abc\nContent-Type: application/pgp-signature\n\n-----BEGIN PGP SIGNATURE-----\nAAAA\n-----END PGP SIGNATURE-----\n--abc--\n", ctype="multipart/signed"))
case("nest-12-truncated-in-headers", "Message cut off inside a part's header block.", multi('boundary="abc"', "--abc\nContent-Type: text/pla"))
case("nest-13-truncated-in-header-name", "Message cut off in the middle of a header name.", crlf("From: a@example.com\nSubje"))


def nest_deep(n):
    # n levels of multipart, each holding one child; the innermost child is text/plain.
    body = "--b%d\nContent-Type: text/plain\n\ninnermost\n--b%d--\n" % (n, n)
    for i in range(n - 1, -1, -1):
        body = "--b%d\nContent-Type: multipart/mixed; boundary=\"b%d\"\n\n%s--b%d--\n" % (i, i + 1, body, i)
    return crlf(BASE + MIME + 'Content-Type: multipart/mixed; boundary="b0"\n\n' + body)


case("nest-14-depth-10", "Ten levels of nested multipart.", nest_deep(10))
case("nest-15-depth-200", "Two hundred levels of nested multipart (resource-limit probe).", nest_deep(200))
case("nest-16-many-parts", "One multipart with 3000 tiny parts (part-count probe).", multi('boundary="abc"', "".join("--abc\nContent-Type: text/plain\n\np%d\n" % i for i in range(3000)) + "--abc--\n"))
def nest_rfc822(n):
    inner = "From: leaf@example.com\nSubject: leaf\n\nleaf body\n"
    for i in range(n):
        inner = "From: l%d@example.com\nSubject: level %d\nContent-Type: message/rfc822\n\n%s" % (i, i, inner)
    return crlf(inner)


case("nest-17-rfc822-depth-30", "message/rfc822 nested 30 levels.", nest_rfc822(30))
case("nest-18-text-html-qp", "A realistic text/html quoted-printable top-level message.", simple('MIME-Version: 1.0\nContent-Type: text/html; charset="utf-8"\nContent-Transfer-Encoding: quoted-printable\n', "<p>caf=C3=A9 =E2=98=95</p>=\n<p>x</p>\n"))
case("nest-19-attachment-inline-disposition", "Content-Disposition `inline` with filename and one with an unknown disposition type.", multi('boundary="abc"', '--abc\nContent-Type: image/png; name="i.png"\nContent-Disposition: inline; filename="i.png"\nContent-Transfer-Encoding: base64\n\niVBORw0KGgo=\n--abc\nContent-Type: text/plain\nContent-Disposition: weird; filename="w.txt"\n\nw\n--abc--\n'))
case("nest-20-content-length-lie", "Header `Content-Length: 5` (mboxcl) but the body is longer.", simple("Content-Length: 5\n", "this body is longer than five bytes\n"))
case("nest-21-mbox-trailer", "A body whose last lines look like the start of the next mbox message.", simple("", "text\n\nFrom bob@example.com Mon May 14 23:39:00 2001\nSubject: not a header here\n"))


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "testdata", "differential")
    os.makedirs(out, exist_ok=True)
    names = set()
    with open(os.path.join(out, "cases.tsv"), "w", encoding="utf-8", newline="\n") as m:
        for name, desc, data in sorted(cases):
            assert name not in names, name
            names.add(name)
            with open(os.path.join(out, name + ".eml"), "wb") as f:
                f.write(data)
            m.write("%s\t%s\n" % (name, desc))
    print("wrote %d cases to %s" % (len(cases), out))


if __name__ == "__main__":
    main()
