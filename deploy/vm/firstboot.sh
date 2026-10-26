#!/usr/bin/env bash
# Nocturne one-click image: first boot (nocturne-firstboot.service).
#
# Writes this machine's Compose .env from the bundle's .env.example: a fresh secret for every
# variable in generated-secrets, the setup claim code (the cloud instance ID, which only the
# owner's console shows) and the public address the setup page checks DNS against. Runs once:
# the unit is skipped when .env exists, so a reboot keeps the secrets the database was
# initialised with.

set -euo pipefail

DIR=/opt/nocturne
ENV_FILE="$DIR/.env"

metadata() { curl -fsS --max-time 2 "$@" 2>/dev/null; }

PROVIDER=
CLAIM=
PUBLIC_IP=
WHERE=

if token=$(metadata -X PUT -H "X-aws-ec2-metadata-token-ttl-seconds: 60" http://169.254.169.254/latest/api/token); then
  PROVIDER=AWS
  CLAIM=$(metadata -H "X-aws-ec2-metadata-token: $token" http://169.254.169.254/latest/meta-data/instance-id)
  PUBLIC_IP=$(metadata -H "X-aws-ec2-metadata-token: $token" http://169.254.169.254/latest/meta-data/public-ipv4 || true)
  WHERE="the EC2 console: Instance ID"
elif CLAIM=$(metadata -H "Metadata-Flavor: Google" http://metadata.google.internal/computeMetadata/v1/instance/id); then
  PROVIDER="Google Cloud"
  PUBLIC_IP=$(metadata -H "Metadata-Flavor: Google" \
    http://metadata.google.internal/computeMetadata/v1/instance/network-interfaces/0/access-configs/0/external-ip || true)
  WHERE="the VM instance's details page in the Google Cloud console: Instance Id"
elif CLAIM=$(metadata -H "Metadata: true" "http://169.254.169.254/metadata/instance/compute/vmId?api-version=2021-02-01&format=text"); then
  PROVIDER=Azure
  PUBLIC_IP=$(metadata -H "Metadata: true" \
    "http://169.254.169.254/metadata/instance/network/interface/0/ipv4/ipAddress/0/publicIpAddress?api-version=2021-02-01&format=text" || true)
  WHERE="the Azure portal: the VM's JSON View, vmId"
elif CLAIM=$(metadata http://169.254.169.254/metadata/v1/id); then
  PROVIDER=DigitalOcean
  PUBLIC_IP=$(metadata http://169.254.169.254/metadata/v1/interfaces/public/0/ipv4/address || true)
  WHERE="the Droplet's page in the DigitalOcean control panel: the number at the end of its URL"
else
  PROVIDER=unknown
  CLAIM=$(openssl rand -hex 6)
  WHERE="this message, also in /etc/motd over SSH"
fi

# An empty PUBLIC_IP is fine (Azure reports none for a Standard SKU address): the setup page then
# asks the internet which address it sees.

set_env() {
  local key=$1 value=$2
  if grep -q "^${key}=" "$ENV_FILE"; then
    sed -i "s|^${key}=.*|${key}=${value}|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

umask 077
cp "$DIR/.env.example" "$ENV_FILE.new"
ENV_FILE="$ENV_FILE.new"
while read -r name; do
  [[ -n "$name" ]] && set_env "$name" "$(openssl rand -hex 32)"
done < "$DIR/generated-secrets"
set_env SETUP_CLAIM_CODE "$CLAIM"
set_env PUBLIC_IP "$PUBLIC_IP"
mv "$DIR/.env.new" "$DIR/.env"

address=${PUBLIC_IP:-"<this machine's address>"}
message="Nocturne setup: open http://${address}/ and enter the setup code from ${WHERE}."
[[ "$PROVIDER" == unknown ]] && message+=" Setup code: ${CLAIM}"
echo "$message"
printf '\n%s\n\n' "$message" > /etc/motd
