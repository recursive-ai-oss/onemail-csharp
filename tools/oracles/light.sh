#!/bin/bash
# light.sh <corpus-name> <root> [diffrun options]  -- worker-side: run one corpus through all four drivers at low priority, with a
# watchdog that stops it if a benchmark starts. Output under ~/onemail/u1b/out.
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 ONEMAIL_ARTIFACTS=$HOME/onemail/u1b/artifacts ONEMAIL_ORACLE_BIN=$HOME/onemail/u1b/bin ONEMAIL_ORACLE_OUT=$HOME/onemail/u1b/out
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -e ~/WORKER-BENCH.lock ] && { echo "bench running; not starting"; exit 3; }
name="$1"; root="$2"; shift 2
setsid nice -n 10 bash "$here/run-corpus.sh" "$name" "$root" "$@" &
pid=$!
while kill -0 $pid 2>/dev/null; do
  if [ -e ~/WORKER-BENCH.lock ]; then kill -TERM -- -$pid 2>/dev/null; echo "bench started: stopped"; exit 4; fi
  sleep 20
done
wait $pid
