#!/usr/bin/env bash
# Cards for every calendar object in the corpora: counts per source format and method, HTML parse errors, and the recurrence sentence parse-back.
#
#   run_sweep.sh [extra root]...
#
# Needs ONEMAIL_CORPUS (the fetched corpora) and dotnet on PATH; the repository is the working directory's ancestor.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
c="${ONEMAIL_CORPUS:?}"
dotnet build "$repo/tools/calcard/CalcardTool" -c Release -v quiet -clp:NoSummary >/dev/null
tool="$(find "${ONEMAIL_ARTIFACTS:-$repo/artifacts}/bin/CalcardTool/release" -name CalcardTool -type f | head -1)"
roots=("$repo/testdata/calendar" "$repo/testdata/olm" "$repo/testdata/msg")
for d in calendar msg olm tnef mime4j dovecot-crlf.msgs enron-sample pst/tika pst/cfreds-leakage; do [ -e "$c/$d" ] && roots+=("$c/$d"); done
nice "$tool" sweep "${roots[@]}" "$@"
