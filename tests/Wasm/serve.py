"""Serves the published wasm test app and collects its console output (POST /log) until it posts /done.
Also serves the files the tests read: GET /__manifest lists them (absolute paths, one per line, from the FILELIST file) and
GET /__files/<absolute path> returns one. The page copies them into the runtime's file system at the same paths, because the
tests find their data relative to their own source paths.
usage: serve.py ROOT PORT LOGFILE TIMEOUT_SECONDS FILELIST   -- exit 0 when /done arrived, 3 on timeout."""
import functools, http.server, sys, threading, urllib.parse

root, port, logfile, timeout, filelist = sys.argv[1], int(sys.argv[2]), sys.argv[3], float(sys.argv[4]), sys.argv[5]
allowed = set(open(filelist).read().split("\n")) - {""}
done = threading.Event()
log = open(logfile, "ab", buffering=0)


class Handler(http.server.SimpleHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        if self.path == "/log":
            log.write(body)
        elif self.path == "/done":
            done.set()
        self.send_response(204)
        self.end_headers()

    def do_GET(self):
        if self.path == "/__manifest":
            data = "\n".join(sorted(allowed)).encode()
        elif self.path.startswith("/__files/") and urllib.parse.unquote(self.path[len("/__files"):]) in allowed:
            with open(urllib.parse.unquote(self.path[len("/__files"):]), "rb") as f:
                data = f.read()
        else:
            return super().do_GET()
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


Handler.extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, ".wasm": "application/wasm", ".js": "text/javascript", ".mjs": "text/javascript"}
server = http.server.ThreadingHTTPServer(("127.0.0.1", port), functools.partial(Handler, directory=root))
threading.Thread(target=server.serve_forever, daemon=True).start()
sys.exit(0 if done.wait(timeout) else 3)
