#!/usr/bin/env bash
# Fetches the ARC test suite's scenarios (MIT licence; data files only) at a pinned commit into ~/onemail/corpus/arc-suite and
# converts them to JSON. Nothing from it is committed to the repository.
#   tools/dkim/fetch-arc-suite.sh
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dest="${ONEMAIL_ARC_SUITE:-$HOME/onemail/corpus/arc-suite}"
commit=f137dcb9d6d5baeef1310024ce9ccca94a9a92c8
sha=0837a2fb11847e0800ecd2cc9b4b9ece76be0fb3074d7afdffb9e99d2adf655e
mkdir -p "$dest" && cd "$dest"
if [[ ! -d tests ]]; then
  curl -fsSL -o s.tgz "https://github.com/ValiMail/arc_test_suite/archive/$commit.tar.gz"
  echo "$sha  s.tgz" | sha256sum -c -
  mkdir x && tar xzf s.tgz -C x --strip-components=1 && mv x/tests x/LICENSE.txt . && rm -rf x s.tgz
fi
"${ONEMAIL_DKIM_ORACLES:-$HOME/onemail/oracles/dkim}/venv/bin/python" "$here/arc_suite_to_json.py" "$dest" "$dest/suite.json"
