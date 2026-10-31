#!/usr/bin/env bash
# Fetch the public .msg data files used by the MSG tests and the oracle comparison. Nothing here is committed or shipped:
# files land in
#   ${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/msg/<set>/<file>
# and are listed in msg-corpus.tsv with their source repository, commit and SHA-256. Only data files are fetched (the
# .msg files themselves), never the code or expected-output files next to them. Both sets are Apache-2.0 data:
#   tika  apache/tika  test-documents of the Microsoft parser module (14 .msg)
#   poi   apache/poi   test-data/hsmf (41 .msg, including the minimised fuzzer cases)
#
#   tools/corpus/fetch-msg.sh
#
# Needs curl and sha256sum. Re-running verifies what is already there.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/msg"

while IFS=$'\t' read -r set repo commit path bytes sha; do
  [[ "$set" == \#* || -z "$set" ]] && continue
  dest="$root/$set"; mkdir -p "$dest"
  file="$dest/$(basename "$path")"
  if [[ ! -f "$file" ]]; then
    curl -fL --retry 3 -sS -o "$file.part" "https://raw.githubusercontent.com/$repo/$commit/$path"
    mv "$file.part" "$file"
  fi
  echo "$sha  $file" | sha256sum -c --quiet - || { echo "checksum mismatch: $file" >&2; exit 1; }
done <"$here/msg-corpus.tsv"
echo "msg corpus ready in $root: $(find "$root" -name '*.msg' | wc -l) files"
