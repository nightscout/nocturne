#!/usr/bin/env bash
# Builds the Nocturne one-click image on a stock Ubuntu 24.04 machine. Packer uploads the
# deploy/vm directory to /tmp/nocturne-vm first.

set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
SRC=/tmp/nocturne-vm

cloud-init status --wait >/dev/null || true

apt-get update
apt-get -y upgrade
apt-get -y install ca-certificates curl openssl ufw

install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
. /etc/os-release
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
  > /etc/apt/sources.list.d/docker.list
apt-get update
apt-get -y install docker-ce docker-ce-cli containerd.io docker-compose-plugin
systemctl enable docker

install -d -m 0755 /opt/nocturne
install -m 0644 "$SRC/bundle/docker-compose.yaml" "$SRC/bundle/.env.example" "$SRC/bundle/generated-secrets" /opt/nocturne/
install -m 0755 "$SRC/firstboot.sh" /opt/nocturne/firstboot.sh
install -m 0644 "$SRC"/systemd/*.service /etc/systemd/system/
systemctl enable nocturne-firstboot.service nocturne-setup.service nocturne.service

# Bake every image in, the setup page's included, so a new machine starts without a pull.
(cd /opt/nocturne && docker compose --env-file .env.example --profile "*" pull --quiet)

ufw default deny incoming
ufw default allow outgoing
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 443/udp
ufw --force enable
