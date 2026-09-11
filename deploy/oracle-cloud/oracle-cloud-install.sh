#!/usr/bin/env bash
# Nocturne on Oracle Cloud. Run from Oracle Cloud Shell:
#
#   BASE_DOMAIN=nocturne.example.com bash <(curl -fsSL https://github.com/nightscout/nocturne/releases/latest/download/oracle-cloud-install.sh)
#
# Creates a VCN with one public subnet, a reserved public IP and an Always Free
# Ampere A1 instance, then converges the instance over SSH: Docker, the Nocturne
# release bundle, secrets and the running stack.
#
# Re-running is the answer to any failure. Every cloud resource is looked up by
# name before it is created, and the instance is brought back to the desired
# state on every run rather than only at first boot, so an interrupted or failed
# run is repaired by running this again.
#
# Cloud Shell disconnects idle sessions, which kills the run. Start it inside
# tmux so that cannot happen:
#
#   tmux new -s nocturne
#   ...run the command above, detach with Ctrl-B then D, reattach with:
#   tmux attach -t nocturne
#
# Optional environment:
#   DESEC_TOKEN             deSEC API token; the apex and wildcard A records are then written
#                           for you (prompted for, hidden, when unset and running interactively)
#   FOLDING=1               run Folding@home 02:00-04:00 UTC nightly so the server never counts as idle
#   FOLDING_TEAM            Folding@home team number (default 0)
#   BUDGET_EMAIL            where the spend alert goes (default: the Oracle account's email)
#   COMPARTMENT_ID          where to create resources (default: tenancy root)
#   NOCTURNE_VERSION        release tag to install (default: latest); re-run with this set to upgrade
#   OCPUS / MEMORY_GB       A1 size (default 1 / 6; the free tier allows 2 / 12 in total)
#   BOOT_VOLUME_GB          boot volume size (default 50; the free tier allows 200 in total)
#   SSH_PUBLIC_KEY_FILE     key to authorise (default: generated at ~/.ssh/nocturne_oci)
#   CAPACITY_RETRY_MINUTES  how long to keep retrying "out of host capacity" (default 30)
#   DNS_WAIT_MINUTES        how long the instance waits for your DNS records (default 10)
#   ALLOW_PAID=1            permit a size above the Always Free allowance, and skip the quota
#                           policy that otherwise stops this tenancy creating anything billable

set -euo pipefail

NAME="nocturne"
COMPARTMENT_ID="${COMPARTMENT_ID:-${OCI_TENANCY:-}}"
NOCTURNE_VERSION="${NOCTURNE_VERSION:-}"
OCPUS="${OCPUS:-1}"
MEMORY_GB="${MEMORY_GB:-6}"
FOLDING="${FOLDING:-}"
FOLDING_TEAM="${FOLDING_TEAM:-0}"
BUDGET_EMAIL="${BUDGET_EMAIL:-}"
BOOT_VOLUME_GB="${BOOT_VOLUME_GB:-50}"
SSH_PUBLIC_KEY_FILE="${SSH_PUBLIC_KEY_FILE:-$HOME/.ssh/nocturne_oci.pub}"
CAPACITY_RETRY_MINUTES="${CAPACITY_RETRY_MINUTES:-30}"
DNS_WAIT_MINUTES="${DNS_WAIT_MINUTES:-10}"
ALLOW_PAID="${ALLOW_PAID:-}"

# Oracle halved the Always Free Ampere allowance on 2026-06-15, without announcing
# it, and terminated over-limit instances from 2026-08-18. One source for both the
# size check below and the quota policy near the end.
FREE_OCPUS=2
FREE_MEMORY_GB=12
FREE_BOOT_VOLUME_GB=200
FREE_E2_MICRO_COUNT=2

