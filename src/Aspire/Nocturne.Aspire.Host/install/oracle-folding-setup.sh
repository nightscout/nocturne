#!/usr/bin/env bash
# Runs after every nocturne-up. Opt-in, and only once.
set -euo pipefail
. /opt/nocturne/install.env
[[ -n "${FOLDING:-}" ]] || exit 0
command -v fah-client >/dev/null 2>&1 && exit 0

# Written before the package installs so its postinst keeps it instead of writing an empty one.
install -d -m 755 /etc/fah-client
cat > /etc/fah-client/config.xml <<EOF
<config>
  <user v="Anonymous"/>
  <team v="${FOLDING_TEAM}"/>
  <cpus v="$(nproc)"/>
  <on-idle v="false"/>
</config>
EOF

for try in $(seq 1 10); do
  if curl -fsSL -o /tmp/fah-client.deb https://download.foldingathome.org/releases/public/fah-client/debian-stable-arm64/release/latest.deb; then
    break
  fi
  echo "[nocturne] Folding@home download failed (attempt $try); retrying in 30s"
  sleep 30
done
[[ -f /tmp/fah-client.deb ]] || { echo "[nocturne] could not download the Folding@home client"; exit 1; }
DEBIAN_FRONTEND=noninteractive apt-get install -y /tmp/fah-client.deb
rm -f /tmp/fah-client.deb

cat > /etc/systemd/system/nocturne-folding-start.service <<EOF
[Unit]
Description=Start the nightly Folding@home window
[Service]
Type=oneshot
ExecStart=/usr/bin/systemctl start fah-client
ExecStart=/usr/local/sbin/nocturne-folding-unpause
EOF
cat > /etc/systemd/system/nocturne-folding-start.timer <<EOF
[Unit]
Description=Nightly Folding@home window, start
[Timer]
OnCalendar=*-*-* 02:00:00
[Install]
WantedBy=timers.target
EOF
cat > /etc/systemd/system/nocturne-folding-stop.service <<EOF
[Unit]
Description=End the nightly Folding@home window
[Service]
Type=oneshot
ExecStart=/usr/bin/systemctl stop fah-client
EOF
cat > /etc/systemd/system/nocturne-folding-stop.timer <<EOF
[Unit]
Description=Nightly Folding@home window, stop
[Timer]
OnCalendar=*-*-* 04:00:00
[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
# Runs only inside the window, never at boot.
systemctl disable fah-client
systemctl start fah-client
/usr/local/sbin/nocturne-folding-unpause
systemctl stop fah-client
systemctl enable --now nocturne-folding-start.timer nocturne-folding-stop.timer
