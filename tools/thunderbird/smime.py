#!/usr/bin/env python3
"""Two-way S/MIME interop between OneMail and Thunderbird (its NSS-based S/MIME), driven as a released program.

  smime.py ours      VECTORS OUT   Thunderbird opens each message in VECTORS/ours/<key>/*.eml with the key's certificate and the test CA
                                   imported (the CA trusted for email); the status is read from the message pane
  smime.py untrusted VECTORS OUT   the same signed message with the CA imported but not trusted for email: a control that shows the
                                   status is not given for free
  smime.py theirs    VECTORS OUT   Thunderbird signs, encrypts and signs+encrypts to the key's own address; the mail goes to a local
                                   SMTP sink and into OUT/thunderbird/<key>/*.eml

VECTORS is made by `SmimeInterop thunderbird <dir>` (tools/smime). Lines "RESULT <mode> <origin> <key> <kind> ok|FAIL|UNSUPPORTED detail"
and a TOTAL line are printed; screenshots go to OUT/shots. Certificates and keys reach Thunderbird through its certificate database
interface (nsIX509CertDB: addCertFromBase64 with the trust string for the CA, importPKCS12File for the user's PKCS #12 file), the
way its certificate manager does. Environment: THUNDERBIRD_DIR, WORK. Nothing of Thunderbird's source was read.
"""
import base64, json, os, re, shutil, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tb import Thunderbird, SmtpSink
from interop import prepare_inbox

WORK = os.environ.get("WORK", os.path.expanduser("~/onemail/work-u63"))
KINDS = ("signed", "signed-opaque", "encrypted", "signed-encrypted", "signed-in-wrapper")
WANT = {  # kind -> (signature icon shown, encryption icon shown)
    "signed": (True, False), "signed-opaque": (True, False), "encrypted": (False, True),
    "signed-encrypted": (True, True), "signed-in-wrapper": (False, False),
}


def import_certs(tb, d, trust_ca=True):
    """Imports the CA (trusted for email unless trust_ca is false) and the user's certificate with its key; returns Thunderbird's report."""
    pem = open(d + "/root.pem").read()
    b64 = "".join(l for l in pem.splitlines() if not l.startswith("-----"))
    # The trust string is ssl,email,object-signing; "C" is a trusted CA. The calls block the main thread, so they run from a timer.
    return tb.script_alerts("""
      const db = Cc["@mozilla.org/security/x509certdb;1"].getService(Ci.nsIX509CertDB);
      const {FileUtils} = ChromeUtils.importESModule("resource://gre/modules/FileUtils.sys.mjs");
      await new Promise(r => setTimeout(r, 100));
      const out = {};
      try { db.addCertFromBase64(%s, %s); out.ca = 'added'; } catch (e) { out.ca = 'ERR ' + e; }
      try { out.p12 = db.importPKCS12File(new FileUtils.File(%s), 'pw'); } catch (e) { out.p12 = 'ERR ' + e; }
      const L = await db.getCerts(); out.certs = Array.from(L).filter(c => c.certType == 2 || (c.subjectName||'').includes('Vector')).map(c => c.displayName + ' | ' + c.emailAddress + ' | ' + c.dbKey + ' | ' + c.nickname);
      return out;""" % (json.dumps(b64), json.dumps("C,C,C" if trust_ca else "c,c,c"), json.dumps(os.path.abspath(d + "/key.p12"))), seconds=60)


def observe(tb, shot_dir, label, kinds):
    """Opens each message of the Inbox in turn and returns what the message pane says about its S/MIME state."""
    print("INBOX", prepare_inbox(tb, len(kinds)), flush=True)
    out = {}
    for kind in kinds:
        r = tb.script("""const f = MailServices.accounts.allServers[0].rootFolder.getChildNamed('Inbox');
            const {MailUtils} = ChromeUtils.importESModule("resource:///modules/MailUtils.sys.mjs");
            const w = Services.wm.getMostRecentWindow("mail:3pane");
            const h = [...f.messages].find(m => m.storeToken == '%s');
            MailUtils.displayMessageInFolderTab(h);
            let am;
            for (let t = 0; t < 60; t++) {
              await new Promise(r => setTimeout(r, 250));
              am = w.document.getElementById("tabmail").currentAboutMessage;
              if (am && am.gMessage && am.gMessage.messageKey == h.messageKey && t >= 12) break;
            }
            await new Promise(r => setTimeout(r, 2500));
            if (!am.gMessage || am.gMessage.messageKey != h.messageKey) return {error: 'message pane shows another message'};
            const o = {};
            for (const id of ['signedHdrIcon', 'encryptedHdrIcon']) {
              const el = am.document.getElementById(id);
              o[id] = el.hidden ? null : (el.getAttribute('src') || '').split('/').pop() + ' / ' + el.getAttribute('alt');
            }
            o.tech = am.document.getElementById('cryptoBox').hidden ? null : am.document.getElementById('cryptoBox').getAttribute('tech');
            o.sigStatus = am.gSignatureStatus; o.encStatus = am.gEncryptionStatus; o.reason = am.gSignatureFailureReason;
            o.signer = am.gSignerCert ? am.gSignerCert.emailAddress : null;
            o.alg = am.gSignatureAlgorithm === undefined ? null : String(am.gSignatureAlgorithm);
            // The pane's document appears a little after the header; wait for it.
            for (let t = 0; t < 40; t++) {
              const doc = am.document.getElementById("messagepane").contentDocument;
              if (doc && doc.body && doc.body.innerText) { o.body = doc.body.innerText; break; }
              await new Promise(r => setTimeout(r, 250));
            }
            o.body = o.body || '';
            return o;""" % kind)
        os.makedirs(shot_dir, exist_ok=True)
        try:
            tb.m.screenshot("%s/%s-%s.png" % (shot_dir, label, kind))
        except Exception as ex:
            r["shot"] = "failed: %s" % ex
        out[kind] = r
    return out


