#!/usr/bin/env bash
# Fetch the public TNEF (winmail.dat) data files used by the TNEF tests and the oracle comparison. Nothing here is committed or shipped:
# files land in
#   ${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/tnef/<set>/<file>
# and are listed in tnef-corpus.tsv with their source repository, commit and SHA-256. Only data files are fetched, never the code or
# expected-output files next to them. Both sets are Apache-2.0 data:
#   poi   apache/poi   test-data/hmef (6 TNEF files, including the minimised oom.tnef)
#   tika  apache/tika  testWINMAIL.dat of the Microsoft parser module (the same bytes as POI's quick-winmail.dat)
#
#   tools/corpus/fetch-tnef.sh
#
# Needs curl and sha256sum. Re-running verifies what is already there.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/tnef"

while IFS=$'\t' read -r set repo commit path bytes sha; do
  [[ "$set" == \#* || -z "$set" ]] && continue
  dest="$root/$set"; mkdir -p "$dest"
  file="$dest/$(basename "$path")"
  if [[ ! -f "$file" ]]; then
    curl -fL --retry 3 -sS -o "$file.part" "https://raw.githubusercontent.com/$repo/$commit/$path"
    mv "$file.part" "$file"
  fi
  echo "$sha  $file" | sha256sum -c --quiet - || { echo "checksum mismatch: $file" >&2; exit 1; }
done <"$here/tnef-corpus.tsv"
echo "tnef corpus ready in $root: $(find "$root" -type f | wc -l) files"
