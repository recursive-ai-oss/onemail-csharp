# Display-HTML checks

Tools for the sanitiser and display HTML (`docs/display-html.md`).

- `SanitizeTool show` sanitises stdin and prints the output and the report. `pages VECTORS OUTDIR HOST` writes the three browser pages
  (sanitised with remote content blocked, sanitised with it allowed, and the raw control). `seeds VECTORS OUTDIR` writes the vectors as fuzz seeds.
  `corpus PATH...` sanitises the HTML bodies of `.html` and mail files (directories are walked) and reports how many keep their visible text
  word for word, how many outputs are unstable, and the remote references and tracking indications found. `display FILE` prints a message's display HTML.
- `browser-check.sh OUTDIR` loads the pages in headless Chromium against a request-logging server and counts script executions and requests;
  the raw page is the control and must show both.
- `compare-dompurify.sh OUTDIR` runs the released DOMPurify (npm, version pinned in the script) over the same vectors in headless Chromium
  and lists the vectors where it keeps a construct this sanitiser removes and the reverse. DOMPurify is run, never read.
- Vectors: `testdata/sanitizer/xss.txt`, our own, one record per `# name` line, separated by `====`.
