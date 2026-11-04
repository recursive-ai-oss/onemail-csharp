#!/usr/bin/env bash
# Two-way PGP/MIME interop run. Needs a .NET 10 SDK, python3, and the OpenPGP programs named below.
#   GPG24, GPG25 : paths to gpg 2.4 and 2.5 (a distribution package and a release build)
#   SQOP         : path to a Sequoia SOP binary (sqop)
#   OUT          : output directory for the regenerated vector set (default: $TMPDIR/pgp-vectors)
# Step 1 has OneMail write its messages, step 2 has each program check them, step 3 has each program write its own.
# Copy OUT/ours and OUT/<program>-<version> to testdata/openpgp/vectors to refresh the committed set.
set -euo pipefail
cd "$(dirname "$0")/../.."
OUT=${OUT:-${TMPDIR:-/tmp}/pgp-vectors}
rm -rf "$OUT"; mkdir -p "$OUT"
ONEMAIL_PGP_VECTORS_OUT=$OUT dotnet test --project tests/OneMail.Tests --filter-method '*WriteOurVectorsWhenAsked'
TOOLS=()
[ -n "${GPG24:-}" ] && TOOLS+=("gpg:$GPG24")
[ -n "${GPG25:-}" ] && TOOLS+=("gpg:$GPG25")
[ -n "${SQOP:-}" ] && TOOLS+=("sop:$SQOP")
status=0
python3 tools/openpgp/interop.py ours "$OUT" "${TOOLS[@]}" || status=1
python3 tools/openpgp/interop.py theirs "$OUT" "${TOOLS[@]}" || status=1
exit $status
