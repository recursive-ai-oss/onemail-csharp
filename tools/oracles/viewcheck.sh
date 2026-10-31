#!/usr/bin/env bash
# Builds the CLI and the OneMail oracle driver, then runs viewcheck.py over the named corpora.
#
#   viewcheck.sh NAME=ROOT...        e.g. viewcheck.sh probes=testdata/differential dovecot=~/onemail/corpus/dovecot-crlf
#
# Environment (optional): ONEMAIL_DOTNET_ROOT (a .NET 10 install; default ~/dotnet10), ONEMAIL_PYTHON (the pinned CPython;
# default ~/onemail/oracles/py313/bin/python), ONEMAIL_VIEWCHECK_DIR (builds and work files; default ~/onemail/viewcheck).
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
base="${ONEMAIL_VIEWCHECK_DIR:-$HOME/onemail/viewcheck}"
py="${ONEMAIL_PYTHON:-$HOME/onemail/oracles/py313/bin/python}"
if [ -n "${ONEMAIL_DOTNET_ROOT:-}" ]; then export DOTNET_ROOT="$ONEMAIL_DOTNET_ROOT"; elif [ -d "$HOME/dotnet10" ]; then export DOTNET_ROOT="$HOME/dotnet10"; fi
export PATH="${DOTNET_ROOT:+$DOTNET_ROOT:}$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export ONEMAIL_ARTIFACTS="$base/artifacts"
mkdir -p "$base"
dotnet publish "$repo/src/OneMail.Cli/OneMail.Cli.csproj" -c Release -o "$base/cli" -v quiet -clp:NoSummary || exit 1
dotnet publish "$here/onemail-driver/OneMailDump.csproj" -c Release -o "$base/driver" -v quiet -clp:NoSummary || exit 1
rc=0
for spec in "$@"; do
  name="${spec%%=*}"; root="${spec#*=}"
  rm -rf "$base/work-$name"
  echo "=== $name"
  "$py" "$here/viewcheck.py" --cli "dotnet $base/cli/RecursiveAI.OneMail.Cli.dll" --driver "dotnet $base/driver/OneMailDump.dll" \
    --root "$root" --work "$base/work-$name" || rc=1
done
exit $rc
