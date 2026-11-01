#!/usr/bin/env bash
# Installs the black-box DKIM/ARC oracles under ~/onemail/oracles/dkim (idempotent): dkimpy 1.1.8 with PyNaCl (PyPI, uv venv),
# Node 22.22.1 and mailauth 8.0.4 (npm). They are run only: their source is not read and nothing is linked or shipped.
set -euo pipefail
root="${ONEMAIL_DKIM_ORACLES:-$HOME/onemail/oracles/dkim}"
mkdir -p "$root" && cd "$root"
export PATH="$HOME/.local/bin:$PATH"
[[ -d venv ]] || uv venv --python 3.13 venv
uv pip install --python venv/bin/python dkimpy==1.1.8 PyNaCl
node_sha=9a6bc82f9b491279147219f6a18add1e18424dce90d41d2a5fcd69d4924ba3aa
if [[ ! -x node/bin/node ]]; then
  curl -fsSL -o node.tar.xz https://nodejs.org/dist/v22.22.1/node-v22.22.1-linux-x64.tar.xz
  echo "$node_sha  node.tar.xz" | sha256sum -c -
  mkdir -p node && tar xf node.tar.xz -C node --strip-components=1 && rm node.tar.xz
fi
export PATH="$PWD/node/bin:$PATH"
mkdir -p npm && (cd npm && [[ -d node_modules/mailauth ]] || npm install --no-audit --no-fund --prefix . mailauth@8.0.4)
venv/bin/python -c "import importlib.metadata as m; print('dkimpy', m.version('dkimpy'))"
node -e "console.log('mailauth', require('$PWD/npm/node_modules/mailauth/package.json').version)"
