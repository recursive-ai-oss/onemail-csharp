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
dotnet "$cli" convert --to pst "$source_path" "$out" > "$work/$label.convert.log" 2>&1 || { echo "convert failed"; tail -3 "$work/$label.convert.log"; exit 1; }
end=$(date +%s)
echo "convert: $(head -1 "$work/$label.convert.log") ($((end - start)) s, $(stat -c %s "$out") bytes)"
dotnet "$ora" pstcheck "$out" "$source_path" 2>&1 | head -12 | sed 's/^/  /'; [ "${PIPESTATUS[0]}" -eq 0 ] || status=1
dotnet "$ora" propexact "$out" 2>&1 | head -6 | sed 's/^/  /'; [ "${PIPESTATUS[0]}" -eq 0 ] || status=1
dotnet "$ora" dump "$out" 2>&1 | grep -E '^(amaps|densityList|scan blocks|scan rootsFound|defects|nbt nodes|bbt blocks)' | sed 's/^/  /'
if [ "$libpff" = 1 ]; then
  set +u; . "${ONEMAIL_LIBPFF_ENV:-$HOME/onemail/oracles/libpff/env.sh}"; set -u
  echo "  pffinfo: $(pffinfo "$out" 2>&1 | grep -E 'File (type|size)|Encryption|Format|Content type|error|unable' | tr '\n' ';')"
  bash "$here/../oracles/libpff-messaging.sh" "$out" "$ora" 2>&1 | sed 's/^/  /' | head -20; [ "${PIPESTATUS[0]}" -eq 0 ] || status=1
fi

[ -z "${KEEP:-}" ] && rm -f "$out"
exit $status
