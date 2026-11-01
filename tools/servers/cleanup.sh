#!/usr/bin/env bash
# Stops what the harness leaves behind: podman's "pause" helper processes (one per run), and any container, conmon, mount helper
# or fake-root daemon still running from the harness's own tool directory. Only processes whose executable lives there are touched.
R=${ONEMAIL_CONTAINERS:-$HOME/onemail/containers}
n=0
for p in $(pgrep -u "$(id -u)" 2>/dev/null); do
  exe=$(readlink "/proc/$p/exe" 2>/dev/null) || continue
  case "$exe" in "$R"/tools/*) kill "$p" 2>/dev/null && n=$((n + 1)) ;; esac
done
echo "stopped $n harness processes"
