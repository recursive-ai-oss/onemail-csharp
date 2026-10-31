#!/usr/bin/env bash
# Every XSS vector of the sanitiser's corpus placed in every text field of a calendar card, rendered in headless Chromium with no sanitiser in
# between: scripts that run and requests that reach a logging server are counted, against the raw vectors as the control.
#
#   tools/calcard/browser-check.sh OUTDIR
#
# Reuses the page runner of tools/sanitizer/browser-check.sh: the card tool writes the same three page kinds.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dotnet build "$repo/tools/calcard/CalcardTool" -c Release -v quiet -clp:NoSummary >/dev/null
tool="$(find "${ONEMAIL_ARTIFACTS:-$repo/artifacts}/bin/CalcardTool/release" -name CalcardTool -type f | head -1)"
SANITIZE_TOOL="$tool" exec bash "$repo/tools/sanitizer/browser-check.sh" "${1:?output directory}"
