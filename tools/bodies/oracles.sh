#!/usr/bin/env bash
# Fetches the HTML-to-text oracles as released builds, user-locally; they only ever run as separate programs.
#   w3m 0.5.3+git20230121-2ubuntu5 (with libgc1) and lynx 2.9.0rel.0-2build2 (Ubuntu noble .debs, GPL-2+ / GPL-2), unpacked with dpkg -x
#   Python html2text 2025.4.15 (GPL-3.0-or-later) in a uv venv
# pandoc is optional (an existing unpack is used if ORACLE_PANDOC points at its bin directory).
# Usage: oracles.sh [root]   (default ~/onemail/oracles/html-text); prints the env file to source.
set -euo pipefail
ROOT=${1:-$HOME/onemail/oracles/html-text}
mkdir -p "$ROOT/dl" "$ROOT/root"
cd "$ROOT/dl"
ls w3m_*.deb >/dev/null 2>&1 && ls libgc1_*.deb >/dev/null 2>&1 || apt-get download w3m lynx lynx-common libgc1 >/dev/null
for d in *.deb; do dpkg -x "$d" "$ROOT/root"; done
if [ ! -x "$ROOT/venv/bin/python" ]; then
  ~/.local/bin/uv venv --python 3.13 "$ROOT/venv" >/dev/null
  ~/.local/bin/uv pip install --python "$ROOT/venv/bin/python" html2text==2025.4.15 >/dev/null
fi
cat > "$ROOT/env.sh" <<EOF
export HTMLTEXT_ROOT="$ROOT"
export W3M="$ROOT/root/usr/bin/w3m"
export LYNX="$ROOT/root/usr/bin/lynx"
export LD_LIBRARY_PATH="$ROOT/root/usr/lib/x86_64-linux-gnu:\${LD_LIBRARY_PATH:-}"
export LYNX_CFG="$ROOT/root/etc/lynx/lynx.cfg"
export HTML2TEXT_PY="$ROOT/venv/bin/python"
EOF
echo "$ROOT/env.sh"
