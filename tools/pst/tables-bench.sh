#!/usr/bin/env bash
# Times one commit that adds, deletes or moves rows of a big folder, with the folder's tables changed row by row ("incremental") or written whole ("rewrite").
#
#   bash tools/pst/tables-bench.sh <out-dir> [sizes=1000,100000,600000] [modes=rewrite,incremental] [ops=add,delete,move] [counts=1,100,1000]
#
# For each size a store with one folder of that many items is built once; every case copies it, makes the changes in one commit and prints a RESULT line
# (time of the edits and of the commit, bytes written by the commit, peak working set). Lines also go to <out-dir>/results.txt.
set -u
out="${1:?out dir}"; sizes="${2:-1000,100000,600000}"; modes="${3:-rewrite,incremental}"; ops="${4:-add,delete,move}"; counts="${5:-1,100,1000}"
mkdir -p "$out"
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEMAIL_ARTIFACTS="$out/artifacts" TMPDIR="$out/tmp"; mkdir -p "$TMPDIR"
dotnet build tests/Durability -c Release -m:4 > "$out/build.log" 2>&1 || { tail -20 "$out/build.log"; exit 1; }
dur="$ONEMAIL_ARTIFACTS/bin/Durability/release/RecursiveAI.OneMail.Durability.dll"
echo "# $(date -u +%FT%TZ) $(nproc) cores, load $(cut -d' ' -f1-3 /proc/loadavg), $(dotnet --version)" | tee -a "$out/results.txt"
for size in ${sizes//,/ }; do
  base="$out/base-$size.pst"
  [ -f "$base" ] || dotnet "$dur" tables-make "$base" "$size" | tee -a "$out/results.txt"
  for op in ${ops//,/ }; do
    for count in ${counts//,/ }; do
      for mode in ${modes//,/ }; do
        dotnet "$dur" tables "$base" "$out/work.pst" "$mode" "$op" "$count" | tee -a "$out/results.txt"
        rm -f "$out/work.pst"
      done
    done
  done
  rm -f "$base"
done
echo "# done load $(cut -d' ' -f1-3 /proc/loadavg)" | tee -a "$out/results.txt"
