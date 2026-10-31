#!/bin/bash
# light.sh <corpus-name> <root> [diffrun options]  -- run one corpus through all four drivers at low priority. On a shared machine,
# set BENCH_LOCK to a file whose presence means a benchmark is running: the run will not start while it exists and stops if it appears.
# Work and output go under ONEMAIL_WORK (default ~/onemail/differential).
work=${ONEMAIL_WORK:-$HOME/onemail/differential}
export ONEMAIL_ARTIFACTS=${ONEMAIL_ARTIFACTS:-$work/artifacts} ONEMAIL_ORACLE_BIN=${ONEMAIL_ORACLE_BIN:-$work/bin} ONEMAIL_ORACLE_OUT=${ONEMAIL_ORACLE_OUT:-$work/out}
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lock=${BENCH_LOCK:-}
[ -n "$lock" ] && [ -e "$lock" ] && { echo "bench running; not starting"; exit 3; }
name="$1"; root="$2"; shift 2
setsid nice -n 10 bash "$here/run-corpus.sh" "$name" "$root" "$@" &
pid=$!
while kill -0 $pid 2>/dev/null; do
  if [ -n "$lock" ] && [ -e "$lock" ]; then kill -TERM -- -$pid 2>/dev/null; echo "bench started: stopped"; exit 4; fi
  sleep 20
done
wait $pid
