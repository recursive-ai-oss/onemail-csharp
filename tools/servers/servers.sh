#!/usr/bin/env bash
# Starts a published server image inside the private network namespace of `userns.sh -n` and runs a command against them.
#
#   userns.sh -n tools/servers/servers.sh [--servers dovecot,stalwart,...] -- <command...>
#
# State lives under $ONEMAIL_SERVER_STATE (default ~/onemail/containers/state). The command gets ONEMAIL_SERVERS (the names
# that came up) in its environment and the ports in ONEMAIL_<SERVER>_<PROTOCOL>_PORT variables (see each start_* function).
set -uo pipefail
D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$D/images.env"
STATE=${ONEMAIL_SERVER_STATE:-$HOME/onemail/containers/state}
LOGS=${ONEMAIL_SERVER_LOGS:-$STATE/logs}
mkdir -p "$STATE" "$LOGS"
PODMAN="$D/pm"
PIDS=()
UP=()

want=dovecot
if [ "${1:-}" = "--servers" ]; then want=$2; shift 2; fi
[ "${1:-}" = "--" ] && shift

cleanup() {
  for c in dovecot stalwart cyrus postfix; do "$PODMAN" rm -f -t 2 "onemail-$c" >/dev/null 2>&1; done
  for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done
  wait 2>/dev/null
  "$D/cleanup.sh" >/dev/null
}
trap cleanup EXIT

wait_port() { # port seconds
  for _ in $(seq 1 $(($2 * 5))); do
    (exec 3<>/dev/tcp/127.0.0.1/$1) 2>/dev/null && return 0
    sleep 0.2
  done
  echo "port $1 did not open; see $LOGS" >&2
  return 1
}

start_sink() {
  python3 "$D/smtp-sink.py" 2525 >"$LOGS/sink.log" 2>&1 &
  PIDS+=($!)
  wait_port 2525 10
}

start_dovecot() {
  rm -rf "$STATE/dovecot"; mkdir -p "$STATE/dovecot/vmail"
  start_sink || return 1
  "$PODMAN" run --rm --name onemail-dovecot --network=host --uidmap 1000:0:1 --gidmap 1000:0:1 --user 1000:1000 \
    -e USER_PASSWORD='{PLAIN}onemail-test' \
    -v "$STATE/dovecot/vmail:/srv/vmail" -v "$D/conf/dovecot-onemail.conf:/etc/dovecot/conf.d/90-onemail.conf:ro" \
    --tmpfs /run/dovecot:rw,mode=1777 "$DOVECOT_IMAGE" >"$LOGS/dovecot.log" 2>&1 &
  PIDS+=($!)
  wait_port 31143 30 || return 1
  export ONEMAIL_DOVECOT_IMAP_PORT=31143 ONEMAIL_DOVECOT_IMAPS_PORT=31993 ONEMAIL_DOVECOT_POP3_PORT=31110 ONEMAIL_DOVECOT_POP3S_PORT=31995 \
         ONEMAIL_DOVECOT_SUBMISSION_PORT=31587 ONEMAIL_DOVECOT_SUBMISSIONS_PORT=31465 ONEMAIL_DOVECOT_USER=test ONEMAIL_DOVECOT_PASSWORD=onemail-test
}

