#!/usr/bin/env bash
# The oracle checks for the Outlook calendar conversion:
#   1. the recurrence expansion differential over the iCalendar generated from the MS-OXOCAL series fixtures
#      (testdata/calendar/oxocal/generated), against dateutil, recurring-ical-events and Ical.Net;
#   2. the parse check: Python icalendar and Ical.Net read every iCalendar file we produce, and their facts are compared with ours.
#
#   oracle-checks.sh <work dir> [extra dir of .ics files to include in the parse check]
#
# Needs dotnet and uv on PATH; creates the python environment on first use (tools/calendar/oracles/setup_python_expand.sh).
set -euo pipefail
work="${1:?work dir}"; extra="${2:-}"; mkdir -p "$work"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
export VENV="${VENV:-$HOME/onemail/oracles/recurrence-venv}"
[ -x "$VENV/bin/python" ] || bash "$repo/tools/calendar/oracles/setup_python_expand.sh" "$VENV"

echo "== expansion differential"
export ONEMAIL_CORPUS="${ONEMAIL_CORPUS:-$work/no-corpus}"
EXPAND_ROOTS="$repo/testdata/calendar/oxocal/generated" bash "$repo/tools/calendar/run_expand_diff.sh" "$work/expand" || echo "expansion differential exit $?"

echo "== parse check"
parse="$work/parse"; rm -rf "$parse"; mkdir -p "$parse"
cp -r "$repo/testdata/calendar/oxocal/generated" "$parse/generated"
[ -n "$extra" ] && cp -r "$extra" "$parse/extra"
dotnet build "$repo/tools/calendar/CalendarDump" -c Release -v quiet -clp:NoSummary >/dev/null
dotnet build "$repo/tools/calendar/oracles/dotnet" -c Release -v quiet -clp:NoSummary >/dev/null
art="${ONEMAIL_ARTIFACTS:-$repo/artifacts}/bin"
dump="$(find "$art/CalendarDump/release" -name CalendarDump -type f | head -1)"
of="$(find "$art/OracleFacts/release" -name OracleFacts.dll | head -1)"
"$dump" facts "$parse" "$work/ours-facts.jsonl"
"$VENV/bin/python" "$repo/tools/calendar/oracles/py_facts.py" icalendar "$parse" "$work/icalendar-facts.jsonl"
dotnet "$of" ical "$parse" "$work/icalnet-facts.jsonl"
echo "files: $(find "$parse" -name '*.ics' | wc -l); ours $(wc -l < "$work/ours-facts.jsonl"), icalendar $(wc -l < "$work/icalendar-facts.jsonl"), Ical.Net $(wc -l < "$work/icalnet-facts.jsonl") parsed"
python3 "$repo/tools/calendar/oracles/compare.py" "$work/ours-facts.jsonl" "$work/icalendar-facts.jsonl" --show 5 | tail -25
python3 "$repo/tools/calendar/oracles/compare.py" "$work/ours-facts.jsonl" "$work/icalnet-facts.jsonl" --show 5 --no-params --skip-zoned --only SUMMARY,LOCATION,DESCRIPTION,UID,STATUS,PRIORITY,SEQUENCE,CLASS,TRANSP,ORGANIZER,ATTENDEE,TRIGGER,ACTION,DTSTAMP | tail -25
