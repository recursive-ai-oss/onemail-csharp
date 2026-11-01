#!/bin/bash
# Experimental: Postfix needs root and the postfix users; with one mapped id only, fakeroot supplies the id switching.
export SERVERNAME=postfix.test DEFAULTDOMAIN=example.com RELAYHOST='[127.0.0.1]:2525' MAILPITSMTP=2525
FK=/fk/usr/lib/x86_64-linux-gnu/libfakeroot
/fk/usr/bin/faked-sysv --foreground >/tmp/faked.key 2>/tmp/faked.err &
sleep 1
export FAKEROOTKEY=$(cut -d: -f1 /tmp/faked.key) LD_LIBRARY_PATH=$FK
echo "key=$FAKEROOTKEY"
mkdir -p /run/cyrus
/usr/cyrus/bin/fakesaslauthd -p /run/cyrus/saslauthd.sock >/tmp/fsa.log 2>&1 &
perl /srv/testserver/env-replace.pl /srv/testserver/main.cf /etc/postfix/main.cf
mkdir -p /etc/postfix/sasl; cp /srv/testserver/sasl_smtpd.conf /etc/postfix/sasl/smtpd.conf
cat >> /etc/postfix/master.cf <<EOT
0.0.0.0:587   inet  n       -       n       -       -       smtpd
  -o syslog_name=postfix/submission
  -o smtpd_sasl_auth_enable=yes
  -o smtpd_sasl_security_options=noanonymous
  -o smtpd_relay_restrictions=permit_sasl_authenticated,defer_unauth_destination
  -o smtpd_tls_security_level=may
EOT
export LD_PRELOAD=$FK/libfakeroot-sysv.so
echo "id: $(id)"
(timeout 25 postfix -c /etc/postfix start-fg 2>&1 | head -20) &
sleep 8
unset LD_PRELOAD
(exec 3<>/dev/tcp/127.0.0.1/587; head -c 200 <&3) 2>&1
cat /tmp/faked.err | head -5
wait
