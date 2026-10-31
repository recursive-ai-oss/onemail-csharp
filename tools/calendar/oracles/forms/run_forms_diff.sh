#!/usr/bin/env bash
# Differential run for the JSON and XML forms of iCalendar and vCard against released ical.js, biweekly, ez-vcard and icalendar.
#
#   run_forms_diff.sh <work dir> [corpus dir] [oracle dir]
#
# Defaults: corpus $ONEMAIL_CORPUS/calendar or $HOME/onemail/corpus/calendar, oracles $HOME/onemail/u18/oracles
# (see setup_forms.sh). Needs a built tools/calendar/FormsDump (dotnet on PATH) and the checkout's testdata.
#
# For every iCalendar/vCard text file of the corpus and the committed fixtures, four comparisons per oracle that has the form:
#   emit    the oracle writes jCal/jCard/xCal/xCard from the text; we write it from the same text
#   read-us the oracle reads OUR output and writes it again (does an independent reader accept it, and keep every property?)
#   read-it we read the ORACLE's output and write it again (do we keep every property of what another writer produced?)
# Each is reduced to one tuple per property and diffed by compare_forms.py; the classes are the output.
set -euo pipefail
work="${1:?work dir}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../../../.." && pwd)"
corpus="${2:-${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/calendar}"
orc="${3:-$HOME/onemail/u18/oracles}"
export PATH="$orc/node/bin:$orc/jdk/bin:$PATH"
dump="dotnet $FORMS_DUMP"
mkdir -p "$work"
cd "$work"
rm -rf ours orc rd-us rd-it manifests report
mkdir -p manifests report

# 1. Our output, per root (the root name keeps the ids unique).
roots=(biweekly ezvcard icaljs sabrevobject govuk thunderbird libical)
for r in "${roots[@]}"; do [[ -d "$corpus/$r" ]] && $dump emit "$corpus/$r" "ours/$r" >/dev/null; done
$dump emit "$repo/testdata/calendar/rfc" ours/rfc >/dev/null
$dump emit "$repo/testdata/calendar/forms/rfc" ours/forms-rfc >/dev/null
# ids: every text file we emitted; its source path sits next to the id in src.tsv
: >src.tsv
for r in "${roots[@]}"; do [[ -d "$corpus/$r" ]] && (cd "$corpus" && find "$r" \( -name '*.ics' -o -name '*.vcf' \) | sort | awk -v c="$corpus/" '{print $0 "\t" c $0}') >> src.tsv; done
for d in rfc:"$repo/testdata/calendar/rfc" forms-rfc:"$repo/testdata/calendar/forms/rfc"; do n="${d%%:*}"; p="${d#*:}"; (cd "$p" && find . \( -name '*.ics' -o -name '*.vcf' \) | sort | sed "s|^\./||" | awk -v n="$n" -v p="$p" '{print n "/" $0 "\t" p "/" $0}') >> src.tsv; done
# a text file the dumper could not emit has no ours/ file: drop it from the id list
awk -F'\t' '{print $1}' src.tsv | while read -r id; do [[ -f "ours/$id.json" ]] && echo "$id"; done > ids.txt
echo "inputs: $(wc -l < ids.txt)"

# 2. Manifests: <job> <input> <output>
mk() { : > "manifests/$1"; }
for t in icaljs biweekly ezvcard icalendar; do mk "$t.txt"; done
while read -r id; do
  src="$(awk -F'\t' -v i="$id" '$1==i{print $2; exit}' src.tsv)"
  ext="${id##*.}"
  for t in icaljs; do
    mkdir -p "orc/$t/$(dirname "$id")" "rd-us/$t/$(dirname "$id")"
    printf 'text2json\t%s\t%s\n' "$src" "$PWD/orc/$t/$id.json" >> manifests/icaljs.txt
    printf 'json2json\t%s\t%s\n' "$PWD/ours/$id.json" "$PWD/rd-us/$t/$id.json" >> manifests/icaljs.txt
  done
  if [[ "$ext" == ics ]]; then
    mkdir -p "orc/biweekly/$(dirname "$id")" "rd-us/biweekly/$(dirname "$id")" "orc/icalendar/$(dirname "$id")" "rd-us/icalendar/$(dirname "$id")"
    printf 'ical-text2json\t%s\t%s\nical-text2xml\t%s\t%s\nical-json2json\t%s\t%s\nical-xml2xml\t%s\t%s\n' \
      "$src" "$PWD/orc/biweekly/$id.json" "$src" "$PWD/orc/biweekly/$id.xml" "$PWD/ours/$id.json" "$PWD/rd-us/biweekly/$id.json" "$PWD/ours/$id.xml" "$PWD/rd-us/biweekly/$id.xml" >> manifests/biweekly.txt
    printf 'text2json\t%s\t%s\njson2json\t%s\t%s\n' "$src" "$PWD/orc/icalendar/$id.json" "$PWD/ours/$id.json" "$PWD/rd-us/icalendar/$id.json" >> manifests/icalendar.txt
  else
    mkdir -p "orc/ezvcard/$(dirname "$id")" "rd-us/ezvcard/$(dirname "$id")"
    printf 'card-text2json\t%s\t%s\ncard-text2xml\t%s\t%s\ncard-json2json\t%s\t%s\ncard-xml2xml\t%s\t%s\n' \
      "$src" "$PWD/orc/ezvcard/$id.json" "$src" "$PWD/orc/ezvcard/$id.xml" "$PWD/ours/$id.json" "$PWD/rd-us/ezvcard/$id.json" "$PWD/ours/$id.xml" "$PWD/rd-us/ezvcard/$id.xml" >> manifests/ezvcard.txt
  fi
