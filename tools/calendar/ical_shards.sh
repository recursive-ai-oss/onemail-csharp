#!/usr/bin/env bash
# Run the Ical.Net expansion oracle over a cases file in parallel shards (each shard restarts itself after a timeout).
#   ical_shards.sh <cases.jsonl> <out.jsonl> [shards]
set -uo pipefail
cases="$1"; out="$2"; n="${3:-8}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
dotnet build "$repo/tools/calendar/oracles/OracleExpand" -c Release -v quiet -clp:NoSummary >/dev/null || exit 1
dll="$(find "${ONEMAIL_ARTIFACTS:-$repo/artifacts}/bin/OracleExpand/release" -name OracleExpand.dll | head -1)"
split -n "l/$n" -d "$cases" "$work/shard."
for s in "$work"/shard.??; do
  (
    done_cases=0; rc=75
    while [ "$rc" = 75 ]; do
      rc=0; dotnet "$dll" "$s" "$s.out" "$done_cases" || rc=$?
      done_cases=$(wc -l < "$s.out")
    done
  ) &
done
wait
cat "$work"/shard.??.out > "$out"
rm -rf "$work"
