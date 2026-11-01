#!/usr/bin/env bash
# Fetches the Stalwart command-line tool (the vendor's published release binary), which the harness uses to provision the
# Stalwart image's domain and account. Run only, never linked or shipped. Idempotent.
set -euo pipefail
VER=v1.0.14
FILE=stalwart-cli-x86_64-unknown-linux-musl.tar.xz
SHA256=$(curl -sSL "https://github.com/stalwartlabs/cli/releases/download/$VER/$FILE.sha256" | awk '{print $1}')
R=${ONEMAIL_CONTAINERS:-$HOME/onemail/containers}/tools/stalwart-cli
mkdir -p "$R"
if [ ! -x "$R/stalwart-cli" ]; then
  curl -sSL -m 300 -o "$R/$FILE" "https://github.com/stalwartlabs/cli/releases/download/$VER/$FILE"
  echo "$SHA256  $R/$FILE" | sha256sum -c -
  tar xJf "$R/$FILE" -C "$R" --strip-components=1
fi
ls "$R"
echo "sha256 $SHA256"