done < ids.txt

# 3. Run the oracles
(ICALJS="$orc/npm/node_modules/ical.js" node "$here/icaljs.mjs" manifests/icaljs.txt) 2>&1 | tail -3
cp="$orc/jars/*"
java -cp "$cp" "$here/Driver.java" manifests/biweekly.txt 2>&1 | tail -3
java -cp "$cp" "$here/Driver.java" manifests/ezvcard.txt 2>&1 | tail -3
"$orc/venv/bin/python" "$here/icalendar_py.py" manifests/icalendar.txt 2>&1 | tail -3

# 4. We read the oracles' output
for t in icaljs biweekly icalendar ezvcard; do
  for f in json xml; do
    [[ -d "orc/$t" ]] && $dump reread "orc/$t" "rd-it/$t" $f >/dev/null || true
  done
done

# 5. Compare
cmp() { # name kind dirA dirB ids
  python3 "$here/compare_forms.py" "$2" "$3" "$4" --ids "$5" --report "$1" --rules "$here/explained.tsv" > "report/$1.txt" 2>&1 || true
  echo "== $1: $(head -2 "report/$1.txt" | cut -c1-230 | tr '\n' ' ')"
}
grep '\.ics$' ids.txt > ids-ics.txt; grep '\.vcf$' ids.txt > ids-vcf.txt
cmp icaljs-emit-ics json ours orc/icaljs ids-ics.txt;  cmp icaljs-emit-vcf json ours orc/icaljs ids-vcf.txt
cmp icaljs-read-us-ics json ours rd-us/icaljs ids-ics.txt; cmp icaljs-read-us-vcf json ours rd-us/icaljs ids-vcf.txt
cmp icaljs-read-it-ics json rd-it/icaljs orc/icaljs ids-ics.txt; cmp icaljs-read-it-vcf json rd-it/icaljs orc/icaljs ids-vcf.txt
cmp biweekly-emit-json json ours orc/biweekly ids-ics.txt; cmp biweekly-emit-xml xml ours orc/biweekly ids-ics.txt
cmp biweekly-read-us-json json ours rd-us/biweekly ids-ics.txt; cmp biweekly-read-us-xml xml ours rd-us/biweekly ids-ics.txt
cmp biweekly-read-it-json json rd-it/biweekly orc/biweekly ids-ics.txt; cmp biweekly-read-it-xml xml rd-it/biweekly orc/biweekly ids-ics.txt
cmp ezvcard-emit-json json ours orc/ezvcard ids-vcf.txt; cmp ezvcard-emit-xml xml ours orc/ezvcard ids-vcf.txt
cmp ezvcard-read-us-json json ours rd-us/ezvcard ids-vcf.txt; cmp ezvcard-read-us-xml xml ours rd-us/ezvcard ids-vcf.txt
cmp ezvcard-read-it-json json rd-it/ezvcard orc/ezvcard ids-vcf.txt; cmp ezvcard-read-it-xml xml rd-it/ezvcard orc/ezvcard ids-vcf.txt
cmp icalendar-emit-ics json ours orc/icalendar ids-ics.txt; cmp icalendar-read-us-ics json ours rd-us/icalendar ids-ics.txt
cmp icalendar-read-it-ics json rd-it/icalendar orc/icalendar ids-ics.txt
