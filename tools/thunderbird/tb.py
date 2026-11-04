"""Throwaway Thunderbird profile, launch and Marionette session, plus a local SMTP sink. Standard library only."""
import os, shutil, socketserver, subprocess, sys, threading, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from marionette import Marionette, wait_port

HOME = os.path.expanduser("~/onemail")
TB_DIR = os.environ.get("THUNDERBIRD_DIR", HOME + "/oracles/thunderbird/thunderbird")
SMTP_PORT = 2525


class SmtpSink(socketserver.ThreadingTCPServer):
    """Accepts any mail on 127.0.0.1 without TLS or authentication and keeps the DATA of every message."""
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, port=SMTP_PORT):
        self.messages = []
        super().__init__(("127.0.0.1", port), _SmtpHandler)
        threading.Thread(target=self.serve_forever, daemon=True).start()


class _SmtpHandler(socketserver.StreamRequestHandler):
    def reply(self, s):
        self.wfile.write(s.encode() + b"\r\n")

    def handle(self):
        self.reply("220 sink ESMTP")
        while True:
            line = self.rfile.readline()
            if not line:
                return
            cmd = line.strip().upper()
            if cmd.startswith(b"EHLO"):
                self.reply("250-sink")
                self.reply("250 8BITMIME")
            elif cmd.startswith(b"HELO") or cmd.startswith(b"MAIL") or cmd.startswith(b"RCPT") or cmd == b"RSET" or cmd == b"NOOP":
                self.reply("250 ok")
            elif cmd == b"DATA":
                self.reply("354 go")
                data = []
                while True:
                    l = self.rfile.readline()
                    if l == b".\r\n" or not l:
                        break
                    data.append(l[1:] if l.startswith(b"..") else l)
                self.server.messages.append(b"".join(data))
                self.reply("250 queued")
            elif cmd == b"QUIT":
                self.reply("221 bye")
                return
            else:
                self.reply("502 no")


USER_JS = """
user_pref("mail.provider.suppress_dialog_on_startup", true);
user_pref("mail.shell.checkDefaultClient", false);
user_pref("mail.winsearch.firstRunDone", true);
user_pref("mailnews.start_page.enabled", false);
user_pref("mail.rights.version", 1);
user_pref("app.update.enabled", false);
user_pref("app.update.auto", false);
user_pref("datareporting.policy.dataSubmissionEnabled", false);
user_pref("datareporting.healthreport.uploadEnabled", false);
user_pref("toolkit.telemetry.enabled", false);
user_pref("browser.shell.checkDefaultBrowser", false);
user_pref("mail.server.default.check_new_mail", false);
user_pref("mailnews.display.prefer_plaintext", true);
user_pref("mailnews.display.html_as", 1);
user_pref("mailnews.message_display.disable_remote_image", true);
user_pref("network.proxy.type", 0);
user_pref("mail.openpgp.fetch_pubkeys_from_gnupg", false);
user_pref("mail.accountmanager.accounts", "account1");
user_pref("mail.accountmanager.defaultaccount", "account1");
user_pref("mail.account.account1.server", "server1");
user_pref("mail.account.account1.identities", "id1");
user_pref("mail.server.server1.type", "pop3");
user_pref("mail.server.server1.hostname", "localhost");
user_pref("mail.server.server1.userName", "vector");
user_pref("mail.server.server1.name", "Vector");
user_pref("mail.server.server1.directory", "%(mail)s/localhost");
user_pref("mail.server.server1.directory-rel", "[ProfD]Mail/localhost");
user_pref("mail.server.server1.check_new_mail", false);
user_pref("mail.server.server1.login_at_startup", false);
user_pref("mail.server.server1.storeContractID", "@mozilla.org/msgstore/maildirstore;1");
user_pref("mail.identity.id1.useremail", "%(email)s");
user_pref("mail.identity.id1.fullName", "Vector");
user_pref("mail.identity.id1.smtpServer", "smtp1");
user_pref("mail.identity.id1.compose_html", false);
user_pref("mail.identity.id1.reply_on_top", 1);
user_pref("mail.smtpservers", "smtp1");
user_pref("mail.smtp.defaultserver", "smtp1");
user_pref("mail.smtpserver.smtp1.hostname", "127.0.0.1");
user_pref("mail.smtpserver.smtp1.port", %(smtp)d);
user_pref("mail.smtpserver.smtp1.try_ssl", 0);
user_pref("mail.smtpserver.smtp1.authMethod", 1);
user_pref("mail.smtpserver.smtp1.description", "sink");
%(extra)s
"""


class Thunderbird:
    def __init__(self, work, email, extra_prefs="", messages=None):
        self.work = work
        self.profile = os.path.join(work, "profile")
        shutil.rmtree(work, ignore_errors=True)
        os.makedirs(self.profile + "/Mail/localhost")
        with open(self.profile + "/user.js", "w") as f:
            f.write(USER_JS % {"mail": self.profile + "/Mail", "email": email, "smtp": SMTP_PORT, "extra": extra_prefs})
        if messages is not None:
            # A maildir store keeps every message as its own file, so the bytes Thunderbird reads are exactly the ones given.
            for sub in ("cur", "new", "tmp"):
                os.makedirs(self.profile + "/Mail/localhost/Inbox/" + sub)
            for name, raw in messages.items():
                with open(self.profile + "/Mail/localhost/Inbox/cur/" + name, "wb") as f:
                    f.write(raw)
        env = dict(os.environ, MOZ_HEADLESS="1", MOZ_REMOTE_ALLOW_SYSTEM_ACCESS="1")
        self.log = open(work + "/thunderbird.log", "w")
        self.proc = subprocess.Popen([TB_DIR + "/thunderbird", "--headless", "--marionette", "--profile", self.profile, "--no-remote"],
                                     env=env, stdout=self.log, stderr=subprocess.STDOUT)
        wait_port()
        time.sleep(4)
        self.m = Marionette()
        self.m.new_session()

    def script(self, js, **kw):
        return self.m.script(js, **kw)

    def script_alerts(self, js, seconds=60):
        """Runs `js` (a function body that returns a promise, or a value) in the background and waits for its result,
        answering any modal dialog Thunderbird raises meanwhile. Returns (value, [dialog texts])."""
        self.script("globalThis.__done = false; globalThis.__r = null; "
                    "Promise.resolve((async () => { %s })()).then(v => { globalThis.__r = v; globalThis.__done = true; }, "
                    "e => { globalThis.__r = 'ERROR ' + e; globalThis.__done = true; }); return 1;" % js)
        alerts = []
        end = time.time() + seconds
        while time.time() < end:
            try:
                if self.script("return globalThis.__done;"):
                    return self.script("return globalThis.__r;"), alerts
            except RuntimeError as ex:
                if "Unexpected" not in str(ex):
                    raise
            try:
                alerts.append(self.m.call("WebDriver:GetAlertText")["value"])
                self.m.call("WebDriver:AcceptAlert")
            except RuntimeError:
                pass
            time.sleep(0.3)
        raise TimeoutError("script did not finish: " + js[:80])

    def drain_alerts(self):
        """Answers a pending modal dialog, if there is one, and returns its text."""
        try:
            text = self.m.call("WebDriver:GetAlertText")["value"]
            self.m.call("WebDriver:AcceptAlert")
            return text
        except RuntimeError:
            return None

    def close(self):
        try:
            self.m.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=15)
        except Exception:
            self.proc.kill()
        self.log.close()
