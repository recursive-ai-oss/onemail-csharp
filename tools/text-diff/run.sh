#!/usr/bin/env bash
# Differential run for the text layer: generate the corpus, run OneMail and the three oracles over it, compare.
#
#   tools/text-diff/run.sh <work dir> [extra cases.jsonl ...]
#
# Needs on PATH: dotnet 10, go, and a python with the standard library (the oracle venv's: ONEMAIL_PY).
# MimeKit comes from NuGet (pinned in MimeKitOracle.csproj); golang.org/x/text is pinned in go/go.mod.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="${1:?work dir}"
shift || true
py="${ONEMAIL_PY:-python3}"
mkdir -p "$work"
"$py" "$here/gen_corpus.py" --out "$work/generated.jsonl"
cat "$work/generated.jsonl" "$@" > "$work/cases.raw.jsonl"
# Renumber so ids stay unique when extra case files are appended.
"$py" - "$work/cases.raw.jsonl" "$work/cases.jsonl" <<'EOF'
import json, sys
with open(sys.argv[2], "w") as out:
    for i, line in enumerate(open(sys.argv[1]), 1):
        c = json.loads(line); c["id"] = i
        out.write(json.dumps(c, separators=(",", ":")) + "\n")
EOF
dotnet run -c Release --project "$here/OneMailOracle" -- "$work/cases.jsonl" > "$work/ours.jsonl"
dotnet run -c Release --project "$here/MimeKitOracle" -- "$work/cases.jsonl" > "$work/mimekit.jsonl"
"$py" "$here/py_oracle.py" "$work/cases.jsonl" > "$work/python.jsonl"
(cd "$here/go" && go mod tidy >/dev/null 2>&1 || true; go run . "$work/cases.jsonl") > "$work/go.jsonl"
cd "$here"
"$py" compare.py "$work/cases.jsonl" "$work/ours.jsonl" "$work/python.jsonl" "$work/go.jsonl" "$work/mimekit.jsonl" --diffs "$work/diffs.jsonl"
