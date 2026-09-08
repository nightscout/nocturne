#!/usr/bin/env bash
# Nocturne on Oracle Cloud. Run from Oracle Cloud Shell:
#
#   BASE_DOMAIN=nocturne.example.com bash <(curl -fsSL https://github.com/nightscout/nocturne/releases/latest/download/oracle-cloud-install.sh)
#
# Creates a VCN with one public subnet, a reserved public IP and an Always Free
# Ampere A1 instance whose first boot installs Docker, downloads the Nocturne
# release bundle and starts it once DNS points at the reserved IP. Every resource
# is looked up by name before it is created, so re-running is safe and prints the
# current state.
#
# Optional environment:
#   DESEC_TOKEN             deSEC API token; the apex and wildcard A records are then written
#                           for you (prompted for, hidden, when unset and running interactively)
#   COMPARTMENT_ID          where to create resources (default: tenancy root)
#   NOCTURNE_VERSION        release tag to install (default: latest)
#   OCPUS / MEMORY_GB       A1 size (default 2 / 12; the free tier allows 4 / 24 in total)
#   BOOT_VOLUME_GB          boot volume size (default 50; the free tier allows 200 in total)
#   SSH_PUBLIC_KEY_FILE     key to authorise (default: generated at ~/.ssh/nocturne_oci)
#   CAPACITY_RETRY_MINUTES  how long to keep retrying "out of host capacity" (default 30)

set -euo pipefail

NAME="nocturne"
COMPARTMENT_ID="${COMPARTMENT_ID:-${OCI_TENANCY:-}}"
NOCTURNE_VERSION="${NOCTURNE_VERSION:-}"
OCPUS="${OCPUS:-2}"
MEMORY_GB="${MEMORY_GB:-12}"
BOOT_VOLUME_GB="${BOOT_VOLUME_GB:-50}"
SSH_PUBLIC_KEY_FILE="${SSH_PUBLIC_KEY_FILE:-$HOME/.ssh/nocturne_oci.pub}"
CAPACITY_RETRY_MINUTES="${CAPACITY_RETRY_MINUTES:-30}"

