#!/usr/bin/env bash
# Fuzzes several targets side by side for the same time: builds and instruments once, then starts one libFuzzer process per target
# and prints one line per target (executions, features, findings).
#
#   tools/fuzz/campaign.sh <seconds> <target>...
#
# Same environment as fuzz/run.sh (ONEMAIL_FUZZ_WORK, ONEMAIL_FUZZ_TOOLS, DOTNET). Logs go to $ONEMAIL_FUZZ_WORK/logs/<target>.log.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
seconds="${1:?seconds}"; shift
work="${ONEMAIL_FUZZ_WORK:?set ONEMAIL_FUZZ_WORK}"
tools="${ONEMAIL_FUZZ_TOOLS:?set ONEMAIL_FUZZ_TOOLS}"
dotnet_bin="${DOTNET:-dotnet}"
bin="$work/bin"; rm -rf "$bin"; mkdir -p "$bin" "$work/logs"
"$dotnet_bin" build "$root/fuzz/OneMail.Fuzz/OneMail.Fuzz.csproj" -c Checked -v quiet -clp:NoSummary -o "$bin"
for asm in RecursiveAI.OneMail RecursiveAI.OneMail.Calendar RecursiveAI.OneMail.Render RecursiveAI.OneMail.Cryptography RecursiveAI.OneMail.Client; do
  "$tools/sharpfuzz/sharpfuzz" "$bin/$asm.dll" >/dev/null
done
export FUZZ_PREBUILT="$bin"
for t in "$@"; do
  "$root/fuzz/run.sh" "$t" "$seconds" > "$work/logs/$t.log" 2>&1 &
done
wait || true
for t in "$@"; do
  log="$work/logs/$t.log"
  execs=$(grep -oE 'stat::number_of_executed_units: *[0-9]+' "$log" | tail -1 | grep -oE '[0-9]+$' || true)
  ft=$(grep -oE 'ft: *[0-9]+' "$log" | tail -1 | grep -oE '[0-9]+$' || true)
  found=$(find "$work/findings/$t" -type f 2>/dev/null | wc -l)
  printf 'FUZZ\t%s\texec=%s\tft=%s\tfindings=%s\n' "$t" "${execs:-?}" "${ft:-?}" "$found"
done
