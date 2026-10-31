#!/usr/bin/env bash
# Runs the Outlook-format rows (PST/OST, MSG, TNEF: OneMail vs XstReader.Api, MsgReader and MimeKit) and prints the ratio table.
# Inputs: the corpora that tools/corpus/fetch-*.sh fetch (ONEMAIL_CORPUS, default ~/onemail/corpus) and the large stores this script
# writes with the test-side PST writer into ONEMAIL_STORES (default ~/onemail/u17/stores). Run it through the machine's bench queue,
# alone on the machine.
#
#   run-outlook-rows.sh OUTDIR [BenchmarkDotNet args, e.g. -f '*TnefRow*']
#
# Without extra arguments it also checks that both sides did equal work (verify.tsv), measures peak memory per row in separate
# processes (peak.tsv), samples the working set over the large stores (curve.tsv), and times pffexport where it is installed
# (pffexport.tsv; a native reference only, it also writes every item to disk, mode items). Arguments replace the default filter and skip those extras.
# Writes BenchmarkDotNet's results under OUTDIR/bdn, the table to OUTDIR/report.tsv, and the machine and its load before, during and after.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?output directory}"; shift || true
mkdir -p "$out"; out="$(cd "$out" && pwd)"
if [ -x "$HOME/dotnet10/dotnet" ]; then export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"; elif [ -f "$HOME/onemail/env.sh" ]; then . "$HOME/onemail/env.sh"; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$out/artifacts}"
export ONEMAIL_STORES="${ONEMAIL_STORES:-$HOME/onemail/u17/stores}"

machine() {
  echo "== $1 $(date -u +%FT%TZ)"; uptime; echo "cores: $(getconf _NPROCESSORS_ONLN)"
  (lscpu 2>/dev/null | grep -E 'Model name|Flags' | cut -c1-400) || sysctl -n machdep.cpu.brand_string hw.model 2>/dev/null || true
  dotnet --version; uname -srm
}
{ machine before; } | tee "$out/machine.txt"

dotnet build -c Release "$here/OneMail.Benchmarks" -v quiet -clp:NoSummary
dll="$ONEMAIL_ARTIFACTS/bin/OneMail.Benchmarks/release/OneMail.Benchmarks.dll"

# The large stores are written once per machine (the writer is deterministic) and are not part of any timing.
dotnet "$dll" gen "$ONEMAIL_STORES" | tee "$out/gen.log"
dotnet "$dll" counts > "$out/counts.tsv"
dotnet "$dll" verify | tee "$out/verify.tsv"

( while sleep 120; do { echo "== during $(date -u +%FT%TZ)"; uptime; } >> "$out/machine.txt"; done ) &
mon=$!
trap 'kill $mon 2>/dev/null || true' EXIT

if [ $# -eq 0 ]; then set -- -f '*PstRow*' '*PstLargeRow*' '*PstFourKiBRow*' '*MsgRow*' '*TnefRow*'; full=1; else full=0; fi
dotnet "$dll" --artifacts "$out/bdn" "$@" > "$out/bdn.log" 2>&1 || { tail -30 "$out/bdn.log"; exit 1; }
[ -d "$out/bdn/results" ] || { echo "no results; see $out/bdn.log"; tail -30 "$out/bdn.log"; exit 1; }

if [ "$full" = 1 ]; then
  : > "$out/peak.tsv"; : > "$out/curve.tsv"
  while IFS=$'\t' read -r input _; do
    case "$input" in
      gen-*|cfreds-ost|tika-*) row=pst ;;
      msg-*) row=msg ;;
      *) row=tnef ;;
    esac
    for side in ours theirs; do DOTNET_GCgen0MaxBudget=0x2000000 dotnet "$dll" peak $row $side "$input" | grep '^PEAK' >> "$out/peak.tsv" || true; done
  done < "$out/counts.tsv"
  for input in gen-u10k gen-u50k; do
    for side in ours theirs; do DOTNET_GCgen0MaxBudget=0x2000000 dotnet "$dll" curve pst $side "$input" | grep '^CURVE' >> "$out/curve.tsv" || true; done
    # The comparator's documented cache release, for the memory comparison (it is much slower, so it is not a timed row).
    DOTNET_GCgen0MaxBudget=0x2000000 ONEMAIL_XST_CLEAR=1 dotnet "$dll" curve pst theirs "$input" | grep '^CURVE' | sed 's/^CURVE\(-END\)\?\ttheirs/CURVE\1\ttheirs-clear/' >> "$out/curve.tsv" || true
  done
  lp="${ONEMAIL_LIBPFF:-$HOME/onemail/oracles/libpff}"
  if [ -f "$lp"/env.sh ]; then
    ( cd "$lp" && set +u && . ./env.sh && set -u
      printf 'input\trun\twall_s\tmax_rss_KiB\n' > "$out/pffexport.tsv"
      for input in cfreds-ost tika-pst tika-bodytypes gen-u1k gen-u10k gen-u50k; do
        f=$(dotnet "$dll" path pst "$input" 2>/dev/null || true); [ -f "$f" ] || continue
        for run in 1 2 3; do
          rm -rf "$HOME/onemail/u17/pffout"; mkdir -p "$HOME/onemail/u17/pffout"
          /usr/bin/time -f "$input\t$run\t%e\t%M" -a -o "$out/pffexport.tsv" pffexport -q -m items -f text -t "$HOME/onemail/u17/pffout/x" "$f" > /dev/null 2>&1 || true
        done
      done
      rm -rf "$HOME/onemail/u17/pffout" )
  fi
fi

kill $mon 2>/dev/null || true
{ machine after; } | tee -a "$out/machine.txt"
dotnet "$dll" report "$out/bdn/results" "$out/counts.tsv" | tee "$out/report.tsv"
