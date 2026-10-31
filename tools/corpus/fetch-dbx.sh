#!/usr/bin/env bash
# Fetch real Outlook Express stores for the DBX checks. Nothing here is committed or shipped: files land in
#   ${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/dbx/
# The source is the NIST CFReDS "Hacking Case" disk image (a Windows XP laptop, 2004, eight raw segments of about 4.5 GB in all). The
# script downloads the segments, checks each against dbx-corpus.tsv, and copies every *.dbx file out of the image's NTFS partition with
# the ntfs-3g command line tools (ntfsls and ntfscat, driven by ntfs_dbx.py from the distribution's package, unpacked with dpkg -x, never built; they read the
# image as a file and need no root). The image and the partition are deleted afterwards unless KEEP_IMAGE=1.
#
#   tools/corpus/fetch-dbx.sh
#
# Needs curl, sha256sum, apt-get (download only) and about 11 GB of free disk while it runs.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
base="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/dbx"
src="$base/hacking-case-image"
out="$base/hacking-case"
tools="${ONEMAIL_NTFS_TOOLS:-$HOME/onemail/oracles/ntfs}"
mkdir -p "$src" "$out" "$tools/debs"

if [[ ! -x "$tools/root/bin/ntfsls" ]]; then
  (cd "$tools/debs" && apt-get download ntfs-3g libntfs-3g89t64)
  for d in "$tools"/debs/*.deb; do dpkg -x "$d" "$tools/root"; done
fi
lib=$(dirname "$(find "$tools/root" -name 'libntfs-3g.so*' | head -1)")
export LD_LIBRARY_PATH="$lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

while IFS=$'\t' read -r name bytes sha; do
  [[ "$name" == \#* || -z "$name" ]] && continue
  if [[ ! -f "$src/$name" ]]; then
    curl -fL --retry 3 -sS -o "$src/$name.part" "https://cfreds-archive.nist.gov/images/hacking-dd/$name"
    mv "$src/$name.part" "$src/$name"
  fi
  [[ "$sha" == "-" ]] && { echo "$name $(stat -c %s "$src/$name") $(sha256sum "$src/$name" | cut -d' ' -f1)"; continue; }
  echo "$sha  $src/$name" | sha256sum -c --quiet - || { echo "checksum mismatch: $name" >&2; exit 1; }
done <"$here/dbx-corpus.tsv"

if [[ ! -f "$src/ntfs.img" ]]; then
  # The disk image holds one NTFS partition starting at sector 63.
  cat "$src"/SCHARDT.00[1-8] | tail -c +$((63 * 512 + 1)) >"$src/ntfs.img"
fi
python3 "$here/ntfs_dbx.py" "$tools/root/bin" "$src/ntfs.img" "/Documents and Settings" "$out"
find "$out" -name '*.dbx' -exec sha256sum {} + | sort -k2 >"$base/hacking-case.sha256"
[[ "${KEEP_IMAGE:-0}" == 1 ]] || rm -f "$src/ntfs.img"
echo "done: $(wc -l <"$base/hacking-case.sha256") files in $out"
