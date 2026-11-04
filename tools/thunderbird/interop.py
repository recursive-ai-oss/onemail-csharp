#!/usr/bin/env python3
"""Two-way PGP/MIME interop between OneMail and Thunderbird (its built-in OpenPGP), driven as a released program.

  interop.py ours   VECTORS_DIR OUT   Thunderbird opens each message in VECTORS_DIR/<origin>/<key>/*.eml; its status is read back
  interop.py theirs VECTORS_DIR OUT   Thunderbird writes signed / encrypted / signed+encrypted messages with each key in
                                      VECTORS_DIR/ours/<key>/key.sec.asc; they go to a local SMTP sink and into OUT/<key>/*.eml

Lines "RESULT <mode> <origin> <key> <kind> ok|FAIL detail" are printed. Screenshots of the message window go to OUT/shots.
Environment: THUNDERBIRD_DIR (the unpacked release, default ~/onemail/oracles/thunderbird/thunderbird), WORK (scratch).
Thunderbird is controlled through Marionette, with the OpenPGP key import and the message-security state read from the
running application; nothing of Thunderbird's source was read.
"""
import json, os, re, shutil, subprocess, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tb import Thunderbird, SmtpSink

WORK = os.environ.get("WORK", os.path.expanduser("~/onemail/work-u58"))

# Thunderbird's own names for the message states (the constants it exposes in the message window).
SIG = {0: "none", 1: "valid-self", 2: "valid-verified", 3: "valid-unverified", 4: "uncertain-key-unavailable",
       5: "uncertain-uid-mismatch", 6: "uncertain-key-not-accepted", 7: "invalid", 8: "invalid-key-rejected", 9: "invalid-date-mismatch"}
ENC = {0: "none", 1: "ok", 2: "failure", 3: "no-secret-key"}

KINDS = ("signed", "encrypted", "signed-encrypted", "sign-then-encrypt")


def read_email(raw):
    return re.search(rb"^To: .*?<?([^<>\s,]+@[^<>\s,]+)>?\s*$", raw, re.M).group(1).decode()


def import_secret(tb, keyfile):
    return tb.script_alerts("""const {EnigmailKeyRing} = ChromeUtils.importESModule("chrome://openpgp/content/modules/keyRing.sys.mjs");
        const {FileUtils} = ChromeUtils.importESModule("resource://gre/modules/FileUtils.sys.mjs");
        const w = Services.wm.getMostRecentWindow("mail:3pane");
        await EnigmailKeyRing.importSecKeyFromFile(w, () => "", true, new FileUtils.File(arguments[0]));
        // As a user does with "Use as personal key": the key's own address is then accepted.
        const {PgpSqliteDb2} = ChromeUtils.importESModule("chrome://openpgp/content/modules/sqliteDb.sys.mjs");
        for (const k of EnigmailKeyRing.getAllKeys().keyList.filter(k => k.secretAvailable)) await PgpSqliteDb2.acceptAsPersonalKey(k.fpr);
        return EnigmailKeyRing.getAllKeys().keyList.filter(k => k.secretAvailable).map(k => k.keyId + ' ' + k.keyTrust);""".replace("arguments[0]", json.dumps(os.path.abspath(keyfile))))


def prepare_inbox(tb, count):
    """Waits until Thunderbird has indexed the maildir Inbox (it rebuilds the folder summary on first use)."""
    return tb.script("""const f = MailServices.accounts.allServers[0].rootFolder.getChildNamed('Inbox');
        let state = '';
        for (let t = 0; t < 80; t++) {
          try {
            f.updateFolder(null);
            if (f.getTotalMessages(false) >= %d && [...f.messages].length >= %d) return 'ready ' + t;
          } catch (e) {
            state = String(e);
            try { f.getDatabaseWithReparse(null, null); } catch (e2) { state += ' / ' + e2; }
          }
          await new Promise(r => setTimeout(r, 500));
        }
        return 'not ready ' + state + ' ' + f.getTotalMessages(false);""" % (count, count))


