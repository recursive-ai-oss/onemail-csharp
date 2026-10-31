#!/usr/bin/env bash
# The conversion checks on the worker. Stages (default: all):
#   build      publishes OxcMailTool and the oracle drivers into $W/bin
#   export     converts the MSG, PST and OST items to EML, parses them with OneMail, and runs MimeKit and Python over them
#   mapi       MAPI -> MIME -> MAPI property comparison over the same items
#   mime       MIME -> MAPI -> MIME over the Mime4j, Dovecot and Enron sample corpora
#   view       how many senders and recipients resolve to SMTP
# Output in $W (default ~/onemail/u16). Corpora come from tools/corpus/fetch*.sh.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
W="${ONEMAIL_OXC_WORK:-$HOME/onemail/u16}"
C="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}"
export DOTNET_ROOT="$HOME/dotnet10" PATH="$HOME/dotnet10:$HOME/go-sdk/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMAIL_ARTIFACTS="$W/artifacts" ONEMAIL_ORACLE_BIN="$W/bin" ONEMAIL_ORACLE_OUT="$W/out"
mkdir -p "$W"
stages="${*:-build export mapi mime view}"
oxc() { "$W/bin/oxc/OxcMailTool" "$@"; }
rc=0
for s in $stages; do
  case $s in
    build)
      dotnet publish "$root/tools/oxcmail/OxcMailTool" -c Release -o "$W/bin/oxc" -v quiet -clp:NoSummary || exit 1
      [ -x "$W/bin/mimekit/MimeKitDump" ] || "$root/tools/oracles/build.sh" "$W/bin" || exit 1 ;;
    export)
      for set in msg pst; do
        rm -rf "$W/eml/$set"
        oxc export "$C/$set" "$W/eml/$set" | tee "$W/export-$set.txt"
        rm -rf "$W/out/oxc-$set"
        "$root/tools/oracles/run-corpus.sh" "oxc-$set" "$W/eml/$set/eml" --drivers mimekit,python,onemail > "$W/run-$set.txt" 2>&1; echo "differential oxc-$set exit $?"
        tail -n 25 "$W/out/oxc-$set/report.md" 2>/dev/null
        "${ONEMAIL_PYTHON:-$HOME/onemail/oracles/py313/bin/python}" "$here/check_export.py" "$W/eml/$set" "$W/out/oxc-$set/dumps" mimekit python || rc=1
      done ;;
    mapi) for set in msg pst; do echo "== rt-mapi $set"; oxc rt-mapi "$C/$set" || rc=1; done ;;
    mime)
      echo "== rt-mime mime4j"; oxc rt-mime "$C/mime4j" || rc=1
      echo "== rt-mime dovecot"; oxc rt-mime "$C/dovecot-crlf.msgs" --sample 2 || rc=1
      echo "== rt-mime enron sample"; oxc rt-mime "$C/enron-sample" || rc=1 ;;
    view) for set in msg pst; do echo "== view $set"; oxc view "$C/$set"; done ;;
  esac
done
exit $rc
