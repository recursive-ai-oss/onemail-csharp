#!/usr/bin/env bash
# Fetch public Outlook data files for the PST/OST tests. Nothing here is committed or shipped: files land in
#   ${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/pst/<set>/
# and are listed in pst-corpus.tsv with their source, terms verdict and SHA-256.
#
#   tools/corpus/fetch-pst.sh cfreds-leakage     # NIST CFReDS "Data Leakage Case": the informant's Outlook 2013 OST
#   tools/corpus/fetch-pst.sh tika               # Apache Tika's two small Unicode PSTs (data files only, Apache-2.0)
#
# cfreds-leakage needs curl, sha1sum, sha256sum and a 7-Zip command-line (7z, 7za or 7zz). The disk image is unpacked to a
# scratch file (about 20 GB) that is removed once the Outlook files are copied out. tika needs curl and sha256sum.
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
      work="$dest/unpack.work"; mkdir -p "$work"
      trap 'rm -rf "$work"' EXIT
      # The 7z holds a raw disk image (~20 GB). 7-Zip reads its partition table as MBR and shows each partition as a file; the
      # second one (1.ntfs) is the Windows volume, which 7-Zip reads as NTFS.
      sevenzip x -y -o"$work" cfreds_2015_data_leakage_pc.7z.001 >/dev/null
      sevenzip x -y -o"$work" "$work/cfreds_2015_data_leakage_pc.dd" 1.ntfs >/dev/null
      sevenzip e -y -o"$dest" "$work/1.ntfs" 'Users/informant/AppData/Local/Microsoft/Outlook/iaman.informant@nist.gov.ost' >/dev/null
    fi
    sha256sum -- ./*.ost ./*.pst 2>/dev/null || true
    ;;
  tika)
    # Source: github.com/apache/tika at the commit below, the Microsoft parser module's test-documents. Only the data files are
    # fetched, never the Java code or expected-output files beside them.
    dest="$root/$set_name"; mkdir -p "$dest"; cd "$dest"
    base=https://raw.githubusercontent.com/apache/tika/c66a83c2b3be6a7595758ab3aae85e07ea23d248/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/resources/test-documents
    for entry in testPST.pst:f2a6b1d2cad00f574e3d1c1211c4b1c854d6526caea77213adc3da92b7813ae3 testPST_variousBodyTypes.pst:24c5e6bbb8bf26a817c977283e40e7b69d2661fec0845abbe177f97efcb05fb0; do
      f="${entry%%:*}"; sha="${entry##*:}"
      if [[ ! -f "$f" ]]; then curl -fL --retry 3 -sS -o "$f.part" "$base/$f"; mv "$f.part" "$f"; fi
      echo "$sha  $f" | sha256sum -c --quiet - || { echo "checksum mismatch: $f" >&2; exit 1; }
    done
    sha256sum -- ./*.pst
    ;;
  *) echo "unknown set $set_name" >&2; exit 2 ;;
esac
