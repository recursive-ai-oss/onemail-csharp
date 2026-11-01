#!/usr/bin/env bash
# Composes messages with non-ASCII display names, subjects and file names and checks what MimeKit, Python email and Go net/mail read from them.
#   names.sh [COUNT]    output in $ONEMAIL_COMPOSITION_WORK (default ~/onemail/composition)
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
W="${ONEMAIL_COMPOSITION_WORK:-$HOME/onemail/composition}"
export DOTNET_ROOT="$HOME/dotnet10" PATH="$HOME/dotnet10:$HOME/go-sdk/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMAIL_ARTIFACTS="$W/artifacts" ONEMAIL_ORACLE_BIN="$W/bin" ONEMAIL_ORACLE_OUT="$W/out"
py="${ONEMAIL_PYTHON:-$HOME/onemail/oracles/py313/bin/python}"
mkdir -p "$W"
dotnet publish "$root/tools/composition/CompositionTool" -c Release -o "$W/bin/comp" -v quiet -clp:NoSummary -m:4 || exit 1
[ -x "$W/bin/mimekit/MimeKitDump" ] || "$root/tools/oracles/build.sh" "$W/bin" || exit 1
rm -rf "$W/names" "$W/out/names"
"$W/bin/comp/CompositionTool" names "$W/names" --count "${1:-200}" || exit 1
"$root/tools/oracles/run-corpus.sh" names "$W/names/eml" --drivers mimekit,python,go > "$W/run-names.txt" 2>&1; echo "differential names exit $?"
"$py" "$here/check_names.py" "$W/names" "$W/out/names/dumps" mimekit python go
