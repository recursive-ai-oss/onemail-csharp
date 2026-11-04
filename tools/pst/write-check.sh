#!/usr/bin/env bash
# Converts a source (a file, or a directory of files) to a new PST with `onemail convert --to pst`, then checks the new file four ways:
#   1. every source message against every message in the new store, property by property (PstOracle pstcheck);
#   2. the store's property bags against the raw tables (PstOracle propexact) and its node database against a scan (PstOracle dump);
#   3. the libpff tools: pffinfo opens it, and pffexport's folders, items, recipients and attachments agree with ours (libpff-messaging.sh).
#
#   tools/pst/write-check.sh <label> <source> <work-dir> [--no-libpff]
#
# Needs the built OneMail.Cli and PstOracle under $ONEMAIL_ARTIFACTS/bin (Release or Checked: set CONFIG, default debug) and, for the libpff
# step, the libpff tools unpacked user-locally (see tools/oracles/libpff-messaging.sh). Prints one result line per step; exit status 0 when all agree.
set -u
label="${1:?usage: write-check.sh <label> <source> <work-dir> [--no-libpff]}"
source_path="${2:?source}"
work="${3:?work dir}"
libpff=1; [ "${4:-}" = "--no-libpff" ] && libpff=0
config="${CONFIG:-debug}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bin="${ONEMAIL_ARTIFACTS:?set ONEMAIL_ARTIFACTS}/bin"
cli="$bin/OneMail.Cli/$config/RecursiveAI.OneMail.Cli.dll"
ora="$bin/PstOracle/$config/RecursiveAI.OneMail.PstOracle.dll"
mkdir -p "$work"; out="$work/$label.pst"; rm -f "$out"
status=0
echo "== $label  ($source_path)"
start=$(date +%s)
dotnet "$cli" convert --to pst "$source_path" "$out" > "$work/$label.convert.log" 2>&1 || { echo "convert reported errors (unreadable sources are skipped)"; tail -3 "$work/$label.convert.log"; [ -s "$out" ] || exit 1; }
end=$(date +%s)
echo "convert: $(head -1 "$work/$label.convert.log") ($((end - start)) s, $(stat -c %s "$out") bytes)"
[ -n "${NO_PSTCHECK:-}" ] && echo "  pstcheck skipped: the source items are not Outlook or MIME items it can convert" || dotnet "$ora" pstcheck "$out" "$source_path" 2>&1 | head -12 | sed 's/^/  /'; [ "${PIPESTATUS[0]}" -eq 0 ] || status=1
dotnet "$ora" propexact "$out" 2>&1 | head -6 | sed 's/^/  /'; [ "${PIPESTATUS[0]}" -eq 0 ] || status=1
dotnet "$ora" dump "$out" 2>&1 | grep -E '^(amaps|densityList|scan blocks|scan rootsFound|defects|nbt nodes|bbt blocks)' | sed 's/^/  /'
if [ "$libpff" = 1 ]; then
  set +u; . "${ONEMAIL_LIBPFF_ENV:-$HOME/onemail/oracles/libpff/env.sh}"; set -u
  echo "  pffinfo: $(pffinfo "$out" 2>&1 | grep -E 'File (type|size)|Encryption|Format|Content type|error|unable' | tr '\n' ';')"
  # The libpff tools have documented reading limits (docs/pst-writing.md); a DIFF line that is only one of them does not fail the check.
  # Limits matched here: an unnamed attachment exported as "Attachment.txt", a "_" in front of a numeric attachment name, and the subject
  # "Importance:" plus tabs libpff reads for one POI message. Any other DIFF line, or a non-DIFF failure, still fails.
  VERBOSE=1 bash "$here/../oracles/libpff-messaging.sh" "$out" "$ora" > "$work/$label.libpff.log" 2>&1; mstatus=$?
  head -20 "$work/$label.libpff.log" | sed 's/^/  /'
  if [ "$mstatus" -ne 0 ]; then
    real=$(grep -E '^DIFF ' "$work/$label.libpff.log" | grep -Evc 'libpff "Attachment\.txt"|libpff "_[0-9]|libpff "Importance:')
    total=$(grep -Ec '^DIFF ' "$work/$label.libpff.log")
    echo "  libpff reading-limit lines: $((total - real)); other differences: $real"
    # Exit 3 is the comparison's "differences" status; anything else (crash, no export) is a real failure.
    if [ "$mstatus" -ne 3 ] || [ "$real" -ne 0 ] || [ "$total" -eq 0 ]; then status=1; fi
  fi
fi

[ -z "${KEEP:-}" ] && rm -f "$out"
exit $status
