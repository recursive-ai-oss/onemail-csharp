#!/usr/bin/env bash
# Fetch the two sample Outlook for Mac archives used by the OLM tests and the oracle comparison. Nothing here is committed or
# shipped: files land in
#   ${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/olm/<file>
# and are listed in olm-corpus.tsv with their source repository, commit and SHA-256. Only the two data files are fetched, never
# the code next to them (BSD-2-Clause data from teverett/OLMReader; notice in THIRD-PARTY-NOTICES.md).
#
#   tools/corpus/fetch-olm.sh
#
# Needs curl and sha256sum. Re-running verifies what is already there.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/olm"
mkdir -p "$root"

while IFS=$'\t' read -r set repo commit path bytes sha; do
  [[ "$set" == \#* || -z "$set" ]] && continue
  file="$root/$(basename "$path")"
  if [[ ! -f "$file" ]]; then
    curl -fL --retry 3 -sS -o "$file.part" "https://raw.githubusercontent.com/$repo/$commit/$path"
    mv "$file.part" "$file"
  fi
  echo "$sha  $file" | sha256sum -c --quiet - || { echo "checksum mismatch: $file" >&2; exit 1; }
done <"$here/olm-corpus.tsv"
echo "olm corpus ready in $root: $(find "$root" -maxdepth 1 -name '*.olm' | wc -l) files"
