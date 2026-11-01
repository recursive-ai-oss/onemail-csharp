#!/usr/bin/env bash
# PST/OST recovery campaigns: each damage class over a generated store and the fetched corpus files, with the libpff tools' counts beside ours.
#
#   bash tools/recovery/check-pst.sh <out dir> [messages]        (default 3000 generated messages)
#
# Per file: `PstOracle pstcampaign` damages a copy in each class (header zeroed, root pages zeroed, allocation maps, density list, random corruption
# at 0.1 / 1 / 5 %, truncation at 25 / 50 / 90 %), recovers it, counts what came back against the intact original (property-exact by message
# signature), repairs into a fresh PST and checks that opens with no defect. Then `pffexport -m all` runs on each damaged file and on each repaired
# file: messages exported and items it lists as recovered, next to ours. Needs a .NET 10 SDK on the PATH, the corpus under ~/onemail/corpus and the
# libpff tools (tools/oracles; ONEMAIL_LIBPFF_ENV, default ~/onemail/oracles/libpff/env.sh), which are run and never linked.
set -uo pipefail
out="${1:?out dir}"; n="${2:-3000}"
mkdir -p "$out"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$out/artifacts}" TMPDIR="${TMPDIR:-$out/tmp}"; mkdir -p "$TMPDIR"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dotnet build "$here/tests/PstOracle" -c Release -m:4 -v quiet -clp:NoSummary > "$out/build.log" 2>&1 || { tail -20 "$out/build.log"; exit 1; }
ora="$ONEMAIL_ARTIFACTS/bin/PstOracle/release/RecursiveAI.OneMail.PstOracle.dll"
set +u; . "${ONEMAIL_LIBPFF_ENV:-$HOME/onemail/oracles/libpff/env.sh}" 2>/dev/null; set -u
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/pst"

dotnet "$ora" pstgen "$out/generated.pst" "$n" 12 10 > "$out/gen.log" 2>&1
files=("$out/generated.pst")
while IFS= read -r f; do files+=("$f"); done < <(find "$corpus" -type f \( -iname '*.pst' -o -iname '*.ost' \) 2>/dev/null | sort)

rc=0
for f in "${files[@]}"; do
  name="$(basename "$f")"; w="$out/work-$name"; rm -rf "$w"; mkdir -p "$w"
  echo "=== $name"
  dotnet "$ora" pstcampaign "$f" "$w" 2>"$w/stderr.txt" | grep -v '^units' | tee "$w/campaign.tsv" || rc=1
  if command -v pffexport >/dev/null; then
    echo "-- libpff: pffexport -m all (items exported / items under .recovered)"
    for d in "$w"/*.pst; do
      b="$(basename "$d" .pst)"; case "$b" in *.repaired) kind=repaired ;; *) kind=damaged ;; esac
      t="$(mktemp -d "$TMPDIR/pff.XXXXXX")"
      timeout 300 pffexport -f all -m all -t "$t/o" "$d" > "$t/log.txt" 2>&1
      # pffexport names an item's directory after its type (Message, Contact, Appointment, Task, ...) and a five-digit number.
      m=$(find "$t/o.export" -type d -regextype posix-extended -regex '.*/[A-Za-z]+[0-9]{5}' ! -name 'Attachment*' ! -path '*/Attachment*/*' 2>/dev/null | wc -l)
      r=$(find "$t/o.recovered" -type d -regextype posix-extended -regex '.*/[A-Za-z]+[0-9]{5}' ! -name 'Attachment*' ! -path '*/Attachment*/*' 2>/dev/null | wc -l)
      printf '%s\t%s\titems %s\trecovered %s\n' "$b" "$kind" "$m" "$r"
      rm -rf "$t"
    done | tee "$w/libpff.tsv"
  fi
done
exit $rc
