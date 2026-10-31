#!/usr/bin/env bash
# The store-conversion checks on the worker. Stages (default: all):
#   build    publishes ConvertTool, OxcMailTool and the onemail tool into $W/bin
#   bytes    MIME corpora -> eml, mbox, maildir, emlx -> read back: every message byte-identical
#   store    every store of the corpus -> each target: items in = out + skipped, folder trees, read back, peak working set
#   oracle   Python mailbox/email read the produced mbox, Maildir and EML trees; folder counts equal ours; the produced EMLs go
#            through the MimeKit, Python and Go differential
#   rt       MAPI -> EML -> MAPI through the engine (OxcMailTool rt-store) and the OLM field check
#   memory   the CMU Enron maildir (517,401 messages) -> Maildir and mbox: peak working set
# Output in $W (default ~/onemail/work-u29). Corpora come from tools/corpus/fetch*.sh. Light work: it stops when the worker's
# benchmark lock appears.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
W="${ONEMAIL_CONVERT_WORK:-$HOME/onemail/work-u29}"
C="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}"
export DOTNET_ROOT="$HOME/dotnet10" PATH="$HOME/dotnet10:$HOME/go-sdk/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMAIL_ARTIFACTS="$W/artifacts"
mkdir -p "$W"
( while sleep 30; do [ -e "$HOME/WORKER-BENCH.lock" ] && { echo "bench lock: stopping" >&2; kill -TERM -- -$$ 2>/dev/null; break; }; done ) &
dog=$!
trap 'kill $dog 2>/dev/null' EXIT
stages="${*:-build bytes store oracle rt}"
ct() { nice "$W/bin/convert/ConvertTool" "$@"; }
py="${ONEMAIL_PYTHON:-$HOME/onemail/oracles/py313/bin/python}"
rc=0
tika="$C/pst/tika"
ost="$C/pst/cfreds-leakage/iaman.informant@nist.gov.ost"
dbxstore="$(find "$C/dbx/hacking-case" -maxdepth 1 -type d -name '*Outlook_Express' | head -1)"
for s in $stages; do
  case $s in
    build)
      for p in convert/ConvertTool:convert oxcmail/OxcMailTool:oxc; do
        dotnet publish "$root/tools/${p%%:*}" -c Release -o "$W/bin/${p##*:}" -v quiet -clp:NoSummary || exit 1
      done
      dotnet publish "$root/src/OneMail.Cli" -c Release -o "$W/bin/cli" -v quiet -clp:NoSummary || exit 1 ;;
    bytes)
      mkdir -p "$W/probes"; cp "$root"/testdata/differential/*.eml "$W/probes/"
      for set in "probes:$W/probes" "mime4j:$C/mime4j" "dovecot-mbox:$C/dovecot-crlf" "dovecot-msgs:$C/dovecot-crlf.msgs" "enron-sample:$C/enron-sample" "dbx-store:$dbxstore"; do
        for f in eml mbox maildir emlx; do echo "== ${set%%:*} -> $f"; ct bytes "${set#*:}" $f "$W/work-bytes" || rc=1; done
      done ;;
    store)
      for set in "ost:$ost" "tika-pst:$tika" "olm-generated:$root/testdata/olm/generated.olm" "olm-samples:$C/olm" "dbx-store:$dbxstore" "dbx-file:$dbxstore/Inbox.dbx" "dovecot-mbox:$C/dovecot-crlf" "enron-sample:$C/enron-sample"; do
        [ -e "${set#*:}" ] || { echo "== ${set%%:*}: missing"; continue; }
        for f in eml mbox maildir emlx; do echo "== ${set%%:*} -> $f"; ct store "${set#*:}" $f "$W/work-store-$f" "$W/folders-${set%%:*}-$f.tsv" || rc=1; done
      done
      # A Maildir made from the dovecot mbox is itself a source; its folders and flags go on to every other store.
      ct store "$C/dovecot-crlf" maildir "$W/work-md" > /dev/null && for f in eml mbox emlx maildir; do echo "== maildir(dovecot) -> $f"; ct store "$W/work-md/out" $f "$W/work-store-$f" "$W/folders-maildir-$f.tsv" || rc=1; done
      for f in ics vcf tnef; do for set in "ost:$ost" "olm-generated:$root/testdata/olm/generated.olm"; do echo "== ${set%%:*} -> $f"; ct store "${set#*:}" $f "$W/work-store-$f" || rc=1; done; done ;;
    oracle)
      for set in ost tika-pst olm-generated dbx-store dovecot-mbox maildir; do
        for f in eml mbox maildir; do
          [ -s "$W/folders-$set-$f.tsv" ] || [ -e "$W/folders-$set-$f.tsv" ] || continue
          # re-run the conversion to a per-set directory, then read it with Python
          src=""; case $set in ost) src="$ost";; tika-pst) src="$tika";; olm-generated) src="$root/testdata/olm/generated.olm";; dbx-store) src="$dbxstore";; dovecot-mbox) src="$C/dovecot-crlf";; maildir) src="$W/work-md/out";; esac
          ct store "$src" $f "$W/work-o" "$W/o-ours.tsv" > /dev/null
          "$py" "$here/check_mailbox.py" "$W/work-o/out" $f > "$W/o-python.tsv"
          if diff -q "$W/o-ours.tsv" "$W/o-python.tsv" > /dev/null; then echo "python mailbox/email: $set -> $f folders+counts equal ($(awk -F'\t' '{n+=$2} END{print n}' "$W/o-python.tsv") messages)"; else echo "python mailbox/email: $set -> $f DIFFER"; diff "$W/o-ours.tsv" "$W/o-python.tsv" | head -5; rc=1; fi
        done
      done
      export ONEMAIL_ORACLE_BIN="${ONEMAIL_ORACLE_BIN:-$HOME/onemail/oxcmail/bin}" ONEMAIL_ORACLE_OUT="$W/oracle-out"
      for set in ost tika-pst olm-generated dbx-store; do
        case $set in ost) src="$ost";; tika-pst) src="$tika";; olm-generated) src="$root/testdata/olm/generated.olm";; dbx-store) src="$dbxstore";; esac
        rm -rf "$W/work-e"; ct store "$src" eml "$W/work-e" > /dev/null
        rm -rf "$ONEMAIL_ORACLE_OUT/conv-$set"
        "$root/tools/oracles/run-corpus.sh" "conv-$set" "$W/work-e/out" --drivers mimekit,python,go,onemail > "$W/diff-$set.txt" 2>&1; echo "differential conv-$set exit $?"; tail -n 12 "$ONEMAIL_ORACLE_OUT/conv-$set/report.md" 2>/dev/null
      done ;;
    rt)
      for set in msg pst; do echo "== rt-store $set"; nice "$W/bin/oxc/OxcMailTool" rt-store "$C/$set" || rc=1; done
      echo "== olm"; ct olm "$root/testdata/olm/generated.olm" || rc=1
      for f in "$C"/olm/*.olm; do [ -e "$f" ] && { echo "== olm $f"; ct olm "$f" || rc=1; }; done ;;
    memory)
      for f in maildir mbox; do echo "== enron-cmu -> $f"; ct store "$C/enron-cmu/maildir" $f "$W/work-mem" || rc=1; done ;;
  esac
done
exit $rc
