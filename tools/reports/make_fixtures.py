#!/usr/bin/env python3
"""Generates testdata/reports: delivery status, disposition notification and feedback reports, Authentication-Results and
Received-SPF headers, and MHTML aggregates, well formed and malformed. Deterministic: no clock, no randomness.

    python3 tools/reports/make_fixtures.py [OUTDIR]        # default testdata/reports

Several messages follow the examples in RFC 3464 appendix E, RFC 8098 section 9, RFC 5965 appendix B, RFC 8601 appendix B and
RFC 7208 section 9.1, adapted to be well formed (the text of an RFC contains page breaks that would split a block). The RFCs are
IETF Trust documents; see THIRD-PARTY-NOTICES.md. Everything else here is invented.
"""
import os
import sys

out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..", "testdata", "reports")
os.makedirs(out, exist_ok=True)
cases = []  # (name, description)


def put(name, text, desc, raw=None, refs=None, eol="\r\n"):
    data = raw if raw is not None else text.replace("\r\n", "\n").replace("\n", eol).encode("utf-8")
    with open(os.path.join(out, name), "wb") as f:
        f.write(data)
    if refs is not None:
        with open(os.path.join(out, name.rsplit(".", 1)[0] + ".refs"), "wb") as f:
            f.write(("\n".join(refs) + "\n").encode("utf-8"))
    cases.append((name, desc))


HDR = "From: Mail Delivery Subsystem <MAILER-DAEMON@mx.example.org>\nTo: <sender@example.com>\nSubject: Returned mail\nDate: Thu, 7 Jul 1994 17:16:05 -0400\nMessage-ID: <bounce1@mx.example.org>\nMIME-Version: 1.0\n"
ORIG = "From: <sender@example.com>\nTo: <user@remote.example>\nSubject: hello\nDate: Sat, 2 Jul 1994 17:10:28 -0400\nMessage-ID: <orig1@example.com>\n\nThe original body.\n"


def report(rtype, machine_ct, machine, human="This is a report.\n", original=ORIG, original_ct="message/rfc822", rt=None, extra_parts=None):
    rt = rtype if rt is None else rt
    ct = "multipart/report; report-type=%s;\n boundary=\"b1\"" % rt if rt else "multipart/report; boundary=\"b1\""
    s = HDR + "Content-Type: " + ct + "\n\n"
    s += "--b1\nContent-Type: text/plain; charset=us-ascii\n\n" + human
    if machine is not None:
        s += "--b1\nContent-Type: " + machine_ct + "\n\n" + machine
    if original is not None:
        s += "--b1\nContent-Type: " + original_ct + "\n\n" + original
    s += "--b1--\n"
    return s


def dsn(name, machine, desc, **kw):
    put(name, report("delivery-status", kw.pop("ct", "message/delivery-status"), machine, **kw), desc)


