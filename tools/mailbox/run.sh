#!/usr/bin/env bash
# Runs the mailbox boundary, round-trip and writer checks. Needs the .NET 10 SDK, Python 3.13 with the standard library
# `mailbox`, and (optionally) the dovecot-crlf corpus file.
#
#   tools/mailbox/run.sh <workdir> [corpus mbox ...]
#
# Builds OneMailRanges and the MimeKit driver into <workdir>/bin, generates the stores into <workdir>/gen, and prints one
# summary block per check. Exit status is non-zero if any check fails.
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mkdir -p "${1:?usage: run.sh <workdir> [corpus mbox ...]}" && cd "$1" && pwd)"
shift
PY="${PYTHON:-python3}"
bin="$work/bin"
rc=0

dotnet publish "$root/tools/mailbox/OneMailRanges/OneMailRanges.csproj" -c Release -o "$bin/omr" -v quiet -clp:NoSummary || exit 2
dotnet publish "$root/tools/oracles/mailbox-driver/MailboxDump.csproj" -c Release -o "$bin/mk" -v quiet -clp:NoSummary || exit 2
OMR="dotnet $bin/omr/OneMailRanges.dll"
MK="dotnet $bin/mk/MailboxDump.dll"
PYR="$PY $root/tools/mailbox/py_ranges.py"

"$PY" --version
rm -rf "$work/gen" "$work/rt" "$work/written"
"$PY" "$root/tools/mailbox/gen_mboxes.py" "$work/gen/mbox" >/dev/null
"$PY" "$root/tools/mailbox/gen_stores.py" "$work/gen/stores" >/dev/null

echo "== mbox boundaries: generated files and any corpus files, against Python mailbox and MimeKit"
$PY "$root/tools/mailbox/compare_mbox.py" --omr "$OMR" --mk "$MK" --py "$PYR" "$work"/gen/mbox/*.mbox "$@" || rc=1

echo "== ground truth: messages read back equal what the independent writers were given"
$OMR verify "$work/gen/mbox" | tail -3 || rc=1

echo "== Maildir listing: OneMail against Python mailbox.Maildir"
$PY "$root/tools/mailbox/py_maildir.py" "$work/gen/stores/maildir" | sort > "$work/py_md.tsv"
$OMR maildir "$work/gen/stores/maildir" | sort > "$work/om_md.tsv"
wc -l < "$work/om_md.tsv" | sed 's/^/messages: /'
diff "$work/py_md.tsv" "$work/om_md.tsv" && echo "identical listings (folder, subdir, unique, flags, sha256, size)" || rc=1

echo "== EMLX: OneMail against the generator's expectations"
$OMR emlx "$work/gen/stores/emlx" | cut -f1-3 | sort > "$work/om_emlx.tsv"
sort "$work/gen/stores/emlx/emlx.expected" | diff - "$work/om_emlx.tsv" && echo "identical ($(wc -l < "$work/om_emlx.tsv") messages: path, sha256, flags)" || rc=1

echo "== round trips"
$OMR roundtrip-mbox "$work/gen/mbox" || true   # explained non-identical files are listed; see docs/mailboxes.md
$OMR roundtrip-maildir "$work/gen/stores/maildir" "$work/rt/maildir" || rc=1
$OMR roundtrip-emlx "$work/gen/stores/emlx" || rc=1

echo "== writers read by Python"
$OMR write "$work/written"
$PY "$root/tools/mailbox/py_check_written.py" "$work/written" || rc=1
$OMR verify "$work/written" | tail -1 || rc=1
exit $rc
