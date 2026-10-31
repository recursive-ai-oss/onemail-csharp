#!/usr/bin/env bash
# Constant-memory check for the DBX reader: writes message databases of growing size and walks each in a fresh process, printing the
# peak working set, the managed allocation per message and the time (DbxTool genbig / enum).
#
#   tools/dbx/memory.sh [work dir] [counts...]       (default ~/onemail/dbx-memory; 100000 400000 1200000)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:-$HOME/onemail/dbx-memory}"
shift || true
counts=("$@")
[[ ${#counts[@]} -gt 0 ]] || counts=(100000 400000 1200000)
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$work/artifacts}"
mkdir -p "$work"
dotnet publish "$here/DbxTool" -c Release -o "$work/tool" -v quiet -clp:NoSummary
for n in "${counts[@]}"; do
  dotnet "$work/tool/DbxTool.dll" genbig "$work/big-$n.dbx" "$n"
  echo "== $n messages, file $(stat -c %s "$work/big-$n.dbx") bytes"
  echo -n "measure: "; dotnet "$work/tool/DbxTool.dll" enum "$work/big-$n.dbx"
  echo -n "read:    "; dotnet "$work/tool/DbxTool.dll" enum "$work/big-$n.dbx" read
  rm -f "$work/big-$n.dbx"
done
