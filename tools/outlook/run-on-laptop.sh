#!/usr/bin/env bash
# Runs one PowerShell script inside the signed-in desktop session of a Windows machine that has Outlook, from Linux over ssh.
# Office automation and the scanpst window need that session, so the script is started by an interactive scheduled task (never from the ssh
# session itself). The task is deleted afterwards. Nothing is forced: if nobody is logged on, the run stops with a message.
#
#   run-on-laptop.sh <script.ps1> [script arguments...]
#
# Environment (all required unless defaulted):
#   OUTLOOK_HOST   address of the machine
#   OUTLOOK_USER   the Windows user whose session is used
#   OUTLOOK_KEY    ssh private key (default ~/.ssh/id_ed25519)
#   OUTLOOK_DIR    working directory on the machine (default C:\Users\<user>\onemail)
#   OUTLOOK_WAIT   seconds to wait for the script (default 900; keep a batch to 1800 or less)
#   OUTLOOK_LOCK   lock file shared with other projects that drive Office on the machine (default C:\Users\<user>\oracle.lock). It is created
#                  atomically before the run (waiting up to 2 h for another holder) and removed after Outlook has been closed. A lock older
#                  than 2 h is stale: it is removed and the run says so. OUTLOOK_LOCK_HELD=1 skips locking for a caller that holds it.
#   OUTLOOK_INSTANCE  who is written into the lock (default <this host>:<pid>)
# The script's output lands in $OUTLOOK_DIR\run\<name>.out.txt and is printed here; the exit code is the script's.
set -u
script="${1:?usage: run-on-laptop.sh <script.ps1> [args...]}"; shift
host="${OUTLOOK_HOST:?set OUTLOOK_HOST}"; user="${OUTLOOK_USER:?set OUTLOOK_USER}"
key="${OUTLOOK_KEY:-$HOME/.ssh/id_ed25519}"
dir="${OUTLOOK_DIR:-C:\\Users\\$user\\onemail}"
wait_s="${OUTLOOK_WAIT:-900}"
lock="${OUTLOOK_LOCK:-C:\\Users\\$user\\oracle.lock}"
instance="${OUTLOOK_INSTANCE:-$(hostname):$$}"
ssh_=(ssh -i "$key" -o BatchMode=yes -o ConnectTimeout=10 "$user@$host")
scp_=(scp -q -i "$key" -o BatchMode=yes)

name="$(basename "$script" .ps1)-$$"
tn="OneMailOracle-$name"

# A user must be logged on to the console; never force a logon.
if ! "${ssh_[@]}" 'tasklist /fi "imagename eq explorer.exe" /fo csv /nh' 2>/dev/null | grep -qi explorer.exe; then
  echo "no signed-in desktop session on $host; try again later" >&2; exit 75
fi

"${ssh_[@]}" "mkdir \"$dir\\run\" 2>nul & mkdir \"$dir\\bin\" 2>nul & exit 0" >/dev/null
here="$(dirname "$script")"
remote_bin="$(echo "$dir" | sed 's|\\|/|g; s|^C:|/C:|')/bin/"
"${scp_[@]}" "$here/Lock.ps1" "$here/Cleanup.ps1" "$user@$host:$remote_bin"
ps="powershell -NoProfile -ExecutionPolicy Bypass -File"
if [ -z "${OUTLOOK_LOCK_HELD:-}" ]; then
  "${ssh_[@]}" "$ps \"$dir\\bin\\Lock.ps1\" acquire \"$lock\" \"$instance\" 120" | tr -d '\r' >&2 || { echo "could not take the lock $lock" >&2; exit 75; }
  # Whatever happens, Outlook is closed and our tasks and helpers are ended before the lock is released.
  trap '"${ssh_[@]}" "$ps \"$dir\\bin\\Cleanup.ps1\"" >/dev/null 2>&1; "${ssh_[@]}" "$ps \"$dir\\bin\\Lock.ps1\" release \"$lock\"" >/dev/null 2>&1' EXIT
fi
"${scp_[@]}" "$script" "$user@$host:$(echo "$dir" | sed 's|\\|/|g; s|^C:|/C:|')/bin/$(basename "$script")"
for extra in "$(dirname "$script")"/*.psm1 "$(dirname "$script")"/*.template; do
  [ -f "$extra" ] && "${scp_[@]}" "$extra" "$user@$host:$(echo "$dir" | sed 's|\\|/|g; s|^C:|/C:|')/bin/"
done

# The wrapper keeps quoting out of schtasks: the task runs a .cmd that runs the script and records its exit code.
args=""; for a in "$@"; do args="$args \"$a\""; done
wrapper="$(mktemp)"
cat > "$wrapper" <<EOF
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\\bin\\$(basename "$script")" $args > "$dir\\run\\$name.out.txt" 2>&1
echo %ERRORLEVEL% > "$dir\\run\\$name.exit.txt"
EOF
unix2dos -q "$wrapper" 2>/dev/null || sed -i 's/$/\r/' "$wrapper"
"${scp_[@]}" "$wrapper" "$user@$host:$(echo "$dir" | sed 's|\\|/|g; s|^C:|/C:|')/run/$name.cmd"; rm -f "$wrapper"

"${ssh_[@]}" "del \"$dir\\run\\$name.exit.txt\" 2>nul & schtasks /Create /TN $tn /TR \"\\\"$dir\\run\\$name.cmd\\\"\" /SC ONCE /ST 00:00 /IT /RU $user /F" >/dev/null || { echo "could not create the task" >&2; exit 76; }
"${ssh_[@]}" "schtasks /Run /TN $tn" >/dev/null

rc=124
for ((t = 0; t < wait_s; t += 3)); do
  sleep 3
  if "${ssh_[@]}" "if exist \"$dir\\run\\$name.exit.txt\" (type \"$dir\\run\\$name.exit.txt\") else (echo pending)" 2>/dev/null | tr -d '\r ' | grep -qE '^[0-9]+$'; then
    rc="$("${ssh_[@]}" "type \"$dir\\run\\$name.exit.txt\"" | tr -d '\r ')"; break
  fi
done
"${ssh_[@]}" "type \"$dir\\run\\$name.out.txt\"" 2>/dev/null | tr -d '\r'
if [ "$rc" = 124 ]; then echo "timed out after ${wait_s}s; ending the task" >&2; "${ssh_[@]}" "schtasks /End /TN $tn" >/dev/null 2>&1; fi
"${ssh_[@]}" "schtasks /Delete /TN $tn /F" >/dev/null 2>&1
exit "$rc"
