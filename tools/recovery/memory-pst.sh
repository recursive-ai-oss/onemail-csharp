#!/usr/bin/env bash
# Peak resident set of scan-mode recovery (header destroyed, so the whole index is rebuilt from a scan) and repair, over generated stores of growing size.
#
#   bash tools/recovery/memory-pst.sh <work dir> [message counts...]      default: 20000 80000 250000 (about 80 MB, 320 MB, 1 GB)
#
# The block and node index is held in memory (about 60 bytes per block and node), nothing else grows: the numbers show how the peak scales.
set -u
work="${1:?work dir}"; shift
runs=("$@"); [ ${#runs[@]} -eq 0 ] && runs=(20000 80000 250000)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$work/artifacts}" TMPDIR="${TMPDIR:-$work/tmp}"; mkdir -p "$TMPDIR" "$work"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dotnet build "$here/tests/PstOracle" -c Release -m:4 -v quiet -clp:NoSummary > "$work/build.log" 2>&1 || { tail -20 "$work/build.log"; exit 1; }
ora="$ONEMAIL_ARTIFACTS/bin/PstOracle/release/RecursiveAI.OneMail.PstOracle.dll"
echo "host: $(nproc) cores, $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2), load $(cut -d' ' -f1-3 /proc/loadavg)"
for n in "${runs[@]}"; do
  rm -f "$work"/*.pst "$work"/*.json
  dotnet "$ora" pstgen "$work/gen.pst" "$n" 12 10 2>&1 | grep -E 'pstgen' | sed 's/^/  /'
  dotnet "$ora" pstdamage "$work/gen.pst" "$work/bad.pst" header
  rm -f "$work/gen.pst"
  echo "== $n messages, $(( $(stat -c %s "$work/bad.pst") / 1048576 )) MiB, header destroyed"
  /usr/bin/time -f '  peak RSS %M KiB, elapsed %e s, cpu %U s user %S s sys' dotnet "$ora" pstrecover "$work/bad.pst" "$work/out.pst" "$work/out.json" 2>&1 | sed 's/^/  /'
  echo "  repaired file $(( $(stat -c %s "$work/out.pst") / 1048576 )) MiB"
done
rm -f "$work"/*.pst "$work"/*.json
echo "load after: $(cut -d' ' -f1-3 /proc/loadavg)"