def read_email(raw):
    return re.search(rb"^To: .*?<?([^<>\s,]+@[^<>\s,]+)>?\s*$", raw, re.M).group(1).decode()


def run_ours(vectors, out, control):
    fails = n = 0
    root = os.path.join(vectors, "ours")
    for key in sorted(os.listdir(root)):
        d = os.path.join(root, key)
        kinds = ["signed"] if control else [k for k in KINDS if os.path.exists("%s/%s.eml" % (d, k))]
        emls = {k: open("%s/%s.eml" % (d, k), "rb").read() for k in kinds}
        expected = open(d + "/expected.txt", encoding="utf-8", newline="").read().strip().replace("\r\n", "\n")
        tb = Thunderbird(os.path.join(WORK, "p-ours"), read_email(emls[kinds[0]]), messages=emls)
        try:
            imp, alerts = import_certs(tb, d, trust_ca=not control)
            print("IMPORT %s %s" % (key, json.dumps(imp)), flush=True)
            if alerts:
                print("DIALOGS", alerts, flush=True)
            time.sleep(1)
            res = observe(tb, out + "/shots", ("untrusted-" if control else "") + key, kinds)
        finally:
            tb.close()
        for kind in kinds:
            r = res[kind]
            n += 1
            if "error" in r:
                fails += 1
                print("RESULT %s ours %s %s FAIL %s" % ("untrusted" if control else "ours", key, kind, r["error"]), flush=True)
                continue
            want_sig, want_enc = WANT[kind]
            sig = r["signedHdrIcon"]; enc = r["encryptedHdrIcon"]
            body_ok = r["body"].replace("\r\n", "\n").strip().startswith(expected.split("\n")[0])
            if control:
                # With the CA not trusted for email the signature must not be shown as good.
                ok = not (sig and "signed-ok" in sig)
            else:
                sig_ok = bool(sig and "signed-ok" in sig) if want_sig else sig is None
                enc_ok = bool(enc and "encrypted-ok" in enc) if want_enc else enc is None
                ok = sig_ok and enc_ok and body_ok and (not want_sig or r["signer"] == read_email(emls[kind]))
            fails += 0 if ok else 1
            print("RESULT %s ours %s %s %s sig=%s enc=%s sigStatus=%s encStatus=%s signer=%s body=%s%s" % (
                "untrusted" if control else "ours", key, kind, "ok" if ok else "FAIL", sig, enc, r["sigStatus"], r["encStatus"], r["signer"],
                "ok" if body_ok else "WRONG", "" if ok else " reason=%s" % r["reason"]), flush=True)
    print("TOTAL %s %d messages read, %d failures" % ("untrusted" if control else "ours", n, fails))
    return fails


MODES = (("signed", True, False), ("encrypted", False, True), ("signed-encrypted", True, True))
BODY = "S/MIME interop vector.\r\ncaf\u00e9 line two, \u00fcber line three.\r\n"


