#!/usr/bin/env bash
# CR CR LF probe: how each pinned oracle reads a CR CR LF line end, inside a fold and elsewhere.
#   run.sh <work dir> [oracle dir]     (oracle dir as for tools/calendar/oracles/forms/run_forms_diff.sh; needs dotnet, python venv)
# Prints one line per oracle and probe file (oracle, file, folded property value, the properties after it, property count).
set -euo pipefail
work="${1:?work dir}"; here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; repo="$(cd "$here/../../.." && pwd)"
orc="${2:-$HOME/onemail/work/oracles}"; venv="${VENV_CAL:-$HOME/onemail/work/venv-cal}"
export PATH="$orc/node/bin:$orc/jdk/bin:$PATH"
mkdir -p "$work"; cd "$work"; rm -rf p o; mkdir p o
python3 "$here/make_probes.py" p
S="python3 $here/summarize.py"
dotnet run --project "$repo/tools/calendar/CalendarDump" -c Release -- facts p o/ours.jsonl >/dev/null
dotnet run --project "$repo/tools/calendar/oracles/dotnet" -c Release -- ical p o/ical.jsonl >/dev/null
dotnet run --project "$repo/tools/calendar/oracles/dotnet" -c Release -- vcards p o/vcards.jsonl >/dev/null
for l in icalendar vobject; do "$venv/bin/python" "$here/../oracles/py_facts.py" $l p o/$l.jsonl; done
mkdir -p o/icaljs o/biweekly o/ezvcard
: > m.icaljs; : > m.biweekly; : > m.ezvcard
for f in p/*; do b=$(basename "$f")
  printf 'text2json\t%s\t%s\n' "$PWD/$f" "$PWD/o/icaljs/$b.json" >> m.icaljs
  case "$b" in *.ics) printf 'ical-text2json\t%s\t%s\n' "$PWD/$f" "$PWD/o/biweekly/$b.json" >> m.biweekly;;
    *) printf 'card-text2json\t%s\t%s\n' "$PWD/$f" "$PWD/o/ezvcard/$b.json" >> m.ezvcard;; esac
done
ICALJS="$orc/npm/node_modules/ical.js" node "$here/../oracles/forms/icaljs.mjs" m.icaljs
cp="$orc/jars/*"
java -cp "$cp" "$here/../oracles/forms/Driver.java" m.biweekly; java -cp "$cp" "$here/../oracles/forms/Driver.java" m.ezvcard
{
  $S facts ours o/ours.jsonl; $S facts Ical.Net o/ical.jsonl; $S facts FolkerKinzel o/vcards.jsonl
  $S facts icalendar o/icalendar.jsonl; $S facts vobject o/vobject.jsonl
  $S json ical.js o/icaljs; $S json biweekly o/biweekly; $S json ez-vcard o/ezvcard
} | sort -t$'\t' -k2,2 -k1,1 | tee report.tsv
