#!/usr/bin/env bash
# Publishes the WebAssembly smoke app, serves it on localhost and runs it in headless Chromium (or Chrome).
# Prints the page's output; exits 0 only when the app printed "wasm smoke: ok" and exited 0.
#
#   smoke/Wasm/run-headless.sh OUTDIR        (BROWSER may name the browser binary)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?output directory}"; mkdir -p "$out"; out="$(cd "$out" && pwd)"
dotnet publish "$here/Wasm.csproj" -c Release -o "$out/publish" -v quiet -clp:NoSummary
root="$out/publish/wwwroot"
browser="${BROWSER:-$(command -v chromium || command -v chromium-browser || command -v google-chrome)}"
port=$(( 20000 + RANDOM % 20000 ))
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$root" >"$out/server.log" 2>&1 &
server=$!
trap 'kill $server 2>/dev/null || true' EXIT
sleep 1
"$browser" --headless=new --no-sandbox --disable-gpu --user-data-dir="$out/profile" --virtual-time-budget=60000 \
  --dump-dom "http://127.0.0.1:$port/index.html" >"$out/dom.html" 2>"$out/browser.log" || true
sed -n '/<pre id="out">/,/<\/pre>/p' "$out/dom.html"
grep -q 'wasm smoke: ok' "$out/dom.html" && grep -q 'exit: 0' "$out/dom.html"
