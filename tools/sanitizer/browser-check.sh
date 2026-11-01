#!/usr/bin/env bash
# Renders every XSS vector, sanitised and raw, in headless Chromium and counts what happened.
#
#   tools/sanitizer/browser-check.sh OUTDIR
#   VECTORS=testdata/sanitizer/review.txt BROWSER=/usr/bin/firefox tools/sanitizer/browser-check.sh OUTDIR
#
# For each page the script starts a small logging HTTP server on a free port, loads the page with --dump-dom, and reads two numbers:
# how many times the page's alert/confirm/prompt/write stand-ins were called (script ran) and how many requests reached the server
# (something was fetched). The raw page is the control: it must show both, otherwise the harness proves nothing.
# VECTORS picks the vector file (default xss.txt). BROWSER may be Chrome/Chromium (the page is dumped with --dump-dom) or Firefox (the page posts
# its result to the logging server, which writes it to a file). A second page parses every sanitised output in the browser (innerHTML, template,
# DOMParser, range fragment) and fails on any element or attribute outside the allowlists or any difference between the parses. On the allow page
# every request must be for a URL the policy let through (allowed-paths.txt), so a fetch the sanitiser meant to remove cannot hide.
# STRUCTURE_ONLY=1 runs only the structure page (for large generated vector sets, tools/sanitizer/mutate.py).
# Exits 0 when the sanitised page (policy: block) shows no script run and no request, and the allow page shows no script run and
# no request for a resource the sanitiser removes outright (frames, objects, media, scripts, link elements, forms).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="${1:?output directory}"; mkdir -p "$out"; out="$(cd "$out" && pwd)"
browser="${BROWSER:-$(command -v chromium || command -v chromium-browser || command -v google-chrome)}"
vectors="${VECTORS:-$repo/testdata/sanitizer/xss.txt}"
tool="${SANITIZE_TOOL:-dotnet run --project $repo/tools/sanitizer/SanitizeTool -c Release --}"

cat >"$out/server.py" <<'PY'
import http.server, sys
log = open(sys.argv[2], "a")
result = sys.argv[3] if len(sys.argv) > 3 else None
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/__result"):
            n = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(n).decode("utf-8", "replace") if n else ""
            if result:
                open(result, "w").write(body)
            self.send_response(200); self.end_headers(); return
        log.write(self.path + "\n"); log.flush()
        self.send_response(200); self.send_header("Content-Type", "image/gif"); self.end_headers()
        self.wfile.write(b"GIF89a\x01\x00\x01\x00\x00\x00\x00;")
    do_POST = do_GET
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
PY

port=$(( 20000 + RANDOM % 20000 ))
[ "${STRUCTURE_ONLY:-0}" = 1 ] || $tool pages "$vectors" "$out" "127.0.0.1:$port"
$tool structure "$vectors" "$out" "127.0.0.1:$port"
status=0
run_page() {  # run_page NAME HTMLFILE -> sets fired, reqs
  local name="$1" file="$2" budget="--virtual-time-budget=20000"
  : >"$out/requests-$name.log"; rm -f "$out/result-$name.txt"
  python3 "$out/server.py" "$port" "$out/requests-$name.log" "$out/result-$name.txt" &
  local server=$!
  sleep 1
  case "$browser" in
    *firefox*)
      mkdir -p "$out/profile-$name"
      "$browser" --headless --no-remote --profile "$out/profile-$name" "file://$file" >"$out/browser-$name.log" 2>&1 &
      local pid=$! waited=0
      while [ ! -s "$out/result-$name.txt" ] && [ "$waited" -lt "${PAGE_TIMEOUT:-60}" ]; do sleep 1; waited=$((waited+1)); done
      kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true
      cp "$out/result-$name.txt" "$out/dom-$name.html" 2>/dev/null || echo "result missing" >"$out/dom-$name.html"
      ;;
    *)
      timeout "${PAGE_TIMEOUT:-60}" "$browser" --headless=new --no-sandbox --disable-gpu --user-data-dir="$out/profile-$name" $budget \
        --dump-dom "file://$file" >"$out/dom-$name.html" 2>"$out/browser-$name.log" || true
      ;;
  esac
  kill "$server" 2>/dev/null || true; wait "$server" 2>/dev/null || true
  fired=$(grep -o 'FIRED=[0-9]*' "$out/dom-$name.html" | head -1 | cut -d= -f2 || true)
  reqs=$(wc -l <"$out/requests-$name.log")
}
if [ "${STRUCTURE_ONLY:-0}" = 1 ]; then
  run_page structure "$out/page-structure.html"
  viol=$(grep -o 'VIOLATIONS=[0-9][0-9]*' "$out/dom-structure.html" | head -1 | cut -d= -f2 || true)
  echo "structure: VIOLATIONS=${viol:-missing} $(grep -o 'OUTPUTS=[0-9][0-9]*' "$out/dom-structure.html" | head -1)"
  [ "${viol:-missing}" = 0 ] || { sed -n '/VIOLATIONS=[0-9]/,/<\/pre>/p' "$out/dom-structure.html" | cut -c1-300 | head -40; exit 1; }
  exit 0
