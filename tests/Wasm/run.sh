#!/usr/bin/env bash
# Builds the format-core tests for the browser (browser-wasm), runs them in headless Chromium (or Chrome) and prints the totals.
# Exits 0 only when the run finished and no test failed. Needs the wasm-tools workload.
#
#   tests/Wasm/run.sh OUTDIR [FILTER]   (BROWSER names the browser binary; TIMEOUT is the seconds allowed, default 1800;
#                                        FILTER keeps the test classes whose full name contains it)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?output directory}"; mkdir -p "$out"; out="$(cd "$out" && pwd)"
filter="${2:-}"
dotnet publish "$here/WasmTests.csproj" -c Release -o "$out/publish" -v quiet -clp:NoSummary
root="$out/publish/wwwroot"
browser="${BROWSER:-$(command -v chromium || command -v chromium-browser || command -v google-chrome)}"
port=$(( 20000 + RANDOM % 20000 ))
: >"$out/console.log"
# The files the tests read, at the absolute paths the build baked into the tests.
repo="$(cd "$here/../.." && pwd)"
{ find "$repo/testdata" "$repo/docs" -type f -not -path "$repo/testdata/client/*" -not -path "$repo/testdata/openpgp/*" -not -path "$repo/testdata/smime/*"
  find "$repo/src/OneMail/Rtf" "$repo/src/OneMail/Conversion/OxcIcal" -name '*.cs'; } >"$out/files.txt"
python3 "$here/serve.py" "$root" "$port" "$out/console.log" "${TIMEOUT:-1800}" "$out/files.txt" &
server=$!
trap 'kill $server 2>/dev/null || true; kill ${chrome:-0} 2>/dev/null || true' EXIT
sleep 1
"$browser" --headless=new --no-sandbox --disable-gpu --user-data-dir="$out/profile" \
  --js-flags=--stack-size=4000 "http://127.0.0.1:$port/index.html?filter=$filter" >"$out/browser.log" 2>&1 &
chrome=$!
status=0
wait $server || status=$?
kill $chrome 2>/dev/null || true
grep -E '^(FAIL|skipped|excluded|wasm tests|exit|exception)' "$out/console.log" || true
[ "$status" = 0 ] || { echo "run did not finish (timeout); last lines:"; tail -5 "$out/console.log"; exit 1; }
grep -q '^exit: 0' "$out/console.log" && grep -q '^wasm tests:.* failed=0 ' "$out/console.log"
