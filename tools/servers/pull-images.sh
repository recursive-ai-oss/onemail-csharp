#!/usr/bin/env bash
# Pulls the published server images by tag and prints their digests (the digests are then pinned in images.env).
D="$(dirname "$0")"
for ref in docker.io/dovecot/dovecot:latest docker.io/stalwartlabs/stalwart:v1.0.1 ghcr.io/cyrusimap/cyrus-docker-test-server:latest docker.io/boky/postfix:5.2.0-alpine; do
  "$D/userns.sh" "$D/pm" pull -q "$ref" >/dev/null 2>&1 || echo "PULL FAILED $ref"
  "$D/userns.sh" "$D/pm" image inspect --format '{{index .RepoDigests 0}} {{.Created}} {{.Size}}' "$ref"
done
