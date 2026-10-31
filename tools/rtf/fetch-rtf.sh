#!/usr/bin/env bash
# Fetch the public .rtf test documents used to check the RTF to HTML converter. Nothing here is committed or shipped: files land in
#   ${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/rtf/<set>/<file>
# and are listed in rtf-corpus.tsv with their source repository, commit and SHA-256. Only data files are fetched, never the code or
# expected-output files next to them. Both sets are Apache-2.0 data:
#   tika  apache/tika  the .rtf files of the Microsoft parser module's test documents
#   poi   apache/poi   test-data/hmef/quick-contents/message.rtf
#
#   tools/rtf/fetch-rtf.sh
#
# Needs curl and sha256sum. Re-running verifies what is already there.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/rtf"

while IFS=$'\t' read -r set repo commit path bytes sha; do
  [[ "$set" == \#* || -z "$set" ]] && continue
  dest="$root/$set"; mkdir -p "$dest"
  file="$dest/$(basename "$path")"
  if [[ ! -f "$file" ]]; then
    curl -fL --retry 3 -sS -o "$file.part" "https://raw.githubusercontent.com/$repo/$commit/$path"
    mv "$file.part" "$file"
  fi
  echo "$sha  $file" | sha256sum -c --quiet - || { echo "checksum mismatch: $file" >&2; exit 1; }
done <"$here/rtf-corpus.tsv"
echo "rtf corpus ready in $root: $(find "$root" -name '*.rtf' | wc -l) files"
