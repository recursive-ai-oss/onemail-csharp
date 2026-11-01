#!/usr/bin/env bash
# Peak working set while enumerating the sample archives and a large generated one.
#   tools/olm/before-after.sh <work dir> [messages]   (default 100000)
# Run it from two checkouts to compare them. Needs python3 and the .NET SDK; writes about 6 KB per message under the work dir.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:?usage: before-after.sh <work dir> [messages]}"
n="${2:-100000}"
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/olm"
dotnet_bin="${DOTNET:-dotnet}"
mkdir -p "$work"
"$dotnet_bin" build "$here/OlmTool/OlmTool.csproj" -c Release -v quiet -clp:NoSummary -o "$work/tool"
for f in "$corpus"/*.olm; do
  echo "== $(basename "$f")"
  "$dotnet_bin" "$work/tool/OlmTool.dll" memory "$f"
done
big="$work/big-$n.olm"
[ -f "$big" ] || python3 "$here/make_olm.py" "$big" --seed 7 --messages "$n" --folders 5 --stored --body-bytes 2500 --attach-bytes 8000
echo "== generated, $n messages, $(du -m "$big" | cut -f1) MiB"
"$dotnet_bin" "$work/tool/OlmTool.dll" zipdir "$big"
"$dotnet_bin" "$work/tool/OlmTool.dll" memory "$big"