log()  { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\n\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# Always Free resources stay free on a Pay As You Go account, but a size above the
# allowance is billed rather than refused, and upgrading to Pay As You Go is the
# usual answer to an Ampere capacity error. Refuse before anything is created.
if [[ -z "$ALLOW_PAID" ]]; then
  OVER=()
  (( OCPUS <= FREE_OCPUS )) || OVER+=("$OCPUS OCPUs, where Always Free allows $FREE_OCPUS")
  (( MEMORY_GB <= FREE_MEMORY_GB )) || OVER+=("$MEMORY_GB GB of memory, where Always Free allows $FREE_MEMORY_GB")
  (( BOOT_VOLUME_GB <= FREE_BOOT_VOLUME_GB )) || OVER+=("a $BOOT_VOLUME_GB GB boot volume, where Always Free allows $FREE_BOOT_VOLUME_GB")
  if [[ ${#OVER[@]} -gt 0 ]]; then
    printf '
'
    for over in "${OVER[@]}"; do info "you asked for $over"; done
    die "that is outside the Always Free allowance, and is charged rather than refused once this account is on Pay As You Go. Set ALLOW_PAID=1 if you mean to pay for it."
  fi
fi

# Runs an oci query and turns JMESPath's null into an empty string so callers can
# test "does this exist" with [[ -n ]].
q() {
  local out
  out=$("$@" 2>/dev/null) || return 0
  [[ "$out" == "null" ]] && out=""
  printf '%s' "$out"
}

# The CLI reports these objects in kebab-case but only accepts camelCase back, so
# anything read, edited and rewritten has to be converted. Nulls are dropped
# because the API rejects them for fields it did not send.
JQ_OCI='
def tocamel: gsub("-(?<c>[a-z])"; .c | ascii_upcase);
def recamel: walk(if type == "object" then (with_entries(.key |= tocamel) | with_entries(select(.value != null))) else . end);
def covers($p; $anysrc): any(.protocol == "6"
  and ($anysrc or .source == "0.0.0.0/0")
  and (."tcp-options" == null
       or (."tcp-options"."destination-port-range" as $x | $x == null or ($x.min <= $p and $x.max >= $p))));
def missing_tcp: [ (if covers(22; true) then empty else 22 end),
                   (if covers(80; false) then empty else 80 end),
                   (if covers(443; false) then empty else 443 end) ];
'

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

if [[ -z "${TMUX:-}" && -t 1 ]]; then
  info "tip: if this Cloud Shell session drops, the run stops. 'tmux new -s nocturne' first makes it survive."
fi

# Always Free compute only exists in the tenancy's home region.
HOME_REGION=$(oci iam region-subscription list --query 'data[?"is-home-region"] | [0]."region-name"' --raw-output)
export OCI_CLI_REGION="$HOME_REGION"

if [[ -z "$NOCTURNE_VERSION" ]]; then
  # The redirect target of /releases/latest names the tag without touching the rate-limited API.
  NOCTURNE_VERSION=$(curl -fsS -o /dev/null -w '%{redirect_url}' https://github.com/nightscout/nocturne/releases/latest | sed 's|.*/tag/||')
  [[ "$NOCTURNE_VERSION" == v* ]] || die "could not look up the latest Nocturne release; set NOCTURNE_VERSION"
fi

log "Nocturne $NOCTURNE_VERSION on $BASE_DOMAIN, region $HOME_REGION"

# ── Files installed on the instance ──────────────────────────────────────────
# Written to temporary files here because they are used twice: embedded in
# cloud-init so a fresh instance configures itself, and pushed over SSH on every
# run so an existing instance picks up fixes to them. The instance is configured
# by nocturne-up alone; cloud-init only plants the files and starts the unit, so
# there is exactly one description of the desired state.

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/nocturne-up" <<'GUEST_EOF'
#!/usr/bin/env bash
# Brings this instance to the state described by /opt/nocturne/install.env.
# Safe to run at any time, as often as you like: every step checks before acting.
set -euo pipefail
. /opt/nocturne/install.env
cd /opt/nocturne

log() { echo "[nocturne] $*"; }

# Serialise against the boot-time run of this same script.
exec 9>/var/lock/nocturne-up.lock
flock -w 600 9 || { log "another nocturne-up is already running"; exit 1; }

# Oracle's Ubuntu image rejects inbound traffic other than SSH in the instance's
# own iptables rules, independently of the VCN security list.
ensure_firewall() {
  local rules=/etc/iptables/rules.v4 changed=""
  [[ -f "$rules" ]] || return 0
  grep -q -- '-A INPUT -j REJECT' "$rules" || return 0
  local rule
  for rule in \
    '-A INPUT -p tcp -m state --state NEW -m tcp --dport 80 -j ACCEPT' \
    '-A INPUT -p tcp -m state --state NEW -m tcp --dport 443 -j ACCEPT' \
    '-A INPUT -p udp -m udp --dport 443 -j ACCEPT'
  do
    if ! grep -qF -- "$rule" "$rules"; then
      sed -i "/-A INPUT -j REJECT/i $rule" "$rules"
      changed=1
    fi
  done
  if [[ -n "$changed" ]]; then
    netfilter-persistent reload
    log "opened ports 80 and 443"
  fi
}

# The instance has no route to the internet until the installer attaches the
# reserved public IP, which can happen after this first runs. Retry rather than
# fail, so the boot-time run heals itself the moment the address appears.
ensure_docker() {
  if command -v docker >/dev/null 2>&1; then
    systemctl enable --now docker >/dev/null 2>&1 || true
    return 0
  fi
  local try
  for try in $(seq 1 20); do
    if curl -fsSL -o /tmp/get-docker.sh https://get.docker.com && sh /tmp/get-docker.sh; then
      rm -f /tmp/get-docker.sh
      systemctl enable --now docker
      log "installed Docker"
      return 0
    fi
    log "could not install Docker (attempt $try); this instance may have no internet access yet. Retrying in 30s"
    sleep 30
  done
  log "giving up on installing Docker. Check that the instance has a public IP attached."
  return 1
}

ensure_bundle() {
  local want="$NOCTURNE_VERSION" have="" try
  [[ -f .bundle-version ]] && have=$(cat .bundle-version)
  if [[ -f docker-compose.yaml && "$have" == "$want" ]]; then
    return 0
  fi
  log "downloading Nocturne $want"
  local base="https://github.com/nightscout/nocturne/releases/download/$want"
  for try in $(seq 1 20); do
    if curl -fsSL -o docker-compose.yaml.new "$base/docker-compose.yaml" \
       && curl -fsSL -o default.env.example.new "$base/default.env.example"; then
      mv docker-compose.yaml.new docker-compose.yaml
      mv default.env.example.new default.env.example
      printf '%s\n' "$want" > .bundle-version
      BUNDLE_CHANGED=1
      return 0
    fi
    log "download failed (attempt $try); retrying in 30s"
    sleep 30
  done
  rm -f docker-compose.yaml.new default.env.example.new
  log "could not download the Nocturne $want release bundle"
  return 1
}

set_env() { sed -i "s|^$1=.*|$1=$2|" .env; }
get_env() { sed -n "s|^$1=||p" .env; }

ensure_env() {
  if [[ ! -f .env ]]; then
    install -m 600 default.env.example .env
    log "created .env"
  fi

  # Settings the release added since this .env was written, so an upgrade does
  # not leave the stack missing values it now expects.
  local line key
  while IFS= read -r line; do
    key=${line%%=*}
    if ! grep -q "^${key}=" .env; then
      printf '%s\n' "$line" >> .env
      log "added new setting $key"
    fi
  done < <(grep -E '^[A-Za-z_][A-Za-z0-9_]*=' default.env.example)

  set_env BASE_DOMAIN "$BASE_DOMAIN"

  # Only ever fill in a blank. Regenerating a password that Postgres has already
  # initialised itself with would lock Nocturne out of its own database.
  for key in INSTANCE_KEY POSTGRES_PASSWORD POSTGRES_APP_PASSWORD POSTGRES_MIGRATOR_PASSWORD POSTGRES_WEB_PASSWORD; do
    grep -q "^${key}=" .env || printf '%s=\n' "$key" >> .env
    if [[ -z "$(get_env "$key")" ]]; then
      set_env "$key" "$(openssl rand -hex 24)"
      log "generated $key"
    fi
  done
  chmod 600 .env

  for key in BASE_DOMAIN INSTANCE_KEY POSTGRES_PASSWORD POSTGRES_APP_PASSWORD POSTGRES_MIGRATOR_PASSWORD POSTGRES_WEB_PASSWORD; do
    [[ -n "$(get_env "$key")" ]] || { log "$key is empty in .env and could not be filled in"; return 1; }
  done
}

resolves_here() { getent ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | grep -qx "$PUBLIC_IP"; }

wait_for_dns() {
  # Unset means wait forever, which is what the boot-time run wants: the server
  # should come up whenever the records eventually appear.
  local deadline=""
  [[ -n "${NOCTURNE_DNS_TIMEOUT:-}" ]] && deadline=$((SECONDS + NOCTURNE_DNS_TIMEOUT))
  while ! { resolves_here "$BASE_DOMAIN" && resolves_here "dns-check.$BASE_DOMAIN"; }; do
    if [[ -n "$deadline" ]] && (( SECONDS >= deadline )); then
      log "$BASE_DOMAIN and *.$BASE_DOMAIN still do not point at $PUBLIC_IP"
      return 1
    fi
    log "waiting for DNS: $BASE_DOMAIN and *.$BASE_DOMAIN must point at $PUBLIC_IP"
    sleep 30
  done
}

ensure_firewall
ensure_docker
ensure_bundle
ensure_env
wait_for_dns

log "starting"
if [[ -n "${BUNDLE_CHANGED:-}" ]]; then
  docker compose pull --quiet
fi
docker compose up -d

# Opt-in, and only once. Left until last because it needs the same internet
# access Docker did, and Nocturne itself matters more than the idle workaround.
if [[ -n "${FOLDING:-}" ]] && ! command -v fah-client >/dev/null 2>&1; then
  /usr/local/sbin/nocturne-folding-setup
fi

log "up to date"
GUEST_EOF

cat > "$WORK/nocturne-folding-unpause" <<'GUEST_EOF'
#!/usr/bin/env python3
# The v8 client installs paused and only its websocket API can change that.
import base64, json, os, socket, sys, time

for _ in range(30):
    try:
        s = socket.create_connection(("127.0.0.1", 7396), timeout=5)
        break
    except OSError:
        time.sleep(2)
else:
    sys.exit("fah-client did not open its control port")

key = base64.b64encode(os.urandom(16)).decode()
s.sendall(("GET /api/websocket HTTP/1.1\r\nHost: 127.0.0.1:7396\r\nUpgrade: websocket\r\n"
           "Connection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n" % key).encode())
resp = b""
while b"\r\n\r\n" not in resp:
    chunk = s.recv(4096)
    if not chunk:
        sys.exit("websocket handshake failed")
    resp += chunk
if not resp.startswith(b"HTTP/1.1 101"):
    sys.exit("websocket handshake refused: " + resp.split(b"\r\n")[0].decode())

payload = json.dumps({"cmd": "state", "state": "fold"}).encode()
mask = os.urandom(4)
s.sendall(bytes([0x81, 0x80 | len(payload)]) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))
time.sleep(2)
s.close()
GUEST_EOF

cat > "$WORK/nocturne-folding-setup" <<'GUEST_EOF'
#!/usr/bin/env bash
set -euo pipefail
. /opt/nocturne/install.env

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
GUEST_EOF

# No Requires=docker.service: docker.service does not exist until nocturne-up
# installs it, and an unsatisfiable Requires leaves the unit silently inactive
# rather than failing. After= is ignored when the unit is absent, so ordering
# still applies once Docker is there.
cat > "$WORK/nocturne.service" <<'GUEST_EOF'
[Unit]
Description=Nocturne
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
TimeoutStartSec=infinity
ExecStart=/usr/local/sbin/nocturne-up

[Install]
WantedBy=multi-user.target
GUEST_EOF

# ── Spend alert ──────────────────────────────────────────────────────────────
# Budgets live in the tenancy root regardless of where the resources go.

log "Spend alert"
TENANCY_ID="${OCI_TENANCY:-$COMPARTMENT_ID}"
BUDGET_ID=$(q oci budgets budget budget list --compartment-id "$TENANCY_ID" --display-name "$NAME" --lifecycle-state ACTIVE --query 'data[0].id' --raw-output)
if [[ -n "$BUDGET_ID" ]]; then
  info "exists"
else
  if [[ -z "$BUDGET_EMAIL" && -n "${OCI_CS_USER_OCID:-}" ]]; then
    BUDGET_EMAIL=$(q oci iam user get --user-id "$OCI_CS_USER_OCID" --query 'data.email' --raw-output)
  fi
  if [[ -z "$BUDGET_EMAIL" && -t 0 ]]; then
    read -rp "    Email address for the spend alert: " BUDGET_EMAIL
  fi
  if [[ -z "$BUDGET_EMAIL" ]]; then
    info "skipped; set BUDGET_EMAIL to be told if this account is ever charged"
  elif out=$(oci budgets budget budget create --compartment-id "$TENANCY_ID" --display-name "$NAME" --amount 1 --reset-period MONTHLY \
         --target-type COMPARTMENT --targets "[\"$TENANCY_ID\"]" --query 'data.id' --raw-output 2>&1) \
       && BUDGET_ID="$out" \
       && out=$(oci budgets budget alert-rule create --budget-id "$BUDGET_ID" --display-name "$NAME" --type ACTUAL --threshold 1 --threshold-type ABSOLUTE \
         --recipients "$BUDGET_EMAIL" \
         --message "Your Nocturne server on Oracle Cloud has been charged. Everything the installer creates is within the free tier, so check the Oracle console for what changed." 2>&1); then
    info "$BUDGET_EMAIL will be emailed if this account is ever charged"
  else
    info "could not create the budget alert; you can add one under Billing > Budgets in the console. Oracle said:"
    info "  $(grep -m1 '"message"' <<<"$out" || head -1 <<<"$out")"
  fi
fi

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

# Checking for the default route itself, not for an empty table: a table with
# some other rule in it but no way out is the case that needs repairing most.
RT_ID=$(oci network vcn get --vcn-id "$VCN_ID" --query 'data."default-route-table-id"' --raw-output)
RT_RULES=$(oci network route-table get --rt-id "$RT_ID" --query 'data."route-rules"')
if ! jq -e --arg igw "$IGW_ID" 'map(select(.destination == "0.0.0.0/0" and ."network-entity-id" == $igw)) | length > 0' >/dev/null <<<"$RT_RULES"; then
  # Only the default route is ours to rewrite. This table can carry routes for
  # subnets the installer never created — a service gateway, a NAT gateway — and
  # replacing the whole list would silently cut their egress.
  oci network route-table update --rt-id "$RT_ID" --force --route-rules "$(jq -c --arg igw "$IGW_ID" "$JQ_OCI"'
    [ .[] | select(.destination != "0.0.0.0/0") | recamel ]
    + [{destination: "0.0.0.0/0", destinationType: "CIDR_BLOCK", networkEntityId: $igw}]' <<<"$RT_RULES")" >/dev/null
  info "added default route"
fi

SL_ID=$(oci network vcn get --vcn-id "$VCN_ID" --query 'data."default-security-list-id"' --raw-output)
SL_RULES=$(oci network security-list get --security-list-id "$SL_ID" --query 'data."ingress-security-rules"')
# 80 and 443 have to be reachable from anywhere or certificates cannot be issued.
# 22 only has to be reachable at all: a user who narrowed SSH to their own address
# meant it, and re-opening it to the world behind their back would be worse than
# the SSH wait below failing with a message.
SL_MISSING=$(jq -c "$JQ_OCI"'missing_tcp' <<<"$SL_RULES")
if [[ "$SL_MISSING" != "[]" ]]; then
  oci network security-list update --security-list-id "$SL_ID" --force --ingress-security-rules "$(jq -c "$JQ_OCI"'
    [ .[] | recamel ]
    + [ missing_tcp[] | {protocol: "6", source: "0.0.0.0/0", tcpOptions: {destinationPortRange: {min: ., max: .}}} ]
    + (if any(.protocol == "17") then [] else [{protocol: "17", source: "0.0.0.0/0", udpOptions: {destinationPortRange: {min: 443, max: 443}}}] end)
    + (if any(.protocol == "1") then [] else [{protocol: "1", source: "0.0.0.0/0", icmpOptions: {type: 3, code: 4}},
                                               {protocol: "1", source: "10.0.0.0/16", icmpOptions: {type: 3}}] end)' <<<"$SL_RULES")" >/dev/null
  info "opened ports $(jq -r 'join(", ")' <<<"$SL_MISSING")"
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
PUBLIC_IP_ID=$(q oci network public-ip list --compartment-id "$COMPARTMENT_ID" --scope REGION --lifetime RESERVED --all \
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
SSH_PRIVATE_KEY="${SSH_PUBLIC_KEY_FILE%.pub}"

# ── Instance ─────────────────────────────────────────────────────────────────

install_env() {
  printf 'BASE_DOMAIN=%s\nPUBLIC_IP=%s\nNOCTURNE_VERSION=%s\nFOLDING=%s\nFOLDING_TEAM=%s\n' \
    "$BASE_DOMAIN" "$PUBLIC_IP" "$NOCTURNE_VERSION" "$FOLDING" "$FOLDING_TEAM"
}

log "Instance"
INSTANCE_ID=$(q oci compute instance list --compartment-id "$COMPARTMENT_ID" --display-name "$NAME" --all \
  --query 'data[?"lifecycle-state"!=`"TERMINATED"` && "lifecycle-state"!=`"TERMINATING"`] | [0].id' --raw-output)

if [[ -z "$INSTANCE_ID" ]]; then
  IMAGE_ID=$(oci compute image list --compartment-id "$COMPARTMENT_ID" --operating-system "Canonical Ubuntu" --operating-system-version "24.04" \
    --shape "VM.Standard.A1.Flex" --sort-by TIMECREATED --sort-order DESC --query 'data[0].id' --raw-output)
  [[ -n "$IMAGE_ID" && "$IMAGE_ID" != "null" ]] || die "no Ubuntu 24.04 image for VM.Standard.A1.Flex in $HOME_REGION"

  # runcmd does nothing that needs the network: the instance may have no route
  # out until the reserved IP is attached below. Everything that does is inside
  # nocturne-up, which retries.
  CLOUD_INIT="$WORK/cloud-init.yaml"
  {
    printf '#cloud-config\npackage_update: true\nwrite_files:\n'
    printf "  - path: /opt/nocturne/install.env\n    permissions: '0600'\n    content: |\n"
    install_env | sed 's/^/      /'
    printf "  - path: /usr/local/sbin/nocturne-up\n    permissions: '0755'\n    content: |\n"
    sed 's/^/      /' "$WORK/nocturne-up"
    printf "  - path: /usr/local/sbin/nocturne-folding-unpause\n    permissions: '0755'\n    content: |\n"
    sed 's/^/      /' "$WORK/nocturne-folding-unpause"
    printf "  - path: /usr/local/sbin/nocturne-folding-setup\n    permissions: '0755'\n    content: |\n"
    sed 's/^/      /' "$WORK/nocturne-folding-setup"
    printf "  - path: /etc/systemd/system/nocturne.service\n    content: |\n"
    sed 's/^/      /' "$WORK/nocturne.service"
    printf 'runcmd:\n  - systemctl daemon-reload\n  - systemctl enable nocturne.service\n  - systemctl start --no-block nocturne.service\n'
  } > "$CLOUD_INIT"

  # OCI caps user-data at 16 KB once base64-encoded.
  ENCODED=$(( ( $(wc -c < "$CLOUD_INIT") + 2 ) / 3 * 4 ))
  (( ENCODED < 16384 )) || die "the generated cloud-init is $ENCODED bytes encoded, over Oracle's 16 KB user-data limit"

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
    (( SECONDS < deadline )) || die "no Ampere A1 capacity in any availability domain after $CAPACITY_RETRY_MINUTES minutes. This is common on the free tier: re-run later (everything created so far is reused), or keep trying for longer with CAPACITY_RETRY_MINUTES=180."
    info "retrying in 60 seconds"
    sleep 60
  done
else
  info "instance exists"
fi

instance_state() { oci compute instance get --instance-id "$INSTANCE_ID" --query 'data."lifecycle-state"' --raw-output; }
await_state() {
  local want=$1 deadline=$((SECONDS + 600))
  while [[ "$(instance_state)" != "$want" ]]; do
    (( SECONDS < deadline )) || die "the instance did not reach $want within ten minutes"
    sleep 10
  done
}

STATE=$(instance_state)
case "$STATE" in
  RUNNING) ;;
  PROVISIONING|STARTING) info "instance is $STATE; waiting"; await_state RUNNING ;;
  STOPPING) info "instance is stopping; waiting to start it again"; await_state STOPPED ;&
  STOPPED)
    info "instance is stopped; starting it"
    oci compute instance action --instance-id "$INSTANCE_ID" --action START >/dev/null
    await_state RUNNING ;;
  *) die "the instance is $STATE, which this script cannot work with. Check it in the Oracle console." ;;
