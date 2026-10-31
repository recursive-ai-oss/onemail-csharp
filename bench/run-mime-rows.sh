#!/usr/bin/env bash
# Runs the MIME rows (OneMail vs MimeKit) and prints the ratio table. Inputs come from the corpora fetched by
# tools/corpus/fetch.sh (ONEMAIL_CORPUS, default ~/onemail/corpus). Run it through the machine's bench queue, alone on the machine.
#
#   run-mime-rows.sh OUTDIR [BenchmarkDotNet args, e.g. --filter '*ParseRow*']
#
# Writes BenchmarkDotNet's results under OUTDIR/bdn, peak-memory lines to OUTDIR/peak.tsv and the table to OUTDIR/report.tsv,
# and records the machine and its load before, during and after.
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

( while sleep 120; do { echo "== during $(date -u +%FT%TZ)"; uptime; } >> "$out/machine.txt"; done ) &
mon=$!
trap 'kill $mon 2>/dev/null || true' EXIT

# Arguments replace the default filter; BenchmarkDotNet rejects a second --filter.
if [ $# -eq 0 ]; then set -- -f '*'; full=1; else full=0; fi
dotnet "$dll" --artifacts "$out/bdn" "$@" > "$out/bdn.log" 2>&1 || { tail -30 "$out/bdn.log"; exit 1; }
[ -d "$out/bdn/results" ] || { echo "no results; see $out/bdn.log"; tail -30 "$out/bdn.log"; exit 1; }

: > "$out/peak.tsv"
if [ "$full" = 1 ]; then
for in in enron-2k enron-20k dovecot-multipart generated-attachment-6m; do
  for row in parse headers bodies write; do
    for side in ours theirs; do dotnet "$dll" peak $row $side $in | grep '^PEAK' >> "$out/peak.tsv"; done
  done
done
for side in ours theirs; do dotnet "$dll" peak mbox $side dovecot | grep '^PEAK' >> "$out/peak.tsv"; dotnet "$dll" peak build $side attachment | grep '^PEAK' >> "$out/peak.tsv"; done
fi

kill $mon 2>/dev/null || true
{ machine after; } | tee -a "$out/machine.txt"
dotnet "$dll" report "$out/bdn/results" | tee "$out/report.tsv"
