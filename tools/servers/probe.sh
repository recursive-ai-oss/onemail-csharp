D="$(dirname "$0")"
for ref in docker.io/dovecot/dovecot:latest docker.io/stalwartlabs/stalwart:v1.0.1 ghcr.io/cyrusimap/cyrus-docker-test-server:latest docker.io/boky/postfix:5.2.0-alpine; do
  echo "=== $ref"
  "$D/userns.sh" "$D/pm" image inspect --format 'user={{.Config.User}} ep={{.Config.Entrypoint}} cmd={{.Config.Cmd}} ports={{.Config.ExposedPorts}} vols={{.Config.Volumes}}' "$ref"
  "$D/userns.sh" "$D/pm" image inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$ref" | grep -v '^PATH' | head -8
done
