#!/usr/bin/env bash
# guest-run.sh start|stop: boots the built Postfix guest on a throw-away overlay (the built image is never modified) and
# forwards its SMTP (25), submission (587), smtps (465) and legacy (588) ports to 127.0.0.1:32025/32587/32465/32588. Prints the qemu pid file path.
set -euo pipefail
G=${ONEMAIL_GUEST:-$HOME/onemail/containers/guest}
case "${1:-}" in
  start)
    [ -f "$G/postfix.qcow2" ] || { echo "build the guest first: tools/servers/guest-build.sh" >&2; exit 1; }
    # No daemonize: qemu stays in the caller's process group, so killing the caller's group stops it.
    qemu-system-x86_64 -enable-kvm -machine q35 -cpu host -smp 2 -m 1024 \
      -drive file="$G/postfix.qcow2",if=virtio,snapshot=on \
      -nic user,model=virtio-net-pci,hostfwd=tcp:127.0.0.1:32025-:25,hostfwd=tcp:127.0.0.1:32587-:587,hostfwd=tcp:127.0.0.1:32465-:465,hostfwd=tcp:127.0.0.1:32588-:588 \
      -display none -serial file:"$G/run-console.log" >"$G/run-qemu.log" 2>&1 &
    echo $! > "$G/qemu.pid"
    ;;
  stop)
    [ -f "$G/qemu.pid" ] && kill "$(cat "$G/qemu.pid")" 2>/dev/null || true
    rm -f "$G/qemu.pid"
    ;;
  *) echo "usage: guest-run.sh start|stop" >&2; exit 2 ;;
esac
