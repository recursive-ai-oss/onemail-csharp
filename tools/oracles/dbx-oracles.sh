#!/usr/bin/env bash
# Installs the DBX comparison oracle user-locally, without root: undbx from the distribution's package (downloaded and unpacked with
# dpkg -x, never built). Prints the package version and hash it used.
#   tools/oracles/dbx-oracles.sh [dir]     (default ~/onemail/oracles/dbx)
# Pinned: Ubuntu noble universe undbx 0.21-4 (amd64), sha256 5e46335bd16e6c38bc54d4d755740e41e15800e256499aa6ebf34db0bdfe43e4.
set -euo pipefail
dir="${1:-$HOME/onemail/oracles/dbx}"
want=5e46335bd16e6c38bc54d4d755740e41e15800e256499aa6ebf34db0bdfe43e4
mkdir -p "$dir/debs" "$dir/root"
cd "$dir/debs"
apt-get download undbx=0.21-4
got=$(sha256sum undbx_0.21-4_amd64.deb | cut -d' ' -f1)
if [ "$got" != "$want" ]; then echo "undbx package hash differs: $got" >&2; exit 1; fi
dpkg -x undbx_0.21-4_amd64.deb "$dir/root"
echo "undbx 0.21-4 sha256 $got: $dir/root/usr/bin/undbx"
