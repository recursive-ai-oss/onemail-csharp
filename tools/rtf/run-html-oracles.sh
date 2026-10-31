#!/usr/bin/env bash
# Convert every <id>.rtf in a directory with the three black-box converters (installed by oracles-html.sh):
#   <id>.pandoc.html  <id>.unrtf.html  <id>.lo.html
# then compare them with OneMail's <id>.ours.html (RtfTool html) using htmlcompare.py. Light work: run it at low priority and not
# while the benchmark lock exists.
#
#   tools/rtf/run-html-oracles.sh <dir>
set -uo pipefail
dir="$(cd "$1" && pwd)"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${ONEMAIL_ORACLES:-$HOME/onemail/oracles}/rtf-html"
shopt -s nullglob
for f in "$dir"/*.rtf; do
  id="$(basename "$f" .rtf)"
  [[ "$id" == *.ours ]] && continue
  timeout 60 "$root/pandoc-3.13/bin/pandoc" -f rtf -t html "$f" -o "$dir/$id.pandoc.html" 2>/dev/null || rm -f "$dir/$id.pandoc.html"
  timeout 60 "$root/unrtf/usr/bin/unrtf" -P "$root/unrtf/usr/share/unrtf/" --html "$f" >"$dir/$id.unrtf.html" 2>/dev/null || rm -f "$dir/$id.unrtf.html"
done
mkdir -p "$dir/lo"
profile="$dir/lo-profile"
rtfs=()
for f in "$dir"/*.rtf; do [[ "$f" == *.ours.rtf ]] || rtfs+=("$f"); done
timeout 1500 soffice -env:UserInstallation=file://"$profile" --headless --convert-to html:"HTML (StarWriter)" --outdir "$dir/lo" "${rtfs[@]}" >"$dir/lo.log" 2>&1
for f in "$dir"/lo/*.html; do mv "$f" "$dir/$(basename "$f" .html).lo.html"; done
python3 "$here/htmlcompare.py" "$dir"
