#!/usr/bin/env bash
# Builds the oracle drivers into $1 (default: $ONEMAIL_ORACLE_BIN or ./bin-oracles). Needs the .NET 10 SDK and Go on PATH.
# The MimeKit driver restores the pinned MimeKit package from nuget.org and runs it as a black box.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:-${ONEMAIL_ORACLE_BIN:-./bin-oracles}}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
dotnet publish "$here/mimekit-driver/MimeKitDump.csproj" -c Release -o "$out/mimekit" -v quiet -clp:NoSummary
dotnet publish "$here/onemail-driver/OneMailDump.csproj" -c Release -o "$out/onemail" -v quiet -clp:NoSummary
(cd "$here/go" && GOTOOLCHAIN=local go build -o "$out/godump" .)
echo "drivers in $out"
