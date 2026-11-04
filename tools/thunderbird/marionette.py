"""A minimal Marionette (Mozilla's remote-control protocol, protocol 3) client, standard library only.

Thunderbird started with --marionette listens on 127.0.0.1:2828. Messages are "<length>:<json>"; commands are
[0, id, name, params] and answered with [1, id, error, result]. Documented at
https://firefox-source-docs.mozilla.org/testing/marionette/Protocol.html
"""
import json, socket, time


class Marionette:
    def __init__(self, port=2828, timeout=120):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.buf = b""
        self.n = 0
        self.hello = self._read()

    def _read(self):
        while b":" not in self.buf:
            self.buf += self._recv()
        n, _, rest = self.buf.partition(b":")
        n = int(n)
        while len(rest) < n:
            rest += self._recv()
        self.buf = rest[n:]
        return json.loads(rest[:n].decode("utf-8"))

    def _recv(self):
        d = self.sock.recv(65536)
        if not d:
            raise ConnectionError("Marionette closed the connection")
        return d

    def call(self, name, params=None):
        self.n += 1
        body = json.dumps([0, self.n, name, params or {}]).encode("utf-8")
        self.sock.sendall(b"%d:%s" % (len(body), body))
        while True:
            m = self._read()
            if m[0] == 1 and m[1] == self.n:
                if m[2]:
                    raise RuntimeError("%s: %s" % (name, m[2].get("message", m[2])))
                return m[3]

    def new_session(self):
        r = self.call("WebDriver:NewSession", {"capabilities": {"alwaysMatch": {"unhandledPromptBehavior": "ignore"}}})
        self.call("Marionette:SetContext", {"value": "chrome"})
        return r

    def script(self, js, args=None, timeout=60000):
        """Runs a function body in the chrome context; `return` gives the value. Promises are awaited."""
        self.call("WebDriver:SetTimeouts", {"script": timeout})
        # ExecuteAsyncScript: the last argument is the resolve callback.
        src = "const done = arguments[arguments.length - 1]; (async () => { " + js + " })().then(v => done(v === undefined ? null : v), e => done({ __error: String(e) + ' | ' + String(e && e.message) + ' | ' + String(e && e.stack) }));"
        r = self.call("WebDriver:ExecuteAsyncScript", {"script": src, "args": args or [], "sandbox": "system"})
        v = r.get("value") if isinstance(r, dict) and "value" in r else r
        if isinstance(v, dict) and "__error" in v:
            raise RuntimeError(v["__error"])
        return v

    def screenshot(self, path):
        import base64
        r = self.call("WebDriver:TakeScreenshot", {"full": False, "hash": False})
        data = r["value"] if isinstance(r, dict) else r
        with open(path, "wb") as f:
            f.write(base64.b64decode(data))

    def close(self):
        try:
            self.call("Marionette:Quit", {"flags": ["eForceQuit"]})
        except Exception:
            pass
        self.sock.close()


def wait_port(port=2828, seconds=90):
    end = time.time() + seconds
    while time.time() < end:
        try:
            socket.create_connection(("127.0.0.1", port), timeout=1).close()
            return
        except OSError:
            time.sleep(0.5)
    raise TimeoutError("Marionette did not start")
