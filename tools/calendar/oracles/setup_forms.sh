#!/usr/bin/env bash
# Fetch the pinned black-box oracles for the JSON and XML forms of iCalendar and vCard. Only released builds are run; their
# source is never read. Everything goes under one user-local directory.
#
#   setup_forms.sh [dir]        default: $HOME/onemail/forms-oracles
#
# Oracles (licence; role):
#   ical.js 2.2.1 from npm (MPL-2.0)               jCal and jCard, both directions
#   ez-vcard 0.12.2, Maven Central (BSD-2-Clause)  vCard, jCard, xCard, both directions
#   biweekly 0.6.8, Maven Central (BSD-2-Clause)   iCalendar, jCal, xCal, both directions
#   icalendar 7.3.0 from PyPI (BSD-2-Clause)       jCal output
# Runtimes: Node 22.22.1 (nodejs.org, SHA-256 checked), Temurin 21 JDK (api.adoptium.net, SHA-256 recorded in the log).
# Their dependencies (vinnie, jackson, commons-codec, freemarker, jsoup) are the artifacts their POMs name, checked against Maven Central's SHA-1.
set -euo pipefail
dir="${1:-$HOME/onemail/forms-oracles}"
mkdir -p "$dir"
cd "$dir"

node_sha=9a6bc82f9b491279147219f6a18add1e18424dce90d41d2a5fcd69d4924ba3aa
if [[ ! -x node/bin/node ]]; then
  curl -fsSL -o node.tar.xz https://nodejs.org/dist/v22.22.1/node-v22.22.1-linux-x64.tar.xz
  echo "$node_sha  node.tar.xz" | sha256sum -c -
  mkdir -p node && tar xf node.tar.xz -C node --strip-components=1
fi
if [[ ! -x jdk/bin/java ]]; then
  curl -fsSL -o jdk.tar.gz 'https://api.adoptium.net/v3/binary/latest/21/ga/linux/x64/jdk/hotspot/normal/eclipse'
  sha256sum jdk.tar.gz
  mkdir -p jdk && tar xzf jdk.tar.gz -C jdk --strip-components=1
fi
export PATH="$PWD/node/bin:$PWD/jdk/bin:$PATH"

mkdir -p npm && (cd npm && [[ -d node_modules/ical.js ]] || npm install --no-audit --no-fund --prefix . ical.js@2.2.1)

mkdir -p jars
fetch() { # group-path artifact version
  local f="jars/$2-$3.jar"
  [[ -f "$f" ]] || curl -fsSL -o "$f" "https://repo1.maven.org/maven2/$1/$2/$3/$2-$3.jar"
  echo "$(curl -fsSL "https://repo1.maven.org/maven2/$1/$2/$3/$2-$3.jar.sha1" | cut -c1-40)  $f" | sha1sum -c -
}
fetch com/googlecode/ez-vcard ez-vcard 0.12.2
fetch net/sf/biweekly biweekly 0.6.8
fetch com/github/mangstadt vinnie 2.0.2
fetch com/fasterxml/jackson/core jackson-core 2.16.1
fetch com/fasterxml/jackson/core jackson-databind 2.16.1
fetch com/fasterxml/jackson/core jackson-annotations 2.16.1
fetch commons-codec commons-codec 1.10
fetch org/freemarker freemarker 2.3.32   # named by ez-vcard's hCard classes, loaded with the library
fetch org/jsoup jsoup 1.17.2             # likewise

venv="$dir/venv"
if [[ ! -x "$venv/bin/python" ]]; then
  uv venv --python 3.13 "$venv"
  uv pip install --python "$venv/bin/python" icalendar==7.3.0
fi
echo "ready: $dir"
