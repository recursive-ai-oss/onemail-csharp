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
exec bwrap --unshare-user --uid "$(id -u)" --gid "$(id -g)" $net ${NETNS:+--cap-add CAP_NET_ADMIN} --bind / / --dev-bind /dev /dev --proc /proc \
  --ro-bind /dev/null /etc/subuid --ro-bind /dev/null /etc/subgid \
  bash -c '[ -n "$NETNS" ] && { ip link set lo up; echo 0 > /proc/sys/net/ipv4/ip_unprivileged_port_start; } 2>/dev/null; exec "$@"' bash "$@"
