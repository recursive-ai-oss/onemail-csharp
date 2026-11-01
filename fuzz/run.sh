#!/usr/bin/env bash
# Build, instrument and run the fuzz target under libFuzzer.
#
#   fuzz/run.sh <target> <seconds> [corpus dir] [extra libFuzzer flags...]
#   fuzz/run.sh replay <target> <file>...      # run saved inputs once, without libFuzzer (reproduce a finding)
#
# Targets: dkim-tags, dkim-key, dns-reply (DKIM/ARC headers, key records, DNS replies), text (encoded words, RFC 2231 parameters, UTF-7, charset decoders), mime-reader, mime-tree (each parser adds its own; see fuzz/README.md)
#
# Environment (nothing is baked into the script):
#   ONEMAIL_FUZZ_WORK    required: where builds, corpora and findings go. Keep it outside the checkout.
#   ONEMAIL_FUZZ_TOOLS   directory holding `fuzz/libfuzzer-dotnet` (the libFuzzer host) and `sharpfuzz/sharpfuzz`
#                        (the instrumenter). LIBFUZZER_DOTNET and SHARPFUZZ override the two paths individually.
#   ONEMAIL_ARTIFACTS    build output root, as for every other project in the repo
#   DOTNET               the dotnet executable (default: dotnet from PATH)
#   FUZZ_TIMEOUT         per-input timeout in seconds (default 10)
#   FUZZ_RSS_MB          memory limit in MB (default 1024)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="${DOTNET:-dotnet}"
work="${ONEMAIL_FUZZ_WORK:?set ONEMAIL_FUZZ_WORK to a scratch directory outside the checkout}"
case "$(readlink -f "$work")/" in
  "$root"/*) echo "ONEMAIL_FUZZ_WORK must be outside the checkout" >&2; exit 2 ;;
esac
tools="${ONEMAIL_FUZZ_TOOLS:-}"
host="${LIBFUZZER_DOTNET:-$tools/fuzz/libfuzzer-dotnet}"
instrument="${SHARPFUZZ:-$tools/sharpfuzz/sharpfuzz}"

mode=fuzz
if [[ "${1:-}" == replay ]]; then
  mode=replay
  shift
fi
target="${1:?usage: run.sh <target> <seconds> [corpus dir] | run.sh replay <target> <file>...}"
shift

# Maximum input length per target.
case "$target" in
  # <target>) maxlen=<bytes> ;;   one line per target in the harness
  text) maxlen=4096 ;;
  mime-reader|mime-tree) maxlen=65536 ;;
  contentlines) maxlen=65536 ;;
  calendar) maxlen=65536 ;;
  recurrence) maxlen=16384 ;;
  vcard-view) maxlen=16384 ;;
  jcal-jcard|xcal-xcard) maxlen=32768 ;;
  ndb) maxlen=8192 ;;
  ltp) maxlen=8192 ;;
  pst-messaging) maxlen=8192 ;;
  msg) maxlen=2048 ;;
  msg-write) maxlen=4096 ;;
  tnef) maxlen=2048 ;;
  mbox) maxlen=65536 ;;
  emlx) maxlen=16384 ;;
  dbx) maxlen=4096 ;;
  span-ndb) maxlen=70000 ;;
  span-mime) maxlen=8192 ;;
  span-cfb) maxlen=1024 ;;
  dbx-recovery|olm-recovery) maxlen=2048 ;;
  reports|authres|mhtml-uri) maxlen=4096 ;;
  mail-open) maxlen=65536 ;;
  rtf-decompress) maxlen=65536 ;;
  rtf-deencap) maxlen=16384 ;;
  rtf-html) maxlen=32768 ;;
  oxcmail) maxlen=16384 ;;
  composition) maxlen=8192 ;;
  oxocal) maxlen=4096 ;;
  calcard) maxlen=8192 ;;
  htmltext) maxlen=16384 ;;
  sanitizer|sanitizer-reparse|sanitizer-display) maxlen=16384 ;;
  olm) maxlen=65536 ;;
  render) maxlen=8192 ;;
  render-datauri) maxlen=65536 ;;
  dkim-tags) maxlen=4096 ;;
  dkim-key) maxlen=9000 ;;
  dns-reply) maxlen=2048 ;;
  *) echo "unknown target '$target'" >&2; exit 2 ;;
esac

bin="$work/bin"
rm -rf "$bin"
mkdir -p "$bin"

# Build once per run: release, then instrument only the library assembly (the harness itself needs no coverage).
"$dotnet_bin" build "$root/fuzz/OneMail.Fuzz/OneMail.Fuzz.csproj" -c Checked -v quiet -clp:NoSummary -o "$bin"

if [[ "$mode" == replay ]]; then
  ONEMAIL_FUZZ_WORK="$work" "$dotnet_bin" "$bin/RecursiveAI.OneMail.Fuzz.dll" replay "$target" "$@"
  exit $?
fi

seconds="${1:?usage: run.sh <target> <seconds> [corpus dir]}"
shift
extra=""
if [[ $# -gt 0 && "${1:0:1}" != "-" ]]; then
  extra="$1"
  shift
fi
[[ -x "$host" ]] || { echo "libFuzzer host not found at '$host' (set ONEMAIL_FUZZ_TOOLS)" >&2; exit 2; }
[[ -x "$instrument" ]] || { echo "sharpfuzz not found at '$instrument' (set ONEMAIL_FUZZ_TOOLS)" >&2; exit 2; }

export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v "$dotnet_bin")")")}"
corpus="$work/corpus/$target"
findings="$work/findings/$target"
mkdir -p "$corpus" "$findings"

# A target may add seeds to "$corpus"; with none, libFuzzer starts from one empty input.
[[ -n "$(ls -A "$corpus")" ]] || printf '\x00' >"$corpus/seed-empty"

"$instrument" "$bin/RecursiveAI.OneMail.dll" >/dev/null
"$instrument" "$bin/RecursiveAI.OneMail.Calendar.dll" >/dev/null
"$instrument" "$bin/RecursiveAI.OneMail.Render.dll" >/dev/null
"$instrument" "$bin/RecursiveAI.OneMail.Cryptography.dll" >/dev/null

# An extra seed directory is read-only for libFuzzer: new finds go to $corpus.
dirs=("$corpus")
[[ -z "$extra" ]] || dirs+=("$extra")

# Run. -max_total_time bounds the run; crashes, hangs and out-of-memory inputs are written to the findings folder.
ONEMAIL_FUZZ_TARGET="$target" ONEMAIL_FUZZ_WORK="$work" "$host" \
  --target_path="$(command -v "$dotnet_bin")" --target_arg="$bin/RecursiveAI.OneMail.Fuzz.dll" \
  -max_total_time="$seconds" -timeout="${FUZZ_TIMEOUT:-10}" -rss_limit_mb="${FUZZ_RSS_MB:-1024}" \
  -max_len="$maxlen" -artifact_prefix="$findings/" -print_final_stats=1 "$@" "${dirs[@]}"
