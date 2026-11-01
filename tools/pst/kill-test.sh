#!/usr/bin/env bash
# Stops the PST builder with SIGKILL at random moments and says what each leftover file is: refused by the readers (the header is written last, so an
# unfinished file has none) or a whole store that opens with no defect. Anything else is a failure.
#
#   tools/pst/kill-test.sh <work-dir> [runs=50] [messages=30000]
#
# Needs PstOracle built (CONFIG, default debug, under $ONEMAIL_ARTIFACTS/bin). The work directory should be on a real disk, not a RAM-backed temp.
set -u
work="${1:?work dir}"; runs="${2:-50}"; count="${3:-30000}"
config="${CONFIG:-debug}"
ora="${ONEMAIL_ARTIFACTS:?set ONEMAIL_ARTIFACTS}/bin/PstOracle/$config/RecursiveAI.OneMail.PstOracle.dll"
mkdir -p "$work"
refused=0; whole=0; bad=0; sizes=""
for i in $(seq 1 "$runs"); do
  f="$work/kill-$i.pst"; rm -f "$f"
  dotnet "$ora" pstgen "$f" "$count" 5 10 > /dev/null 2>&1 &
  pid=$!
  # 0.15 s to 2.5 s: from before the first block to after the last write
  sleep "0.$(printf '%03d' $((RANDOM % 1000)))" ; [ $((RANDOM % 3)) -eq 0 ] && sleep 1
  kill -9 "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
  size=$(stat -c %s "$f" 2>/dev/null || echo 0)
  out=$(dotnet "$ora" propexact "$f" 2>&1 | head -3)
  if echo "$out" | grep -q '^open failed'; then
    refused=$((refused + 1)); kind=refused
  elif echo "$out" | grep -q 'differences 0;.*defects 0 dropped 0 ndbDefects 0'; then
    whole=$((whole + 1)); kind=whole
  else
    bad=$((bad + 1)); kind=BAD; echo "BAD run $i: $out"
  fi
  sizes="$sizes $kind:$((size / 1024 / 1024))MiB"
  rm -f "$f"
done
echo "kill test: $runs runs: $refused refused by the readers, $whole whole and clean, $bad other"
echo "leftovers:$sizes"
[ "$bad" -eq 0 ]
