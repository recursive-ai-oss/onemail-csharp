#!/usr/bin/env bash
# Runs the protocol rows (bench/ProtocolRows: this library's IMAP and SMTP clients against MailKit 4.18.2) on local servers started in a
# private network namespace (tools/servers): IMAP against Dovecot, SMTP against the Postfix guest.
#
#   run-protocol-rows.sh OUTDIR [imap|smtp|all|smtptrace] [extra env as VAR=value ...]
#
# Inputs: the CMU Enron maildir that tools/corpus fetches put under ONEMAIL_CORPUS (default ~/onemail/corpus/enron-cmu/maildir) for
# the IMAP sample; the SMTP messages are generated. Every side runs in its own process; each prints three timed repetitions after a
# warm-up and their median. Run it through the machine's bench queue, alone on the machine. Writes OUTDIR/{machine.txt,imap.txt,smtp.txt}.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
if [ "${1:-}" = "--inner" ]; then
  # Inside the namespace: the servers are up and their ports are in the environment.
  kind=$2; out=$3; dll=$4
  export ROWS_REPS=${ROWS_REPS:-3}
  rows() { echo "== $* $(date -u +%FT%TZ)" >> "$out/$kind.txt"; dotnet "$dll" "$@" 2>&1 | tee -a "$out/$kind.txt"; }
  if [ "$kind" = imap ]; then
    export ROWS_HOST=127.0.0.1 ROWS_PORT=$ONEMAIL_DOVECOT_IMAPS_PORT ROWS_USER=$ONEMAIL_DOVECOT_USER ROWS_PASS=$ONEMAIL_DOVECOT_PASSWORD ROWS_SECURITY=implicit
    export ROWS_CORPUS=${ROWS_CORPUS:-${ONEMAIL_CORPUS:-$HOME/onemail/corpus}/enron-cmu/maildir}
    rows imap-prepare ours
    for r in imap-summary imap-full; do for s in ours mailkit; do rows $r $s; done; done
  elif [ "$kind" = smtptrace ]; then
    export ROWS_HOST=127.0.0.1 ROWS_PORT=$ONEMAIL_POSTFIX_SUBMISSION_PORT ROWS_USER=$ONEMAIL_POSTFIX_USER ROWS_PASS=$ONEMAIL_POSTFIX_PASSWORD ROWS_SECURITY=starttls
    for s in ours mailkit-nodelay; do rows smtp-trace $s; done
  else
    export ROWS_HOST=127.0.0.1 ROWS_PORT=$ONEMAIL_POSTFIX_SUBMISSION_PORT ROWS_USER=$ONEMAIL_POSTFIX_USER ROWS_PASS=$ONEMAIL_POSTFIX_PASSWORD ROWS_SECURITY=starttls
    for r in smtp-small smtp-big; do for s in ${ROWS_SMTP_SIDES:-ours ours-data mailkit mailkit-nodelay}; do rows $r $s; done; done
  fi
  exit 0
fi

out="${1:?output directory}"; what="${2:-all}"; shift 2 || shift $#
for kv in "$@"; do export "$kv"; done
mkdir -p "$out"; out="$(cd "$out" && pwd)"
if [ -x "$HOME/dotnet10/dotnet" ]; then export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"; elif [ -f "$HOME/onemail/env.sh" ]; then . "$HOME/onemail/env.sh"; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEMAIL_ARTIFACTS="${ONEMAIL_ARTIFACTS:-$out/artifacts}"
machine() {
  echo "== $1 $(date -u +%FT%TZ)"; uptime; echo "cores: $(getconf _NPROCESSORS_ONLN)"
  (lscpu 2>/dev/null | grep -E 'Model name|Flags' | cut -c1-400) || sysctl -n machdep.cpu.brand_string hw.model 2>/dev/null || true
  dotnet --version; uname -srm
}
{ machine before; } | tee "$out/machine.txt"
dotnet build -c Release "$here/ProtocolRows" -v quiet -clp:NoSummary || exit 1
dll="$ONEMAIL_ARTIFACTS/bin/ProtocolRows/release/ProtocolRows.dll"
( while sleep 60; do { echo "== during $(date -u +%FT%TZ)"; uptime; } >> "$out/machine.txt"; done ) &
mon=$!
trap 'kill $mon 2>/dev/null || true' EXIT
cd "$root"
if [ "$what" = imap ] || [ "$what" = all ]; then
  "$root/tools/servers/userns.sh" -n "$root/tools/servers/servers.sh" --servers dovecot -- "$here/run-protocol-rows.sh" --inner imap "$out" "$dll"
fi
if [ "$what" = smtptrace ]; then
  "$root/tools/servers/userns.sh" -n "$root/tools/servers/servers.sh" --servers postfix -- "$here/run-protocol-rows.sh" --inner smtptrace "$out" "$dll"
fi
if [ "$what" = smtp ] || [ "$what" = all ]; then
  "$root/tools/servers/userns.sh" -n "$root/tools/servers/servers.sh" --servers postfix -- "$here/run-protocol-rows.sh" --inner smtp "$out" "$dll"
fi
{ machine after; } | tee -a "$out/machine.txt"
