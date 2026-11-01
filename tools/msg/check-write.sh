#!/usr/bin/env bash
# Writes MSG files from every kind of source through `onemail convert --to msg`, then compares what our reader and
# extract-msg 0.55.0 (run only, in a venv) read from the written files. Prints the counts per source; exit 1 on any
# unexplained difference.
#
#   [ONLY=msg|pst|tnef|olm|dovecot|enron] check-write.sh <corpus dir> <work dir> <python with extract-msg> [max files per source]
#
# The corpus dir holds msg/, pst/, tnef/, olm/, dovecot-crlf and enron-sample/maildir as the fetch scripts lay them out.
set -u
CORPUS=$1
WORK=$2
PY=$3
MAX=${4:-300}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$WORK"
dotnet build -v quiet -clp:NoSummary "$ROOT/src/OneMail.Cli/OneMail.Cli.csproj" >"$WORK/build-cli.log" 2>&1 || { tail -20 "$WORK/build-cli.log"; exit 2; }
dotnet build -v quiet -clp:NoSummary "$ROOT/tools/msg/MsgTool/MsgTool.csproj" >"$WORK/build-tool.log" 2>&1 || { tail -20 "$WORK/build-tool.log"; exit 2; }
ART=${ONEMAIL_ARTIFACTS:-$ROOT/artifacts}
CLI=$(find "$ART" -name RecursiveAI.OneMail.Cli.dll -path '*/bin/*' | head -1)
TOOL=$(find "$ART" -name MsgTool.dll -path '*/bin/*' | head -1)
[ -n "$CLI" ] && [ -n "$TOOL" ] || { echo "cannot find the built CLI ($CLI) or MsgTool ($TOOL) under $ART"; exit 2; }
echo "cli=$CLI tool=$TOOL"

fail=0
run() { # name, input...  (each input converted into its own directory)
  local name=$1; shift
  [ -n "${ONLY:-}" ] && [ "$ONLY" != "$name" ] && return
  local out="$WORK/out/$name"
  rm -rf "$out"; mkdir -p "$out"
  local n=0
  for input in "$@"; do
    n=$((n + 1)); [ $n -gt "$MAX" ] && break
    dotnet "$CLI" convert --to msg "$input" "$out/$n" >>"$WORK/convert-$name.log" 2>&1 || echo "  convert exit $? for $input"
  done
  local files; files=$(find "$out" -name '*.msg' | wc -l)
  echo "== $name: $n inputs -> $files MSG files"
  if [ "$files" -gt 0 ]; then
    # extract-msg is slow per file: compare an even sample of at most MAX files.
    local sample="$WORK/sample/$name"; rm -rf "$sample"; mkdir -p "$sample"
    local step=$(( (files + MAX - 1) / MAX ))
    find "$out" -name '*.msg' | sort | awk -v s="$step" -v d="$sample" 'NR % s == 1 || s == 1 { printf "ln -s \"%s\" \"%s/%d.msg\"\n", $0, d, NR }' | sh
    "$PY" "$ROOT/tools/msg/compare_extract_msg.py" "$TOOL" "$sample" >"$WORK/compare-$name.log" 2>&1
    local rc=$?
    tail -4 "$WORK/compare-$name.log"
    [ $rc -ne 0 ] && fail=1
  fi
}

run msg $(find "$CORPUS/msg" -type f \( -iname '*.msg' -o -iname '*.oft' \) | sort)
run pst $(find "$CORPUS/pst" -type f \( -iname '*.pst' -o -iname '*.ost' \) | sort)
run tnef $(find "$CORPUS/tnef" -type f | sort)
run olm $(find "$CORPUS/olm" -type f -name '*.olm' | sort)
[ -f "$CORPUS/dovecot-crlf" ] && run dovecot "$CORPUS/dovecot-crlf"
[ -d "$CORPUS/enron-sample/maildir" ] && run enron "$CORPUS/enron-sample/maildir"
exit $fail