start_stalwart() {
  local S="$STATE/stalwart" CLI="${ONEMAIL_CONTAINERS:-$HOME/onemail/containers}/tools/stalwart-cli/stalwart-cli"
  rm -rf "$S"; mkdir -p "$S/etc" "$S/data"
  echo '{"@type":"RocksDb","path":"/var/lib/stalwart/"}' > "$S/etc/config.json"
  local common=(--rm --network=host --uidmap 2000:0:1 --gidmap 2000:0:1 --user 2000:2000 -v "$S/etc:/etc/stalwart" -v "$S/data:/var/lib/stalwart")
  # First run in recovery mode: only the management listener, so the domain and account can be provisioned before any mail service starts.
  "$PODMAN" run --name onemail-stalwart "${common[@]}" -e STALWART_RECOVERY_MODE=1 -e STALWART_RECOVERY_ADMIN=admin:secret "$STALWART_IMAGE" >"$LOGS/stalwart-setup.log" 2>&1 &
  local pid=$!
  wait_port 8080 30 || { kill $pid; return 1; }
  cat > "$S/plan.ndjson" <<'PLAN'
{"@type":"upsert","object":"Domain","matchOn":["name"],"value":{"dom":{"name":"example.test","certificateManagement":{"@type":"Manual"},"dkimManagement":{"@type":"Automatic"},"dnsManagement":{"@type":"Manual"}}}}
{"@type":"upsert","object":"Account","matchOn":["name","domainId"],"value":{"u":{"@type":"User","name":"test","domainId":"#dom","credentials":{"0":{"@type":"Password","secret":"onemail-test"}}}}}
{"@type":"update","object":"SystemSettings","value":{"defaultHostname":"mail.example.test","defaultDomainId":"#dom"}}
{"@type":"upsert","object":"NetworkListener","matchOn":["name"],"value":{"l1":{"name":"smtp","bind":{"[::]:25":true},"protocol":"smtp"},"l2":{"name":"submission","bind":{"[::]:587":true},"protocol":"smtp"},"l3":{"name":"submissions","bind":{"[::]:465":true},"protocol":"smtp","implicitTls":true},"l4":{"name":"imap","bind":{"[::]:143":true},"protocol":"imap"},"l5":{"name":"imaps","bind":{"[::]:993":true},"protocol":"imap","implicitTls":true},"l6":{"name":"pop3","bind":{"[::]:110":true},"protocol":"pop3"},"l7":{"name":"pop3s","bind":{"[::]:995":true},"protocol":"pop3","implicitTls":true},"l8":{"name":"https","bind":{"[::]:443":true},"protocol":"http","implicitTls":true}}}
PLAN
  # The management port opens before the API answers, so the first requests may fail.
  local ok=
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    XDG_CACHE_HOME="$S/cache" HOME="$S" "$CLI" --url http://127.0.0.1:8080 --user admin --password secret apply --file "$S/plan.ndjson" >"$LOGS/stalwart-apply.log" 2>&1 && { ok=1; break; }
    sleep 2
  done
  [ -n "$ok" ] || { cat "$LOGS/stalwart-apply.log" >&2; kill $pid; return 1; }
  "$PODMAN" stop -t 5 onemail-stalwart >/dev/null 2>&1; wait $pid 2>/dev/null
  "$PODMAN" run --name onemail-stalwart "${common[@]}" "$STALWART_IMAGE" >"$LOGS/stalwart.log" 2>&1 &
  PIDS+=($!)
  wait_port 143 40 || return 1
  export ONEMAIL_STALWART_IMAP_PORT=143 ONEMAIL_STALWART_IMAPS_PORT=993 ONEMAIL_STALWART_POP3_PORT=110 ONEMAIL_STALWART_POP3S_PORT=995 \
         ONEMAIL_STALWART_SMTP_PORT=25 ONEMAIL_STALWART_SUBMISSION_PORT=587 ONEMAIL_STALWART_SUBMISSIONS_PORT=465 \
         ONEMAIL_STALWART_USER=test@example.test ONEMAIL_STALWART_PASSWORD=onemail-test
}

start_cyrus() {
  local S="$STATE/cyrus"
  rm -rf "$S"; mkdir -p "$S/var-imap" "$S/tls"
  openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj /CN=localhost -addext subjectAltName=DNS:localhost,IP:127.0.0.1 \
    -keyout "$S/tls/tls.key" -out "$S/tls/tls.crt" >/dev/null 2>&1 || return 1
  python3 "$D/syslog-sink.py" "$S/log.sock" "$LOGS/cyrus-syslog.log" &
  PIDS+=($!)
  sleep 0.5
  # The image's /var/imap is copied out so the container's one user owns a writable copy (an anonymous volume would need a chown).
  "$PODMAN" run --rm --name onemail-cyrus --network=host --uidmap 999:0:1 --gidmap 8:0:1 --user 999:8 \
    -v "$S/log.sock:/dev/log" -v "$S/tls:/onemail:ro" -v "$D/conf/cyrus-start.sh:/onemail/start.sh:ro" --tmpfs /run:rw,mode=1777 --tmpfs /tmp:rw,mode=1777 \
    --entrypoint /bin/bash "$CYRUS_IMAGE" /onemail/start.sh >"$LOGS/cyrus.log" 2>&1 &
  PIDS+=($!)
  wait_port 143 60 || return 1
  # The user's mailbox is created through the admin account (the image's own account script does not leave one behind).
  local made=
  for _ in $(seq 1 60); do
    python3 - >>"$LOGS/cyrus-setup.log" 2>&1 <<'PY' && { made=1; break; }
import imaplib, ssl
c = ssl._create_unverified_context()
a = imaplib.IMAP4_SSL("127.0.0.1", 993, ssl_context=c)
a.login("admin", "admin")
r = a.create("user/user1")
assert r[0] == "OK", r
PY
    sleep 1
  done
  [ -n "$made" ] || return 1
  export ONEMAIL_CYRUS_IMAP_PORT=143 ONEMAIL_CYRUS_IMAPS_PORT=993 ONEMAIL_CYRUS_POP3_PORT=110 ONEMAIL_CYRUS_POP3S_PORT=995 \
         ONEMAIL_CYRUS_USER=user1 ONEMAIL_CYRUS_PASSWORD=x
}

# Stalwart first: its provisioning step needs port 8080, which Dovecot and Cyrus also open for their HTTP services.
for s in stalwart dovecot cyrus; do
  case ",$want," in *",$s,"*) ;; *) continue ;; esac
  if "start_$s"; then UP+=("$s"); else echo "failed to start $s" >&2; fi
done
export ONEMAIL_SERVERS="${UP[*]}"
echo "servers up: ${ONEMAIL_SERVERS:-none}" >&2
if [ "$#" -gt 0 ]; then "$@"; fi
