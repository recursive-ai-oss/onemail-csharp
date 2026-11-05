#!/usr/bin/env bash
# Writes our reader's view of every PST in a set, in the shape compare-views.py expects.
#
#   bash tools/outlook/ours-views.sh <set dir> <out dir> [filter, default *.pst] [max items per folder, default 100000]
set -uo pipefail
set_dir="${1:?set dir}"; out="${2:?out dir}"; filter="${3:-*.pst}"; cap="${4:-100000}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$out/artifacts}" TMPDIR="${TMPDIR:-$out/tmp}"; mkdir -p "$out" "$TMPDIR"
dotnet build "$here/tests/PstOracle" -c Release -m:4 -v quiet -clp:NoSummary > "$out/build.log" 2>&1 || { tail -20 "$out/build.log"; exit 1; }
ora="$ONEMAIL_ARTIFACTS/bin/PstOracle/release/RecursiveAI.OneMail.PstOracle.dll"
cd "$set_dir"
find . -type f -name "$filter" | sed 's|^\./||' | sort | while IFS= read -r rel; do
  dotnet "$ora" outlookview "$rel" "$cap" > "$out/$(echo "$rel" | sed 's|/|__|g').ours.json" 2> "$out/$(echo "$rel" | sed 's|/|__|g').err" || true
done
ls "$out"/*.ours.json | wc -l
