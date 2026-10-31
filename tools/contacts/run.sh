#!/usr/bin/env bash
# Contact checks on the worker: reads every contact and distribution list item of the MSG/PST/OST corpora, converts them to vCards and back.
#   run.sh [stage]       stages: build dump rt-contact export all (default all)
# State goes under $WORK (default ~/onemail/u20); corpora are the ones tools/corpus fetches into ~/onemail/corpus.
set -euo pipefail
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
here="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${WORK:-$HOME/onemail/u20}"
CORPUS="${CORPUS:-$HOME/onemail/corpus}"
export ONEMAIL_ARTIFACTS="$WORK/artifacts" TMPDIR="$WORK/tmp"
mkdir -p "$WORK/out" "$TMPDIR"
stage="${1:-all}"
tool() { dotnet "$ONEMAIL_ARTIFACTS/bin/ContactTool/release/ContactTool.dll" "$@"; }
if [[ $stage == build || $stage == all ]]; then
  dotnet build "$here/tools/contacts/ContactTool" -c Release -v quiet -clp:NoSummary
fi
inputs=("$CORPUS/msg" "$CORPUS/pst")
if [[ $stage == dump || $stage == all ]]; then
  for d in "${inputs[@]}"; do tool dump "$d" > "$WORK/out/dump-$(basename "$d").jsonl"; done
  cat "$WORK"/out/dump-*.jsonl > "$WORK/out/dump.jsonl"
  wc -l "$WORK/out/dump.jsonl"
fi
if [[ $stage == rt-contact || $stage == all ]]; then
  for d in "${inputs[@]}"; do echo "== $d"; tool rt-contact "$d"; done
fi
if [[ $stage == export || $stage == all ]]; then
  rm -rf "$WORK/out/vcf"; mkdir -p "$WORK/out/vcf"
  for d in "${inputs[@]}"; do tool export "$d" "$WORK/out/vcf/$(basename "$d")"; done
fi
