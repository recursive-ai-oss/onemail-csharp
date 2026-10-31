#!/usr/bin/env bash
# Builds the benchmark project and prints the ratio table for an existing run directory: report.sh OUTDIR
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -x "$HOME/dotnet10/dotnet" ]; then export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"; elif [ -f "$HOME/onemail/env.sh" ]; then . "$HOME/onemail/env.sh"; fi
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$HOME/onemail/u4/artifacts}"
dotnet build -c Release "$here/OneMail.Benchmarks" -v quiet -clp:NoSummary > /dev/null
dotnet "$ONEMAIL_ARTIFACTS/bin/OneMail.Benchmarks/release/OneMail.Benchmarks.dll" report "$(cd "${1:?run directory}" && pwd)/bdn/results"
