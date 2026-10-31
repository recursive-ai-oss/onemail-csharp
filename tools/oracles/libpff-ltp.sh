#!/usr/bin/env bash
# Compares what the LTP layer reads with what the libpff tools export, at the level pffexport exposes: the folder hierarchy,
# the items in each folder (subject, size, flags, submit/delivery/creation times, sender, conversation topic) and the
# recipient and attachment counts of each item.
#
#   tools/oracles/libpff-ltp.sh <file> [pstoracle.dll]
#
# Needs the libpff tools unpacked user-locally (`. $ONEMAIL_LIBPFF_ENV`, default: env.sh in the oracle directory), the built
# PstOracle (tests/PstOracle) and a dotnet on the PATH. The libpff tools are run, never linked. Exit status is 0 when the two
# agree on everything compared. Light job: one process, a few seconds per small file.
set -euo pipefail
file="${1:?usage: libpff-ltp.sh <file> [pstoracle.dll]}"
dll="${2:-${ONEMAIL_ARTIFACTS:-$HOME/onemail/artifacts}/bin/PstOracle/debug/RecursiveAI.OneMail.PstOracle.dll}"
# shellcheck disable=SC1090
# the env file extends LD_LIBRARY_PATH without defining it first
set +u
oracle=libpff
. "${ONEMAIL_LIBPFF_ENV:-$HOME/onemail/oracles/$oracle/env.sh}"
set -u

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
echo "== pffexport"
pffexport -f all -m all -t "$work/out" "$file" >"$work/log.txt" 2>&1 || true
echo "messages exported: $(find "$work/out.export" -type d -name 'Message*' | wc -l)"
# pffexport ends with a pass over recovered (unlinked) items; its complaints there are about stale data, not the live tree.
marker=$(grep -n 'Exporting recovered items' "$work/log.txt" | head -1 | cut -d: -f1 || true)
marker=${marker:-999999999}
awk -v m="$marker" '/unable|mismatch/ { if (NR < m) live++; else recovered++ } END { printf "pffexport complaints: live tree %d, recovered-items pass %d\n", live, recovered }' "$work/log.txt"
echo "== ours"
dotnet "$dll" ltp "$file" | grep -E '^ltp (pcs|properties|values|defects|defect|notHeap)'
echo "== compare"
dotnet "$dll" compare "$file" "$work/out.export"