def observe(tb, shot_dir, label, kinds):
    """Opens each message of the Inbox in turn and returns what the message pane says about its security."""
    print("INBOX", prepare_inbox(tb, len(kinds)), flush=True)
    out = {}
    for kind in kinds:
        r = tb.script("""const f = MailServices.accounts.allServers[0].rootFolder.getChildNamed('Inbox');
            const {MailUtils} = ChromeUtils.importESModule("resource:///modules/MailUtils.sys.mjs");
            const w = Services.wm.getMostRecentWindow("mail:3pane");
            const h = [...f.messages].find(m => m.storeToken == '%s');
            MailUtils.displayMessageInFolderTab(h);
            let am, e;
            for (let t = 0; t < 60; t++) {
              await new Promise(r => setTimeout(r, 250));
              am = w.document.getElementById("tabmail").currentAboutMessage;
              e = am && am.Enigmail && am.Enigmail.hdrView;
              if (e && am.gMessage && am.gMessage.messageKey == h.messageKey && t >= 12) break;
            }
            await new Promise(r => setTimeout(r, 1500));
            if (!am.gMessage || am.gMessage.messageKey != h.messageKey) return {sig: -1, enc: -1, body: 'message pane shows another message'};
            const body = am.document.getElementById("messagepane").contentDocument.body.innerText;
            return {sig: e.msgSignatureState, enc: e.msgEncryptionState, sigText: e.msgSignedStateString, encText: e.msgEncryptedStateString,
                    keyId: e.msgSignatureKeyId, body: body};""" % kind)
        os.makedirs(shot_dir, exist_ok=True)
        try:
            tb.m.screenshot("%s/%s-%s.png" % (shot_dir, label, kind))
        except Exception as ex:
            r["shot"] = "failed: %s" % ex
        out[kind] = r
    return out


def ours(vectors, out):
    fails = n = unsupported = explained_n = 0
    for origin in sorted(os.listdir(vectors)):
        if origin == "thunderbird" or (os.environ.get("ORIGINS") and origin not in os.environ["ORIGINS"].split(",")):
            continue
        for key in sorted(os.listdir(os.path.join(vectors, origin))):
            d = os.path.join(vectors, origin, key)
            if not os.path.exists(d + "/key.sec.asc"):
                continue
            kinds = [k for k in KINDS if os.path.exists("%s/%s.eml" % (d, k))]
            emls = [open("%s/%s.eml" % (d, k), "rb").read() for k in kinds]
            expected = open(d + "/expected.txt", encoding="utf-8", newline="").read().strip().replace("\r\n", "\n")
            tb = Thunderbird(os.path.join(WORK, "p-ours"), read_email(emls[0]), messages=dict(zip(kinds, emls)))
            try:
                imp, alerts = import_secret(tb, d + "/key.sec.asc")
                print("IMPORT %s %s %s %s" % (origin, key, imp, alerts), flush=True)
                if isinstance(imp, str) and imp.startswith("ERROR"):
                    # Thunderbird refused the key itself, so no message can be read: reported apart from failures.
                    for kind in kinds:
                        print("RESULT ours %s %s %s UNSUPPORTED key import refused: %s" % (origin, key, kind, imp[:120]), flush=True)
                    unsupported += len(kinds)
                    continue
                time.sleep(1)
                res = observe(tb, out + "/shots", "%s-%s" % (origin, key), kinds)
            finally:
                tb.close()
            for kind in kinds:
                r = res[kind]
                want_sig = kind != "encrypted"
                want_enc = kind != "signed"
                sig_ok = (r["sig"] in (1, 2, 3)) if want_sig else r["sig"] == 0
                enc_ok = (r["enc"] == 1) if want_enc else r["enc"] == 0
                body_ok = r["body"].replace("\r\n", "\n").strip().startswith(expected.split("\n")[0])
                ok = sig_ok and enc_ok and body_ok
                # Sequoia's messages for these keys use version 6 public-key encrypted session keys and version 2 encrypted data
                # (RFC 9580; GnuPG lists them as "unknown version"). Thunderbird 159 cannot decrypt them and shows an empty body.
                rfc9580_message = origin.startswith("sequoia") and kind != "signed" and r["enc"] == 0
                explained = (not ok) and rfc9580_message
                n += 1
                if explained:
                    explained_n += 1
                else:
                    fails += 0 if ok else 1
                print("RESULT ours %s %s %s %s sig=%s enc=%s body=%s%s" % (origin, key, kind, "ok" if ok else ("EXPLAINED" if explained else "FAIL"),
                      SIG.get(r["sig"]), ENC.get(r["enc"]), "ok" if body_ok else "WRONG", "" if ok else " sigText=%s" % r["sigText"]), flush=True)
    print("TOTAL ours %d messages read, %d failures, %d explained (RFC 9580 encrypted packets), %d on keys Thunderbird refuses" % (n, fails, explained_n, unsupported))
    return fails


