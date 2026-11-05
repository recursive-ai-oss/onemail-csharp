#!/usr/bin/env bash
# Copies a directory from a Linux build machine to the Outlook machine and back, through this host, with tar over two ssh connections.
#
#   push.sh to   <dir on build machine> <subdir under $OUTLOOK_DIR\set>     # build machine -> Outlook machine
#   push.sh from <subdir under $OUTLOOK_DIR\results> <local dir>            # Outlook machine -> here
#
# Environment: OUTLOOK_HOST, OUTLOOK_USER, OUTLOOK_KEY (as run-on-laptop.sh) and BUILD_HOST, BUILD_USER, BUILD_KEY for the build machine.
set -euo pipefail
dir="${OUTLOOK_DIR:-C:\\Users\\${OUTLOOK_USER:?}\\onemail}"
key="${OUTLOOK_KEY:-$HOME/.ssh/id_ed25519}"
L=(ssh -i "$key" -o BatchMode=yes "${OUTLOOK_USER:?}@${OUTLOOK_HOST:?}")
W=(ssh -i "${BUILD_KEY:?}" -o BatchMode=yes "${BUILD_USER:?}@${BUILD_HOST:?}")
case "${1:?to|from}" in
  to)   "${L[@]}" "mkdir \"$dir\\set\\$3\" 2>nul & exit 0" >/dev/null
        "${W[@]}" "tar -C '$2' -cf - ." | "${L[@]}" "tar -C \"$dir\\set\\$3\" -xf -" ;;
  from) mkdir -p "$3"
        "${L[@]}" "tar -C \"$dir\\results\\$2\" -cf - ." | tar -C "$3" -xf - ;;
  *) echo "usage: push.sh to|from ..." >&2; exit 2 ;;
esac
