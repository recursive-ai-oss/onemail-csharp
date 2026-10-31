#!/usr/bin/env bash
# Compares this sanitiser with the released DOMPurify (npm, run as a black box in headless Chromium) on the XSS vectors.
#
#   tools/sanitizer/compare-dompurify.sh OUTDIR
#
# Both outputs are scored by the same script in the same page: it parses each output into a template element and lists what it finds
# (script-bearing elements, event handlers, script URLs, frames and plugins, remote fetches). DOMPurify runs with its default configuration.
# The report lists, per category, the vectors where OneMail's output has a finding DOMPurify's lacks ("looser", must be none for the
# script categories) and the reverse ("stricter").
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="${1:?output directory}"; mkdir -p "$out"; out="$(cd "$out" && pwd)"
browser="${BROWSER:-$(command -v chromium || command -v chromium-browser || command -v google-chrome)}"
tool="${SANITIZE_TOOL:-dotnet run --project $repo/tools/sanitizer/SanitizeTool -c Release --}"
version="${DOMPURIFY_VERSION:-3.4.17}"

(cd "$out" && npm install --no-audit --no-fund --silent --prefix "$out/node" "dompurify@$version" >/dev/null)
cp "$out/node/node_modules/dompurify/dist/purify.min.js" "$out/purify.min.js"
$tool json "$repo/testdata/sanitizer/xss.txt" "$out/vectors.json" "leak.invalid:8080"

python3 - "$out" <<'PY'
import json, sys
out = sys.argv[1]
# "<" is escaped so a vector holding </script> cannot end the page script.
vectors = open(out + "/vectors.json").read().replace("<", "\\u003c")
page = """<!doctype html><meta charset=utf-8><script src="purify.min.js"></script><pre id=r>pending</pre><script>
try {
const V = %s;
const DANGER_EL = new Set(['script','iframe','object','embed','applet','frame','frameset','base','meta','link','form','input','button','textarea','select','svg','math','video','audio','noscript','template']);
function score(html) {
  const f = new Set();
  const t = document.createElement('template'); t.innerHTML = html;
  const walk = (n) => {
    if (n.nodeType === 1) {
      const name = n.localName;
      if (name === 'script') f.add('script-element');
      else if (['iframe','object','embed','applet','frame','frameset'].includes(name)) f.add('frame-or-plugin');
      else if (DANGER_EL.has(name)) f.add('other-active-element:' + name);
      if (name === 'style') { const c = n.textContent.toLowerCase(); if (c.includes('@import') || c.includes('expression') || c.includes('javascript') || c.includes('behavior') || c.includes('-moz-binding')) f.add('css-active'); if (c.includes('leak.invalid')) f.add('remote-fetch'); }
      for (const a of Array.from(n.attributes)) {
        const an = a.name.toLowerCase(), av = a.value.replace(/[\\u0000-\\u0020]/g,'').toLowerCase();
        if (an.startsWith('on')) f.add('handler');
        if (av.includes('javascript:') || av.includes('vbscript:')) f.add('script-url');
        if (['srcdoc','formaction','action'].includes(an)) f.add('srcdoc-or-action');
        if (an === 'style' && (av.includes('expression') || av.includes('behavior') || av.includes('-moz-binding'))) f.add('css-active');
        if (av.includes('leak.invalid') && an !== 'href') f.add('remote-fetch');
        if ((an === 'href' || an === 'src') && av.startsWith('data:') && !av.startsWith('data:image/png') && !av.startsWith('data:image/gif') && !av.startsWith('data:image/jpeg')) f.add('data-url');
      }
      for (const c of n.childNodes) walk(c);
      if (name === 'template') for (const c of n.content.childNodes) walk(c);
    }
  };
  for (const c of t.content.childNodes) walk(c);
  return [...f].sort();
}
const rows = V.map(v => ({ name: v.name, ours: score(v.ours), dompurify: score(DOMPurify.sanitize(v.raw)), raw: score(v.raw) }));
document.getElementById('r').textContent = 'RESULT' + JSON.stringify(rows) + 'END';
} catch (e) { document.getElementById('r').textContent = 'ERR ' + e + ' ' + e.stack; }
</script>""" % vectors
open(out + "/compare.html", "w").write(page)
PY

"$browser" --headless=new --no-sandbox --disable-gpu --user-data-dir="$out/profile" --allow-file-access-from-files --virtual-time-budget=15000 \
  --dump-dom "file://$out/compare.html" >"$out/compare-dom.html" 2>"$out/browser.log" || true

python3 - "$out" <<'PY'
import json, re, sys, html
out = sys.argv[1]
dom = open(out + "/compare-dom.html").read()
m = re.search(r"<pre id=\"r\">RESULT(.*?)END</pre>", dom, re.S)
if not m:
    print("no result: the page did not finish"); sys.exit(1)
rows = json.loads(html.unescape(m.group(1)))
script_cats = {"script-element", "handler", "script-url", "frame-or-plugin", "css-active", "srcdoc-or-action", "data-url"}
looser, stricter, both = {}, {}, 0
for r in rows:
    o, d = set(r["ours"]), set(r["dompurify"])
    for c in o - d: looser.setdefault(c, []).append(r["name"])
    for c in d - o: stricter.setdefault(c, []).append(r["name"])
print(f"vectors: {len(rows)}")
print(f"OneMail output with any finding: {sum(1 for r in rows if r['ours'])}; DOMPurify default: {sum(1 for r in rows if r['dompurify'])}; raw input: {sum(1 for r in rows if r['raw'])}")
print("OneMail looser than DOMPurify:")
for c, names in sorted(looser.items()): print(f"  {c}: {len(names)} {'(SCRIPT CATEGORY)' if c in script_cats else ''} {names[:8]}")
print("OneMail stricter than DOMPurify (vectors where DOMPurify keeps a finding):")
for c, names in sorted(stricter.items()): print(f"  {c}: {len(names)}")
bad = [c for c in looser if c in script_cats]
sys.exit(1 if bad else 0)
PY