log()  { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\n\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# Runs an oci query and turns JMESPath's null into an empty string so callers can
# test "does this exist" with [[ -n ]].
q() {
  local out
  out=$("$@" 2>/dev/null) || return 0
  [[ "$out" == "null" ]] && out=""
  printf '%s' "$out"
}

command -v oci >/dev/null || die "the oci command is not available. Open Cloud Shell from the Oracle Cloud console (terminal icon, top right) and run this there."
command -v jq >/dev/null || die "jq is not available"
[[ -n "$COMPARTMENT_ID" ]] || die "COMPARTMENT_ID is not set and OCI_TENANCY is empty. Are you in Cloud Shell?"

CONFIG="$HOME/.nocturne-oci/config"
if [[ -z "${BASE_DOMAIN:-}" && -f "$CONFIG" ]]; then
  . "$CONFIG"
  info "using $BASE_DOMAIN from $CONFIG (set BASE_DOMAIN to override)"
fi
if [[ -z "${BASE_DOMAIN:-}" ]]; then
  read -rp "Domain Nocturne should answer on (e.g. nocturne.example.com): " BASE_DOMAIN
fi
BASE_DOMAIN="${BASE_DOMAIN,,}"
[[ "$BASE_DOMAIN" =~ ^([a-z0-9-]+\.)+[a-z]{2,}$ ]] || die "BASE_DOMAIN must be a domain name with at least two labels, not an IP address"
mkdir -p "$(dirname "$CONFIG")"
printf 'BASE_DOMAIN=%s\n' "$BASE_DOMAIN" > "$CONFIG"

# Always Free compute only exists in the tenancy's home region.
HOME_REGION=$(oci iam region-subscription list --query 'data[?"is-home-region"] | [0]."region-name"' --raw-output)
export OCI_CLI_REGION="$HOME_REGION"

if [[ -z "$NOCTURNE_VERSION" ]]; then
  # The redirect target of /releases/latest names the tag without touching the rate-limited API.
  NOCTURNE_VERSION=$(curl -fsS -o /dev/null -w '%{redirect_url}' https://github.com/nightscout/nocturne/releases/latest | sed 's|.*/tag/||')
  [[ "$NOCTURNE_VERSION" == v* ]] || die "could not look up the latest Nocturne release; set NOCTURNE_VERSION"
fi

log "Nocturne $NOCTURNE_VERSION on $BASE_DOMAIN, region $HOME_REGION"

# ── Network ──────────────────────────────────────────────────────────────────

log "Network"
VCN_ID=$(q oci network vcn list --compartment-id "$COMPARTMENT_ID" --display-name "$NAME" --lifecycle-state AVAILABLE --query 'data[0].id' --raw-output)
if [[ -z "$VCN_ID" ]]; then
  VCN_ID=$(oci network vcn create --compartment-id "$COMPARTMENT_ID" --display-name "$NAME" --dns-label "$NAME" \
    --cidr-block 10.0.0.0/16 --wait-for-state AVAILABLE --query 'data.id' --raw-output)
  info "created VCN"
else
  info "VCN exists"
fi

IGW_ID=$(q oci network internet-gateway list --compartment-id "$COMPARTMENT_ID" --vcn-id "$VCN_ID" --lifecycle-state AVAILABLE --query 'data[0].id' --raw-output)
if [[ -z "$IGW_ID" ]]; then
  IGW_ID=$(oci network internet-gateway create --compartment-id "$COMPARTMENT_ID" --vcn-id "$VCN_ID" --display-name "$NAME" \
    --is-enabled true --wait-for-state AVAILABLE --query 'data.id' --raw-output)
  info "created internet gateway"
fi

RT_ID=$(oci network vcn get --vcn-id "$VCN_ID" --query 'data."default-route-table-id"' --raw-output)
if [[ "$(oci network route-table get --rt-id "$RT_ID" --query 'length(data."route-rules")')" == "0" ]]; then
  oci network route-table update --rt-id "$RT_ID" --force \
    --route-rules "[{\"destination\":\"0.0.0.0/0\",\"destinationType\":\"CIDR_BLOCK\",\"networkEntityId\":\"$IGW_ID\"}]" >/dev/null
  info "added default route"
fi

SL_ID=$(oci network vcn get --vcn-id "$VCN_ID" --query 'data."default-security-list-id"' --raw-output)
if ! oci network security-list get --security-list-id "$SL_ID" \
     --query 'data."ingress-security-rules"[?protocol==`"6"` && "tcp-options"."destination-port-range".min==`443`]' \
     | jq -e 'length > 0' >/dev/null; then
  oci network security-list update --security-list-id "$SL_ID" --force --ingress-security-rules '[
    {"protocol":"6","source":"0.0.0.0/0","tcpOptions":{"destinationPortRange":{"min":22,"max":22}}},
    {"protocol":"6","source":"0.0.0.0/0","tcpOptions":{"destinationPortRange":{"min":80,"max":80}}},
    {"protocol":"6","source":"0.0.0.0/0","tcpOptions":{"destinationPortRange":{"min":443,"max":443}}},
    {"protocol":"17","source":"0.0.0.0/0","udpOptions":{"destinationPortRange":{"min":443,"max":443}}},
    {"protocol":"1","source":"0.0.0.0/0","icmpOptions":{"type":3,"code":4}},
    {"protocol":"1","source":"10.0.0.0/16","icmpOptions":{"type":3}}
  ]' >/dev/null
  info "opened ports 80 and 443"
fi

SUBNET_ID=$(q oci network subnet list --compartment-id "$COMPARTMENT_ID" --vcn-id "$VCN_ID" --display-name "$NAME" --lifecycle-state AVAILABLE --query 'data[0].id' --raw-output)
if [[ -z "$SUBNET_ID" ]]; then
  SUBNET_ID=$(oci network subnet create --compartment-id "$COMPARTMENT_ID" --vcn-id "$VCN_ID" --display-name "$NAME" --dns-label "$NAME" \
    --cidr-block 10.0.0.0/24 --route-table-id "$RT_ID" --security-list-ids "[\"$SL_ID\"]" \
    --wait-for-state AVAILABLE --query 'data.id' --raw-output)
  info "created subnet"
fi

# ── Reserved public IP ───────────────────────────────────────────────────────
# Reserved rather than ephemeral so the address survives a stop/start and the
# user's DNS records stay valid. Created before the instance so the DNS records
# can be added while it boots.

log "Public IP"
PUBLIC_IP_ID=$(q oci network public-ip list --compartment-id "$COMPARTMENT_ID" --scope REGION --lifetime RESERVED \
  --query "data[?\"display-name\"=='$NAME'] | [0].id" --raw-output)
if [[ -z "$PUBLIC_IP_ID" ]]; then
  PUBLIC_IP_ID=$(oci network public-ip create --compartment-id "$COMPARTMENT_ID" --lifetime RESERVED --display-name "$NAME" \
    --query 'data.id' --raw-output)
  info "reserved a public IP"
fi
PUBLIC_IP=$(oci network public-ip get --public-ip-id "$PUBLIC_IP_ID" --query 'data."ip-address"' --raw-output)

# ── DNS ──────────────────────────────────────────────────────────────────────

desec() { curl -fsS -H "Authorization: Token $DESEC_TOKEN" -H "Content-Type: application/json" "$@"; }
resolves_to_ip() { getent ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | grep -qx "$PUBLIC_IP"; }

if resolves_to_ip "$BASE_DOMAIN" && resolves_to_ip "dns-check.$BASE_DOMAIN"; then
  DNS_READY=1
  info "DNS already points at $PUBLIC_IP"
elif [[ -z "${DESEC_TOKEN:-}" && -t 0 ]]; then
  printf '\n'
  info "If $BASE_DOMAIN is managed at deSEC (desec.io, including free dedyn.io names), paste an"
  info "API token and the DNS records are created for you. Otherwise press Enter to add them yourself."
  read -rsp "    deSEC token: " DESEC_TOKEN
  printf '\n'
fi

if [[ -n "${DNS_READY:-}" ]]; then
  :
elif [[ -n "${DESEC_TOKEN:-}" ]]; then
  log "DNS records at deSEC"
  # BASE_DOMAIN may sit below the zone deSEC hosts (nocturne.example.com in example.com).
  ZONE="$BASE_DOMAIN"
  while [[ "$ZONE" == *.* ]] && ! DOMAIN_JSON=$(desec "https://desec.io/api/v1/domains/$ZONE/" 2>/dev/null); do
    ZONE="${ZONE#*.}"
  done
  [[ "$ZONE" == *.* ]] || die "no domain in this deSEC account contains $BASE_DOMAIN. Add it at desec.io first (for a free name, a dynDNS domain under dedyn.io), or run again without a token and create the records yourself."
  SUBNAME="${BASE_DOMAIN%"$ZONE"}"
  SUBNAME="${SUBNAME%.}"
  TTL=$(jq -r '.minimum_ttl // 3600' <<<"$DOMAIN_JSON")
  desec -X PUT "https://desec.io/api/v1/domains/$ZONE/rrsets/" -d "$(jq -cn --arg ip "$PUBLIC_IP" --arg s "$SUBNAME" --arg w "*${SUBNAME:+.$SUBNAME}" --argjson ttl "$TTL" \
    '[{subname:$s,type:"A",ttl:$ttl,records:[$ip]},{subname:$w,type:"A",ttl:$ttl,records:[$ip]}]')" >/dev/null \
    || die "could not write the DNS records at deSEC. The token needs write access to $ZONE."
  info "$BASE_DOMAIN and *.$BASE_DOMAIN point at $PUBLIC_IP"
else
  printf '\n'
  info "Create these two DNS records at your domain provider now:"
  printf '\n      %-28s A   %s\n      %-28s A   %s\n\n' "$BASE_DOMAIN" "$PUBLIC_IP" "*.$BASE_DOMAIN" "$PUBLIC_IP"
  info "Nocturne waits until both resolve before requesting certificates. Do not proxy them through Cloudflare."
fi

# ── SSH key ──────────────────────────────────────────────────────────────────

if [[ ! -f "$SSH_PUBLIC_KEY_FILE" ]]; then
  [[ "$SSH_PUBLIC_KEY_FILE" == "$HOME/.ssh/nocturne_oci.pub" ]] || die "SSH_PUBLIC_KEY_FILE $SSH_PUBLIC_KEY_FILE does not exist"
  mkdir -p "$HOME/.ssh"
  # Cloud Shell runs in FIPS mode, which rejects Ed25519.
  ssh-keygen -q -t rsa -b 4096 -N '' -f "$HOME/.ssh/nocturne_oci" -C "$NAME"
  info "generated an SSH key at ~/.ssh/nocturne_oci"
fi

# ── Instance ─────────────────────────────────────────────────────────────────

log "Instance"
INSTANCE_ID=$(q oci compute instance list --compartment-id "$COMPARTMENT_ID" --display-name "$NAME" \
  --query 'data[?"lifecycle-state"!=`"TERMINATED"` && "lifecycle-state"!=`"TERMINATING"`] | [0].id' --raw-output)

if [[ -z "$INSTANCE_ID" ]]; then
  IMAGE_ID=$(oci compute image list --compartment-id "$COMPARTMENT_ID" --operating-system "Canonical Ubuntu" --operating-system-version "24.04" \
    --shape "VM.Standard.A1.Flex" --sort-by TIMECREATED --sort-order DESC --query 'data[0].id' --raw-output)
  [[ -n "$IMAGE_ID" && "$IMAGE_ID" != "null" ]] || die "no Ubuntu 24.04 image for VM.Standard.A1.Flex in $HOME_REGION"

  CLOUD_INIT=$(mktemp)
  trap 'rm -f "$CLOUD_INIT"' EXIT
  cat > "$CLOUD_INIT" <<'CLOUD_INIT_EOF'
#cloud-config
package_update: true
write_files:
  - path: /opt/nocturne/install.env
    permissions: '0600'
    content: |
      BASE_DOMAIN=@BASE_DOMAIN@
      PUBLIC_IP=@PUBLIC_IP@
      NOCTURNE_VERSION=@NOCTURNE_VERSION@
  - path: /usr/local/sbin/nocturne-up
    permissions: '0755'
    content: |
      #!/usr/bin/env bash
      set -euo pipefail
      . /opt/nocturne/install.env
      cd /opt/nocturne
      log() { echo "[nocturne] $*"; }

      if [[ ! -f docker-compose.yaml ]]; then
        log "downloading Nocturne $NOCTURNE_VERSION"
        base="https://github.com/nightscout/nocturne/releases/download/$NOCTURNE_VERSION"
        curl -fsSL -o docker-compose.yaml "$base/docker-compose.yaml"
        curl -fsSL -o default.env.example "$base/default.env.example"
      fi

      if [[ ! -f .env ]]; then
        log "generating secrets"
        gen() { openssl rand -hex 24; }
        sed -e "s|^BASE_DOMAIN=.*|BASE_DOMAIN=$BASE_DOMAIN|" \
            -e "s|^INSTANCE_KEY=.*|INSTANCE_KEY=$(gen)|" \
            -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$(gen)|" \
            -e "s|^POSTGRES_APP_PASSWORD=.*|POSTGRES_APP_PASSWORD=$(gen)|" \
            -e "s|^POSTGRES_MIGRATOR_PASSWORD=.*|POSTGRES_MIGRATOR_PASSWORD=$(gen)|" \
            -e "s|^POSTGRES_WEB_PASSWORD=.*|POSTGRES_WEB_PASSWORD=$(gen)|" \
            default.env.example > .env
        chmod 600 .env
        if grep -qE '^(BASE_DOMAIN|INSTANCE_KEY|POSTGRES_[A-Z_]*PASSWORD)=$' .env; then
          log "a required value in .env is still empty; the release's .env.example has changed shape"
          exit 1
        fi
      fi

      resolves_here() { getent ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | grep -qx "$PUBLIC_IP"; }
      until resolves_here "$BASE_DOMAIN" && resolves_here "dns-check.$BASE_DOMAIN"; do
        log "waiting for DNS: $BASE_DOMAIN and *.$BASE_DOMAIN must point at $PUBLIC_IP"
        sleep 30
      done

      log "starting"
      docker compose up -d
  - path: /etc/systemd/system/nocturne.service
    content: |
      [Unit]
      Description=Nocturne
      Requires=docker.service
      After=docker.service network-online.target
      Wants=network-online.target

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      TimeoutStartSec=infinity
      ExecStart=/usr/local/sbin/nocturne-up

      [Install]
      WantedBy=multi-user.target
runcmd:
  # Oracle's Ubuntu image rejects inbound traffic other than SSH in the instance's
  # own iptables rules, independently of the VCN security list.
  - |
    if [ -f /etc/iptables/rules.v4 ]; then
      sed -i '/-A INPUT -j REJECT/i -A INPUT -p tcp -m state --state NEW -m tcp --dport 80 -j ACCEPT' /etc/iptables/rules.v4
      sed -i '/-A INPUT -j REJECT/i -A INPUT -p tcp -m state --state NEW -m tcp --dport 443 -j ACCEPT' /etc/iptables/rules.v4
      sed -i '/-A INPUT -j REJECT/i -A INPUT -p udp -m udp --dport 443 -j ACCEPT' /etc/iptables/rules.v4
      netfilter-persistent reload
    fi
  - curl -fsSL https://get.docker.com | sh
  - systemctl enable --now docker
  - systemctl daemon-reload
  - systemctl enable nocturne.service
  - systemctl start --no-block nocturne.service
CLOUD_INIT_EOF
  sed -i -e "s|@BASE_DOMAIN@|$BASE_DOMAIN|" -e "s|@PUBLIC_IP@|$PUBLIC_IP|" -e "s|@NOCTURNE_VERSION@|$NOCTURNE_VERSION|" "$CLOUD_INIT"

  mapfile -t ADS < <(oci iam availability-domain list --compartment-id "$COMPARTMENT_ID" --query 'data[].name' --raw-output | jq -r '.[]')
  deadline=$((SECONDS + CAPACITY_RETRY_MINUTES * 60))
  while [[ -z "$INSTANCE_ID" ]]; do
    for ad in "${ADS[@]}"; do
      info "launching in $ad"
      if out=$(oci compute instance launch --compartment-id "$COMPARTMENT_ID" --availability-domain "$ad" --display-name "$NAME" \
          --shape "VM.Standard.A1.Flex" --shape-config "{\"ocpus\":$OCPUS,\"memoryInGBs\":$MEMORY_GB}" \
          --image-id "$IMAGE_ID" --subnet-id "$SUBNET_ID" --assign-public-ip false \
          --boot-volume-size-in-gbs "$BOOT_VOLUME_GB" \
          --ssh-authorized-keys-file "$SSH_PUBLIC_KEY_FILE" --user-data-file "$CLOUD_INIT" \
          --wait-for-state RUNNING --query 'data.id' --raw-output 2>&1); then
        INSTANCE_ID="$out"
        info "instance is running"
        break
      fi
      if grep -qi "capacity" <<<"$out"; then
        info "no Ampere A1 capacity in $ad right now"
      elif grep -qi "TooManyRequests" <<<"$out"; then
        info "Oracle is rate-limiting launch attempts; pausing two minutes"
        sleep 120
      else
        die "launch failed: $out"
      fi
    done
    [[ -n "$INSTANCE_ID" ]] && break
    (( SECONDS < deadline )) || die "no Ampere A1 capacity in any availability domain after $CAPACITY_RETRY_MINUTES minutes. This is common on the free tier: re-run later (everything created so far is reused), or try a smaller size with OCPUS=1 MEMORY_GB=6."
    info "retrying in 60 seconds"
    sleep 60
  done
else
  info "instance exists"
fi

VNIC_ID=$(oci compute instance list-vnics --instance-id "$INSTANCE_ID" --query 'data[0].id' --raw-output)
PRIVATE_IP_ID=$(oci network private-ip list --vnic-id "$VNIC_ID" --query 'data[0].id' --raw-output)
if [[ "$(q oci network public-ip get --public-ip-id "$PUBLIC_IP_ID" --query 'data."assigned-entity-id"' --raw-output)" != "$PRIVATE_IP_ID" ]]; then
  oci network public-ip update --public-ip-id "$PUBLIC_IP_ID" --private-ip-id "$PRIVATE_IP_ID" >/dev/null
  info "attached the reserved IP"
fi

# ── Wait for Nocturne ────────────────────────────────────────────────────────

log "Waiting for https://$BASE_DOMAIN"
info "The instance installs Docker, then waits for your DNS records before starting."
info "This usually takes 3 to 5 minutes once DNS is in place."
deadline=$((SECONDS + 15 * 60))
until curl -fsS -o /dev/null --max-time 10 "https://$BASE_DOMAIN/api/v1/status.json"; do
  if (( SECONDS >= deadline )); then
    printf '\n'
    info "Not answering yet. That is expected if the DNS records are new. Check again with:"
    info "  curl https://$BASE_DOMAIN/api/v1/status.json"
    info "or re-run this script, which is safe and skips everything that already exists."
    break
  fi
  sleep 15
done

printf '\n'
log "Done"
info "Open https://$BASE_DOMAIN to create your first site and passkey."
info "SSH:   ssh -i ~/.ssh/nocturne_oci ubuntu@$PUBLIC_IP"
info "Files: /opt/nocturne on the instance. Database passwords are in .env; Nocturne generated them and only it needs them."
info "Logs:  sudo journalctl -u nocturne   and   cd /opt/nocturne && sudo docker compose logs"

INSTANCE_KEY=$(ssh -i "${SSH_PUBLIC_KEY_FILE%.pub}" -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o ConnectTimeout=10 \
  "ubuntu@$PUBLIC_IP" "sudo sed -n 's/^INSTANCE_KEY=//p' /opt/nocturne/.env" 2>/dev/null || true)
printf '\n'
if [[ -n "$INSTANCE_KEY" ]]; then
  info "Your instance key. It is the master credential for this installation, used for administrative"
  info "API access and account recovery. Save it in a password manager now; it is shown again on re-runs."
  printf '\n      %s\n' "$INSTANCE_KEY"
else
  info "The instance key was not readable yet. Re-run this script later to see it, or read it with:"
  info "  ssh -i ~/.ssh/nocturne_oci ubuntu@$PUBLIC_IP sudo grep ^INSTANCE_KEY= /opt/nocturne/.env"
fi
