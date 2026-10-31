#!/usr/bin/env bash
# Install the black-box RTF to HTML oracles user-locally (released builds only, never built from source) under
#   ${ONEMAIL_ORACLES:-$HOME/onemail/oracles}/rtf-html
#
#   pandoc 3.13   GitHub release tarball, sha256 checked   (GPL-2.0-or-later; run only)
#   unrtf 0.21.10 Ubuntu noble universe .deb, extracted with dpkg -x   (GPL-3.0-or-later; run only)
#   LibreOffice   the distribution package already on the machine (Ubuntu 24.04 libreoffice-core 24.2.7, MPL-2.0); only its version is recorded
#
# Prints the three versions. Needs curl, sha256sum and dpkg-deb.
set -euo pipefail
root="${ONEMAIL_ORACLES:-$HOME/onemail/oracles}/rtf-html"; mkdir -p "$root"; cd "$root"

pandoc_sha=38aa33b325a2bc96a4c39409631212db55702f564250bcdbead180b3bf646389
if [[ ! -x pandoc-3.13/bin/pandoc ]]; then
  curl -fL --retry 3 -sS -o pandoc.tar.gz https://github.com/jgm/pandoc/releases/download/3.13/pandoc-3.13-linux-amd64.tar.gz
  echo "$pandoc_sha  pandoc.tar.gz" | sha256sum -c --quiet -
  tar xzf pandoc.tar.gz && rm pandoc.tar.gz
fi

if [[ ! -x unrtf/usr/bin/unrtf ]]; then
  curl -fL --retry 3 -sS -o unrtf_0.21.10-clean-1_amd64.deb http://archive.ubuntu.com/ubuntu/pool/universe/u/unrtf/unrtf_0.21.10-clean-1_amd64.deb
  echo "6c985ac3d8a982b6efcc167e1d2a9d00b02cf68f3a217a594ed2be4c8f5ae9e9  unrtf_0.21.10-clean-1_amd64.deb" | sha256sum -c --quiet -
  dpkg-deb -x unrtf_0.21.10-clean-1_amd64.deb unrtf
fi
"$root/pandoc-3.13/bin/pandoc" --version | head -1
"$root/unrtf/usr/bin/unrtf" --version 2>&1 | head -1 || true
soffice --version
