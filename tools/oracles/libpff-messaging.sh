#!/usr/bin/env bash
# Compares what the messaging layer reads with what the libpff tools export (pffexport -f all -m all): the folder tree, the items in
# each folder and, for each item, every value pffexport writes (times, size, flags, subject, sender, importance, recipients, body files,
# internet headers, conversation index, attachment bytes, embedded messages). Items libpff recovers are counted separately.
# It also checks that every property bag equals the raw lists-tables-properties entries.
#
#   tools/oracles/libpff-messaging.sh <file> [pstoracle.dll]
#
# Needs the libpff tools unpacked user-locally (`. $ONEMAIL_LIBPFF_ENV`, default: env.sh in the oracle directory), the built
# PstOracle (tests/PstOracle) and a dotnet on the PATH. The libpff tools are run, never linked. Exit status is 0 when everything agrees.
set -euo pipefail
file="${1:?usage: libpff-messaging.sh <file> [pstoracle.dll]}"
dll="${2:-${ONEMAIL_ARTIFACTS:-$HOME/onemail/artifacts}/bin/PstOracle/debug/RecursiveAI.OneMail.PstOracle.dll}"
set +u
. "${ONEMAIL_LIBPFF_ENV:-$HOME/onemail/oracles/libpff/env.sh}"
set -u

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
echo "== $(basename "$file")"
pffexport -f all -m all -t "$work/out" "$file" >"$work/log.txt" 2>&1 || true
echo "pffexport messages: $(find "$work/out.export" -type d -name 'Message*' | wc -l), recovered: $(find "$work/out.recovered" -type d -name 'Message*' 2>/dev/null | wc -l)"
status=0
dotnet "$dll" propexact "$file" || status=$?
dotnet "$dll" messaging "$file" "$work/out.export" ${VERBOSE:+v} || status=$?
exit $status
