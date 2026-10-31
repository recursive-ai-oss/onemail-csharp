#!/usr/bin/env bash
# Fetches the test corpora into $ONEMAIL_CORPUS (default ~/onemail/corpus), verifies them against the pinned SHA-256
# values in corpora.tsv and unpacks them. Safe to re-run: a corpus that is present and matching is left alone.
#
#   fetch.sh [name ...]      names from corpora.tsv; default: enron-cmu dovecot-crlf mime4j (edo-enron-pst has no live source)
#   PIN=1 fetch.sh           an empty pin is filled in the printed line instead of failing (first fetch only)
#   SAMPLE_ONLY=1 fetch.sh   unpack only the 1-in-100 Enron sample (for machines that do not need all 517,000 files)
#
# Outputs under $ONEMAIL_CORPUS:
#   enron-cmu/maildir/...        the CMU Enron maildir        (messages are real people's mail: never commit, quote or upload)
#   enron-sample/maildir/...     1 file in 100 of the same, picked by a hash of the name (about 5,000 messages)
#   dovecot-crlf.msgs/NNNNNN.eml the Dovecot test mbox split at "From " lines
#   mime4j/testmsgs, mime4j/mimetools-testmsgs   message files only (*.msg) from the Apache James Mime4j test resources
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}"
mkdir -p "$corpus"

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
md5() { if command -v md5sum >/dev/null; then md5sum "$1" | cut -d' ' -f1; else md5 -q "$1"; fi; }
field() { awk -F'\t' -v n="$1" -v c="$2" '$1 == n { print $c }' "$here/corpora.tsv"; }

# fetch NAME: download if missing, then check the pin.
fetch() {
  local name="$1" file url pin path got
  file="$(field "$name" 2)"; pin="$(field "$name" 3)"; url="$(field "$name" 4)"
  [ -n "$file" ] || { echo "unknown corpus $name" >&2; return 1; }
  [ "$url" != - ] || { echo "$name: no live download (see corpora.tsv); skipped" >&2; return 0; }
  path="$corpus/$file"
  if [ ! -f "$path" ]; then
    echo "fetching $name"
    curl -fL --retry 3 --retry-delay 5 -o "$path.part" "$url"
    mv "$path.part" "$path"
  fi
  got="$(sha256 "$path")"
  if [ -z "$pin" ]; then
    [ "${PIN:-0}" = 1 ] || { echo "$name has no pinned hash yet; run with PIN=1 once and commit the printed line" >&2; return 1; }
    printf 'PIN\t%s\t%s\t%s\n' "$name" "$got" "$(wc -c < "$path" | tr -d ' ')"
  elif [ "$got" != "$pin" ]; then
    echo "$name: sha256 $got does not match the pin $pin" >&2; return 1
  fi
  echo "ok $name $got"
}

# unpacked MARKER_DIR: unpacking is skipped when the marker for this archive hash exists.
marker() { echo "$corpus/.unpacked-$1-$(sha256 "$corpus/$(field "$1" 2)" | cut -c1-16)"; }

for name in "${@:-enron-cmu dovecot-crlf mime4j}"; do
  for n in $name; do
    fetch "$n"
    case "$n" in
      enron-cmu)
        if [ ! -f "$(marker enron-cmu)" ]; then
          if [ "${SAMPLE_ONLY:-0}" != 1 ]; then
            mkdir -p "$corpus/enron-cmu"
            tar -xzf "$corpus/$(field enron-cmu 2)" -C "$corpus/enron-cmu"
          fi
          rm -rf "$corpus/enron-sample"
          python3 "$here/extract.py" sample "$corpus/$(field enron-cmu 2)" "$corpus/enron-sample" 100
          touch "$(marker enron-cmu)"
        fi ;;
      dovecot-crlf)
        if [ ! -f "$(marker dovecot-crlf)" ]; then
          rm -rf "$corpus/dovecot-crlf.msgs"
          python3 "$here/extract.py" mbox "$corpus/dovecot-crlf" "$corpus/dovecot-crlf.msgs"
          touch "$(marker dovecot-crlf)"
        fi ;;
      mime4j)
        if [ ! -f "$(marker mime4j)" ]; then
          rm -rf "$corpus/mime4j"
          python3 "$here/extract.py" mime4j "$corpus/$(field mime4j 2)" "$corpus/mime4j"
          touch "$(marker mime4j)"
        fi ;;
      edo-enron-pst)
        [ -f "$corpus/$(field edo-enron-pst 2)" ] || continue
        fetch edo-enron-pst-md5
        want="$(awk '$2 == "'"$(field edo-enron-pst 2)"'" { print $1 }' "$corpus/$(field edo-enron-pst-md5 2)")"
        have="$(md5 "$corpus/$(field edo-enron-pst 2)")"
        [ "$want" = "$have" ] || { echo "edo-enron-pst: md5 $have does not match the published $want" >&2; exit 1; }
        echo "ok edo-enron-pst md5 $have matches the published list" ;;
    esac
  done
done
