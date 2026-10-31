#!/usr/bin/env bash
# Runs the report differential over the corpus messages that carry report, authentication or related-part headers.
#
#   tools/oracles/reports/corpus.sh [CORPUS_DIR] [OUT]
#
# Selects files by grep into a flat directory of symlinks, then calls run.sh's compare step on it.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
corpus="${1:-$HOME/onemail/corpus}"
out="${2:-$HOME/onemail/reports/corpus-out}"
stage="$out/stage"
rm -rf "$stage"; mkdir -p "$stage"
pat='^(content-type:.*(report-type|multipart/related|delivery-status|disposition-notification|feedback-report)|authentication-results:|received-spf:|content-location:|content-base:)'
n=0
for d in dovecot-crlf.msgs mime4j enron-cmu/maildir; do
  [ -d "$corpus/$d" ] || continue
  while IFS= read -r f; do
    n=$((n+1)); ln -s "$f" "$stage/$(printf '%06d' $n)-$(basename "$f" | tr -c "A-Za-z0-9._-" _).eml"
  done < <(find "$corpus/$d" -type f -print0 | xargs -0 -P 8 grep -lEi -m1 "$pat" 2>/dev/null || true)
done
echo "selected $n files"
export ONEMAIL_ORACLE_BIN="${ONEMAIL_ORACLE_BIN:-$HOME/onemail/reports/bin}"
bash "$here/run.sh" "$stage" "$out" | grep -v '^urljoin' || true