# ---- delivery status (RFC 3464) ----
MTA_BLOCK = "Reporting-MTA: dns; cs.utk.edu\n"
dsn("dsn-01-simple.eml", MTA_BLOCK + "\nOriginal-Recipient: rfc822;louisl@larry.slip.umd.edu\nFinal-Recipient: rfc822;louisl@larry.slip.umd.edu\nAction: failed\nStatus: 4.0.0\nDiagnostic-Code: smtp; 426 connection timed out\nLast-Attempt-Date: Thu, 7 Jul 1994 17:15:49 -0400\n", "RFC 3464 E.1: one recipient")
dsn("dsn-02-multi-recipient.eml", MTA_BLOCK + "\nOriginal-Recipient: rfc822;arathib@vnet.ibm.com\nFinal-Recipient: rfc822;arathib@vnet.ibm.com\nAction: failed\nStatus: 5.0.0 (permanent failure)\nDiagnostic-Code: smtp;  550 'arathib@vnet.IBM.COM' is not a\n registered gateway user\nRemote-MTA: dns; vnet.ibm.com\n\nOriginal-Recipient: rfc822;johnh@hpnjld.njd.hp.com\nFinal-Recipient: rfc822;johnh@hpnjld.njd.hp.com\nAction: delayed\nStatus: 4.0.0 (hpnjld.njd.jp.com: host name lookup failure)\n\nOriginal-Recipient: rfc822;wsnell@sdcc13.ucsd.edu\nFinal-Recipient: rfc822;wsnell@sdcc13.ucsd.edu\nAction: failed\nStatus: 5.0.0\nDiagnostic-Code: smtp; 550 user unknown\nRemote-MTA: dns; sdcc13.ucsd.edu\n", "RFC 3464 E.2: three recipients, folded diagnostic, comments in Status")
dsn("dsn-03-gateway.eml", "Reporting-MTA: mailbus; SYS30\n\nFinal-Recipient: unknown; nair_s\nStatus: 5.0.0 (unknown permanent failure)\nAction: failed\n", "RFC 3464 E.3: gateway, unknown address type", original=None)
dsn("dsn-04-delayed.eml", "Reporting-MTA: dns; sun2.nsfnet-relay.ac.uk\n\nFinal-Recipient: rfc822;thomas@de-montfort.ac.uk\nStatus: 4.0.0 (unknown temporary failure)\nAction: delayed\nWill-Retry-Until: Sun, 17 Jul 1994 00:36:51 +0100\n", "RFC 3464 E.4 shape: delayed, Will-Retry-Until, no returned content", original=None)
dsn("dsn-05-all-fields.eml", "Reporting-MTA: dns; mx.example.org\nDSN-Gateway: smtp; gw.example.org\nReceived-From-MTA: dns; relay.example.net\nOriginal-Envelope-ID: QQ314159+2B265\nArrival-Date: Sat, 2 Jul 1994 17:10:28 -0400\nX-Queue-Id: 8A1C2\n\nOriginal-Recipient: rfc822;list@example.org\nFinal-Recipient: rfc822;a+2Bb+3Dc@example.org\nAction: expanded\nStatus: 2.1.5\nRemote-MTA: dns; mail.example.org\nDiagnostic-Code: smtp; 250 2.1.5 expanded\nLast-Attempt-Date: Sat, 2 Jul 1994 17:15:49 -0400\nFinal-Log-ID: log-4711\nX-Extra: one\nX-Extra: two\n\nFinal-Recipient: rfc822;ok@example.org\nAction: delivered\nStatus: 2.0.0\n\nFinal-Recipient: rfc822;next@example.org\nAction: relayed\nStatus: 2.0.0\n", "every RFC 3464 field, xtext, expanded/delivered/relayed, repeated extension field")
dsn("dsn-06-global.eml", "Reporting-MTA: dns; mx.example.org\n\nOriginal-Recipient: utf-8; \u7528\u6237@\u4f8b\u3048.example\nFinal-Recipient: utf-8; \u7528\u6237@\u4f8b\u3048.example\nAction: failed\nStatus: 5.1.1\nDiagnostic-Code: smtp; 550 5.1.1 \u7528\u6237 unknown\n", "RFC 6533: message/global-delivery-status with UTF-8 addresses and text", ct="message/global-delivery-status")
dsn("dsn-07-headers-only.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;x@example.org\nAction: failed\nStatus: 5.1.1\n", "original returned as text/rfc822-headers", original="From: <sender@example.com>\nTo: <x@example.org>\nSubject: hi\nMessage-ID: <orig2@example.com>\n", original_ct="text/rfc822-headers")
put("dsn-08-lf.eml", report("delivery-status", "message/delivery-status", MTA_BLOCK + "\nFinal-Recipient: rfc822;x@example.org\nAction: failed\nStatus: 5.1.1\n"), "LF line ends", eol="\n")
put("dsn-09-crlf.eml", report("delivery-status", "message/delivery-status", MTA_BLOCK + "\nFinal-Recipient: rfc822;x@example.org\nAction: failed\nStatus: 5.1.1\n"), "CRLF line ends")
dsn("dsn-10-no-recipient.eml", MTA_BLOCK, "per-message block only")
dsn("dsn-11-blank-lines.eml", MTA_BLOCK + "\n\n\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\n\n\n\nFinal-Recipient: rfc822;b@example.org\nAction: failed\nStatus: 5.1.2\n", "several blank lines between groups")
dsn("dsn-12-leading-blank.eml", "\n\n" + MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\n", "blank lines before the first group")
dsn("dsn-13-no-colon-line.eml", MTA_BLOCK + "garbage line here\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\n", "a line without a colon inside a group")
dsn("dsn-14-fold-first.eml", " continuation first\n" + MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\n", "a fold before any field")
dsn("dsn-15-duplicate-action.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nAction: delayed\nStatus: 5.1.1\n", "Action twice")
dsn("dsn-16-missing-fields.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\n", "no Action and no Status")
dsn("dsn-17-bad-values.eml", MTA_BLOCK + "\nFinal-Recipient: a@example.org\nAction: exploded\nStatus: 55.1\nLast-Attempt-Date: yesterday\nDiagnostic-Code: no type here\n", "unknown Action, bad Status, bad date, untyped values")
dsn("dsn-18-space-before-colon.eml", "Reporting-MTA : dns; cs.utk.edu\n\nFinal-Recipient : rfc822;a@example.org\nAction : failed\nStatus : 5.1.1\n", "whitespace before the colon")
dsn("dsn-19-space-only-line.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\n  \nStatus: 4.0.0\nAction: delayed\n", "a line holding only blanks between fields (RFC 3464 E.4 as printed)")
put("dsn-20-unterminated.eml", report("delivery-status", "message/delivery-status", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\n", original=None).replace("--b1--\n", ""), "the closing delimiter is missing")
dsn("dsn-21-empty.eml", "", "an empty delivery-status body")
dsn("dsn-22-no-report-type.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\n", "multipart/report without report-type", rt="")
dsn("dsn-23-wrong-second-part.eml", "plain text, not a report\n", "second part is text/plain", ct="text/plain")
put("dsn-24-one-part.eml", report("delivery-status", "", None, original=None), "multipart/report with only the explanation")
put("dsn-25-8bit.eml", None, "raw Latin-1 bytes in a diagnostic", raw=report("delivery-status", "message/delivery-status", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\nDiagnostic-Code: smtp; 550 caf\u00e9 inconnu\n").replace("\n", "\r\n").encode("latin-1"))
dsn("dsn-26-lone-cr.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\nDiagnostic-Code: smtp; a\rb\n", "a bare CR inside a value")
dsn("dsn-27-folded-long.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\nDiagnostic-Code: smtp; 550-5.1.1 The email account that you tried to reach does not exist.\n\t Please try double-checking the recipient's email address for typos\n  or unnecessary spaces.\n", "folds with tab and two spaces")
dsn("dsn-28-tab-status.eml", MTA_BLOCK + "\nFinal-Recipient:\trfc822;a@example.org\nAction:\tfailed\nStatus:\t5.1.1\n", "tab after the colon")
dsn("dsn-29-no-space-after-colon.eml", MTA_BLOCK + "\nFinal-Recipient:rfc822;a@example.org\nAction:failed\nStatus:5.1.1\n", "no space after the colon")
dsn("dsn-30-case.eml", "REPORTING-MTA: DNS; MX.Example.ORG\n\nFINAL-RECIPIENT: RFC822;A@Example.org\nACTION: FAILED\nSTATUS: 5.1.1\n", "upper-case names and keywords")
dsn("dsn-31-empty-values.eml", "Reporting-MTA:\n\nFinal-Recipient:\nAction:\nStatus:\n", "fields with empty values")
dsn("dsn-32-trailing-blank-block.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\n\n\n", "blank lines at the end")
dsn("dsn-33-embedded-nul.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nAction: failed\nStatus: 5.1.1\nDiagnostic-Code: smtp; a\x00b\n", "NUL inside a value")
dsn("dsn-34-colon-in-name.eml", MTA_BLOCK + "\nFinal-Recipient: rfc822;a@example.org\nX Bad Name: v\nAction: failed\nStatus: 5.1.1\n", "a name with a space")

# base64 / quoted-printable transfer encoding on the report part
put("dsn-35-base64-body.eml", HDR + "Content-Type: multipart/report; report-type=delivery-status; boundary=\"b1\"\n\n--b1\nContent-Type: text/plain\n\nx\n--b1\nContent-Type: message/delivery-status\nContent-Transfer-Encoding: base64\n\nUmVwb3J0aW5nLU1UQTogZG5zOyBjcy51dGsuZWR1CgpGaW5hbC1SZWNpcGllbnQ6IHJmYzgyMjthQGV4YW1wbGUub3JnCkFjdGlvbjogZmFpbGVkClN0YXR1czogNS4xLjEK\n--b1--\n", "base64 transfer encoding on the status part")

# ---- disposition notification (RFC 8098, RFC 3798) ----
MH = "Reporting-UA: joes-pc.cs.example.com; Foomail 97.1\nOriginal-Recipient: rfc822;Joe_Recipient@example.com\nFinal-Recipient: rfc822;Joe_Recipient@example.com\nOriginal-Message-ID: <199509192301.23456@example.org>\nDisposition: manual-action/MDN-sent-manually; displayed\n"


def mdn(name, machine, desc, **kw):
    put(name, report("disposition-notification", kw.pop("ct", "message/disposition-notification"), machine, **kw), desc)


mdn("mdn-01-displayed.eml", MH, "RFC 8098 section 9 example")
mdn("mdn-02-processed-error.eml", "Reporting-UA: mx; Mailer 1.0\nMDN-Gateway: smtp; gw.example.org\nFinal-Recipient: rfc822;x@example.org\nOriginal-Message-ID: <id@example.org>\nDisposition: automatic-action/MDN-sent-automatically; processed/error,warning\nError: unable to process\nWarning: something odd\nFailure: none\nX-Vendor: 7\n", "automatic, modifiers, Error/Warning/Failure fields", original=None)
mdn("mdn-03-deleted-expired.eml", "Final-Recipient: rfc822;x@example.org\nDisposition: automatic-action/MDN-sent-automatically; deleted/expired\n", "deleted with a modifier")
mdn("mdn-04-headers-only.eml", MH, "original as headers only", original="From: <a@example.org>\nSubject: x\n", original_ct="text/rfc822-headers")
mdn("mdn-05-extra-block.eml", MH + "\nFinal-Recipient: rfc822;second@example.org\nDisposition: manual-action/MDN-sent-manually; denied\n", "a second block after a blank line")
mdn("mdn-06-missing-disposition.eml", "Final-Recipient: rfc822;x@example.org\n", "no Disposition")
mdn("mdn-07-bad-disposition.eml", "Final-Recipient: rfc822;x@example.org\nDisposition: whatever\n", "Disposition without modes")
mdn("mdn-08-global.eml", "Final-Recipient: utf-8; \u7528\u6237@example.org\nDisposition: manual-action/MDN-sent-manually; dispatched\n", "RFC 6533 global form", ct="message/global-disposition-notification")
mdn("mdn-09-failed.eml", "Final-Recipient: rfc822;x@example.org\nDisposition: manual-action/MDN-sent-manually; failed\n", "failed")
mdn("mdn-10-duplicate.eml", MH + "Disposition: automatic-action/MDN-sent-automatically; displayed\n", "two Disposition fields")
mdn("mdn-11-leading-blank.eml", "\n" + MH, "blank line before the fields")
mdn("mdn-12-folded.eml", "Reporting-UA: joes-pc.cs.example.com;\n Foomail 97.1\nFinal-Recipient: rfc822;x@example.org\nDisposition: manual-action/MDN-sent-manually;\n displayed (comment)\n", "folded values and a comment")
mdn("mdn-13-no-original.eml", MH, "no third part", original=None)

# ---- feedback reports (RFC 5965, RFC 6591, RFC 6650) ----
FH = "Feedback-Type: abuse\nUser-Agent: SomeGenerator/1.0\nVersion: 1\n"


def arf(name, machine, desc, **kw):
    put(name, report("feedback-report", kw.pop("ct", "message/feedback-report"), machine, **kw), desc)


arf("arf-01-required.eml", FH, "RFC 5965 B.1: required fields")
arf("arf-02-full.eml", FH + "Original-Mail-From: <somespammer@example.net>\nOriginal-Rcpt-To: <user@example.com>\nOriginal-Rcpt-To: <other@example.com>\nArrival-Date: Thu, 8 Mar 2005 14:00:00 -0400\nReporting-MTA: dns; mail.example.com\nSource-IP: 192.0.2.1\nIncidents: 3\nAuthentication-Results: mail.example.com;\n spf=fail smtp.mail=somespammer@example.com\nReported-Domain: example.net\nReported-URI: http://example.net/earn_money.html\nReported-URI: mailto:user@example.com\nRemoval-Recipient: user@example.com\nX-Vendor: yes\n", "RFC 5965 B.2: every field")
arf("arf-03-auth-failure.eml", "Feedback-Type: auth-failure\nUser-Agent: Mailer/2\nVersion: 1\nAuth-Failure: signature\nDelivery-Result: delivered\nDKIM-Domain: example.net\nDKIM-Identity: @example.net\nDKIM-Selector: sel1\nDKIM-Canonicalized-Header: ZnJvbTpzb21l\nIdentity-Alignment: dkim\nSource-IP: 2001:db8::1\n", "RFC 6591 authentication failure report, IPv6 source")
arf("arf-04-not-spam.eml", "Feedback-Type: not-spam\nUser-Agent: Mailer/2\nVersion: 1\n", "RFC 5965 registry: not-spam")
arf("arf-05-virus.eml", "Feedback-Type: virus\nUser-Agent: Av/1\nVersion: 1\nSource-IP: 192.0.2.9 (zombie)\n", "virus; Source-IP with a comment")
arf("arf-06-headers-only.eml", FH, "original as text/rfc822-headers", original="From: <s@example.net>\nSubject: x\n", original_ct="text/rfc822-headers")
arf("arf-07-missing-version.eml", "Feedback-Type: abuse\nUser-Agent: X/1\n", "no Version")
arf("arf-08-bad-values.eml", "Feedback-Type: abuse\nUser-Agent: X/1\nVersion: 2\nSource-IP: not-an-ip\nIncidents: many\nArrival-Date: sometime\nReporting-MTA: mail.example.com\n", "bad Version, Source-IP, Incidents, date, untyped MTA")
arf("arf-09-extra-block.eml", FH + "\nFeedback-Type: fraud\nUser-Agent: Y/1\nVersion: 1\n", "a second block")
arf("arf-10-unknown-type.eml", "Feedback-Type: newkind\nUser-Agent: X/1\nVersion: 1\n", "a type outside the registry")
arf("arf-11-duplicates.eml", FH + "Feedback-Type: fraud\nSource-IP: 192.0.2.1\nSource-IP: 192.0.2.2\n", "single-valued fields repeated")
arf("arf-12-no-original.eml", FH, "no third part", original=None)
arf("arf-13-blank-first.eml", "\n" + FH, "blank line before the fields")
arf("arf-14-case.eml", "FEEDBACK-TYPE: ABUSE\nuser-agent: X/1\nVERSION: 1\n", "name and keyword case")

# ---- Authentication-Results (RFC 8601) ----
AUTH = [
    ("auth-01-none.eml", ["example.org 1; none"], "RFC 8601 B.2: none with a version"),
    ("auth-02-spf.eml", ["example.com;\n          spf=pass smtp.mailfrom=example.net"], "RFC 8601 B.3"),
    ("auth-03-several.eml", ["example.com;\n          auth=pass (cram-md5) smtp.auth=sender@example.net;\n          spf=pass smtp.mailfrom=example.net", "example.com; iprev=pass\n          policy.iprev=192.0.2.200"], "RFC 8601 B.4: two fields"),
    ("auth-04-comment-heavy.eml", ["foo.example.net (foobar) 1 (baz);\n    dkim (Because I like it) / 1 (One yay) = (wait for it) fail\n      policy (A dot can go here) . (like that) expired\n      (this surprised me) = (as I wasn't expecting it) 1362471462"], "RFC 8601 B.7: comments everywhere"),
    ("auth-05-dkim-reason.eml", ["mx.example.com; dkim=fail reason=\"signature verification failed\" header.d=example.net header.i=@example.net header.b=abcd1234"], "reason and several header properties"),
    ("auth-06-multiple-methods.eml", ["mx.example.com; spf=pass smtp.mailfrom=a@example.net; dkim=pass header.d=example.net; dmarc=pass header.from=example.net"], "three methods"),
    ("auth-07-google.eml", ["mx.google.com;\n       dkim=pass header.i=@example.net header.s=sel header.b=Abc123;\n       spf=pass (google.com: domain of a@example.net designates 192.0.2.1 as permitted sender) smtp.mailfrom=a@example.net;\n       dmarc=pass (p=NONE sp=NONE dis=NONE) header.from=example.net"], "a Gmail-style field with comments after results"),
    ("auth-08-microsoft.eml", ["spf=pass (sender IP is 192.0.2.1) smtp.mailfrom=example.net; dkim=pass (signature was verified) header.d=example.net; dmarc=pass action=none header.from=example.net;compauth=pass reason=100"], "Microsoft style: no authserv-id first, vendor properties without a type"),
    ("auth-09-version-method.eml", ["example.com; dkim/1=pass header.d=a.example; x-vendor/2=neutral"], "method versions"),
    ("auth-10-quoted-values.eml", ["\"mx host.example\" 1; spf=pass smtp.mailfrom=\"a b@example.net\" reason=\"the \\\"best\\\" reason\""], "quoted authserv-id, property and reason"),
    ("auth-11-no-id.eml", ["; spf=pass smtp.mailfrom=a@example.net"], "no authserv-id"),
    ("auth-12-bad-version.eml", ["example.com 2; spf=pass smtp.mailfrom=a@example.net", "example.com x; spf=pass", "example.com 1.0; spf=pass"], "versions other than 1"),
    ("auth-13-trailing-semicolon.eml", ["example.com; spf=pass smtp.mailfrom=a@example.net;"], "trailing semicolon"),
    ("auth-14-empty-clause.eml", ["example.com; ; spf=pass smtp.mailfrom=a@example.net"], "empty clause"),
    ("auth-15-garbage.eml", ["example.com; spf=pass smtp.mailfrom=a@example.net; =pass; dkim; !!! ; arc=pass"], "clauses that are not method=result"),
    ("auth-16-none-and-results.eml", ["example.com; none; spf=pass smtp.mailfrom=a@example.net", "example.com; spf=pass smtp.mailfrom=a@example.net; none"], "none beside results"),
    ("auth-17-case.eml", ["EXAMPLE.com; SPF=PASS SMTP.MailFrom=A@Example.NET; DKIM=Fail Header.D=Example.Net"], "case of names and keywords"),
    ("auth-18-unterminated.eml", ["example.com; spf=pass smtp.mailfrom=\"a@example.net"], "unterminated quote"),
    ("auth-19-unterminated-comment.eml", ["example.com; spf=pass (never closed smtp.mailfrom=a@example.net"], "unterminated comment"),
    ("auth-20-empty-value.eml", ["example.com; dkim=pass header.b=; spf=pass smtp.mailfrom=\"\""], "empty property values"),
    ("auth-21-id-only.eml", ["example.com"], "authserv-id and nothing else"),
    ("auth-22-folded.eml", ["example.com 1;\n\tdkim=pass\n\theader.d=example.net\n\theader.s=\n\tsel"], "folds in odd places"),
    ("auth-23-ipv6-and-at.eml", ["mx.example.com; iprev=pass policy.iprev=2001:db8::1 (mail.example.net); spf=pass smtp.mailfrom=@example.net; smtp.helo=x"], "IPv6 value, bare @domain, stray property"),
    ("auth-24-arc.eml", ["mx.example.com 1; arc=none", "mx.example.com; arc=pass header.oldest-pass=0 smtp.remote-ip=192.0.2.1"], "arc results with header.oldest-pass"),
    ("auth-26-exchange.eml", ["spf=pass (sender IP is 192.0.2.1) smtp.mailfrom=example.net; dkim=pass (signature was verified) header.d=example.net"], "first token is a result, no identifier"),
    ("auth-27-method-version-first.eml", ["dkim/1=pass header.d=a.example; spf=pass"], "first token is a versioned method"),
    ("auth-25-result-comment.eml", ["mx.example.com; spf=softfail (domain owner discourages use of this host) smtp.mailfrom=a@example.net", "mx.example.com; spf=temperror (dns timeout)", "mx.example.com; spf=permerror"], "all the SPF-style results"),
]
for name, values, desc in AUTH:
    body = HDR.replace("MIME-Version: 1.0\n", "") + "".join("Authentication-Results: " + v + "\n" for v in values) + "Content-Type: text/plain\n\nbody\n"
    put(name, body, desc)

# ---- Received-SPF (RFC 7208 section 9.1) ----
SPF = [
    ("spf-01-pass.eml", ["pass (mybox.example.org: domain of myname@example.com designates 192.0.2.1 as permitted sender)\n receiver=mybox.example.org; client-ip=192.0.2.1;\n envelope-from=\"myname@example.com\"; helo=foo.example.com;"], "RFC 7208 9.1 example"),
    ("spf-02-fail.eml", ["fail (mx.example.org: domain of x@example.com does not designate 192.0.2.9 as permitted sender) client-ip=192.0.2.9; envelope-from=x@example.com; helo=bad.example; identity=mailfrom; receiver=mx.example.org; mechanism=-all; problem=none"], "every key"),
    ("spf-03-google.eml", ["pass (google.com: domain of a@example.net designates 192.0.2.1 as permitted sender) client-ip=192.0.2.1;"], "Gmail style"),
    ("spf-04-old.eml", ["Pass (mailfrom) identity=mailfrom; client-ip=2001:db8::1; helo=h.example; envelope-from=<a@example.net>; receiver=mx"], "old style: capitalised result, angle brackets in a value"),
    ("spf-05-none.eml", ["none"], "result only"),
    ("spf-06-unknown.eml", ["maybe (hm) client-ip=192.0.2.1"], "unknown result"),
    ("spf-07-bad-pair.eml", ["softfail client-ip=192.0.2.1; junk; =x; helo=a"], "words that are not pairs"),
    ("spf-08-quoted.eml", ["permerror (bad record) problem=\"a \\\"b\\\" c\"; client-ip=192.0.2.1"], "quoted problem text"),
    ("spf-09-folded.eml", ["neutral (comment\n spans lines) client-ip=192.0.2.1;\n\thelo=x"], "folds"),
    ("spf-10-temperror.eml", ["temperror client-ip=not-an-ip; envelope-from=a@b"], "bad client-ip"),
]
for name, values, desc in SPF:
    body = HDR.replace("MIME-Version: 1.0\n", "") + "".join("Received-SPF: " + v + "\n" for v in values) + "Content-Type: text/plain\n\nbody\n"
    put(name, body, desc)

# ---- MHTML (RFC 2557) ----
PNG = "iVBORw0KGgo="


def related(name, root_headers, parts, desc, refs, top="", ct_extra="", boundary="rb"):
    s = "From: <saved@example.org>\nSubject: page\nMIME-Version: 1.0\n" + top
    s += "Content-Type: multipart/related; type=\"text/html\"; boundary=\"%s\"%s\n\n" % (boundary, ct_extra)
    s += "--%s\nContent-Type: text/html; charset=utf-8\n%s\n<html><body>page</body></html>\n" % (boundary, root_headers)
    for headers in parts:
        s += "--%s\nContent-Type: image/png\nContent-Transfer-Encoding: base64\n%s\n%s\n" % (boundary, headers, PNG)
    s += "--%s--\n" % boundary
    put(name, s, desc, refs=refs)


related("mht-01-cid.eml", "Content-ID: <root@example.org>\n", ["Content-ID: <logo@example.org>\n", "Content-ID: <Banner@Example.org>\n"], "cid references", ["cid:logo@example.org", "cid:<logo@example.org>", "CID:logo@example.org", "cid:logo%40example.org", "cid:banner@example.org", "cid:Banner@Example.org", "cid:missing@example.org", "cid:root@example.org"])
related("mht-02-absolute.eml", "Content-Location: http://www.example.org/dir/page.html\n", ["Content-Location: http://www.example.org/dir/a.png\n", "Content-Location: http://www.example.org/img/b.png\n", "Content-Location: http://other.example.net/c.png\n"], "absolute locations; relative references resolve against the root", ["a.png", "./a.png", "../img/b.png", "/img/b.png", "http://www.example.org/dir/a.png", "http://other.example.net/c.png", "//other.example.net/c.png", "HTTP://WWW.EXAMPLE.ORG/dir/a.png", "a.png#frag", "a.png?x=1", "missing.png", "http://www.example.org:80/dir/a.png", "page.html"])
related("mht-03-relative.eml", "Content-Location: index.html\n", ["Content-Location: images/a.png\n", "Content-Location: b.png\n"], "relative locations, no real base: thismessage:/", ["images/a.png", "./images/a.png", "b.png", "images/../b.png", "/b.png", "c.png"])
related("mht-04-start.eml", "Content-ID: <first@example.org>\n", ["Content-ID: <second@example.org>\nContent-Location: http://x.example/s.html\n"], "the start parameter names the second part", ["cid:first@example.org", "cid:second@example.org", "http://x.example/s.html"], ct_extra="; start=\"<second@example.org>\"")
related("mht-05-start-missing.eml", "Content-ID: <first@example.org>\n", ["Content-ID: <second@example.org>\n"], "start names no part", ["cid:first@example.org"], ct_extra="; start=\"<nowhere@example.org>\"")
related("mht-06-duplicates.eml", "Content-Location: http://x.example/\n", ["Content-Location: http://x.example/a.png\nContent-ID: <same@example.org>\n", "Content-Location: http://x.example/a.png\nContent-ID: <same@example.org>\n"], "two parts with the same location and the same Content-ID", ["a.png", "cid:same@example.org"])
related("mht-07-escapes.eml", "Content-Location: http://x.example/dir/\n", ["Content-Location: http://x.example/dir/my%20pic.png\n", "Content-Location: http://x.example/dir/caf%C3%A9.png\n", "Content-Location: http://x.example/dir/up%2Fcase.png\n"], "percent escapes", ["my%20pic.png", "my pic.png", "caf%c3%a9.png", "caf%C3%A9.png", "caf\u00e9.png", "up%2fcase.png", "up%2Fcase.png", "up/case.png"])
related("mht-08-content-base.eml", "Content-Base: http://base.example/root/\nContent-Location: page.html\n", ["Content-Location: http://base.example/root/a.png\n"], "legacy Content-Base on the root", ["a.png", "http://base.example/root/a.png"])
related("mht-09-folded-location.eml", "Content-Location: http://x.example/\n", ["Content-Location: http://x.example/very/long/\n path/a.png\n"], "a Content-Location split over two lines", ["very/long/path/a.png", "very/long/ path/a.png"])
related("mht-10-file-urls.eml", "Content-Location: file:///C:/Users/me/page.htm\n", ["Content-Location: file:///C:/Users/me/page_files/a.png\n"], "file: URLs as saved by a browser", ["page_files/a.png", "file:///C:/Users/me/page_files/a.png", "file:///c:/Users/me/page_files/a.png"])
related("mht-11-base-argument.eml", "Content-Location: http://x.example/a/page.html\n", ["Content-Location: http://y.example/z/img.png\n", "Content-Location: http://x.example/a/img.png\n"], "references with an HTML base element", ["img.png\thttp://y.example/z/", "img.png", "img.png\t/z/"])
related("mht-12-cid-location.eml", "Content-Location: http://x.example/\n", ["Content-Location: cid:foo@bar.net\nContent-ID: <other@bar.net>\n"], "a Content-Location holding a cid: URL must not satisfy cid: (RFC 2557 8.3)", ["cid:foo@bar.net", "cid:other@bar.net"])
related("mht-13-rfc-example.eml", "", ["Content-ID: <97116092511xyz@foo.bar.net>\nContent-Location: fiction1/fiction2\n", "Content-ID: <97116092811xyz@foo.bar.net>\nContent-Location: fiction1/fiction3\n"], "RFC 2557 section 4.2 example", ["fiction1/fiction2", "cid:97116092811xyz@foo.bar.net", "fiction1/fiction3", "fiction1/fiction4"])

related("mht-19-related-location.eml", "", ["Content-Location: a.png\n", "Content-Location: sub/b.png\n"], "the multipart itself carries the base location", ["a.png", "sub/b.png", "./a.png", "sub/../a.png", "http://www.example.org/pages/a.png", "c.png"], top="Content-Location: http://www.example.org/pages/\n")
related("mht-20-related-base.eml", "", ["Content-Location: a.png\n"], "the multipart carries a legacy Content-Base", ["a.png", "http://www.example.org/base/a.png"], top="Content-Base: http://www.example.org/base/\n")

# a single-part archive, and related inside mixed
put("mht-14-single.eml", "From: <a@example.org>\nSubject: saved\nMIME-Version: 1.0\nContent-Type: text/html; charset=utf-8\nContent-Location: http://x.example/page.html\n\n<html>page</html>\n", "a lone HTML part", refs=["page.html", "other.html"])
put("mht-15-nested.eml", "From: <a@example.org>\nMIME-Version: 1.0\nContent-Type: multipart/mixed; boundary=\"mx\"\n\n--mx\nContent-Type: text/plain\n\nhi\n--mx\nContent-Type: multipart/related; boundary=\"rb\"\n\n--rb\nContent-Type: text/html\nContent-Location: http://x.example/p.html\n\n<html/>\n--rb\nContent-Type: image/png\nContent-Transfer-Encoding: base64\nContent-Location: http://x.example/a.png\n\n" + PNG + "\n--rb--\n--mx--\n", "related inside mixed", refs=["a.png", "p.html"])
put("mht-16-outer-resource.eml", "From: <a@example.org>\nMIME-Version: 1.0\nContent-Type: multipart/related; boundary=\"o\"\n\n--o\nContent-Type: multipart/related; boundary=\"i\"\n\n--i\nContent-Type: text/html\nContent-Location: http://x.example/p.html\n\n<html/>\n--i--\n--o\nContent-Type: image/png\nContent-Transfer-Encoding: base64\nContent-Location: http://x.example/a.png\n\n" + PNG + "\n--o--\n", "a related inside a related; the image lives in the outer one", refs=["a.png"])
put("mht-17-mid.eml", "Message-ID: <msg1@example.org>\nFrom: <a@example.org>\nMIME-Version: 1.0\nContent-Type: multipart/related; boundary=\"rb\"\n\n--rb\nContent-Type: text/html\n\n<html/>\n--rb\nContent-Type: image/png\nContent-Transfer-Encoding: base64\nContent-ID: <img1@example.org>\n\n" + PNG + "\n--rb--\n", "mid: references", refs=["mid:msg1@example.org/img1@example.org", "mid:other@example.org/img1@example.org", "mid:msg1@example.org", "mid:msg1%40example.org/img1%40example.org"])
put("mht-18-no-parts.eml", "From: <a@example.org>\nMIME-Version: 1.0\nContent-Type: multipart/related; boundary=\"rb\"\n\n--rb--\n", "a related with no parts", refs=["a.png"])

with open(os.path.join(out, "cases.tsv"), "w", encoding="utf-8", newline="\n") as f:
    f.write("# file\tdescription\n")
    for n, d in cases:
        f.write("%s\t%s\n" % (n, d))
print("wrote %d fixtures to %s" % (len(cases), out))
