#!/usr/bin/env bash
# Runs the calendar and contact rows (OneMail.Calendar vs Ical.Net 5.2.3 and FolkerKinzel.VCards 8.2.0) and prints the ratio table.
# Inputs: the holiday calendars that tools/calendar/corpus.py fetch puts under ONEMAIL_CORPUS/calendar (default ~/onemail/corpus);
# every other input is generated with fixed values. Run it through the machine's bench queue, alone on the machine.
#
#   run-calendar-rows.sh OUTDIR [BenchmarkDotNet args, e.g. -f '*RecurrenceRow*']
#
# Without extra arguments it also checks that both sides did equal work (verify.tsv), measures peak memory and bytes per operation
# per row in separate processes (peak.tsv) and times libical's parser where the distribution package is installed (libical.tsv; a
# native reference for the parse rows only). Arguments replace the default filter and skip those extras.
# Writes BenchmarkDotNet's results under OUTDIR/bdn, the table to OUTDIR/report.tsv, and the machine and its load before, during and after.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?output directory}"; shift || true
mkdir -p "$out"; out="$(cd "$out" && pwd)"
if [ -x "$HOME/dotnet10/dotnet" ]; then export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"; elif [ -f "$HOME/onemail/env.sh" ]; then . "$HOME/onemail/env.sh"; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$out/artifacts}"

machine() {
  echo "== $1 $(date -u +%FT%TZ)"; uptime; echo "cores: $(getconf _NPROCESSORS_ONLN)"
  (lscpu 2>/dev/null | grep -E 'Model name|Flags' | cut -c1-400) || sysctl -n machdep.cpu.brand_string hw.model 2>/dev/null || true
  dotnet --version; uname -srm
}
{ machine before; } | tee "$out/machine.txt"

dotnet build -c Release "$here/OneMail.Benchmarks" -v quiet -clp:NoSummary
dll="$ONEMAIL_ARTIFACTS/bin/OneMail.Benchmarks/release/OneMail.Benchmarks.dll"

dotnet "$dll" cdescribe > "$out/inputs.tsv"
dotnet "$dll" ccounts > "$out/counts.tsv"
dotnet "$dll" cverify | tee "$out/verify.tsv"

( while sleep 120; do { echo "== during $(date -u +%FT%TZ)"; uptime; } >> "$out/machine.txt"; done ) &
mon=$!
trap 'kill $mon 2>/dev/null || true' EXIT

if [ $# -eq 0 ]; then set -- -f '*IcsParse*' '*IcsWrite*' '*IcsEdit*' '*VCardParseRow*' '*VCardWriteRow*' '*VCardEditRow*' '*RecurrenceRow*' '*Forms*'; full=1; else full=0; fi
dotnet "$dll" --artifacts "$out/bdn" "$@" > "$out/bdn.log" 2>&1 || { tail -30 "$out/bdn.log"; exit 1; }
[ -d "$out/bdn/results" ] || { echo "no results; see $out/bdn.log"; tail -30 "$out/bdn.log"; exit 1; }

if [ "$full" = 1 ]; then
  : > "$out/peak.tsv"
  while IFS=$'\t' read -r row input; do
    for side in ours theirs; do
      DOTNET_GCgen0MaxBudget=2000000 dotnet "$dll" cpeak "$row" "$side" "$input" | grep '^PEAK' >> "$out/peak.tsv" || true
    done
  done < <(for i in ics-invite ics-holidays ics-50k; do printf 'icsparse\t%s\nicsedit\t%s\n' $i $i; done
           for i in build-1 build-1k build-50k; do printf 'icswrite\t%s\n' $i; done
           for i in vcf-1 vcf-10k; do printf 'vcfparse\t%s\nvcfedit\t%s\n' $i $i; done
           for i in build-1 build-10k; do printf 'vcfwrite\t%s\n' $i; done
           for i in daily-utc weekly-floating monthly-ny yearly-berlin daily-ny-long; do printf 'recur\t%s\n' $i; done)

  # libical's parser, from the distribution's runtime package (no source build): the C program declares the two public functions it calls.
  if [ -e /usr/lib/x86_64-linux-gnu/libical.so.3 ] || [ -e /usr/lib/aarch64-linux-gnu/libical.so.3 ]; then
    if gcc -O2 "$here/native/libical_parse.c" -o "$out/libical_parse" -l:libical.so.3 2>"$out/libical-build.log"; then
      mkdir -p "$out/ics"
      dotnet "$dll" cwrite "$out/ics" > /dev/null
      printf 'input\tmedian_ns\n' > "$out/libical.tsv"
      for f in "$out"/ics/*.ics; do printf '%s\t%s\n' "$(basename "$f" .ics)" "$("$out/libical_parse" "$f" 30)" >> "$out/libical.tsv"; done
      rm -rf "$out/ics"
    fi
  fi
fi

kill $mon 2>/dev/null || true
{ machine after; } | tee -a "$out/machine.txt"
dotnet "$dll" report "$out/bdn/results" "$out/counts.tsv" | tee "$out/report.tsv"