esac

VNIC_ID=$(oci compute instance list-vnics --instance-id "$INSTANCE_ID" --query 'data[0].id' --raw-output)
PRIVATE_IP_ID=$(oci network private-ip list --vnic-id "$VNIC_ID" --query 'data[0].id' --raw-output)
if [[ "$(q oci network public-ip get --public-ip-id "$PUBLIC_IP_ID" --query 'data."assigned-entity-id"' --raw-output)" != "$PRIVATE_IP_ID" ]]; then
  oci network public-ip update --public-ip-id "$PUBLIC_IP_ID" --private-ip-id "$PRIVATE_IP_ID" >/dev/null
  info "attached the reserved IP"
fi

# ── Instance setup ───────────────────────────────────────────────────────────
# Runs on every invocation, not just the first. cloud-init only ever runs once,
# so without this a failure inside the instance could never be repaired by
# running this script again.

ssh_run() {
  ssh -i "$SSH_PRIVATE_KEY" -o StrictHostKeyChecking=accept-new -o BatchMode=yes \
      -o ConnectTimeout=10 -o ServerAliveInterval=15 "ubuntu@$PUBLIC_IP" "$@"
}
push_file() {  # push_file <local> <remote path> <mode>
  ssh_run "cat > /tmp/nocturne-push && sudo install -D -m $3 /tmp/nocturne-push $2 && rm -f /tmp/nocturne-push" < "$1"
}