def correspondent(vectors, out):
    """Our signed.eml from a sender whose public key is imported with each acceptance level, no secret key: how Thunderbird
    treats a correspondent. Not accepted is what a fresh import gives; 'unverified' is "Accept this key for this address"."""
    fails = n = 0
    for key in sorted(os.listdir(os.path.join(vectors, "ours"))):
        d = os.path.join(vectors, "ours", key)
        raw = open(d + "/signed.eml", "rb").read()
        email = read_email(raw)
        for level, want in (("none", (4, 5, 6)), ("unverified", (3,))):
            tb = Thunderbird(os.path.join(WORK, "p-corr"), email, messages={"signed": raw})
            try:
                imp = tb.script_alerts("""const {EnigmailKeyRing} = ChromeUtils.importESModule("chrome://openpgp/content/modules/keyRing.sys.mjs");
                    const {PgpSqliteDb2} = ChromeUtils.importESModule("chrome://openpgp/content/modules/sqliteDb.sys.mjs");
                    const w = Services.wm.getMostRecentWindow("mail:3pane");
                    const r = await EnigmailKeyRing.importKeyDataSilent(w, %s, false);
                    const k = EnigmailKeyRing.getAllKeys().keyList[0];
                    if (!k) return 'no key';
                    if (%s) await PgpSqliteDb2.updateAcceptance(k.fpr, [%s], "unverified");
                    return k.keyId + ' ' + JSON.stringify(await PgpSqliteDb2.getFingerprintAcceptance(null, k.fpr));""" % (
                        json.dumps(open(d + "/key.pub.asc").read()), "true" if level == "unverified" else "false", json.dumps(email)))
                if imp[0] == "no key":
                    res = None
                else:
                    time.sleep(1)
                    res = observe(tb, out + "/shots", "corr-%s-%s" % (key, level), ["signed"])["signed"]
            finally:
                tb.close()
            if imp[0] == "no key":
                print("RESULT correspondent ours %s signed acceptance=%s UNSUPPORTED public key import refused" % (key, level), flush=True)
                continue
            ok = res["sig"] in want and res["enc"] == 0
            n += 1
            fails += 0 if ok else 1
            print("RESULT correspondent ours %s signed acceptance=%s %s sig=%s (%s)" % (key, level, "ok" if ok else "FAIL", SIG.get(res["sig"]), imp[0]), flush=True)
    print("TOTAL correspondent %d messages, %d failures" % (n, fails))
    return fails


