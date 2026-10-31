#!/usr/bin/env bash
# Runs the differential runner over one corpus with the drivers built by build.sh.
#
#   run-corpus.sh NAME ROOT [diffrun.py run options ...]
#
# Environment (all optional): ONEMAIL_ORACLE_BIN (driver build, default ~/onemail/work/bin), ONEMAIL_ORACLE_OUT (dumps and
# reports, default ~/onemail/work/out), ONEMAIL_PYTHON (the pinned CPython, default ~/onemail/oracles/py313/bin/python),
# ONEMAIL_DOTNET_ROOT (a .NET 10 install; default ~/dotnet10 when present, else DOTNET_ROOT).
# Exit status 75 means --max-seconds ran out: run the same command again to continue where it stopped.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
name="$1"; root="$2"; shift 2
bin="${ONEMAIL_ORACLE_BIN:-$HOME/onemail/work/bin}"
out="${ONEMAIL_ORACLE_OUT:-$HOME/onemail/work/out}"
py="${ONEMAIL_PYTHON:-$HOME/onemail/oracles/py313/bin/python}"
# A shell may already point DOTNET_ROOT at an older install; the MimeKit driver needs .NET 10.
if [ -n "${ONEMAIL_DOTNET_ROOT:-}" ]; then export DOTNET_ROOT="$ONEMAIL_DOTNET_ROOT"; elif [ -d "$HOME/dotnet10" ]; then export DOTNET_ROOT="$HOME/dotnet10"; fi
export PATH="${DOTNET_ROOT:+$DOTNET_ROOT:}$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
"$py" "$here/diffrun.py" run --corpus "$name" --root "$root" --out "$out" --bin "$bin" "$@"
rc=$?
"$py" "$here/diffrun.py" report --corpus "$name" --out "$out" > /dev/null 2>&1
rrc=$?
[ $rc -eq 0 ] && rc=$rrc   # a report that finds unexplained differences fails the run
exit $rc