log "Instance setup"
if [[ ! -f "$SSH_PRIVATE_KEY" ]]; then
  info "No private key at $SSH_PRIVATE_KEY, so this script cannot configure the instance."
  info "Copy the files yourself, or re-run without SSH_PUBLIC_KEY_FILE to use a generated key."
else
  info "Waiting for SSH on $PUBLIC_IP"
  deadline=$((SECONDS + 600))
  until ssh_run true </dev/null >/dev/null 2>"$WORK/ssh.err"; do
    if grep -q "REMOTE HOST IDENTIFICATION HAS CHANGED" "$WORK/ssh.err"; then
      # The reserved IP outlived an instance that has since been replaced.
      info "the host key changed (the instance was rebuilt); forgetting the old one"
      ssh-keygen -R "$PUBLIC_IP" >/dev/null 2>&1 || true
    fi
    (( SECONDS < deadline )) || die "could not reach the instance over SSH at $PUBLIC_IP after ten minutes. Check the security list allows port 22 and that the reserved IP is attached."
    sleep 10
  done

  # Stop the boot-time run before replacing any of its files. install(1)
  # truncates in place, and bash reads a running script by file offset, so
  # rewriting nocturne-up underneath a live interpreter resumes it mid-line.
  ssh_run "sudo systemctl stop nocturne" </dev/null >/dev/null 2>&1 || true

  info "Installing the setup scripts"
  install_env > "$WORK/install.env"
  push_file "$WORK/install.env" /opt/nocturne/install.env 0600
  push_file "$WORK/nocturne-up" /usr/local/sbin/nocturne-up 0755
  push_file "$WORK/nocturne-folding-unpause" /usr/local/sbin/nocturne-folding-unpause 0755
  push_file "$WORK/nocturne-folding-setup" /usr/local/sbin/nocturne-folding-setup 0755
  push_file "$WORK/nocturne.service" /etc/systemd/system/nocturne.service 0644
  ssh_run "sudo systemctl daemon-reload && sudo systemctl enable nocturne.service" </dev/null 2>&1 | sed 's/^/    /' || true

  log "Configuring the instance"
  info "Docker, the Nocturne $NOCTURNE_VERSION bundle, secrets, then the stack. Safe to repeat."
  printf '\n'
  if ssh_run "sudo env NOCTURNE_DNS_TIMEOUT=$((DNS_WAIT_MINUTES * 60)) /usr/local/sbin/nocturne-up" </dev/null 2>&1 | sed 's/^/    /'; then
    # --no-block, because systemd's own run of nocturne-up has no DNS deadline:
    # a blocking start could sit here for ever with its output going nowhere.
    ssh_run "sudo systemctl start --no-block nocturne" </dev/null >/dev/null 2>&1 || true
  else
    printf '\n'
    info "The instance did not finish configuring itself. Nothing is lost: fix what it reported"
    info "and run this script again, which resumes from wherever it stopped."
    info "  ssh -i ${SSH_PRIVATE_KEY/#$HOME/\~} ubuntu@$PUBLIC_IP sudo /usr/local/sbin/nocturne-up"
  fi
