#!/usr/bin/env bash
# Fetches the pinned Thunderbird release and unpacks it user-locally. Nothing is installed system-wide.
#   THUNDERBIRD_ROOT: where to unpack (default ~/onemail/oracles/thunderbird)
set -euo pipefail
VERSION=159.0
SHA256=a7cb57c4dfec66e566e1cbbdcf7e0aab67aad7262dd0ab38b4f4e8df2b771074
URL=https://archive.mozilla.org/pub/thunderbird/releases/$VERSION/linux-x86_64/en-US/thunderbird-$VERSION.tar.xz
ROOT=${THUNDERBIRD_ROOT:-$HOME/onemail/oracles/thunderbird}
mkdir -p "$ROOT"
cd "$ROOT"
if [ ! -x thunderbird/thunderbird ]; then
  [ -f "thunderbird-$VERSION.tar.xz" ] || curl -sSLO "$URL"
  echo "$SHA256  thunderbird-$VERSION.tar.xz" | sha256sum -c -
  tar xf "thunderbird-$VERSION.tar.xz"
fi
echo "thunderbird $VERSION in $ROOT/thunderbird"
