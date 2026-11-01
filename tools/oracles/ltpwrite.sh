#!/usr/bin/env bash
# Rebuilds every property and table context of the corpus PST/OST files with the LTP builders and compares the read-back with the
# original, then prints how the original lays its heaps out. Builds the PstOracle first (Release). Run it inside the queue on the worker.
#
#   tools/oracles/ltpwrite.sh [file...]      default: the three files under ~/onemail/corpus/pst
set -euo pipefail
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$HOME/onemail/work-u38/artifacts}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
dotnet build "$root/tests/PstOracle/PstOracle.csproj" -c Release -v quiet -clp:NoSummary
dll="$ONEMAIL_ARTIFACTS/bin/PstOracle/release/RecursiveAI.OneMail.PstOracle.dll"
files=("$@")
if [ ${#files[@]} -eq 0 ]; then
  files=("$HOME/onemail/corpus/pst/tika/testPST.pst" "$HOME/onemail/corpus/pst/tika/testPST_variousBodyTypes.pst" "$HOME/onemail/corpus/pst/cfreds-leakage/iaman.informant@nist.gov.ost")
fi
status=0
for f in "${files[@]}"; do
  echo "=== $f"
  dotnet "$dll" ltpwrite "$f" -v || status=$?
done
exit $status
