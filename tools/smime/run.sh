#!/usr/bin/env bash
# S/MIME interop run: this library against OpenSSL and MimeKit in both directions, the published RFC 9216 example keys,
# and a scan of the corpora for S/MIME items.
#   tools/smime/run.sh <work dir> [corpus dir]
# Needs: dotnet on PATH, curl, and an `openssl` (or ONEMAIL_OPENSSL=/path/to/openssl). OpenSSL and MimeKit are run as released
# builds and treated as black boxes. Nothing fetched is committed.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:?usage: run.sh <work dir> [corpus dir]}"
corpus="${2:-$HOME/onemail/corpus}"
mkdir -p "$work"
bin="$work/bin"
dotnet build "$here/SmimeInterop" -c Release -v quiet -clp:NoSummary -o "$bin"
io="dotnet $bin/SmimeInterop.dll"

echo "== interop"
$io "$work/interop" | tee "$work/interop.log"

echo "== RFC 9216 example keys"
rfc="$work/rfc9216.txt"
[[ -s "$rfc" ]] || curl -sSL -o "$rfc" https://www.rfc-editor.org/rfc/rfc9216.txt
echo "06fbf1ed1fe5975a0e7842a73eee0b9d3625a8e790adcb5e5fa123c3bf6b2f71  $rfc" | sha256sum -c -
$io rfc9216 "$rfc" | tail -20 | tee "$work/rfc9216.log"

echo "== S/MIME items in the corpora"
paths=()
for d in msg pst mime4j dovecot-crlf.msgs enron-sample; do [[ -e "$corpus/$d" ]] && paths+=("$corpus/$d"); done
$io scan "${paths[@]}" | tee "$work/scan.log"
if [[ -d "$corpus/enron-cmu" ]]; then
  echo "Enron maildir messages mentioning pkcs7: $(timeout 600 grep -rlI -i -m1 'application/.*pkcs7' "$corpus/enron-cmu" 2>/dev/null | wc -l)" | tee -a "$work/scan.log"
fi
