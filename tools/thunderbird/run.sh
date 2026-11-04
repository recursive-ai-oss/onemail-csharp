#!/usr/bin/env bash
# Runs every Thunderbird interop check (OpenPGP, then S/MIME) against the committed vectors and prints the RESULT/TOTAL lines.
# Needs python3 (standard library only), the pinned release from setup.sh and, for the whitespace probe, a gpg (GPG=path).
# Thunderbird runs headless (MOZ_HEADLESS), so no display or Xvfb is needed. Messages Thunderbird writes land in
# $OUT/thunderbird/<key>/ (copy them to testdata/openpgp/vectors/thunderbird to refresh the committed set) and, for S/MIME,
# $OUT/smime/thunderbird/<key>/ (copy to testdata/smime/vectors/thunderbird).
#   OUT  output and screenshots (default $TMPDIR/tb-interop)      WORK  scratch profiles (default ~/onemail/work-tb)
set -uo pipefail
cd "$(dirname "$0")"
VEC=../../testdata/openpgp/vectors
OUT=${OUT:-${TMPDIR:-/tmp}/tb-interop}
export WORK=${WORK:-$HOME/onemail/work-tb}
bash setup.sh >&2
rm -rf "$OUT"; mkdir -p "$OUT"
status=0
python3 interop.py ours "$VEC" "$OUT" || status=1
python3 interop.py theirs "$VEC" "$OUT" || status=1
python3 interop.py correspondent "$VEC" "$OUT" || status=1
[ -n "${GPG:-}" ] && { python3 interop.py whitespace "$VEC" "$OUT" || status=1; }
# S/MIME: the certificates, the CA and our messages come from `SmimeInterop thunderbird <dir>` (tools/smime); the committed set is used here.
SVEC=../../testdata/smime/vectors
python3 smime.py ours "$SVEC" "$OUT/smime" || status=1
python3 smime.py untrusted "$SVEC" "$OUT/smime" || status=1
python3 smime.py theirs "$SVEC" "$OUT/smime" || status=1
exit $status
