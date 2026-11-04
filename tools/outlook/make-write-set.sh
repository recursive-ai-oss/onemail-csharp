#!/usr/bin/env bash
# Generates the files the Outlook checks open: builder output of several item kinds, the in-place durability scenarios' edited files (both table
# modes) with the files they started from, compactions, a 100,000-row folder before and after row-by-row edits, and written MSG/OFT files.
#
#   bash tools/outlook/make-write-set.sh <out-dir>
#
# Needs a .NET 10 SDK on the PATH and the corpora under ~/onemail/corpus (ONEMAIL_CORPUS). Everything it writes is generated from our own code
# and the fetched corpora; none of it is committed.
set -uo pipefail
out="${1:?out dir}"; mkdir -p "$out"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$out/artifacts}" TMPDIR="${TMPDIR:-$out/tmp}"; mkdir -p "$TMPDIR"
for p in src/OneMail.Cli tests/PstOracle tests/Durability tools/msg/MsgTool; do
  dotnet build "$here/$p" -c Release -m:4 -v quiet -clp:NoSummary > "$out/build-$(basename "$p").log" 2>&1 || { tail -20 "$out/build-$(basename "$p").log"; exit 1; }
done
bin="$ONEMAIL_ARTIFACTS/bin"
cli() { dotnet "$bin/OneMail.Cli/release/RecursiveAI.OneMail.Cli.dll" "$@"; }
dur() { dotnet "$bin/Durability/release/RecursiveAI.OneMail.Durability.dll" "$@"; }
ora() { dotnet "$bin/PstOracle/release/RecursiveAI.OneMail.PstOracle.dll" "$@"; }

mkdir -p "$out/builder" "$out/edited-incremental" "$out/edited-rewrite" "$out/compact" "$out/large" "$out/msg" "$out/controls"

# Files Outlook itself wrote (controls: they must open cleanly, or the oracle setup is wrong).
cp "$corpus"/pst/tika/*.pst "$out/controls/"

# 1. Builder output.
cli convert --to pst "$corpus/enron-sample/maildir" "$out/builder/mail-maildir.pst" > "$out/builder/mail-maildir.log" 2>&1
for f in "$corpus"/pst/tika/*.pst; do cli convert --to pst "$f" "$out/builder/from-$(basename "$f")" > "$out/builder/from-$(basename "$f").log" 2>&1; done
cli convert --to pst "$here/testdata/msg/generated" "$out/builder/from-msg.pst" > "$out/builder/from-msg.log" 2>&1
for d in libical thunderbird ezvcard; do
  [ -d "$corpus/calendar/$d" ] && cli convert --to pst "$corpus/calendar/$d" "$out/builder/items-$d.pst" > "$out/builder/items-$d.log" 2>&1
done
ora pstgen "$out/builder/generated-3000.pst" 3000 12 10 > "$out/builder/generated-3000.log" 2>&1

# 2. The durability scenarios: each base before (copy) and after each scenario, in each table mode.
for mode in incremental rewrite; do
  ONEMAIL_TABLE_UPDATES=$mode dur make "$out/edited-$mode" > "$out/edited-$mode.log" 2>&1
done
for b in ours tika1 tika2; do dur base $b "$out/controls/base-$b.pst" > /dev/null 2>&1; done

# 3. Compaction of edited files (a rewrite through the builder).
for f in "$out"/edited-incremental/edited-ours-delete.pst "$out"/edited-incremental/edited-tika1-mixed.pst "$out"/edited-incremental/edited-ours-mixed.pst; do
  [ -f "$f" ] && cli convert --to pst "$f" "$out/compact/compact-$(basename "$f")" > "$out/compact/$(basename "$f").log" 2>&1
done

# 4. 100,000 rows built, then edited row by row (incremental) and by rewrite.
dur tables-make "$out/large/base-100k.pst" 100000 > "$out/large/make.log" 2>&1
for mode in incremental rewrite; do
  for op in add delete move; do
    dur tables "$out/large/base-100k.pst" "$out/large/$op-$mode.pst" $mode $op 200 > "$out/large/$op-$mode.log" 2>&1
  done
done

# 5. MSG and OFT: converted from MIME and Outlook items, plus the committed generated MSGs.
cp "$here"/testdata/msg/generated/*.msg "$out/msg/"
cli convert --to msg "$corpus/pst/tika/testPST.pst" "$out/msg/from-tika-pst" > "$out/msg/from-tika-pst.log" 2>&1

ls -R "$out" | grep -c '\.pst$' | sed 's/^/pst files: /'
