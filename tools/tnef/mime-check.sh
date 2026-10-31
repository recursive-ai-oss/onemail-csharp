#!/usr/bin/env bash
# TNEF inside MIME, end to end: wraps every TNEF file (the public corpus and the generated fixtures) in MIME messages, converts them to
# MAPI and reads them through the shared view (TnefTool mime), then compares the view's attachments with tnefparse, tnef, ytnef and
# MimeKit (compare_mime.py). Exit status is the number of unexplained differences.
#
#   tools/tnef/mime-check.sh [work dir]            (default ~/onemail/tnef-mime)
#
# Needs: the TNEF oracles from tools/oracles/tnef-oracles.sh (ONEMAIL_TNEF_ORACLES, default ~/onemail/oracles/tnef), the corpus from
# tools/corpus/fetch-tnef.sh (ONEMAIL_CORPUS, default ~/onemail/corpus), and a .NET 10 SDK on the PATH.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
work="${1:-$HOME/onemail/tnef-mime}"
oracles="${ONEMAIL_TNEF_ORACLES:-$HOME/onemail/oracles/tnef}"
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}"
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$work/artifacts}"
mkdir -p "$work"
rc=0

dotnet publish "$here/TnefTool" -c Release -o "$work/bin/tool" -v quiet -clp:NoSummary || exit 1
dotnet publish "$here/MimeKitTnef" -c Release -o "$work/bin/drivers" -v quiet -clp:NoSummary || exit 1

rm -rf "$work/files" "$work/out"
mkdir -p "$work/files"
find "$corpus/tnef" -type f \( -name '*.dat' -o -name '*.tnef' \) -exec cp -n {} "$work/files/" \;
# the same stream twice (Tika's testWINMAIL.dat is POI's quick-winmail.dat) counts once
rm -f "$work/files/tika-testWINMAIL.dat" "$work/files/testWINMAIL.dat"
dotnet "$work/bin/tool/TnefTool.dll" gen "$work/files" >/dev/null 2>&1

echo "== TnefTool mime"
dotnet "$work/bin/tool/TnefTool.dll" mime "$work/out" "$work/files"/* || rc=1
echo "== MimeKit"
dotnet "$work/bin/drivers/MimeKitTnef.dll" "$work/out" > "$work/mimekit.jsonl"
echo "== compare_mime"
python3 "$here/compare_mime.py" --view "$work/out/view.jsonl" --corpus "$work/files" --py "$oracles/venv/bin/python" \
  --tnef "$oracles/root/usr/bin/tnef" --ytnef "$oracles/root/usr/bin/ytnef" --lib "$oracles/root/usr/lib/x86_64-linux-gnu" \
  --mimekit "$work/mimekit.jsonl" || rc=1
exit $rc
