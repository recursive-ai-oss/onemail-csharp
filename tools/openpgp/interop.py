#!/usr/bin/env python3
"""Two-way PGP/MIME interop between OneMail and other OpenPGP programs, run as black boxes.

  interop.py ours   VECTORS_DIR TOOL...   check the messages OneMail wrote (VECTORS_DIR/ours/*) with each tool
  interop.py theirs OUT_DIR     TOOL...   make keys and PGP/MIME messages with each tool (OUT_DIR/<tool>/<keytype>/)

A TOOL is gpg:<path to gpg>, or sop:<path to a SOP implementation such as sqop>. The MIME parts are cut out of the .eml
files by a splitter written here (not by OneMail), so the bytes a tool is asked to verify are the bytes on the wire.
Only the Python standard library is used. Results are printed as lines "RESULT <tool> <keytype> <kind> ok|FAIL detail".
"""
import os, re, subprocess, sys, shutil, tempfile, quopri, email, email.policy

BODY = ("Interop test caf\u00e9.\r\n"
        "A line that is quite long enough to need some wrapping when it is encoded, as long lines are.   \r\n"
        "From the other side.\r\n")


def run(cmd, data=None, env=None, check=False):
    p = subprocess.run(cmd, input=data, capture_output=True, env=env)
    if check and p.returncode != 0:
        raise RuntimeError("%s failed: %s" % (cmd[:3], p.stderr.decode(errors="replace")[:400]))
    return p


def split_multipart(raw):
    """Returns (header block, [raw parts]) for a multipart message, cutting at the boundary lines."""
    head, _, body = raw.partition(b"\r\n\r\n")
    m = re.search(rb'boundary="?([^";\r\n]+)"?', head)
    b = b"--" + m.group(1)
    pieces = body.split(b"\r\n" + b)
    pieces[0] = pieces[0][len(b) + 2:] if pieces[0].startswith(b + b"\r\n") else pieces[0].split(b + b"\r\n", 1)[-1]
    parts = []
    for p in pieces:
        if p.startswith(b"--"):
            break
        if p.startswith(b"\r\n"):
            p = p[2:]
        parts.append(p)
    return head, parts


def part_body(part):
    return part.partition(b"\r\n\r\n")[2]


def text_of(entity_bytes):
    msg = email.message_from_bytes(entity_bytes, policy=email.policy.compat32)
    return msg.get_payload(decode=True).decode("utf-8")


def qp_part():
    lines = BODY.encode("utf-8").split(b"\r\n")[:-1]
    # quopri breaks long lines with a bare LF ("=\n"); a signed text has CRLF line ends only (RFC 3156 section 5), so the soft breaks get CRLF too.
    enc = b"\r\n".join(quopri.encodestring(l).rstrip(b"\n").replace(b"=\n", b"=\r\n") for l in lines) + b"\r\n"
    return b"Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" + enc


def wrap(headers_extra, ctype, body):
    return (b"From: Vector <%s>\r\nTo: %s\r\nSubject: Interop vector\r\nMIME-Version: 1.0\r\n" % (EMAIL, EMAIL)
            + b"Content-Type: " + ctype + b"\r\n\r\n" + body)


EMAIL = b"vector@example.org"


