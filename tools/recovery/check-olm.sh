#!/usr/bin/env bash
# OLM damaged-archive recovery: damages the sample archives with damage_olm.py, runs `onemail recover` on each variant and on the intact original, and
# compares the item counts (compare_olm.py). Exit status = number of crashes (a non-zero exit of the tool) plus unexplained losses.
#
#   tools/recovery/check-olm.sh [work dir]       (default ~/onemail/recovery-check/olm)
#
# Needs: a .NET 10 SDK on the PATH, python3, the OLM samples fetched by tools/corpus/fetch-olm.sh (ONEMAIL_CORPUS, default ~/onemail/corpus/olm).
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:-$HOME/onemail/recovery-check/olm}"
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/olm"
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$work/artifacts}"
mkdir -p "$work"
rm -rf "$work/damaged" "$work/out"; mkdir -p "$work/out"
dotnet publish "$here/../../src/OneMail.Cli" -c Release -o "$work/cli" -v quiet -clp:NoSummary || exit 1
rc=0
for olm in $(find "$corpus" -name '*.olm' | sort) ${ONEMAIL_EXTRA_OLM:-}; do
  n=$(basename "$olm" .olm)
  python3 "$here/damage_olm.py" "$olm" "$work/damaged/$n" || exit 1
  mkdir -p "$work/out/$n"
  dotnet "$work/cli/RecursiveAI.OneMail.Cli.dll" recover "$olm" "$work/out/$n/intact" >/dev/null 2>"$work/out/$n.intact.err" || { echo "CRASH $n intact"; rc=$((rc + 1)); }
  for v in "$work"/damaged/"$n"/*.olm; do
    vn=$(basename "$v" .olm)
    dotnet "$work/cli/RecursiveAI.OneMail.Cli.dll" recover "$v" "$work/out/$n/$vn" >/dev/null 2>"$work/out/$n/$vn.err" || { echo "CRASH $n $vn (exit $?)"; rc=$((rc + 1)); }
  done
  python3 "$here/compare_olm.py" "$work/out/$n" || rc=$((rc + $?))
done
exit $rc
