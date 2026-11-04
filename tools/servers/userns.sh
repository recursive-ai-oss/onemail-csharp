#!/usr/bin/env bash
# userns.sh [-n] cmd...  Runs cmd in a private user namespace in which this user keeps its own uid and the subuid/subgid
# tables are empty. The host has subuid ranges but no newuidmap helper, which makes rootless podman fail; with empty tables it
# falls back to its documented one-id mapping. With -n the command also gets a private network namespace with only loopback
# up: servers in containers then bind their standard ports (25, 143, 993, ...) without clashing with anything on the host, and
# a test process started in the same namespace reaches them on 127.0.0.1.
net=; unset NETNS
if [ "${1:-}" = "-n" ]; then net=--unshare-net; export NETNS=1; shift; fi
mkdir -p "$HOME/onemail/containers/run/tmp" "$HOME/onemail/containers/run/crun"
# In the private network namespace the capability to configure it is kept just long enough to bring loopback up and let any
# process bind low ports (25, 143, 993, ...), then dropped by the exec of a non-root process.
bwrap --unshare-user --uid "$(id -u)" --gid "$(id -g)" $net ${NETNS:+--cap-add CAP_NET_ADMIN} --bind / / --dev-bind /dev /dev --proc /proc \
  --ro-bind /dev/null /etc/subuid --ro-bind /dev/null /etc/subgid \
  bash -c '[ -n "$NETNS" ] && { ip link set lo up; echo 0 > /proc/sys/net/ipv4/ip_unprivileged_port_start; } 2>/dev/null; exec "$@"' bash "$@"
status=$?

# Podman's rootless "pause" helper starts in its own user namespace, and the last one is started by the container-cleanup command that
# conmon runs after the container exits, so nothing inside the namespace can reap it. Left alone, each run leaves one holding kernel
# session keys, and the per-user quota (200) runs out after a few runs. From out here, stop the ones this run started: a process named
# podman with no arguments, parent init, no older than the run.
sleep 1
for p in $(pgrep -u "$(id -u)" -x -f podman 2>/dev/null); do
  [ "$(tr '\0' ' ' < "/proc/$p/cmdline" 2>/dev/null)" = "podman " ] || continue
  [ "$(ps -o ppid= -p "$p" 2>/dev/null | tr -d ' ')" = 1 ] || continue
  # Other projects run podman under the same user: stop only a helper that provably belongs to this harness, by its executable (the
  # user-local podman in the tool directory) or, where the executable can't be read, by the config path its environment carries.
  R=${ONEMAIL_CONTAINERS:-$HOME/onemail/containers}
  exe=$(readlink "/proc/$p/exe" 2>/dev/null)
  case "$exe" in
    "$R"/tools/*) ;;
    "") tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null | grep -qxF "CONTAINERS_CONF=$R/conf/containers.conf" || continue ;;
    *) continue ;;
  esac
  age=$(ps -o etimes= -p "$p" 2>/dev/null | tr -d ' ')
  [ -n "$age" ] && [ "$age" -le "$((SECONDS + 2))" ] && kill "$p" 2>/dev/null
done
exit $status