def whitespace(vectors, out):
    """The signed text of a multipart/signed message with trailing spaces and a line starting "From ", signed by GnuPG
    from the committed GnuPG vector key, to see which of them Thunderbird refuses (the committed GnuPG vectors have both)."""
    gpg = os.environ["GPG"]
    d = os.path.join(vectors, "gnupg-2.4.4", "rsa3072")
    email = "vector@example.org"
    home = os.path.join(WORK, "gpghome")
    shutil.rmtree(home, ignore_errors=True)
    os.makedirs(home, mode=0o700)
    env = dict(os.environ, GNUPGHOME=home)
    subprocess.run([gpg, "--batch", "--import", d + "/key.sec.asc"], env=env, capture_output=True, check=True)
    variants = {
        "plain": "Hello there.\r\nSecond line.\r\n",
        "trailing-space": "Hello there.   \r\nSecond line.\r\n",
        "from-line": "Hello there.\r\nFrom the other side.\r\n",
        "both": "Hello there.   \r\nFrom the other side.\r\n",
        "bare-lf": "Hello there=\nagain.\r\nSecond line.\r\n",
    }
    emls = {}
    for name, text in variants.items():
        for cte in ("7bit", "quoted-printable"):
            body = text
            if cte == "quoted-printable":
                body = text.replace("   \r\n", "  =20\r\n")
            part = ("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: %s\r\n\r\n%s" % (cte, body)).encode()
            sig = subprocess.run([gpg, "--batch", "--no-tty", "--pinentry-mode", "loopback", "--passphrase", "", "--detach-sign", "--armor", "--digest-algo", "SHA256"],
                                 input=part, env=env, capture_output=True, check=True).stdout.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
            msg = (b"From: Vector <%s>\r\nTo: %s\r\nSubject: ws\r\nMIME-Version: 1.0\r\n" % (email.encode(), email.encode())
                   + b'Content-Type: multipart/signed; protocol="application/pgp-signature"; micalg=pgp-sha256; boundary="bnd"\r\n\r\n'
                   + b"--bnd\r\n" + part + b"\r\n--bnd\r\nContent-Type: application/pgp-signature; name=\"signature.asc\"\r\n\r\n" + sig + b"--bnd--\r\n")
            emls["%s-%s" % (name, "qp" if cte != "7bit" else "7bit")] = msg
    tb = Thunderbird(os.path.join(WORK, "p-ws"), email, messages=emls)
    try:
        import_secret(tb, d + "/key.sec.asc")
        time.sleep(1)
        res = observe(tb, out + "/shots", "ws", list(emls))
    finally:
        tb.close()
    fails = 0
    for k, r in res.items():
        # A bare LF inside the signed text is not what the sender signed once the line endings are made CRLF.
        ok = (r["sig"] == 7) if k.startswith("bare-lf") else (r["sig"] == 1)
        fails += 0 if ok else 1
        print("RESULT whitespace gnupg-2.4.4 rsa3072 %s %s sig=%s" % (k, "ok" if ok else "FAIL", SIG.get(r["sig"])), flush=True)
    return fails


BODY = ("Interop test caf\u00e9.\r\n"
        "A line that is quite long enough to need some wrapping when it is encoded, as long lines are.   \r\n"
        "From the other side.\r\n")

MODES = (("signed", True, False), ("encrypted", False, True), ("signed-encrypted", True, True))

# Plain text, no format=flowed and no wrapping, so the decoded text can be compared with what was typed.
THEIRS_PREFS = """
user_pref("mailnews.send_plaintext_flowed", false);
user_pref("mailnews.wraplength", 0);
user_pref("mail.identity.id1.openpgp_key_id", "%s");
"""


def compose_and_send(tb, email, sign, encrypt):
    return tb.script_alerts("""
        const params = Cc["@mozilla.org/messengercompose/composeparams;1"].createInstance(Ci.nsIMsgComposeParams);
        const fields = Cc["@mozilla.org/messengercompose/composefields;1"].createInstance(Ci.nsIMsgCompFields);
        fields.to = %s; fields.subject = 'Interop vector'; fields.body = %s;
        params.type = Ci.nsIMsgCompType.New; params.format = Ci.nsIMsgCompFormat.PlainText;
        params.composeFields = fields; params.identity = MailServices.accounts.allIdentities[0];
        MailServices.compose.OpenComposeWindowWithParams(null, params);
        let w = null;
        for (let t = 0; t < 80 && !w; t++) { await new Promise(r => setTimeout(r, 250)); w = Services.wm.getMostRecentWindow("msgcompose"); }
        await new Promise(r => setTimeout(r, 3000));
        w.gSelectedTechnologyIsPGP = true;
        const state = {signed: w.gSendSigned, encrypted: w.gSendEncrypted};
        // Turning encryption on also turns signing on, so signing is settled last.
        if (w.gSendEncrypted != %s) w.toggleEncryptMessage();
        await new Promise(r => setTimeout(r, 500));
        if (w.gSendSigned != %s) w.toggleGlobalSignMessage();
        await new Promise(r => setTimeout(r, 1000));
        const set = {signed: w.gSendSigned, encrypted: w.gSendEncrypted, pgp: w.gSelectedTechnologyIsPGP};
        w.SendMessage();
        return JSON.stringify({before: state, set: set});""" % (json.dumps(email), json.dumps(BODY.replace("\r\n", "\n")),
                                                         "true" if encrypt else "false", "true" if sign else "false"), seconds=30)


