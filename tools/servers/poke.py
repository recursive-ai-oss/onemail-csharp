#!/usr/bin/env python3
"""Quick liveness probe for the servers in the harness (Python's own client libraries; nothing of ours).

poke.py imap|imaps|pop3|pop3s|smtp|smtps PORT USER PASSWORD
"""
import imaplib, poplib, smtplib, ssl, sys

kind, port, user, password = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
if kind.startswith("imap"):
    c = imaplib.IMAP4_SSL("127.0.0.1", port, ssl_context=ctx) if kind == "imaps" else imaplib.IMAP4("127.0.0.1", port)
    if kind == "imap":
        c.starttls(ctx)
    print("capability:", c.capability()[1])
    print("login:", c.login(user, password))
    print("select:", c.select("INBOX"))
    c.logout()
elif kind.startswith("pop3"):
    c = poplib.POP3_SSL("127.0.0.1", port, context=ctx) if kind == "pop3s" else poplib.POP3("127.0.0.1", port)
    if kind == "pop3":
        c.stls(ctx)
    print("capa:", c.capa())
    c.user(user); print("pass:", c.pass_(password)); print("stat:", c.stat()); c.quit()
else:
    c = smtplib.SMTP_SSL("127.0.0.1", port, context=ctx) if kind == "smtps" else smtplib.SMTP("127.0.0.1", port)
    c.ehlo()
    if kind == "smtp":
        c.starttls(context=ctx); c.ehlo()
    print("esmtp:", sorted(c.esmtp_features.items()))
    print("login:", c.login(user, password))
    c.quit()