class Tool:
    def __init__(self, spec, work):
        self.kind, _, self.path = spec.partition(":")
        self.work = work
        if self.kind == "gpg":
            self.home = tempfile.mkdtemp(prefix="gh", dir=work)
            os.chmod(self.home, 0o700)
            libs = os.path.join(os.path.dirname(os.path.dirname(self.path)), "lib")
            self.env = dict(os.environ, GNUPGHOME=self.home)
            if os.path.isdir(libs):
                self.env["LD_LIBRARY_PATH"] = libs + ":" + os.environ.get("LD_LIBRARY_PATH", "")
            self.gpg = [self.path, "--batch", "--no-tty", "--pinentry-mode", "loopback", "--passphrase", "", "--status-fd", "2"]
            self.version = run([self.path, "--version"], env=self.env).stdout.decode().splitlines()[0].split()[-1]
            self.name = "gnupg-" + self.version
        else:
            self.env = dict(os.environ)
            self.version = run([self.path, "version"]).stdout.decode().strip().splitlines()[0].split()[-1]
            self.name = "sequoia-sqop-" + self.version

    # -- keys --
    def import_key(self, sec):
        if self.kind == "gpg":
            run(self.gpg + ["--import"], sec, self.env, check=True)
        else:
            self.sec = sec

    def fingerprint(self):
        out = run(self.gpg + ["--list-secret-keys", "--with-colons"], env=self.env).stdout.decode()
        return re.search(r"^fpr:+([0-9A-F]+):", out, re.M).group(1)

    def pub_of(self, sec):
        if self.kind == "sop":
            return run([self.path, "extract-cert"], sec, check=True).stdout
        return run(self.gpg + ["--export", "--armor"], env=self.env, check=True).stdout

    # -- operations; each returns (ok, detail) --
    def verify_detached(self, data, sig):
        d = tempfile.mkdtemp(dir=self.work)
        open(d + "/data", "wb").write(data)
        open(d + "/sig", "wb").write(sig)
        if self.kind == "gpg":
            p = run(self.gpg + ["--verify", d + "/sig", d + "/data"], env=self.env)
            ok = p.returncode == 0 and b"GOODSIG" in p.stderr and b"VALIDSIG" in p.stderr
            return ok, p.stderr.decode(errors="replace")[-200:]
        open(d + "/cert", "wb").write(self.pub_of(self.sec))
        p = run([self.path, "verify", d + "/sig", d + "/cert"], data)
        return p.returncode == 0 and len(p.stdout.strip()) > 0, p.stderr.decode(errors="replace")[-200:]

    def decrypt(self, msg):
        """Returns (plain or None, signature_ok, detail)."""
        if self.kind == "gpg":
            p = run(self.gpg + ["--decrypt"], msg, self.env)
            ok = p.returncode == 0 and b"DECRYPTION_OKAY" in p.stderr
            return (p.stdout if ok else None), (b"GOODSIG" in p.stderr and b"VALIDSIG" in p.stderr), p.stderr.decode(errors="replace")[-200:]
        d = tempfile.mkdtemp(dir=self.work)
        open(d + "/key", "wb").write(self.sec)
        open(d + "/cert", "wb").write(self.pub_of(self.sec))
        p = run([self.path, "decrypt", "--verify-with", d + "/cert", "--verifications-out", d + "/ver", d + "/key"], msg)
        sig_ok = os.path.exists(d + "/ver") and os.path.getsize(d + "/ver") > 0
        return (p.stdout if p.returncode == 0 else None), sig_ok, p.stderr.decode(errors="replace")[-200:]

    def sign_detached(self, data, digest):
        if self.kind == "gpg":
            return run(self.gpg + ["--detach-sign", "--armor", "--digest-algo", digest, "--local-user", self.fp], data, self.env, check=True).stdout
        d = tempfile.mkdtemp(dir=self.work)
        open(d + "/key", "wb").write(self.sec)
        return run([self.path, "sign", d + "/key"], data, check=True).stdout

    def encrypt(self, data, sign):
        if self.kind == "gpg":
            cmd = self.gpg + ["--encrypt", "--armor", "--trust-model", "always", "-r", self.fp, "--local-user", self.fp]
            if sign:
                cmd.append("--sign")
            return run(cmd, data, self.env, check=True).stdout
        d = tempfile.mkdtemp(dir=self.work)
        open(d + "/key", "wb").write(self.sec)
        open(d + "/cert", "wb").write(self.pub_of(self.sec))
        cmd = [self.path, "encrypt"] + (["--sign-with", d + "/key"] if sign else []) + [d + "/cert"]
        return run(cmd, data, check=True).stdout

    def digest_micalg(self):
        return (b"pgp-sha256", "SHA256") if self.kind == "gpg" else (b"pgp-sha512", None)


def crlf(b):
    return b.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")


def ours(vectors, tools, work):
    root = os.path.join(vectors, "ours")
    fails = 0
    for t in tools:
        for name in sorted(os.listdir(root)):
            d = os.path.join(root, name)
            if t.kind == "gpg" and name.startswith("v6"):
                continue  # GnuPG (2.4 and 2.5) does not read version 6 keys
            if t.kind == "gpg" and "native" in name:
                continue  # GnuPG does not implement RFC 9580's native Ed25519/X25519 algorithms
            t_h = Tool.__new__(Tool)
            t_h.__dict__.update(t.__dict__)
            if t.kind == "gpg":
                t_h.home = tempfile.mkdtemp(prefix="gh", dir=work); os.chmod(t_h.home, 0o700)
                t_h.env = dict(t.env, GNUPGHOME=t_h.home)
            t_h.import_key(open(d + "/key.sec.asc", "rb").read())
            t_h.fp = t_h.fingerprint() if t.kind == "gpg" else None
            expected = open(d + "/expected.txt", encoding="utf-8", newline="").read().strip()
            for kind in ("signed", "encrypted", "signed-encrypted", "sign-then-encrypt",
                         "protected-signed", "protected-signed-encrypted", "protected-sign-then-encrypt"):
                raw = open("%s/%s.eml" % (d, kind), "rb").read()
                head, parts = split_multipart(raw)
                # The protected-headers forms carry the message headers inside the signed or encrypted entity as well;
                # a program that does not know the scheme still has to verify and decrypt them as ordinary entities.
                kind_base = kind[len("protected-"):] if kind.startswith("protected-") else kind
                if kind_base == "signed":
                    ok, detail = t_h.verify_detached(parts[0], part_body(parts[1]))
                    ok = ok and text_of(parts[0]).strip() == expected
                else:
                    plain, sig_ok, detail = t_h.decrypt(part_body(parts[1]))
                    ok = plain is not None
                    if ok and kind_base == "sign-then-encrypt":
                        h2, p2 = split_multipart(plain)
                        v, detail = t_h.verify_detached(p2[0], part_body(p2[1]))
                        ok = v and text_of(p2[0]).strip() == expected
                    elif ok:
                        ok = text_of(plain).strip() == expected and (sig_ok == (kind_base == "signed-encrypted"))
                print("RESULT %s %s %s %s %s" % (t.name, name, kind, "ok" if ok else "FAIL", "" if ok else detail.replace("\n", " ")))
                fails += 0 if ok else 1
    return fails


