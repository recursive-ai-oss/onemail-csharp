#!/usr/bin/env bash
# Runs the DKIM/ARC fuzz targets with small seed corpora: tools/dkim/fuzz-batch.sh <work dir> <seconds> <target>...
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$1"; secs="$2"; shift 2
export ONEMAIL_FUZZ_WORK="$work" LIBFUZZER_DOTNET="${LIBFUZZER_DOTNET:-$HOME/onemail/tools/libfuzzer-dotnet}" SHARPFUZZ="${SHARPFUZZ:-$HOME/onemail/tools/sharpfuzz}"
for t in "$@"; do
  seeds="$work/seeds/$t"; mkdir -p "$seeds"
  case "$t" in
    dkim-tags) printf 'v=1; a=rsa-sha256; c=relaxed/relaxed; d=example.com; s=rsa; h=from:subject; bh=2jUSOH9NhtVGCQWNr9BrIAPreKQjO6Sn7XIkfJVOzv8=; l=12; x=1900000000; t=1700000000; i=1; cv=none; b=AAAABBBB' >"$seeds/a" ;;
    dkim-key) printf 'v=DKIM1; k=rsa; t=y:s; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDkHlOQoBTzWRiGs5V6NpP3idY6Wk08a5qhdR6wy5bdOKb2jLQiY/J16JYi0Qvx/byYzCNb3W91y3FutACDfzwQ/BC/e/8uBsCR+yz1Lxj+PL6lHvqMKrM3rG4hstT5QjvHO9PzoxZyVYLzBfO2EeC3Ip3G+2kryOTIKT+l/K4w3QIDAQAB' >"$seeds/a"
              printf 'v=DKIM1; k=ed25519; p=11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=' >"$seeds/b" ;;
    dns-reply) python3 - "$seeds" <<'P'
import sys
d=sys.argv[1]
q=bytes([1,0x81,0x80,0,0,1,0,0,0,0,0,0])  # id 0x0001 prefix handled below
name=b'\x01x\x07example\x03com\x00'
hdr=bytes([0x81,0x80,0,1,0,1,0,1,0,0,0,0])
ans=bytes([0xC0,12,0,16,0,1,0,0,0,60,0,6,5])+b'hello'
open(d+'/a','wb').write(bytes([0,1])+hdr+name+bytes([0,16,0,1])+ans)
P
  esac
  "$here/../../fuzz/run.sh" "$t" "$secs" "$seeds"
done
