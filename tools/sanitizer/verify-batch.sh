#!/usr/bin/env bash
# One worker job for the display-HTML work: the Checked test suite, the two fuzz targets, the NativeAOT smoke app and the real-mail sweep.
#
#   tools/sanitizer/verify-batch.sh WORKDIR [FUZZ_SECONDS]
#
# Needs .NET 10 on PATH, the libFuzzer host and SharpFuzz (see fuzz/run.sh) and, for the sweep, mail corpora under $ONEMAIL_CORPUS
# (default ~/onemail/corpus). Prints one summary line per step; the full logs are under WORKDIR/logs.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="${1:?work directory}"; secs="${2:-540}"
mkdir -p "$work/logs"; work="$(cd "$work" && pwd)"
export ONEMAIL_ARTIFACTS="$work/artifacts" TMPDIR="$work/tmp"; mkdir -p "$TMPDIR"
export LIBFUZZER_DOTNET="${LIBFUZZER_DOTNET:-$HOME/onemail/tools/libfuzzer-dotnet}" SHARPFUZZ="${SHARPFUZZ:-$HOME/onemail/tools/sharpfuzz}"
[ -x "$LIBFUZZER_DOTNET" ] || LIBFUZZER_DOTNET=$(find "$HOME/onemail/tools/libfuzzer-dotnet" -maxdepth 2 -type f -name 'libfuzzer-dotnet*' -perm -u+x | head -1)
[ -x "$SHARPFUZZ" ] || SHARPFUZZ=$(find "$HOME/onemail/tools/sharpfuzz" -maxdepth 3 -type f -name sharpfuzz -perm -u+x | head -1)
cd "$repo"

# Seeds for the fuzz targets: every vector, under two option bytes.
dotnet build tools/sanitizer/SanitizeTool -c Release -v quiet -clp:NoSummary >"$work/logs/tool-build.log" 2>&1 || { echo "tool build failed"; tail -5 "$work/logs/tool-build.log"; exit 1; }
tool="dotnet $ONEMAIL_ARTIFACTS/bin/SanitizeTool/release/SanitizeTool.dll"
$tool seeds testdata/sanitizer/xss.txt "$work/seeds"

# Fuzz targets, in the background, while the suite runs.
pids=()
for t in sanitizer sanitizer-reparse; do
  w="$work/fuzz/$t"; rm -rf "$w"; mkdir -p "$w"
  ( export ONEMAIL_FUZZ_WORK="$w"; cp -r "$work/seeds" "$w/corpus"; bash fuzz/run.sh "$t" "$secs" "$w/corpus" >"$w/run.log" 2>&1; echo "exit=$?" >>"$w/run.log" ) &
  pids+=($!)
  sleep 20
done

# AOT smoke.
( dotnet publish smoke/Aot -r linux-x64 -warnaserror -p:CppCompilerAndLinker=gcc -o "$work/aot" -v quiet -clp:NoSummary >"$work/logs/aot-publish.log" 2>&1 \
    && "$work/aot/RecursiveAI.OneMail.AotSmoke" >"$work/logs/aot-run.log" 2>&1; echo "aot exit=$?" >>"$work/logs/aot-run.log" ) || true
echo "aot: $(grep -E '^sanitizer:' "$work/logs/aot-run.log" 2>/dev/null) $(tail -1 "$work/logs/aot-run.log" 2>/dev/null) warnings=$(grep -c 'warning' "$work/logs/aot-publish.log" 2>/dev/null)"

# Checked suite.
dotnet build tests/OneMail.Tests -c Checked -v quiet -clp:NoSummary >"$work/logs/tests-build.log" 2>&1
dotnet test --project tests/OneMail.Tests -c Checked --no-build >"$work/logs/tests.log" 2>&1
echo "tests: $(grep -E '^\s+(total|failed|succeeded|skipped):' "$work/logs/tests.log" | tr -s ' \n' ' ')"

# Real-mail sweep.
corpus="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}"
for d in msg pst tnef mime4j enron-sample; do
  [ -e "$corpus/$d" ] || continue
  echo "corpus $d: $(timeout 600 $tool corpus "$corpus/$d" 2>&1 | head -12 | tr '\n' ' ')"
done

wait "${pids[@]}"
for t in sanitizer sanitizer-reparse; do
  w="$work/fuzz/$t"
  echo "fuzz $t: $(grep -E '^stat::number_of_executed_units|^stat::new_units_added' "$w/run.log" | tr '\n' ' ') $(tail -1 "$w/run.log") findings=$(ls "$w/findings/$t" 2>/dev/null | wc -l)"
  grep -m3 -E 'ERROR|Exception' "$w/run.log"
done
