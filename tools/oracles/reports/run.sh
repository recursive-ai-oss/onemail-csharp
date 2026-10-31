#!/usr/bin/env bash
# Builds the report drivers and runs the report differential over testdata/reports (or another directory).
#
#   tools/oracles/reports/run.sh [ROOT] [OUT]
#
# Needs .NET 10 and the pinned CPython (ONEMAIL_PYTHON, default ~/onemail/oracles/py313/bin/python). Writes dumps and
# report.md to OUT (default ~/onemail/u8/out). Exit status 1 when any difference is unexplained.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${1:-$here/../../../testdata/reports}"
out="${2:-$HOME/onemail/u8/out}"
bin="${ONEMAIL_ORACLE_BIN:-$HOME/onemail/u8/bin}"
py="${ONEMAIL_PYTHON:-$HOME/onemail/oracles/py313/bin/python}"
if [ -d "$HOME/dotnet10" ]; then export DOTNET_ROOT="$HOME/dotnet10" PATH="$HOME/dotnet10:$PATH"; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
mkdir -p "$bin" "$out"
dotnet publish "$here/onemail/OneMailReports.csproj" -c Release -o "$bin/onemail" -v quiet -clp:NoSummary
dotnet publish "$here/mimekit/MimeKitReports.csproj" -c Release -o "$bin/mimekit" -v quiet -clp:NoSummary
"$py" "$here/compare.py" run --root "$root" --out "$out" --bin "$bin" --python "$py"
if [ -f "$here/urljoin-pairs.tsv" ]; then
  "$py" "$here/compare.py" urljoin --pairs "$here/urljoin-pairs.tsv" --out "$out" --bin "$bin" --python "$py"
fi
