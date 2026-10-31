#!/usr/bin/env bash
# Contact checks on the worker: reads every contact and distribution list item of the MSG/PST/OST corpora, converts them to vCards and back.
#   run.sh [stage]       stages: build gen dump rt-contact extract-msg export all (default all)
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
VENV_MSG="${VENV_MSG:-$HOME/onemail/u14/venv-msg}"
inputs=("$CORPUS/msg" "$CORPUS/pst" "$WORK/gen")
if [[ $stage == gen || $stage == all ]]; then
  rm -rf "$WORK/gen"; tool gen "$WORK/gen" 20261031 64
fi
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
if [[ $stage == extract-msg || $stage == all ]]; then
  # Contact fields against the Python package extract-msg, over the corpus and the generated MSG files.
  "$VENV_MSG/bin/python" -c 'import importlib.metadata as m; print("extract-msg", m.version("extract-msg"))'
  "$VENV_MSG/bin/python" "$here/tools/contacts/compare_extract_msg.py" "$WORK/out/dump.jsonl" "$CORPUS/msg" "$WORK/gen"
fi
