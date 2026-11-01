#!/usr/bin/env bash
# Runs the S/MIME structure fuzz target with small seeds: tools/smime/fuzz.sh <work dir> <seconds>
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$1"; secs="$2"
export ONEMAIL_FUZZ_WORK="$work" LIBFUZZER_DOTNET="${LIBFUZZER_DOTNET:-$HOME/onemail/tools/libfuzzer-dotnet}" SHARPFUZZ="${SHARPFUZZ:-$HOME/onemail/tools/sharpfuzz}"
seeds="$work/seeds/smime-structure"; mkdir -p "$seeds"
# Modes 1 to 3 edit a valid signed, encrypted or triple-wrapped message; mode 0 reads the rest as a message.
printf '\x01' >"$seeds/signed"
printf '\x02' >"$seeds/enveloped"
printf '\x03' >"$seeds/triple"
printf '\x01\x00\x40\x41\x01\x10\x20\x00' >"$seeds/signed-edit"
printf '\x00Content-Type: multipart/signed; protocol="application/pkcs7-signature"; micalg=sha-256; boundary=b\r\n\r\n--b\r\n\r\nx\r\n--b\r\nContent-Type: application/pkcs7-signature\r\nContent-Transfer-Encoding: base64\r\n\r\nMAA=\r\n--b--\r\n' >"$seeds/structure"
printf '\x00Content-Type: application/pkcs7-mime; smime-type=enveloped-data\r\nContent-Transfer-Encoding: base64\r\n\r\nMAA=\r\n' >"$seeds/p7m"
"$here/../../fuzz/run.sh" smime-structure "$secs" "$seeds"
