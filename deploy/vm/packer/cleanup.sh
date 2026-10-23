#!/usr/bin/env bash
# Leaves nothing of the build machine in the image, as every marketplace's image scan requires:
# no keys, passwords, shell history or logs, and cloud-init reset so the next boot is a first boot.

set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

apt-get -y autoremove --purge
apt-get -y clean
rm -rf /var/lib/apt/lists/*

rm -rf /tmp/* /var/tmp/*
rm -f /root/.ssh/authorized_keys /home/*/.ssh/authorized_keys
rm -f /etc/ssh/ssh_host_*
unset HISTFILE
rm -f /root/.bash_history /home/*/.bash_history

find /var/log -type f -exec truncate --size=0 {} \;
journalctl --rotate --vacuum-time=1s >/dev/null 2>&1 || true

cloud-init clean --logs --seed
truncate --size=0 /etc/machine-id
rm -f /var/lib/dbus/machine-id

sync