fi
raw_fired=0; raw_reqs=0
for f in "$out"/page-raw-*.html; do
  n=$(basename "$f" .html)
  run_page "$n" "$f"
  raw_fired=$(( raw_fired + ${fired:-0} )); raw_reqs=$(( raw_reqs + reqs ))
  echo "$n: FIRED=${fired:-missing} requests=$reqs"
done
for page in sanitized allow; do
  run_page "$page" "$out/page-$page.html"
  echo "$page: FIRED=${fired:-missing} requests=$reqs"
done
echo "raw control: FIRED=$raw_fired requests=$raw_reqs"
[ "$raw_fired" -gt 0 ] || { echo "control failed: the raw pages ran no script"; status=1; }
[ "$raw_reqs" -gt 0 ] || { echo "control failed: the raw pages fetched nothing"; status=1; }
grep -qE 'FIRED=0 ' "$out/dom-sanitized.html" || { echo "FAIL: script ran on the sanitised page"; status=1; }
[ "$(wc -l <"$out/requests-sanitized.log")" -eq 0 ] || { echo "FAIL: requests on the sanitised page:"; cat "$out/requests-sanitized.log"; status=1; }
grep -qE 'FIRED=0 ' "$out/dom-allow.html" || { echo "FAIL: script ran on the allow page"; status=1; }
if grep -E '^/p/(script|iframe|object|embed|video|audio|track|poster|link-css|import|prefetch|preload|prerender|form|input-image|input-src|bgsound|codebase|refresh|base|ping|longdesc|dynsrc|lowsrc|data-src|import2|has|var|nest|page|kf|sel-break|supports)($|[?.#])' "$out/requests-allow.log"; then
  echo "FAIL: the allow page fetched something the sanitiser removes"; status=1
fi
run_page structure "$out/page-structure.html"
viol=$(grep -o 'VIOLATIONS=[0-9][0-9]*' "$out/dom-structure.html" | head -1 | cut -d= -f2 || true)
echo "structure: VIOLATIONS=${viol:-missing} $(grep -o 'OUTPUTS=[0-9][0-9]*' "$out/dom-structure.html" | head -1)"
if [ "${viol:-missing}" != 0 ]; then echo "FAIL: structure check"; sed -n '/VIOLATIONS=[0-9]/,/<\/pre>/p' "$out/dom-structure.html" | cut -c1-300 | head -40; status=1; fi
# Every request on the allow page must be one the policy allowed.
unexpected=$(grep -v '^$' "$out/requests-allow.log" | sort -u | grep -v -x -F -f "$out/allowed-paths.txt" || true)
if [ -n "$unexpected" ]; then echo "FAIL: the allow page fetched a URL the policy did not allow:"; echo "$unexpected" | head -20; status=1; fi
sort -u "$out/requests-allow.log" | wc -l | sed 's/^/allow page: distinct paths fetched: /'
exit $status
