#!/usr/bin/env bash
# Compares OneMail's reading of Outlook for Mac archives with two released programs, run as separate processes:
#   the olmreader 1.9.0 jar (Maven Central, sha1 below, BSD-2-Clause) and the olm-reader 1.0.0 npm package (Apache-2.0).
# Archives compared: the two public samples (tools/corpus/fetch-olm.sh) and two archives written by make_olm.py.
#
#   tools/olm/run.sh [work dir]
#
# Needs: java and javac (17 or later), node and npm, python3, curl, and the .NET SDK on PATH. Network for the first run only.
# Environment: ONEMAIL_CORPUS (default ~/onemail/corpus), ONEMAIL_ORACLES (default ~/onemail/oracles), DOTNET (default dotnet).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="${1:-${TMPDIR:-/var/tmp}/olm-run}"
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/olm"
oracles="${ONEMAIL_ORACLES:-$HOME/onemail/oracles}/olm"
dotnet_bin="${DOTNET:-dotnet}"
jar_sha1=da66d6ee52b2e1782eb338772a0cdd998d874393
mkdir -p "$work" "$oracles"

bash "$repo/tools/corpus/fetch-olm.sh" >/dev/null

# The oracles, pinned.
jar="$oracles/olmreader-1.9.0.jar"
if [[ ! -f "$jar" ]]; then
  curl -fsSL -o "$jar" https://repo1.maven.org/maven2/com/khubla/olmreader/olmreader/1.9.0/olmreader-1.9.0.jar
fi
echo "$jar_sha1  $jar" | sha1sum -c --quiet -
if [[ ! -d "$oracles/npm/node_modules/olm-reader" ]]; then
  (cd "$oracles" && mkdir -p npm && cd npm && npm install --no-audit --no-fund --silent olm-reader@1.0.0)
fi

# Our dump tool and the jar driver.
"$dotnet_bin" build "$here/OlmTool/OlmTool.csproj" -c Release -v quiet -clp:NoSummary -o "$work/tool"
mkdir -p "$work/java"
javac -proc:none -cp "$jar" -d "$work/java" "$here/OlmDump.java"

# Archives written by the generator.
python3 "$here/make_olm.py" "$work/generated-1.olm" --seed 1 --messages 40 --folders 5
python3 "$here/make_olm.py" "$work/generated-2.olm" --seed 2 --messages 60 --stored --body-bytes 4000 --attach-bytes 70000 --folders 3

fail=0
for f in "$corpus/outlook_example.olm" "$corpus/bill_rapp.olm" "$work/generated-1.olm" "$work/generated-2.olm"; do
  n="$(basename "$f" .olm)"
  echo "== $n"
  # The old JAXB inside the jar cannot define classes on current Java; this switch turns that optimisation off.
  java -Dcom.sun.xml.bind.v2.bytecode.ClassTailor.noOptimize=true -cp "$jar:$work/java" OlmDump "$f" >"$work/$n.oracle.txt" 2>"$work/$n.oracle.err" || true
  "$dotnet_bin" "$work/tool/OlmTool.dll" dump "$f" >"$work/$n.ours.jsonl"
  python3 "$here/compare.py" "$work/$n.oracle.txt" "$work/$n.ours.jsonl" || fail=1
  # The npm package writes EML files; it reads dates in the machine's zone, so run it in UTC.
  rm -rf "$work/$n.npm"
  TZ=UTC node "$oracles/npm/node_modules/.bin/olm-extract" "$f" "$work/$n.npm" >/dev/null 2>&1 || true
  python3 "$here/compare_npm.py" "$work/$n.npm" "$work/$n.ours.jsonl" | grep -E 'paired|DIFF|explained|unexplained' || true
done
exit $fail
