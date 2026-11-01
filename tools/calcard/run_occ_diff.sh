#!/usr/bin/env bash
# The card's occurrence lists against the library's expansion and recurring-ical-events, over the recurring components of the calendar corpus.
#
#   run_occ_diff.sh <work dir>
#
# Needs ONEMAIL_CORPUS (fetched calendar corpus), VENV (python environment from tools/calendar/oracles/setup_python_expand.sh) and dotnet on PATH.
set -euo pipefail
work="${1:?work dir}"; mkdir -p "$work"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
corpus="${ONEMAIL_CORPUS:?}/calendar"
venv="${VENV:-$HOME/onemail/oracles/recurrence-venv}"
roots=()
for d in libical icaljs biweekly sabrevobject thunderbird govuk; do [ -d "$corpus/$d" ] && roots+=("$corpus/$d"); done
roots+=("$repo/testdata/calendar/rfc" "$repo/testdata/calendar/recurrence/generated" "$repo/testdata/calendar/generated/hand")
dotnet build "$repo/tools/calendar/RecurrenceDump" -c Release -v quiet -clp:NoSummary >/dev/null
dotnet build "$repo/tools/calcard/CalcardTool" -c Release -v quiet -clp:NoSummary >/dev/null
art="${ONEMAIL_ARTIFACTS:-$repo/artifacts}/bin"
dump="$(find "$art/RecurrenceDump/release" -name RecurrenceDump -type f | head -1)"
tool="$(find "$art/CalcardTool/release" -name CalcardTool -type f | head -1)"
"$dump" cases "$work/cases.jsonl" "${roots[@]}"
"$dump" expand "$work/cases.jsonl" "$work/ours.jsonl"
"$venv/bin/python" "$repo/tools/calendar/oracles/py_expand.py" rec "$work/cases.jsonl" "$work/rec.jsonl"
"$tool" occ "$work/cases.jsonl" "$work/card.jsonl"
python3 "$repo/tools/calcard/compare_occ.py" "$work/card.jsonl" "$work/ours.jsonl" "$work/rec.jsonl"
