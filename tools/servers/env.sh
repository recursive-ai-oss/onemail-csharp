# Source me: puts the user-local podman on PATH and points it at its own config and storage.
R=${ONEMAIL_CONTAINERS:-$HOME/onemail/containers}
T="$R/tools/podman-linux-amd64/usr/local"
export CONTAINERS_CONF="$R/conf/containers.conf" CONTAINERS_STORAGE_CONF="$R/conf/storage.conf" \
       CONTAINERS_REGISTRIES_CONF="$R/conf/registries.conf" XDG_RUNTIME_DIR="$R/run"
export PATH="$T/bin:$PATH"
# containers-image reads its trust policy from the user config directory; the images are pinned by digest instead.
if [ ! -e "$HOME/.config/containers/policy.json" ] && [ ! -e /etc/containers/policy.json ]; then
  mkdir -p "$HOME/.config/containers"; cp "$R/conf/policy.json" "$HOME/.config/containers/policy.json"
fi
