#!/usr/bin/env bash
# The expansion differential: our occurrences against python-dateutil, recurring-ical-events and Ical.Net over every recurring
# component of the calendar corpus and the generated recurrence fixtures, in a fixed window per component.
#
#   run_expand_diff.sh <work dir>
#
# Needs: ONEMAIL_CORPUS (the fetched calendar corpus, see corpus.py), VENV (the python environment from
# oracles/setup_python_expand.sh), DOTNET on PATH, and the repository checked out as the working directory.
set -euo pipefail
work="${1:?work dir}"; mkdir -p "$work"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
corpus="${ONEMAIL_CORPUS:?}/calendar"
venv="${VENV:-$HOME/onemail/oracles/recurrence-venv}"
roots=()
for d in libical icaljs biweekly sabrevobject thunderbird govuk; do [ -d "$corpus/$d" ] && roots+=("$corpus/$d"); done
roots+=("$repo/testdata/calendar/rfc" "$repo/testdata/calendar/recurrence/generated" "$repo/testdata/calendar/generated/hand")

dotnet build "$repo/tools/calendar/RecurrenceDump" -c Release -v quiet -clp:NoSummary >/dev/null
dotnet build "$repo/tools/calendar/oracles/OracleExpand" -c Release -v quiet -clp:NoSummary >/dev/null
dump="$(find "${ONEMAIL_ARTIFACTS:-$repo/artifacts}/bin/RecurrenceDump/release" -name RecurrenceDump -type f | head -1)"
ical="$(find "${ONEMAIL_ARTIFACTS:-$repo/artifacts}/bin/OracleExpand/release" -name OracleExpand.dll | head -1)"

"$dump" cases "$work/cases.jsonl" "${roots[@]}"
"$dump" expand "$work/cases.jsonl" "$work/ours.jsonl"
"$venv/bin/python" "$repo/tools/calendar/oracles/py_expand.py" rec "$work/cases.jsonl" "$work/rec.jsonl"
"$venv/bin/python" "$repo/tools/calendar/oracles/py_expand.py" du "$work/cases.jsonl" "$work/du.jsonl"
rm -f "$work/ical.jsonl"; done_cases=0; rc=75
while [ "$rc" = 75 ]; do
  rc=0; dotnet "$ical" "$work/cases.jsonl" "$work/ical.jsonl" "$done_cases" || rc=$?
  done_cases=$(wc -l < "$work/ical.jsonl")
done
status=0
for o in rec du ical; do
  peers=(); for p in rec du ical; do [ "$p" != "$o" ] && peers+=(--peer "$work/$p.jsonl"); done
  python3 "$repo/tools/calendar/oracles/compare_expand.py" "$work/ours.jsonl" "$work/$o.jsonl" "$o" "${peers[@]}" || status=1
done
exit $status
