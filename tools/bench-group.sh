#!/usr/bin/env bash
# One timed group: tools/bench-group.sh <out-dir> <mime|outlook> <filter>...   (filters are BenchmarkDotNet --filter globs)
set -u
out="${1:?out dir}"; kind="${2:?mime|outlook}"; shift 2
export ROWS_STAGES=bdn ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$(dirname "$out")/art-bench}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
set -f
"$here/bench/run-$kind-rows.sh" "$out" -f "$@"
