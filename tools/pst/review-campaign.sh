#!/usr/bin/env bash
# Final campaign for the in-place commit path: exhaustive power-cut replays (the header torn too), process kills, soaks, and leftovers through the libpff tools.
# Run on a machine with the corpus under ~/onemail/corpus and the libpff tools unpacked (tools/oracles/libpff-messaging.sh). Output: stdout; files under $ONEMAIL_WORK.
set -u
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
O="${ONEMAIL_WORK:-$HOME/onemail/work}/inplace-review"
rm -rf "$O"; mkdir -p "$O/tmp"
export ONEMAIL_ARTIFACTS="$O/art" TMPDIR="$O/tmp"
R="${1:?usage: review-campaign.sh <checkout>}"   # the repository checkout
cd "$R"
for p in tests/Durability tests/PstOracle; do dotnet build $p -c Checked -m:8 -v q -clp:NoSummary 2>&1 | grep -E 'error|warn' | sort -u | head; done
D="$O/art/bin/Durability/checked/RecursiveAI.OneMail.Durability.dll"
ORA="$O/art/bin/PstOracle/checked/RecursiveAI.OneMail.PstOracle.dll"
cd "$O"
echo "== replays: every cut, 5 leftover kinds each"
for b in ours tika1 tika2; do dotnet $D campaign "$O/camp" --bases $b --cases 100000 --parallel 8 2>&1 | cut -c1-330 | grep -v '^$'; done
echo "== replays with the 564-byte header torn at the sector boundary (the commit's stated assumption)"
DURABILITY_TEAR_HEADER=1 dotnet $D campaign "$O/camp-tear" --bases ours,tika1 --scenarios add,folders,mixed --cases 100000 --parallel 8 2>&1 | grep -E 'campaign:|^ours|^tika' | cut -c1-330
echo "== process kills"
for b in ours tika1 tika2; do
  for s in add delete folders grow split named mixed; do
    dotnet $D kill "$O/kills" $b $s 80 deterministic --parallel 8 2>&1 | head -4
    dotnet $D kill "$O/kills" $b $s 40 random --parallel 8 2>&1 | head -4
  done
done
echo "== soak"
for b in ours tika1 tika2; do dotnet $D soak "$O/soak" $b 9000 91 --commit-every 30 2>&1 | tail -3 | cut -c1-300; done
echo "== leftovers through libpff"
dotnet $D leftovers "$O/left" --per-phase 3
set +u; . "${ONEMAIL_LIBPFF_ENV:-$HOME/onemail/oracles/libpff/env.sh}"; set -u
ok=0; bad=0
for f in "$O"/left/*.pst; do
  if pffinfo "$f" > "$O/pffinfo.out" 2>&1; then ok=$((ok+1)); else bad=$((bad+1)); echo "pffinfo failed: $(basename "$f")"; fi
done
echo "pffinfo over every leftover: $ok opened, $bad failed"
ls "$O"/left | sed -E 's/.*-(old|new|mid|FAIL)(-invalidmaps)?\.pst/\1\2/' | sort | uniq -c
n=0; agree=0; dis=0
for f in $(ls "$O"/left/*.pst | grep -v invalidmaps | grep -v FAIL | awk 'NR%2==1'); do
  out=$(cd "$R" && timeout 120 bash tools/oracles/libpff-messaging.sh "$f" "$ORA" 2>&1); rc=$?
  n=$((n+1))
  if [ $rc -eq 0 ]; then agree=$((agree+1)); else dis=$((dis+1)); echo "DIFF $(basename "$f"): $(echo "$out" | tail -2 | tr '\n' ' ' | cut -c1-250)"; fi
done
echo "libpff-messaging over every second leftover with valid maps: $n files, $agree agree, $dis differ"
echo "== done"
