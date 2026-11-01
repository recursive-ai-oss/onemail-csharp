#!/usr/bin/env bash
# Runs browser-check.sh for each vector file in each installed browser and prints one summary block per run.
#   tools/sanitizer/run-browsers.sh WORKDIR [vector files...]     (default: xss.txt and review.txt)
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="${1:?work directory}"; shift
files=("$@"); [ ${#files[@]} -gt 0 ] || files=("$repo/testdata/sanitizer/xss.txt" "$repo/testdata/sanitizer/review.txt")
rc=0
for b in "$(command -v google-chrome || command -v chromium)" "$(command -v firefox || true)"; do
  [ -n "$b" ] || continue
  for v in "${files[@]}"; do
    name="$(basename "$b")-$(basename "$v" .txt)"
    rm -rf "$work/$name"
    echo "=== $name ($("$b" --version 2>/dev/null | head -1))"
    VECTORS="$v" BROWSER="$b" bash "$here/browser-check.sh" "$work/$name" 2>&1 | grep -v '^page-raw' | cut -c1-300 | tail -25 || true
    [ "${PIPESTATUS[0]}" = 0 ] || rc=1
  done
done
exit $rc
