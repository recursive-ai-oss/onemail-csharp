#!/usr/bin/env bash
# Strict-mode regression dumps and the recovery runs over the MSG corpus.
#
#   tools/msg/regress.sh <out dir> [--no-recover]
#
# Builds MsgTool and writes, under <out dir>:
#   strict.json.sha   a SHA-256 of the JSON property dump of every corpus and generated message, one line per file (compare two trees with diff)
#   strict.summary    MsgTool summary over the same files
# and unless --no-recover is given:
#   recover.summary   MsgTool summary --recover (the refused files, opened by recovery)
#   damage.tsv        MsgTool damage over the corpus and the generated fixtures
# The corpus is the one tools/corpus/fetch-msg.sh fetches (${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/msg).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="${1:?usage: regress.sh <out dir> [--no-recover]}"
mkdir -p "$out"
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/msg"
dotnet_bin="${DOTNET:-dotnet}"
"$dotnet_bin" build "$here/MsgTool/MsgTool.csproj" -c Release -v quiet -clp:NoSummary -o "$out/tool"
tool=("$dotnet_bin" "$out/tool/MsgTool.dll")

mapfile -t files < <(find "$corpus" "$repo/testdata/msg/generated" -type f \( -iname '*.msg' -o -iname '*.oft' \) | sort)
echo "${#files[@]} files"
: > "$out/strict.json.sha"
for f in "${files[@]}"; do
  printf '%s  %s\n' "$("${tool[@]}" json "$f" | sha256sum | cut -c1-64)" "$(basename "$f")" >> "$out/strict.json.sha"
done
"${tool[@]}" summary "${files[@]}" | cut -f1-9,13 > "$out/strict.summary"

if [ "${2:-}" != "--no-recover" ]; then
  "${tool[@]}" summary --recover "${files[@]}" | cut -f1-9,13 > "$out/recover.summary"
  "${tool[@]}" damage "${files[@]}" > "$out/damage.tsv" || echo "damage run reported problems" >&2
  tail -1 "$out/damage.tsv"
fi
