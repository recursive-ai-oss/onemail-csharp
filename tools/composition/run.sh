#!/usr/bin/env bash
# The composition checks on the worker. Stages (default: all):
#   build     publishes CompositionTool and the oracle drivers into $W/bin
#   sweep     replies, reply-alls and both forwards of the corpus samples: in-process checks, counts per format
#   oracles   parses what the sweep wrote with MimeKit, Python email and Go net/mail and compares headers and recipients with it
# Output in $W (default ~/onemail/composition). Corpora come from tools/corpus/fetch*.sh.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
W="${ONEMAIL_COMPOSITION_WORK:-$HOME/onemail/composition}"
C="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}"
export DOTNET_ROOT="$HOME/dotnet10" PATH="$HOME/dotnet10:$HOME/go-sdk/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMAIL_ARTIFACTS="$W/artifacts" ONEMAIL_ORACLE_BIN="$W/bin" ONEMAIL_ORACLE_OUT="$W/out"
mkdir -p "$W"
stages="${*:-build sweep oracles}"
sets="${ONEMAIL_SWEEP_SETS:-enron-sample dovecot-crlf.msgs mime4j msg pst tnef olm dbx}"
tool() { "$W/bin/comp/CompositionTool" "$@"; }
py="${ONEMAIL_PYTHON:-$HOME/onemail/oracles/py313/bin/python}"
rc=0
for s in $stages; do
  case $s in
    build)
      dotnet publish "$root/tools/composition/CompositionTool" -c Release -o "$W/bin/comp" -v quiet -clp:NoSummary || exit 1
      [ -x "$W/bin/mimekit/MimeKitDump" ] || "$root/tools/oracles/build.sh" "$W/bin" || exit 1 ;;
    sweep)
      # The samples: 5,000+ Enron messages, the Dovecot mbox messages, the Mime4j messages, then every Outlook, TNEF, OLM and DBX item.
      for set in $sets; do
        echo "== sweep $set"
        rm -rf "$W/sweep/$set"
        tool sweep "$C/$set" "$W/sweep/$set" ${ONEMAIL_SWEEP_ARGS:-} | tee "$W/sweep-$set.txt" || rc=1
      done ;;
    oracles)
      for name in $sets; do
        set="$W/sweep/$name"
        [ -d "$set/eml" ] || continue
        rm -rf "$W/out/comp-$name"
        "$root/tools/oracles/run-corpus.sh" "comp-$name" "$set/eml" --drivers mimekit,python,go > "$W/run-$name.txt" 2>&1; echo "differential comp-$name exit $?"
        "$py" "$here/check_compose.py" "$set" "$W/out/comp-$name/dumps" mimekit python go || rc=1
      done ;;
  esac
done
exit $rc
