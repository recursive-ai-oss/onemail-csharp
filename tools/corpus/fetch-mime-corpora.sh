#!/usr/bin/env bash
# Fetches the real-world MIME corpora used by the round-trip sweeps into a cache directory outside the repository.
# Nothing here is ever committed. Each download is checked against a pinned SHA-256; a mismatch stops the script.
#
#   tools/corpus/fetch-mime-corpora.sh [cache dir]      (default: $ONEMAIL_CORPUS or ~/onemail/corpus)
#
# Corpora and their terms are listed in THIRD-PARTY-NOTICES.md.
#   enron-cmu    CMU Enron Email Dataset, 7 May 2015 version (maildir, one file per message)
#   dovecot-crlf the mbox that Dovecot publishes as input for its imaptest tool
#   mime4j       the message files (only) of Apache James Mime4j's test resources, at a pinned commit
set -euo pipefail

cache="${1:-${ONEMAIL_CORPUS:-$HOME/onemail/corpus}}"
mkdir -p "$cache"

ENRON_URL="https://www.cs.cmu.edu/~enron/enron_mail_20150507.tar.gz"
ENRON_SHA256="b3da1b3fe0369ec3140bb4fbce94702c33b7da810ec15d718b3fadf5cd748ca7"
DOVECOT_URL="https://dovecot.org/tmp/dovecot-crlf"
DOVECOT_SHA256="e07fd2fab006d9a96c80300e41cd27ee71f0afbbb4e22c5589cc3e468b2ee18c"
MIME4J_COMMIT="5616ba583c7b4191222e2f16bf2cdc7fa08bd2fa"
MIME4J_URL="https://codeload.github.com/apache/james-mime4j/tar.gz/$MIME4J_COMMIT"
MIME4J_SHA256="3477ec64d1db787ce882d9b6f70866e454f7dd18551ebfc84d3c3c7dc23755f4"

verify() { # file expected-sha256
  local got
  got="$(sha256sum "$1" | cut -d' ' -f1)"
  if [[ "$2" == "PIN-ME" ]]; then
    echo "sha256 of $1: $got (not pinned yet)" >&2
  elif [[ "$got" != "$2" ]]; then
    echo "checksum mismatch for $1: expected $2, got $got" >&2
    exit 1
  fi
}

fetch() { # url file expected-sha256
  if [[ ! -s "$2" ]]; then
    curl --fail --location --silent --show-error --output "$2.part" "$1"
    mv "$2.part" "$2"
  fi
  verify "$2" "$3"
}

# --- Dovecot imaptest mbox (a single file) ---
fetch "$DOVECOT_URL" "$cache/dovecot-crlf" "$DOVECOT_SHA256"

# --- Apache James Mime4j test messages: only the message files, never the Java code or expected-output files ---
if [[ ! -d "$cache/mime4j" ]]; then
  tmp="$cache/mime4j.tar.gz"
  fetch "$MIME4J_URL" "$tmp" "$MIME4J_SHA256"
  mkdir -p "$cache/mime4j"
  tar -xzf "$tmp" -C "$cache/mime4j" --strip-components=1 \
    --wildcards '*/core/src/test/resources/testmsgs/*.msg' '*/core/src/test/resources/mimetools-testmsgs/*.msg'
  rm -f "$tmp"
fi

# --- CMU Enron maildir (large; extracted once) ---
if [[ ! -d "$cache/enron" ]]; then
  fetch "$ENRON_URL" "$cache/enron.tar.gz" "$ENRON_SHA256"
  mkdir -p "$cache/enron"
  tar -xzf "$cache/enron.tar.gz" -C "$cache/enron" --strip-components=1
  rm -f "$cache/enron.tar.gz"
fi

echo "corpora ready in $cache"
