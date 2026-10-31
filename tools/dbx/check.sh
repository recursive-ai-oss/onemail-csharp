#!/usr/bin/env bash
# Generated Outlook Express stores against undbx: writes the stores with DbxTool, extracts them with OneMail and with undbx, and
# compares the messages of every folder as hashes (compare.py). Exit status is the number of differing folders.
#
#   tools/dbx/check.sh [work dir]            (default ~/onemail/dbx-check)
#
# Needs: undbx from tools/oracles/dbx-oracles.sh (ONEMAIL_DBX_ORACLES, default ~/onemail/oracles/dbx), a .NET 10 SDK on the PATH, python3.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:-$HOME/onemail/dbx-check}"
oracles="${ONEMAIL_DBX_ORACLES:-$HOME/onemail/oracles/dbx}"
extra="${ONEMAIL_DBX_STORES:-}"        # more store folders to check (real files), space separated
if [[ -z "$extra" ]]; then
  # every directory of the fetched real corpus that holds a Folders.dbx
  extra=$(find "${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/dbx" -iname Folders.dbx -printf '%h\n' 2>/dev/null | sort -u | tr '\n' ' ')
fi
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$work/artifacts}"
mkdir -p "$work"
rm -rf "$work/stores" "$work/ours" "$work/undbx"
dotnet publish "$here/DbxTool" -c Release -o "$work/tool" -v quiet -clp:NoSummary || exit 1
dotnet "$work/tool/DbxTool.dll" gen "$work/stores" || exit 1
rc=0
for s in "$work"/stores/* $extra; do
  n=$(basename "$s")
  dotnet "$work/tool/DbxTool.dll" extract "$s" "$work/ours/$n" || rc=$((rc + 1))
  mkdir -p "$work/undbx/$n"; "$oracles/root/usr/bin/undbx" -v 0 "$s" "$work/undbx/$n" >"$work/undbx-$n.log" 2>&1
  echo "== $n: $(dotnet "$work/tool/DbxTool.dll" summary "$s")"
  python3 "$here/compare.py" "$work/ours/$n" "$work/undbx/$n" || rc=$((rc + $?))
done
exit $rc
