#!/usr/bin/env bash
# Stops what the harness leaves behind: podman's "pause" helper processes (one per run), and any container, conmon, mount helper
# or fake-root daemon still running from the harness's own tool directory. Only processes whose executable lives there are touched,
# and only those in the same user namespace as this script: a run inside userns.sh cleans up its own, never another run's.
R=${ONEMAIL_CONTAINERS:-$HOME/onemail/containers}
n=0
ns=$(readlink /proc/self/ns/user)
for p in $(pgrep -u "$(id -u)" 2>/dev/null); do
  [ "$(readlink "/proc/$p/ns/user" 2>/dev/null)" = "$ns" ] || continue
  exe=$(readlink "/proc/$p/exe" 2>/dev/null) || continue
  case "$exe" in "$R"/tools/*) kill "$p" 2>/dev/null && n=$((n + 1)) ;; esac
done
echo "stopped $n harness processes"
