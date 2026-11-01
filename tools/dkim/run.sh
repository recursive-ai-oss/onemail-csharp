#!/usr/bin/env bash
# Two-way interop run: this library's signatures checked by each oracle, and each oracle's signatures checked by this library.
#   tools/dkim/run.sh <work dir>
# Needs: dotnet on PATH, and the oracles installed by tools/dkim/setup.sh.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:?usage: run.sh <work dir>}"
root="${ONEMAIL_DKIM_ORACLES:-$HOME/onemail/oracles/dkim}"
mkdir -p "$work"
bin="$work/bin"
dotnet build "$here/DkimInterop" -c Release -v quiet -clp:NoSummary -o "$bin"
io="dotnet $bin/DkimInterop.dll"
$io gen "$work"
$io verify "$work" ours-signed "$work/ours-verifies-ours.json"
"$root/venv/bin/python" "$here/dkimpy_driver.py" verify "$work"
"$root/venv/bin/python" "$here/dkimpy_driver.py" sign "$work" || true
$io verify "$work" dkimpy-signed "$work/ours-verifies-dkimpy.json"
export PATH="$root/node/bin:$PATH" NODE_PATH="$root/npm/node_modules"
node "$here/mailauth_driver.js" verify "$work"
node "$here/mailauth_driver.js" sign "$work" || true
$io verify "$work" mailauth-signed "$work/ours-verifies-mailauth.json"
$io compare "$work" dkimpy mailauth