def theirs(out, tools, work):
    fails = 0
    for t in tools:
        types = ["ed25519-cv25519", "rsa3072"]
        if t.kind == "sop":
            types = ["v6-ed25519-x25519", "v4-ed25519-cv25519"]
        for ktype in types:
            d = os.path.join(out, t.name, ktype)
            os.makedirs(d, exist_ok=True)
            if t.kind == "gpg":
                t.home = tempfile.mkdtemp(prefix="gh", dir=work); os.chmod(t.home, 0o700)
                t.env = dict(t.env, GNUPGHOME=t.home)
                algo, enc = ("ed25519", "cv25519") if ktype.startswith("ed") else ("rsa3072", "rsa3072")
                run(t.gpg + ["--quick-generate-key", "Vector <%s>" % EMAIL.decode(), algo, "sign", "never"], env=t.env, check=True)
                t.fp = t.fingerprint()
                run(t.gpg + ["--quick-add-key", t.fp, enc, "encr", "never"], env=t.env, check=True)
                sec = run(t.gpg + ["--export-secret-keys", "--armor"], env=t.env, check=True).stdout
                pub = run(t.gpg + ["--export", "--armor"], env=t.env, check=True).stdout
            else:
                profile = "rfc9580" if ktype.startswith("v6") else "rfc4880"
                sec = run([t.path, "generate-key", "--profile", profile, "Vector <%s>" % EMAIL.decode()], check=True).stdout
                t.sec = sec
                pub = t.pub_of(sec)
            open(d + "/key.sec.asc", "wb").write(sec)
            open(d + "/key.pub.asc", "wb").write(pub)
            open(d + "/expected.txt", "w", encoding="utf-8", newline="").write(BODY)
            open(d + "/info.txt", "w").write("%s %s\n" % (t.name, t.version))
            part = qp_part()
            micalg, digest = t.digest_micalg()
            sig = crlf(t.sign_detached(part, digest))
            bnd = b"vector-bnd"
            signed = wrap(b"", b'multipart/signed; protocol="application/pgp-signature"; micalg=%s; boundary="%s"' % (micalg, bnd),
                          b"--" + bnd + b"\r\n" + part + b"\r\n--" + bnd + b'\r\nContent-Type: application/pgp-signature; name="signature.asc"\r\n\r\n' + sig + b"--" + bnd + b"--\r\n")
            open(d + "/signed.eml", "wb").write(signed)
            for kind, sign in (("encrypted", False), ("signed-encrypted", True)):
                armored = crlf(t.encrypt(part, sign))
                eml = wrap(b"", b'multipart/encrypted; protocol="application/pgp-encrypted"; boundary="%s"' % bnd,
                           b"--" + bnd + b"\r\nContent-Type: application/pgp-encrypted\r\n\r\nVersion: 1\r\n\r\n--" + bnd
                           + b'\r\nContent-Type: application/octet-stream; name="encrypted.asc"\r\n\r\n' + armored + b"--" + bnd + b"--\r\n")
                open("%s/%s.eml" % (d, kind), "wb").write(eml)
            # sign-then-encrypt: the signed entity is the plaintext of the encrypted message
            inner = signed.partition(b"MIME-Version: 1.0\r\n")[2]
            armored = crlf(t.encrypt(inner, False))
            eml = wrap(b"", b'multipart/encrypted; protocol="application/pgp-encrypted"; boundary="%s"' % bnd,
                       b"--" + bnd + b"\r\nContent-Type: application/pgp-encrypted\r\n\r\nVersion: 1\r\n\r\n--" + bnd
                       + b'\r\nContent-Type: application/octet-stream; name="encrypted.asc"\r\n\r\n' + armored + b"--" + bnd + b"--\r\n")
            open(d + "/sign-then-encrypt.eml", "wb").write(eml)
            print("RESULT %s %s generated ok" % (t.name, ktype))
    return fails


if __name__ == "__main__":
    mode, target, specs = sys.argv[1], sys.argv[2], sys.argv[3:]
    work = tempfile.mkdtemp(prefix="pgpi", dir=os.environ.get("TMPDIR", "/tmp"))
    tools = [Tool(s, work) for s in specs]
    try:
        n = ours(target, tools, work) if mode == "ours" else theirs(target, tools, work)
    finally:
        for t in tools:
            if t.kind == "gpg":
                subprocess.run([t.path.replace("/gpg", "/gpgconf"), "--kill", "all"], env=t.env, capture_output=True)
        shutil.rmtree(work, ignore_errors=True)
    sys.exit(1 if n else 0)
