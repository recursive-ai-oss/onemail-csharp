#!/usr/bin/env bash
# Runs the durability checks for in-place PST writing and compares edited files with the libpff tools. Output goes to <out>/report.txt.
#
#   bash tools/pst/inplace-check.sh <out-dir> [cases=300] [kills=100] [soak-ops=1000] [phases=replay,kill,soak,libpff]
#
#   1. power-cut replays: each scenario's writes are recorded, then the file is rebuilt as a power cut at many points could leave it (all earlier
#      writes; a random subset of the writes since the last flush; the last write torn at a sector boundary). Every leftover must open as one of the
#      states the scenario passes through.
#   2. process kills: the scenario runs in a child process on a real file, which is stopped with SIGKILL at the Nth write or flush, or after a random time.
#   3. soak: random operations with a check after every commit (allocation maps, counts, rows, read-back, freed space wiped).
#   4. every scenario's finished file, and the soak files, through pffinfo and pffexport against our reader.
#
# Needs the corpus under ~/onemail/corpus (the Tika PSTs) and, for step 4, the libpff tools (tools/oracles/libpff-messaging.sh).
set -u
out="${1:?out dir}"; cases="${2:-300}"; kills="${3:-100}"; soakops="${4:-1000}"; phases="${5:-replay,kill,soak,libpff}"
mkdir -p "$out"
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEMAIL_ARTIFACTS="$out/artifacts" TMPDIR="$out/tmp"; mkdir -p "$TMPDIR"
report="$out/report.txt"; : > "$report"
log() { echo "$*" | tee -a "$report"; }
dotnet build tests/Durability -c Checked -m:8 > "$out/build-durability.log" 2>&1 || { tail -20 "$out/build-durability.log"; exit 1; }
dotnet build tests/PstOracle -c Checked -m:8 > "$out/build-oracle.log" 2>&1 || { tail -20 "$out/build-oracle.log"; exit 1; }
dur="$ONEMAIL_ARTIFACTS/bin/Durability/checked/RecursiveAI.OneMail.Durability.dll"
rc=0
has() { case ",$phases," in *",$1,"*) return 0;; esac; return 1; }
has replay && { log "== power-cut replays"
dotnet "$dur" campaign "$out/campaign" --cases "$cases" --parallel 8 2>&1 | tee -a "$report"; [ "${PIPESTATUS[0]}" -eq 0 ] || rc=1; }
has kill && { log "== process kills"
for b in ours tika1 tika2; do
  for s in add delete folders grow split named mixed; do
    dotnet "$dur" kill "$out/kills" $b $s "$kills" deterministic --parallel 6 2>&1 | tee -a "$report"; [ "${PIPESTATUS[0]}" -eq 0 ] || rc=1
    dotnet "$dur" kill "$out/kills" $b $s "$((kills / 2))" random --parallel 6 2>&1 | tee -a "$report"; [ "${PIPESTATUS[0]}" -eq 0 ] || rc=1
  done
done; }
has soak && { log "== soak"
for b in ours tika1 tika2; do
  dotnet "$dur" soak "$out/soak" $b "$soakops" 11 --commit-every 100 2>&1 | tee -a "$report"; [ "${PIPESTATUS[0]}" -eq 0 ] || rc=1
done; }
has libpff && { log "== libpff"
dotnet "$dur" make "$out/edited" > /dev/null 2>&1
ora="$ONEMAIL_ARTIFACTS/bin/PstOracle/checked/RecursiveAI.OneMail.PstOracle.dll"
for f in "$out"/edited/*.pst "$out"/soak/*.pst; do
  bash tools/oracles/libpff-messaging.sh "$f" "$ora" 2>&1 | tail -4 | sed "s|^|$(basename "$f"): |" | tee -a "$report"
done; }
log "== done rc=$rc"
exit $rc