fi

# ── Spend cap ────────────────────────────────────────────────────────────────
# A budget only sends email. A quota policy actually refuses the request, so an
# account that gets upgraded to Pay As You Go later — the usual cure for Ampere
# capacity errors — still cannot be billed for a resource nobody meant to create.
# Each shape has both a per-availability-domain quota and a regional one; the
# regional pair is what makes this a real total rather than a per-domain cap, and
# leaving them zeroed by the wildcard would block A1 launches outright.
#
# Written after the instance exists so a policy Oracle rejects, or one naming a
# quota that has been renamed, can never block the launch it is meant to protect.

log "Spend cap"
QUOTA_STATEMENTS=(
  "zero compute-core quota /*/ in tenancy"
  "set compute-core quota standard-a1-core-count to $FREE_OCPUS in tenancy"
  "set compute-core quota standard-a1-core-regional-count to $FREE_OCPUS in tenancy"
  "set compute-core quota standard-e2-micro-core-count to $FREE_E2_MICRO_COUNT in tenancy"
  "zero compute-memory quota /*/ in tenancy"
  "set compute-memory quota standard-a1-memory-count to $FREE_MEMORY_GB in tenancy"
  "set compute-memory quota standard-a1-memory-regional-count to $FREE_MEMORY_GB in tenancy"
)
QUOTA_JSON=$(printf '%s
' "${QUOTA_STATEMENTS[@]}" | jq -Rsc 'split("
") | map(select(length > 0))')

quota_error() {
  info "$1 Nothing is enforced, so watch the spend alert instead. Oracle said:"
  info "  $(grep -m1 '"message"' <<<"$2" || head -1 <<<"$2")"
}

# The policy is written "in tenancy", so this has to see every compartment. An
# instance list is not recursive, so walk the tree rather than trusting the root.
OTHER_INSTANCES=0
while read -r cid; do
  [[ -n "$cid" ]] || continue
  found=$(q oci compute instance list --compartment-id "$cid" --all \
    --query "length(data[?\"lifecycle-state\"!=\`\"TERMINATED\"\` && \"display-name\"!='$NAME'])" --raw-output)
  OTHER_INSTANCES=$((OTHER_INSTANCES + ${found:-0}))
done < <(
  printf '%s\n' "$TENANCY_ID"
  q oci iam compartment list --compartment-id "$TENANCY_ID" --compartment-id-in-subtree true \
    --lifecycle-state ACTIVE --all --query 'data[].id' --raw-output | jq -r '.[]? // empty' 2>/dev/null
)

if [[ -n "$ALLOW_PAID" ]]; then
  info "skipped: ALLOW_PAID is set, so this tenancy is allowed to create paid resources"
elif [[ "$COMPARTMENT_ID" != "$TENANCY_ID" ]]; then
  info "skipped: Nocturne is in a compartment, and a tenancy-wide quota is yours to decide on"
elif [[ -n "$OTHER_INSTANCES" && "$OTHER_INSTANCES" != "0" ]]; then
  info "skipped: this tenancy runs $OTHER_INSTANCES other instance(s) and the policy would restrict them too"
else
  QUOTA_ID=$(q oci limits quota list --compartment-id "$TENANCY_ID" --name "$NAME" --query 'data[0].id' --raw-output)
  # Both sides through jq: the CLI renders the statement list indented, so
  # comparing it to the compact form would rewrite the policy on every run.
  CURRENT_QUOTA=$(q oci limits quota get --quota-id "${QUOTA_ID:-none}" --query 'data.statements' | jq -c . 2>/dev/null || true)
  if [[ -z "$QUOTA_ID" ]]; then
    if out=$(oci limits quota create --compartment-id "$TENANCY_ID" --name "$NAME" \
        --description "Holds this tenancy to the Oracle Always Free allowance" \
        --statements "$QUOTA_JSON" 2>&1); then
      info "this tenancy can now only create Always Free compute, upgraded to Pay As You Go or not"
      info "(new quota policies take up to ten minutes to take effect)"
    else
      quota_error "could not create the quota policy." "$out"
    fi
  elif [[ "$CURRENT_QUOTA" == "$QUOTA_JSON" ]]; then
    info "exists"
  elif out=$(oci limits quota update --quota-id "$QUOTA_ID" --statements "$QUOTA_JSON" --force 2>&1); then
    info "updated to the current Always Free allowance"
  else
    quota_error "could not update the quota policy." "$out"
  fi
fi

# ── Wait for Nocturne ────────────────────────────────────────────────────────

log "Waiting for https://$BASE_DOMAIN"
info "Certificates are issued on the first request, which takes a minute or so."
deadline=$((SECONDS + 10 * 60))
until curl -fsS -o /dev/null --max-time 10 "https://$BASE_DOMAIN/api/v1/status.json"; do
  if (( SECONDS >= deadline )); then
    printf '\n'
    info "Not answering yet. That is expected if the DNS records are new. Check again with:"
    info "  curl https://$BASE_DOMAIN/api/v1/status.json"
    info "or re-run this script, which is safe and repairs anything that did not finish."
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
info "Fix:   re-run this script. It repairs the instance as well as the cloud resources."
printf '\n'
info "Back up the SSH key. It exists only in this Cloud Shell home, which Oracle deletes after six months"
info "without a Cloud Shell session. Nocturne keeps running without it, but you lose the way onto the server."
info "Cloud Shell menu (top left of this terminal) > Download, then enter:  .ssh/nocturne_oci"

INSTANCE_KEY=$(ssh -i "$SSH_PRIVATE_KEY" -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o ConnectTimeout=10 \
  "ubuntu@$PUBLIC_IP" "sudo sed -n 's/^INSTANCE_KEY=//p' /opt/nocturne/.env" </dev/null 2>/dev/null || true)
printf '\n'
if [[ -n "$INSTANCE_KEY" ]]; then
  info "Your instance key. It is the master credential for this installation, used for administrative"
  info "API access and account recovery. Save it in a password manager now; it is shown again on re-runs."
  printf '\n      %s\n' "$INSTANCE_KEY"
else
  info "The instance key was not readable yet. Re-run this script later to see it, or read it with:"
  info "  ssh -i ~/.ssh/nocturne_oci ubuntu@$PUBLIC_IP sudo grep ^INSTANCE_KEY= /opt/nocturne/.env"
fi
