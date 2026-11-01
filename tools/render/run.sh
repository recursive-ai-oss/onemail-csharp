#!/usr/bin/env bash
# The render checks on the worker. Stages (default: gen sweep fonts remote chrome):
#   build    builds RenderTool (default fonts) and RenderTool with the optional font packages into $W/bin
#   gen      writes the generated and hostile cases to $W/gen
#   sweep    every corpus item -> PDF and PNG with the default fonts; PDF text (pdftotext -raw) against the display HTML's text
#   fonts    the generated cases and the corpus again with the CJK, emoji, Arabic and Hebrew font packages
#   remote   the remote-image cases with remote content allowed and no Resources callback: every request is listed, nothing is fetched
#   chrome   headless Chrome prints the same display HTML: page counts and a pixel score against our page 1
# Output in $W (default ~/onemail/work-u34). Corpora come from tools/corpus/fetch*.sh. Light work: it stops when the worker's
# benchmark lock appears.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
W="${ONEMAIL_RENDER_WORK:-$HOME/onemail/work-u34}"
C="${ONEMAIL_CORPUS:-$HOME/onemail/corpus}"
export DOTNET_ROOT="$HOME/dotnet10" PATH="$HOME/dotnet10:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMAIL_ARTIFACTS="$W/artifacts"
mkdir -p "$W"
( while sleep 30; do [ -e "$HOME/WORKER-BENCH.lock" ] && { echo "bench lock: stopping" >&2; kill -TERM -- -$$ 2>/dev/null; break; }; done ) &
dog=$!
trap 'kill $dog 2>/dev/null' EXIT
stages="${*:-gen sweep fonts remote chrome}"
per="${RENDER_PER:-60}"
corpora() {
  echo "$C/msg" "$C/pst" "$C/tnef" "$C/olm" "$C/mime4j" "$C/enron-sample"
  d="$(find "$C/dbx/hacking-case" -maxdepth 1 -type d -name '*Outlook_Express' | head -1)"; [ -n "$d" ] && echo "$d"
  [ -d "$C/dovecot-crlf.msgs" ] && echo "$C/dovecot-crlf.msgs"
}
for s in $stages; do
  echo "== $s"
  case $s in
    build)
      dotnet build "$root/tools/render/RenderTool" -c Release -o "$W/bin/default" -v quiet -clp:NoSummary 2>&1 | tail -3
      dotnet build "$root/tools/render/RenderTool" -c Release -p:WithFonts=true -o "$W/bin/fonts" -v quiet -clp:NoSummary 2>&1 | tail -3 ;;
    gen)
      rm -rf "$W/gen"; dotnet "$W/bin/default/RenderTool.dll" gen "$W/gen" ;;
    sweep)
      rm -rf "$W/sweep"; /usr/bin/time -v dotnet "$W/bin/default/RenderTool.dll" sweep "$W/sweep/gen" --keep 40 "$W/gen" 2>"$W/sweep-gen.time" | tee "$W/sweep-gen.txt"
      /usr/bin/time -v dotnet "$W/bin/default/RenderTool.dll" sweep "$W/sweep/corpus" --per "$per" --keep 60 $(corpora) 2>"$W/sweep-corpus.time" | tee "$W/sweep-corpus.txt"
      grep -E 'Maximum resident|Elapsed' "$W/sweep-gen.time" "$W/sweep-corpus.time" ;;
    fonts)
      rm -rf "$W/fonts"; dotnet "$W/bin/fonts/RenderTool.dll" sweep "$W/fonts/gen" --keep 40 "$W/gen" | tee "$W/fonts-gen.txt"
      dotnet "$W/bin/fonts/RenderTool.dll" sweep "$W/fonts/corpus" --per "$per" --keep 0 $(corpora) | tee "$W/fonts-corpus.txt" ;;
    remote)
      rm -rf "$W/remote"; dotnet "$W/bin/default/RenderTool.dll" sweep "$W/remote" --allow-remote --keep 5 "$W/gen/remote-image.eml" $(corpora) --per 30 | tee "$W/remote.txt"
      python3 "$here/summarize.py" unserved "$W/remote/results.jsonl" ;;
    chrome)
      python3 "$here/chrome_compare.py" "$W/sweep/corpus/docs" "$W/sweep/gen/docs" | tee "$W/chrome.txt" ;;
  esac
done
for r in sweep/gen sweep/corpus fonts/gen fonts/corpus; do [ -f "$W/$r/results.jsonl" ] && { echo "-- $r"; python3 "$here/summarize.py" text "$W/$r/results.jsonl"; }; done
