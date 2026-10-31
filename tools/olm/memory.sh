#!/usr/bin/env bash
# Enumerates large generated archives and reports peak working set and allocation per item, to show the working set stays flat
# as the archive grows.
#
#   tools/olm/memory.sh [work dir] [message counts...]      (default counts: 20000 80000)
#
# Writes archives into the work directory (about 6 KB per message) and removes them afterwards. Needs python3 and the .NET SDK.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:-${TMPDIR:-/var/tmp}/olm-memory}"; shift || true
counts=("${@:-20000 80000}")
dotnet_bin="${DOTNET:-dotnet}"
mkdir -p "$work"
"$dotnet_bin" build "$here/OlmTool/OlmTool.csproj" -c Release -v quiet -clp:NoSummary -o "$work/tool"
for n in ${counts[@]}; do
  f="$work/big-$n.olm"
  python3 "$here/make_olm.py" "$f" --seed 7 --messages "$n" --folders 5 --stored --body-bytes 2500 --attach-bytes 8000
  echo "archive: $n messages, $(du -m "$f" | cut -f1) MiB"
  "$dotnet_bin" "$work/tool/OlmTool.dll" zipdir "$f"
  "$dotnet_bin" "$work/tool/OlmTool.dll" memory "$f"
  rm -f "$f"
done
