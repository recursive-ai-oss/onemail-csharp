#!/usr/bin/env bash
# Installs the TNEF comparison oracles user-locally, without root: tnef and ytnef from the distribution's packages (downloaded and
# unpacked with dpkg -x, never built) and tnefparse 1.4.0 in a uv venv. Prints the package versions and hashes it used.
#   tools/oracles/tnef-oracles.sh [dir]     (default ~/onemail/oracles/tnef)
set -euo pipefail
dir="${1:-$HOME/onemail/oracles/tnef}"
mkdir -p "$dir/debs" "$dir/root"
cd "$dir/debs"
apt-get download tnef ytnef-tools libytnef0
for d in *.deb; do dpkg -x "$d" "$dir/root"; echo "$(sha256sum "$d")"; done
"$HOME/.local/bin/uv" venv --python 3.13 "$dir/venv" -q
"$HOME/.local/bin/uv" pip install -q --python "$dir/venv/bin/python" tnefparse==1.4.0
echo "tnef: $dir/root/usr/bin/tnef   ytnef: $dir/root/usr/bin/ytnef   lib: $dir/root/usr/lib/*-linux-gnu   python: $dir/venv/bin/python"
