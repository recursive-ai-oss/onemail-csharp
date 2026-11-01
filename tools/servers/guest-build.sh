#!/usr/bin/env bash
# Builds the Postfix test guest: a Debian cloud image, pinned by URL and SHA-512, with Postfix and Dovecot's auth service
# installed by cloud-init, then powered off. Needs the host's network (run it once, outside userns.sh), qemu with KVM and
# genisoimage, all user-local or already installed; nothing needs root. State: $ONEMAIL_GUEST (default ~/onemail/containers/guest).
#
# Why a guest: Postfix's master process needs real root and several system users, and an unprivileged user without newuidmap
# cannot give a container that. Inside a guest it has all of it.
set -euo pipefail
D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
G=${ONEMAIL_GUEST:-$HOME/onemail/containers/guest}
REL=20261026-2643
IMG=debian-12-genericcloud-amd64-$REL.qcow2
URL=https://cloud.debian.org/images/cloud/bookworm/$REL/$IMG
SHA512=0464b5d4ae3aa4f658468d88da83af7857b71548b30d46dcb6282770955a747fb95a983476db90d25afd0653af607224c7ebd05122ab71faa106fd712aaad652
mkdir -p "$G"
if [ ! -f "$G/$IMG" ]; then
  curl -fsSL -o "$G/$IMG.part" "$URL"
  echo "$SHA512  $G/$IMG.part" | sha512sum -c - >/dev/null
  mv "$G/$IMG.part" "$G/$IMG"
fi
rm -f "$G/postfix.qcow2"
qemu-img create -q -f qcow2 -b "$G/$IMG" -F qcow2 "$G/postfix.qcow2" 3G
S="$G/seed"; rm -rf "$S"; mkdir -p "$S"
cp "$D/conf/guest-user-data.yaml" "$S/user-data"
printf 'instance-id: onemail-postfix\nlocal-hostname: mail\n' > "$S/meta-data"
genisoimage -quiet -output "$G/seed.iso" -volid cidata -joliet -rock "$S/user-data" "$S/meta-data"
# The user-mode network gives the guest outbound access for apt; nothing listens on the host.
timeout 900 qemu-system-x86_64 -enable-kvm -machine q35 -cpu host -smp 2 -m 1024 \
  -drive file="$G/postfix.qcow2",if=virtio -drive file="$G/seed.iso",if=virtio,format=raw,readonly=on \
  -nic user,model=virtio-net-pci -display none -serial file:"$G/build-console.log" -no-reboot
echo "guest built: $G/postfix.qcow2"
