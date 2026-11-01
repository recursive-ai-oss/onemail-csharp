#!/usr/bin/env bash
# DBX deleted-message recovery: generated stores (messages deleted and unlinked the way docs/recovery.md describes) and the fetched real corpus.
# Prints, per file, the recovered count per tag, checks the byte ranges by re-reading, checks that nothing recovered is live, and checks recall
# against the generated manifest. Then runs undbx in recovery mode on the same files for a count comparison. Exit status = violations.
#
#   tools/recovery/check-dbx.sh [work dir]            (default ~/onemail/recovery-check)
#
# Needs: a .NET 10 SDK on the PATH; undbx from tools/oracles/dbx-oracles.sh (ONEMAIL_DBX_ORACLES, default ~/onemail/oracles/dbx) for the comparison.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:-$HOME/onemail/recovery-check}"
oracles="${ONEMAIL_DBX_ORACLES:-$HOME/onemail/oracles/dbx}"
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/dbx"
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$work/artifacts}"
mkdir -p "$work"
rm -rf "$work/gen" "$work/undbx"
dotnet publish "$here/../dbx/DbxTool" -c Release -o "$work/tool" -v quiet -clp:NoSummary || exit 1
tool="$work/tool/DbxTool.dll"
rc=0
dotnet "$tool" gen-deleted "$work/gen" || exit 1
echo "== generated stores"
dotnet "$tool" recover-check "$work/gen" "$work/gen/manifest.tsv" | tee "$work/generated.txt" || rc=$((rc + 1))
if [[ -d "$corpus" ]]; then
  echo "== real corpus (CFReDS Hacking Case)"
  dotnet "$tool" recover-check "$(dirname "$(find "$corpus" -iname Inbox.dbx | head -1)")" | tee "$work/corpus.txt" || rc=$((rc + 1))
fi
if [[ -x "$oracles/root/usr/bin/undbx" ]]; then
  echo "== undbx -r (messages it writes per file, with and without -r)"
  for f in "$work"/gen/*.dbx $(find "$corpus" -iname '*.dbx' ! -iname Folders.dbx 2>/dev/null | head -400); do
    n=$(basename "$f" .dbx)
    plain=$(mktemp -d); rec=$(mktemp -d)
    "$oracles/root/usr/bin/undbx" -v 0 "$f" "$plain" >/dev/null 2>&1
    "$oracles/root/usr/bin/undbx" -v 0 -r "$f" "$rec" >/dev/null 2>&1
    a=$(find "$plain" -type f -name '*.eml' | wc -l); b=$(find "$rec" -type f -name '*.eml' | wc -l)
    [[ "$a" != "$b" ]] && echo "undbx $n: $a messages, $b with -r"
    rm -rf "$plain" "$rec"
  done | tee "$work/undbx.txt"
fi
exit $rc
