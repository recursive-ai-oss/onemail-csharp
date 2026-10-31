#!/usr/bin/env bash
# Prints the libpff tools' view of a PST/OST (pffinfo header lines, pffexport counts) so it can be read beside ours:
#
#   tools/oracles/libpff-ndb.sh <file> [pstoracle.dll]
#
# Needs the libpff tools unpacked user-locally (`. $ONEMAIL_LIBPFF_ENV`, default ~/onemail/oracles/libpff/env.sh) and, for
# our side, the built PstOracle (tests/PstOracle) and a dotnet on the PATH. The libpff tools are run, never linked.
set -euo pipefail
file="${1:?usage: libpff-ndb.sh <file> [pstoracle.dll]}"
oracle_dll="${2:-}"
# shellcheck disable=SC1090
. "${ONEMAIL_LIBPFF_ENV:-$HOME/onemail/oracles/libpff/env.sh}"

echo "== pffinfo"
pffinfo "$file" | sed -n '/File size/,/Encryption type/p'

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
echo "== pffexport"
pffexport -f all -m all -t "$work/out" "$file" >"$work/log.txt" 2>&1 || true
echo "folders exported:  $(find "$work/out.export" -type d ! -name 'Message*' ! -name 'Attachment*' | wc -l)"
echo "messages exported: $(find "$work/out.export" -type d -name 'Message*' | wc -l)"
echo "items libpff could not read: $(grep -c 'unable to determine item type' "$work/log.txt" || true)"

if [[ -n "$oracle_dll" ]]; then
  echo "== ours"
  dotnet "$oracle_dll" dump "$file" | grep -E '^(format|length|nbt (nodes|type (Normal|Search|Assoc))|bbt|amaps|defects)'
fi
