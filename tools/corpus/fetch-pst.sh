#!/usr/bin/env bash
# Fetch public Outlook data files for the PST/OST tests. Nothing here is committed or shipped: files land in
#   ${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/pst/<set>/
# and are listed in pst-corpus.tsv with their source, terms verdict and SHA-256.
#
#   tools/corpus/fetch-pst.sh cfreds-leakage     # NIST CFReDS "Data Leakage Case": the informant's Outlook 2013 OST
#
# Needs curl, sha1sum, sha256sum and a 7-Zip command-line (7z, 7za or 7zz). The disk image is unpacked to a scratch
# file (about 20 GB) that is removed once the Outlook files are copied out.
set -euo pipefail

root="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/pst"
set_name="${1:?usage: fetch-pst.sh <set>}"

sevenzip() {
  local c
  for c in 7zz 7z 7za; do command -v "$c" >/dev/null 2>&1 && { "$c" "$@"; return; }; done
  echo "no 7-Zip command-line found" >&2; return 127
}

fetch() { # url sha1 file
  local url="$1" sha1="$2" file="$3"
  if [[ ! -f "$file" ]]; then curl -fL --retry 3 -o "$file.part" "$url"; mv "$file.part" "$file"; fi
  echo "$sha1  $file" | tr 'A-F' 'a-f' | sha1sum -c -
}

case "$set_name" in
  cfreds-leakage)
    # Source: https://cfreds-archive.nist.gov/data_leakage_case/data-leakage-case.html (SHA-1 list on hash_values.html)
    dest="$root/$set_name"; mkdir -p "$dest"; cd "$dest"
    base=https://cfreds-archive.nist.gov/data_leakage_case/images/pc
    fetch "$base/cfreds_2015_data_leakage_pc.7z.001" F07632FAA66A47088DEB07BDB45CC568E4BF650B cfreds_2015_data_leakage_pc.7z.001
    fetch "$base/cfreds_2015_data_leakage_pc.7z.002" 5DEE46ABF6FA833268E5AE199A13854CCF42689B cfreds_2015_data_leakage_pc.7z.002
    fetch "$base/cfreds_2015_data_leakage_pc.7z.003" 1687686F819092E05047F195F102D8FA0C38ED66 cfreds_2015_data_leakage_pc.7z.003
    if ! ls ./*.ost ./*.pst >/dev/null 2>&1; then
      work="$(mktemp -d "$dest/unpack.XXXXXX")"
      trap 'rm -rf "$work"' EXIT
      sevenzip x -y -o"$work" cfreds_2015_data_leakage_pc.7z.001 >/dev/null
      img="$(ls "$work"/*.dd "$work"/*.001 "$work"/* 2>/dev/null | head -1)"
      # 7-Zip reads the partition table and the NTFS volume inside the raw image directly.
      sevenzip x -y -o"$work/vol" "$img" -ir'!*.ost' -ir'!*.pst' >/dev/null || true
      find "$work/vol" \( -iname '*.ost' -o -iname '*.pst' \) -type f -print0 | while IFS= read -r -d '' f; do
        cp -- "$f" "$dest/$(basename "$f")"
      done
    fi
    sha256sum -- ./*.ost ./*.pst 2>/dev/null || true
    ;;
  *) echo "unknown set $set_name" >&2; exit 2 ;;
esac
