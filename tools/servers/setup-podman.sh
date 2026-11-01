#!/usr/bin/env bash
# Installs a rootless container runtime under ~/onemail/containers (no sudo, nothing system-wide):
# the static podman bundle (podman, crun, fuse-overlayfs, pasta, netavark) from its published release.
# Idempotent. Source env.sh afterwards to use it.
set -euo pipefail
VER=v6.1.4
SHA256=9dd51073fac5a1742f88cf3caff5554615cb4d67ba554d25e8807be635547af5   # podman-linux-amd64.tar.gz
R=${ONEMAIL_CONTAINERS:-$HOME/onemail/containers}
mkdir -p "$R/tools" "$R/conf" "$R/storage" "$R/run"
if [ ! -x "$R/tools/podman-linux-amd64/usr/local/bin/podman" ]; then
  curl -sSL -m 600 -o "$R/tools/podman.tgz" "https://github.com/mgoltzsche/podman-static/releases/download/$VER/podman-linux-amd64.tar.gz"
  echo "$SHA256  $R/tools/podman.tgz" | sha256sum -c -
  tar xzf "$R/tools/podman.tgz" -C "$R/tools"
fi
T="$R/tools/podman-linux-amd64/usr/local"
cat > "$R/conf/containers.conf" <<C
[containers]
cgroups="disabled"
[engine]
helper_binaries_dir=["$T/lib/podman","$T/bin"]
runtime="crun"
conmon_path=["$T/lib/podman/conmon"]
[engine.runtimes]
crun=["$T/bin/crun"]
[network]
default_rootless_network_cmd="pasta"
C
# Without newuidmap only one uid maps into the container, so chown to other uids is ignored while unpacking images.
cat > "$R/conf/storage.conf" <<C
[storage]
driver="overlay"
runroot="$R/run"
graphroot="$R/storage"
[storage.options]
ignore_chown_errors="true"
mount_program="$T/bin/fuse-overlayfs"
C
echo '{"default":[{"type":"insecureAcceptAnything"}]}' > "$R/conf/policy.json"
echo 'unqualified-search-registries=["docker.io"]' > "$R/conf/registries.conf"
echo "installed: $R"
