#!/usr/bin/env bash
# One pass of the HTML-to-text comparison on the worker: extracts the HTML bodies of the corpora, generates the cases, converts them,
# and compares with w3m, lynx and html2text. Needs oracles.sh to have run and a built BodiesTool in $W/tool.
# usage: run-compare.sh [work-dir]   (default ~/onemail/work-u28)
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
W=${1:-$HOME/onemail/work-u28}
C=$HOME/onemail/corpus
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10
. "$HOME/onemail/oracles/html-text/env.sh"
T=$W/tool/BodiesTool
if [ ! -d "$W/html" ]; then
  mkdir -p "$W/html"
  $T html "$W/html" dv 5000 1 "$C/dovecot-crlf"
  $T html "$W/html" ms 5000 1 "$C/msg"
  $T html "$W/html" ol 5000 1 "$C"/olm/*.olm
  $T html "$W/html" ps 5000 1 "$C"/pst/tika/*.pst
  $T html "$W/html" mj 5000 1 "$C/mime4j"
  $T html "$W/html" en 5000 1 "$C/enron"
fi
"$HTML2TEXT_PY" "$HERE/gen_cases.py" "$W/html" 300 28
$T text "$W/html"
$T hidden "$W/html"
"$HTML2TEXT_PY" "$HERE/compare.py" "$W/html" --show "${SHOW:-5}"