def theirs(vectors, out):
    fails = n = unsupported = 0
    sink = SmtpSink()
    for key in sorted(os.listdir(os.path.join(vectors, "ours"))):
        d = os.path.join(vectors, "ours", key)
        email = "vector-%s@example.org" % key

        tb = Thunderbird(os.path.join(WORK, "p-theirs"), email, extra_prefs="")
        try:
            imp, alerts = import_secret(tb, d + "/key.sec.asc")
            print("IMPORT theirs %s %s %s" % (key, imp, alerts), flush=True)
            if isinstance(imp, str) and imp.startswith("ERROR"):
                for kind, _, _ in MODES:
                    print("RESULT theirs thunderbird %s %s UNSUPPORTED key import refused: %s" % (key, kind, imp[:120]), flush=True)
                unsupported += len(MODES)
                continue
            keyid = imp[0].split()[0]
            tb.script("Services.prefs.setStringPref('mail.identity.id1.openpgp_key_id', %s); Services.prefs.setBoolPref('mailnews.send_plaintext_flowed', false); Services.prefs.setIntPref('mailnews.wraplength', 0); return 1;" % json.dumps(keyid))
            dest = os.path.join(out, "thunderbird", key)
            os.makedirs(dest, exist_ok=True)
            for kind, sign, enc in MODES:
                before = len(sink.messages)
                r, al = compose_and_send(tb, email, sign, enc)
                end = time.time() + 30
                while len(sink.messages) == before and time.time() < end:
                    a = tb.drain_alerts()
                    if a:
                        al.append(a)
                    time.sleep(0.3)
                n += 1
                if len(sink.messages) == before:
                    fails += 1
                    print("RESULT theirs thunderbird %s %s FAIL nothing was sent: %s %s" % (key, kind, r, al), flush=True)
                    continue
                raw = sink.messages[-1]
                open("%s/%s.eml" % (dest, kind), "wb").write(raw)
                head = raw.split(b"\r\n\r\n", 1)[0].decode("latin-1")
                ct = re.search(r"^Content-Type:\s*([^;\r\n]+)", head, re.M | re.I)
                ct = ct.group(1).strip().lower() if ct else "?"
                want = {"signed": "multipart/signed", "encrypted": "multipart/encrypted", "signed-encrypted": "multipart/encrypted"}[kind]
                ok = ct == want
                fails += 0 if ok else 1
                print("RESULT theirs thunderbird %s %s %s content-type=%s state=%s dialogs=%s" % (key, kind, "ok" if ok else "FAIL", ct, r, al), flush=True)
            shutil.copy(d + "/key.sec.asc", dest + "/key.sec.asc")
            shutil.copy(d + "/key.pub.asc", dest + "/key.pub.asc")
            open(dest + "/expected.txt", "w", encoding="utf-8", newline="").write(BODY)
            open(dest + "/info.txt", "w").write("Thunderbird %s\n" % tb.script("return Services.appinfo.version;"))
        finally:
            tb.close()
    print("TOTAL theirs %d messages written, %d failures, %d on keys Thunderbird refuses" % (n, fails, unsupported))
    return fails


if __name__ == "__main__":
    mode, vectors, out = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out, exist_ok=True)
    sys.exit(1 if {"ours": ours, "theirs": theirs, "correspondent": correspondent, "whitespace": whitespace}[mode](vectors, out) else 0)