def compose_and_send(tb, email, sign, encrypt, sign_nick, enc_nick):
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
        w.gSelectedTechnologyIsPGP = false;
        const state = {signed: w.gSendSigned, encrypted: w.gSendEncrypted};
        if (w.gSendEncrypted != %s) w.toggleEncryptMessage();
        await new Promise(r => setTimeout(r, 500));
        if (w.gSendSigned != %s) w.toggleGlobalSignMessage();
        await new Promise(r => setTimeout(r, 1000));
        const set = {signed: w.gSendSigned, encrypted: w.gSendEncrypted, pgp: w.gSelectedTechnologyIsPGP};
        w.SendMessage();
        return JSON.stringify({before: state, set: set});""" % (json.dumps(email), json.dumps(BODY.replace("\r\n", "\n")),
                                                         "true" if encrypt else "false", "true" if sign else "false"), seconds=40)


def theirs(vectors, out):
    fails = n = unsupported = 0
    sink = SmtpSink()
    for key in sorted(os.listdir(os.path.join(vectors, "ours"))):
        if os.environ.get("KEYS") and key not in os.environ["KEYS"].split(","):
            continue
        d = os.path.join(vectors, "ours", key)
        email = "vector-%s@example.org" % key
        tb = Thunderbird(os.path.join(WORK, "p-theirs"), email)
        try:
            imp, alerts = import_certs(tb, d)
            print("IMPORT theirs %s %s" % (key, json.dumps(imp)), flush=True)
            found = next((c.split(" | ") for c in imp["certs"] if email in c), None) if isinstance(imp, dict) else None
            nick = found[0] if found else None
            if nick is None:
                for kind, _, _ in MODES:
                    print("RESULT theirs thunderbird %s %s UNSUPPORTED certificate import refused: %s" % (key, kind, imp.get("p12")), flush=True)
                unsupported += len(MODES)
                continue
            tb.script("""const p = Services.prefs, id = 'mail.identity.id1.';
                p.setStringPref(id + 'signing_cert_name', %s); p.setStringPref(id + 'encryption_cert_name', %s);
                p.setStringPref(id + 'signing_cert_dbkey', %s); p.setStringPref(id + 'encryption_cert_dbkey', %s);
                p.setBoolPref('mailnews.send_plaintext_flowed', false); p.setIntPref('mailnews.wraplength', 0); return 1;""" % (json.dumps(nick), json.dumps(nick), json.dumps(found[2]), json.dumps(found[2])))
            dest = os.path.join(out, "thunderbird", key)
            os.makedirs(dest, exist_ok=True)
            for kind, sign, enc in MODES:
                before = len(sink.messages)
                r, al = compose_and_send(tb, email, sign, enc, nick, nick)
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
                    try:
                        print("DEBUG", tb.script("""const o = []; for (const w of Services.wm.getEnumerator(null)) { const dl = w.document.querySelectorAll('dialog, .dialogStack browser, dialog[open]'); o.push(w.document.URL + ' ' + (w.document.title||'') + ' dialogs=' + dl.length + (w.gSendSigned !== undefined ? ' S=' + w.gSendSigned + ' E=' + w.gSendEncrypted : '') + ' text=' + ((w.document.getElementById('infoBody')||{}).textContent||'') + ' ' + ((w.document.getElementById('infoTitle')||{}).textContent||'')); } return o;"""), flush=True)
                        tb.m.screenshot(out + "/fail-%s-%s.png" % (key, kind))
                        pass
                    except Exception as ex:
                        print("DEBUG failed", ex, flush=True)
                    continue
                raw = sink.messages[-1]
                open("%s/%s.eml" % (dest, kind), "wb").write(raw)
                head = raw.split(b"\r\n\r\n", 1)[0].decode("latin-1")
                ct = re.search(r"^Content-Type:\s*([^\r\n]+(?:\r\n[ \t][^\r\n]+)*)", head, re.M | re.I)
                ct = re.sub(r"\s+", " ", ct.group(1)).strip().lower() if ct else "?"
                want = {"signed": "multipart/signed", "encrypted": "application/pkcs7-mime", "signed-encrypted": "application/pkcs7-mime"}[kind]
                ok = ct.startswith(want)
                fails += 0 if ok else 1
                print("RESULT theirs thunderbird %s %s %s content-type=%s state=%s dialogs=%s" % (key, kind, "ok" if ok else "FAIL", ct, r, al), flush=True)
            for f in ("cert.pem", "root.pem", "key.p12", "expected.txt"):
                shutil.copy(d + "/" + f, dest + "/" + f)
            open(dest + "/info.txt", "w").write("Thunderbird %s\n" % tb.script("return Services.appinfo.version;"))
        finally:
            tb.close()
    print("TOTAL theirs %d messages written, %d failures, %d on keys Thunderbird refuses" % (n, fails, unsupported))
    return fails


if __name__ == "__main__":
    mode, vectors, out = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out, exist_ok=True)
    f = {"ours": lambda v, o: run_ours(v, o, False), "untrusted": lambda v, o: run_ours(v, o, True), "theirs": theirs}[mode](vectors, out)
    sys.exit(1 if f else 0)
