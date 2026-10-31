#!/usr/bin/env bash
# vCard checks on the worker: the typed view against vobject and FolkerKinzel.VCards, over the fetched corpus, the generated vCards and the
# vCards the contact conversion produces; and the byte-identical round trip of the untouched files.
#   vcard_checks.sh            needs the corpora (tools/calendar/corpus.py fetch) and run.sh's export stage first
# State goes under $WORK (default ~/onemail/work). Prints one summary block per root.
set -euo pipefail
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
here="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${WORK:-$HOME/onemail/work}"
CORPUS="${CORPUS:-$HOME/onemail/corpus}"
VENV="${VENV:-$HOME/onemail/work/venv-cal}"
export ONEMAIL_ARTIFACTS="$WORK/artifacts" TMPDIR="$WORK/tmp"
mkdir -p "$WORK/facts" "$TMPDIR"
tool() { dotnet "$ONEMAIL_ARTIFACTS/bin/ContactTool/release/ContactTool.dll" "$@"; }
dotnet build "$here/tools/contacts/ContactTool" -c Release -v quiet -clp:NoSummary
dotnet build "$here/tools/calendar/oracles/dotnet" -c Release -v quiet -clp:NoSummary
oracle() { dotnet "$ONEMAIL_ARTIFACTS/bin/OracleFacts/release/OracleFacts.dll" "$@"; }

# Every .vcf of the fetched corpora in one folder, keeping the relative paths.
rm -rf "$WORK/vcf-corpus"; mkdir -p "$WORK/vcf-corpus"
(cd "$CORPUS/calendar" && find . -iname '*.vcf' -print0 | xargs -0 cp --parents -t "$WORK/vcf-corpus")
rm -rf "$WORK/vcf-gen"; mkdir -p "$WORK/vcf-gen"
python3 "$here/tools/contacts/make_vcards.py" "$WORK/vcf-gen" 20261031 60
cp "$here"/testdata/calendar/rfc/*.vcf "$WORK/vcf-gen/" 2>/dev/null || true
cp "$here"/testdata/calendar/generated/*.vcf "$WORK/vcf-gen/" 2>/dev/null || true

for root in vcf-corpus vcf-gen out/vcf; do
  r="$WORK/$root"; tag="$(echo "$root" | tr '/' '-')"
  echo "== $root: $(find "$r" -iname '*.vcf' | wc -l) files"
  tool vfacts "$r" "$WORK/facts/$tag.ours.jsonl"
  "$VENV/bin/python" "$here/tools/calendar/oracles/py_facts.py" vobject "$r" "$WORK/facts/$tag.vobject.jsonl"
  oracle vtyped "$r" "$WORK/facts/$tag.folker.jsonl"
  echo "-- vobject"
  python3 "$here/tools/contacts/compare_vcard.py" "$WORK/facts/$tag.ours.jsonl" "$WORK/facts/$tag.vobject.jsonl" --root "$r" | grep -E '^files|^total|UNEXPLAINED'
  echo "-- FolkerKinzel.VCards"
  python3 "$here/tools/contacts/compare_vcard.py" "$WORK/facts/$tag.ours.jsonl" "$WORK/facts/$tag.folker.jsonl" --root "$r" --no-types --skip KIND,LANG,MEMBER,UID,RELATED | grep -E '^files|^total|UNEXPLAINED'
done

echo "== byte-identical round trip of the untouched vCards"
dotnet build "$here/tools/calendar/CalendarDump" -c Release -v quiet -clp:NoSummary
dotnet "$ONEMAIL_ARTIFACTS/bin/CalendarDump/release/CalendarDump.dll" roundtrip "$WORK/vcf-gen" "$WORK/vcf-corpus" "$WORK/out/vcf" | tail -8
