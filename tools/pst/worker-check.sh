#!/usr/bin/env bash
# Builds the PST writer's test tools on the worker (Checked, so the unsafe decoders assert their bounds) and runs the PST writer tests and
# the write-check over the corpora in ~/onemail/corpus. Output goes to <out>/report.txt.
#
#   bash tools/pst/worker-check.sh <out-dir>
set -u
out="${1:?out dir}"; mkdir -p "$out"
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEMAIL_ARTIFACTS="$out/artifacts" TMPDIR="$out/tmp" CONFIG=checked; mkdir -p "$TMPDIR"
report="$out/report.txt"; : > "$report"
log() { echo "$*" | tee -a "$report"; }
dotnet build tests/PstOracle -c Checked -m:8 > "$out/build-oracle.log" 2>&1 || { tail -20 "$out/build-oracle.log"; exit 1; }
dotnet build src/OneMail.Cli -c Checked -m:8 > "$out/build-cli.log" 2>&1 || { tail -20 "$out/build-cli.log"; exit 1; }
dotnet build tests/OneMail.Tests -c Checked -m:8 > "$out/build-tests.log" 2>&1 || { tail -20 "$out/build-tests.log"; exit 1; }
log "build: $(grep -E 'Warning\(s\)|Error\(s\)' "$out/build-tests.log" | tr '\n' ' ')"
tests="$ONEMAIL_ARTIFACTS/bin/OneMail.Tests/checked/RecursiveAI.OneMail.Tests"
"$tests" -namespace RecursiveAI.OneMail.Tests.Pst > "$out/test-pst.log" 2>&1; log "pst tests: $(grep -E "Total:" "$out/test-pst.log" | tail -1)"
"$tests" -namespace RecursiveAI.OneMail.Tests.Conversion > "$out/test-conv.log" 2>&1; log "conversion tests: $(grep -E "Total:" "$out/test-conv.log" | tail -1)"

c=~/onemail/corpus
rc=0
check() { bash tools/pst/write-check.sh "$@" 2>&1 | tee -a "$report"; [ "${PIPESTATUS[0]}" -eq 0 ] || rc=1; }
for f in "$c"/pst/tika/*.pst; do check "tika-$(basename "$f" .pst)" "$f" "$out/w"; done
for f in "$c"/pst/cfreds-leakage/*.ost; do check "cfreds-$(basename "$f" .ost)" "$f" "$out/w"; done
check msg-poi "$c/msg/poi" "$out/w"
check msg-tika "$c/msg/tika" "$out/w"
check tnef "$c/tnef" "$out/w"
check olm "$c/olm" "$out/w"
check eml-mime4j "$c/mime4j" "$out/w"
check eml-enron-sample "$c/enron-sample" "$out/w"
check mbox-dovecot "$c/dovecot-crlf" "$out/w"
log "write-check rc=$rc"
exit $rc
