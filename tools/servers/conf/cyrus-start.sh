#!/bin/bash
# Starts only the Cyrus master of the test-server image, as the image's cyrus user (the harness maps just that one user id),
# on the standard ports with TLS. The image's own start script also runs Postfix and a mail catcher, which need more users.
set -e
export IMAPPORT=143 POP3PORT=110 HTTPPORT=8080 LMTPPORT=8024 SIEVEPORT=4190 WEBPORT=8001 SMTPPORT=8587 MAILPITPORT=8025
export SERVERNAME=cyrus.test DEFAULTDOMAIN=example.com JMAPBASEURL=http://localhost:8080
mkdir -p /run/cyrus /tmp/cy
perl /srv/testserver/env-replace.pl /srv/testserver/imapd.conf /etc/imapd.conf
{
  echo "tls_server_cert: /onemail/tls.crt"
  echo "tls_server_key: /onemail/tls.key"
  echo "tls_versions: tls1_2 tls1_3"
  echo "allowstarttls: 1"
  # Without the idle daemon a session polls for other sessions' changes; the default is a minute.
  echo "imapidlepoll: 1"
  # Cyrus replaces 8-bit bytes in appended headers with X unless told otherwise; the round-trip tests compare bytes.
  echo "munge8bit: 0"
} >> /etc/imapd.conf
perl /srv/testserver/env-replace.pl /srv/testserver/cyrus.conf /etc/cyrus.conf
# Implicit-TLS twins of the IMAP and POP3 services.
sed -i 's|^SERVICES {|SERVICES {\n    imaps listen=993 cmd="imapd -s"\n    pop3s listen=995 cmd="pop3d -s"|' /etc/cyrus.conf
# The annotator is a Perl helper for JMAP tests; not needed here.
sed -i '/annotator/d' /etc/cyrus.conf /etc/imapd.conf
/usr/cyrus/bin/master -p /run/cyrus/master.pid -D -L /run/cyrus/log &
master=$!
for _ in $(seq 1 60); do [ -f /run/cyrus/ready ] && break; sleep 1; done
chmod 666 /run/cyrus/saslauthd.sock 2>/dev/null || true
wait $master
