#!/usr/bin/env bash
# iCalendar -> Outlook item -> iCalendar over the calendar corpora, and the expansion oracles on what comes back:
#   1. OxocalTool icsdiff: every field of every event group compared with the round trip (counts per class on stdout);
#   2. the series of the exported iCalendar (written to <work>/back) expanded by our engine, python-dateutil,
#      recurring-ical-events and Ical.Net, so that the converted items are held to the same oracles as the originals.
#
#   ics-roundtrip-checks.sh <work dir>
#
# Needs ONEMAIL_CORPUS (the fetched corpora), dotnet and uv on PATH; the repository is the working directory's ancestor.
set -euo pipefail
work="${1:?work dir}"; mkdir -p "$work"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
c="${ONEMAIL_CORPUS:?}/calendar"
export VENV="${VENV:-$HOME/onemail/oracles/recurrence-venv}"
[ -x "$VENV/bin/python" ] || bash "$repo/tools/calendar/oracles/setup_python_expand.sh" "$VENV"
dotnet build "$repo/tools/oxocal/OxocalTool" -c Release -v quiet -clp:NoSummary >/dev/null
tool="$(find "${ONEMAIL_ARTIFACTS:-$repo/artifacts}/bin/OxocalTool/release" -name OxocalTool -type f | head -1)"
roots=("$repo/testdata/calendar")
for d in libical icaljs biweekly sabrevobject thunderbird govuk; do [ -d "$c/$d" ] && roots+=("$c/$d"); done
rm -rf "$work/back"
echo "== field-by-field round trip"
"$tool" icsdiff --samples 3 --write-back "$work/back" --out "$work/icsdiff.tsv" "${roots[@]}" | tee "$work/icsdiff.log" | sed -n '1,/^loss codes/p'
echo "== expansion oracles on the exported iCalendar"
EXPAND_ROOTS="$work/back" bash "$repo/tools/calendar/run_expand_diff.sh" "$work/expand"
