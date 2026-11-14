#!/usr/bin/env bash
# Nocturne on a Linux server that has systemd. Run as root on the server:
#
#   curl -fsSL https://github.com/nightscout/nocturne/releases/latest/download/install.sh -o install.sh
#   sudo BASE_DOMAIN=nocturne.example.com bash install.sh
#
# Installs Docker when it is absent, downloads the Nocturne release bundle to
# /opt/nocturne, generates the keys and passwords, waits for DNS and starts the
# stack. It installs itself as /usr/local/sbin/nocturne-up with a systemd unit,
# so the stack comes back after a reboot.
#
# Re-running is the answer to any failure: every step checks before it acts.
#
# Optional environment:
#   BASE_DOMAIN        the domain Nocturne answers on (asked for when unset; remembered)
#   DUCKDNS_TOKEN      DuckDNS account token, for a BASE_DOMAIN under duckdns.org; the record
#                      is then set for you (asked for, hidden, when unset and run interactively)
#   PUBLIC_IP          this server's public IPv4 address (default: detected)
#   NOCTURNE_VERSION   release bundle to install (default: latest). Selects the compose
#                      file, not the container images: those are pinned in .env and kept
#                      current by Watchtower, so this cannot pin you to an older build
#   DNS_WAIT_MINUTES   how long to wait for your DNS records before handing over to the
#                      systemd unit, which keeps waiting (default 10)
#
# Modes:
#   (none)        install or repair, as described above
#   --converge    bring the server to the state in /opt/nocturne/install.env and ask nothing.
#                 This is what the systemd unit runs.
#
# Another installer can use this file as a library: with NOCTURNE_INSTALL_LIB set,
# sourcing it defines the functions and runs nothing.

set -euo pipefail

INSTALL_DIR=/opt/nocturne
INSTALL_ENV="$INSTALL_DIR/install.env"
SELF_PATH=/usr/local/sbin/nocturne-up
UNIT_PATH=/etc/systemd/system/nocturne.service
RELEASES=https://github.com/nightscout/nocturne/releases

# The secret parameters of the Aspire AppHost that a deployment form shows.
GENERATED_SECRETS=(@@GENERATED_SECRETS@@)

log()  { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\n\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }
warn() { printf '\n\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }

# ── Asking ───────────────────────────────────────────────────────────────────
# Piped into bash, stdin is the script, so questions go to the terminal itself.

ask_source() {
  if [[ -t 0 ]]; then
    printf '/dev/stdin'
  elif { : < /dev/tty; } 2>/dev/null; then
    printf '/dev/tty'
  fi
}
can_ask() { [[ -n "$(ask_source)" ]]; }

# ── Domain and release ───────────────────────────────────────────────────────

# Sets BASE_DOMAIN to its lower-case form, or stops.
normalise_domain() {
  BASE_DOMAIN="${BASE_DOMAIN,,}"
  [[ "$BASE_DOMAIN" =~ ^([a-z0-9-]+\.)+[a-z]{2,}$ ]] \
    || die "BASE_DOMAIN must be a domain name with at least two labels, not an IP address"
}

latest_version() {
  # The redirect target of /releases/latest names the tag without touching the rate-limited API.
  local version
  version=$(curl -fsS -o /dev/null -w '%{redirect_url}' "$RELEASES/latest" | sed 's|.*/tag/||')
  [[ "$version" == v* ]] || die "could not look up the latest Nocturne release; set NOCTURNE_VERSION"
  printf '%s' "$version"
}

detect_public_ip() {
  local url ip
  for url in https://checkip.amazonaws.com https://api.ipify.org https://ipv4.icanhazip.com; do
    ip=$(curl -4fsS --max-time 10 "$url" 2>/dev/null | tr -d '[:space:]') || continue
    if [[ "$ip" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
      printf '%s' "$ip"
      return 0
    fi
  done
  return 1
}

# ── DNS ──────────────────────────────────────────────────────────────────────
# These read BASE_DOMAIN and PUBLIC_IP.

resolves_to_ip() { getent ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | grep -qx "$PUBLIC_IP"; }

# The second name stands for the wildcard record.
dns_ready() { resolves_to_ip "$BASE_DOMAIN" && resolves_to_ip "dns-check.$BASE_DOMAIN"; }

# DuckDNS answers every name below yourname.duckdns.org with yourname's address,
# so one record covers the apex and the wildcard, and BASE_DOMAIN may sit at any
# depth under it. Prints nothing for a domain that is not under duckdns.org.
duckdns_name() {
  [[ "$BASE_DOMAIN" == *.duckdns.org ]] || return 0
  local name="${BASE_DOMAIN%.duckdns.org}"
  printf '%s' "${name##*.}"
}

# Returns 0 on OK, 1 on KO, 2 when DuckDNS could not be reached.
duckdns_update() {
  local out
  out=$(curl -sS --max-time 30 -G "https://www.duckdns.org/update" \
    --data-urlencode "domains=$(duckdns_name)" --data-urlencode "token=$DUCKDNS_TOKEN" "$@" 2>/dev/null) || return 2
  [[ "$out" == OK* ]]
}

# Points the name at this server, or says why it could not.
duckdns_records() {
  local status=0
  if [[ ! "$DUCKDNS_TOKEN" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
    warn "that is not a DuckDNS token. It is the value labelled token at the top of duckdns.org once you are signed in, shaped like 01234567-89ab-cdef-0123-456789abcdef."
    return 1
  fi
  # A name that once pointed at a home connection may still carry an IPv6
  # address, which certificate validation would follow instead of this server.
  duckdns_update --data-urlencode "clear=true" || status=$?
  if (( status == 0 )); then
    duckdns_update --data-urlencode "ip=$PUBLIC_IP" || status=$?
  fi
  case "$status" in
    0) info "$BASE_DOMAIN and *.$BASE_DOMAIN point at $PUBLIC_IP" ;;
    2) warn "could not reach duckdns.org." ; return 1 ;;
    *) warn "DuckDNS refused the update. Check the token is the one shown on duckdns.org, and that $(duckdns_name) is listed under your domains there."; return 1 ;;
  esac
}

# Sets the records where it can, and otherwise prints the ones to create by hand.
# Never fails: the caller waits for DNS afterwards either way.
setup_dns() {
  local name source
  name=$(duckdns_name)
  source=$(ask_source)

  if dns_ready; then
    info "DNS already points at $PUBLIC_IP"
    return 0
  fi

  # A token that turns out to be wrong is worth another go rather than an abort: it
  # is the one thing here the user can fix in a few seconds.
  while [[ -n "$name" ]]; do
    if [[ -z "${DUCKDNS_TOKEN:-}" ]]; then
      [[ -n "$source" ]] || break
      printf '\n'
      info "Paste your DuckDNS token (shown at the top of duckdns.org once signed in) and the DNS"
      info "record for $name.duckdns.org is set for you. Otherwise press Enter to set it yourself."
      # Nothing echoes as it is typed. In a browser terminal a paste that did not
      # land looks exactly like one that did, so say what was read: without that
      # the next thing on screen is an unexplained failure.
      read -rsp "    DuckDNS token: " DUCKDNS_TOKEN < "$source"
      printf '\n'
      DUCKDNS_TOKEN="${DUCKDNS_TOKEN//[[:space:]]/}"
      [[ -n "$DUCKDNS_TOKEN" ]] || break
      info "read a ${#DUCKDNS_TOKEN}-character token"
    fi
    log "DNS record at DuckDNS"
    if duckdns_records; then
      return 0
    fi
    DUCKDNS_TOKEN=
    [[ -n "$source" ]] || break
  done

  printf '\n'
  if [[ -n "$name" ]]; then
    info "At duckdns.org, set the current ip of $name to $PUBLIC_IP now."
  else
    info "Create these two DNS records at your domain provider now:"
    printf '\n      %-28s A   %s\n      %-28s A   %s\n\n' "$BASE_DOMAIN" "$PUBLIC_IP" "*.$BASE_DOMAIN" "$PUBLIC_IP"
    info "Do not proxy them through Cloudflare."
  fi
  info "Nocturne waits until $BASE_DOMAIN and *.$BASE_DOMAIN resolve before requesting certificates."
}

# ── Settings kept on the server ──────────────────────────────────────────────

install_env() {
  printf 'BASE_DOMAIN=%s\nPUBLIC_IP=%s\nNOCTURNE_VERSION=%s\n' "$BASE_DOMAIN" "$PUBLIC_IP" "$NOCTURNE_VERSION"
}

# A value the caller set wins over the remembered one.
load_install_env() {
  [[ -f "$INSTALL_ENV" ]] || return 0
  local line key
  while IFS= read -r line; do
    key=${line%%=*}
    [[ "$key" =~ ^[A-Z_][A-Z0-9_]*$ ]] || continue
    [[ -n "${!key:-}" ]] || printf -v "$key" '%s' "${line#*=}"
  done < "$INSTALL_ENV"
}

# Keeps the settings another installer wrote there, e.g. Oracle Cloud's.
save_install_env() {
  local kept=""
  [[ -f "$INSTALL_ENV" ]] && kept=$(grep -vE '^(BASE_DOMAIN|PUBLIC_IP|NOCTURNE_VERSION)=' "$INSTALL_ENV" || true)
  { install_env; [[ -z "$kept" ]] || printf '%s\n' "$kept"; } > "$INSTALL_ENV.new"
  chmod 600 "$INSTALL_ENV.new"
  mv "$INSTALL_ENV.new" "$INSTALL_ENV"
}

# No Requires=docker.service: docker.service does not exist until this script
# installs it, and an unsatisfiable Requires leaves the unit silently inactive
# rather than failing. After= is ignored when the unit is absent, so ordering
# still applies once Docker is there.
unit_file() {
  cat <<UNIT_EOF
[Unit]
Description=Nocturne
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
TimeoutStartSec=infinity
ExecStart=$SELF_PATH --converge

[Install]
WantedBy=multi-user.target
UNIT_EOF
}

# Renames into place: bash reads a running script by file offset, so a file
# rewritten underneath a live interpreter resumes it mid-line.
install_self() {
  local source="${BASH_SOURCE[0]}"
  if [[ -f "$source" ]]; then
    if [[ "$(readlink -f "$source")" != "$SELF_PATH" ]]; then
      install -m 755 "$source" "$SELF_PATH.new"
      mv "$SELF_PATH.new" "$SELF_PATH"
    fi
  else
    # Piped into bash, there is no file to copy.
    curl -fsSL -o "$SELF_PATH.new" "$RELEASES/download/$NOCTURNE_VERSION/install.sh" \
      || die "could not download install.sh for $NOCTURNE_VERSION to keep on this server"
    chmod 755 "$SELF_PATH.new"
    mv "$SELF_PATH.new" "$SELF_PATH"
  fi
  unit_file > "$UNIT_PATH"
  systemctl daemon-reload
  systemctl enable nocturne.service >/dev/null 2>&1
}

# ── The server's state ───────────────────────────────────────────────────────

ensure_firewall() {
  # Oracle's Ubuntu image rejects inbound traffic other than SSH in the instance's
  # own iptables rules, independently of the VCN security list.
  local rules=/etc/iptables/rules.v4 changed="" rule
  if [[ -f "$rules" ]] && grep -q -- '-A INPUT -j REJECT' "$rules"; then
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
      info "opened ports 80 and 443"
    fi
  fi

  if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then
    ufw allow 80/tcp >/dev/null
    ufw allow 443/tcp >/dev/null
    ufw allow 443/udp >/dev/null
  fi
}

# A new cloud server can be without a route to the internet for its first
# minutes. Retry rather than fail, so a boot-time run heals itself.
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
      info "installed Docker"
      return 0
    fi
    info "could not install Docker (attempt $try); this server may have no internet access yet. Retrying in 30s"
    sleep 30
  done
  warn "giving up on installing Docker. Check that this server can reach the internet."
  return 1
}

ensure_bundle() {
  local want="$NOCTURNE_VERSION" have="" try
  [[ -f .bundle-version ]] && have=$(cat .bundle-version)
  if [[ -f docker-compose.yaml && "$have" == "$want" ]]; then
    return 0
  fi
  info "downloading Nocturne $want"
  local base="$RELEASES/download/$want"
  for try in $(seq 1 20); do
    if curl -fsSL -o docker-compose.yaml.new "$base/docker-compose.yaml" \
       && curl -fsSL -o default.env.example.new "$base/default.env.example"; then
      mv docker-compose.yaml.new docker-compose.yaml
      mv default.env.example.new default.env.example
      printf '%s\n' "$want" > .bundle-version
      BUNDLE_CHANGED=1
      return 0
    fi
    info "download failed (attempt $try); retrying in 30s"
    sleep 30
  done
  rm -f docker-compose.yaml.new default.env.example.new
  warn "could not download the Nocturne $want release bundle"
  return 1
}

set_env() { sed -i "s|^$1=.*|$1=$2|" .env; }
get_env() { sed -n "s|^$1=||p" .env; }

ensure_env() {
  if [[ ! -f .env ]]; then
    install -m 600 default.env.example .env
    info "created .env"
  fi

  # Settings the release added since this .env was written, so an upgrade does
  # not leave the stack missing values it now expects.
  local line key
  while IFS= read -r line; do
    key=${line%%=*}
    if ! grep -q "^${key}=" .env; then
      printf '%s\n' "$line" >> .env
      info "added new setting $key"
    fi
  done < <(grep -E '^[A-Za-z_][A-Za-z0-9_]*=' default.env.example)

  set_env BASE_DOMAIN "$BASE_DOMAIN"

  # Only ever fill in a blank. Regenerating a password that Postgres has already
  # initialised itself with would lock Nocturne out of its own database.
  for key in "${GENERATED_SECRETS[@]}"; do
    grep -q "^${key}=" .env || printf '%s=\n' "$key" >> .env
    if [[ -z "$(get_env "$key")" ]]; then
      set_env "$key" "$(openssl rand -hex 24)"
      info "generated $key"
    fi
  done
  chmod 600 .env

  for key in BASE_DOMAIN "${GENERATED_SECRETS[@]}"; do
    [[ -n "$(get_env "$key")" ]] || { warn "$key is empty in .env and could not be filled in"; return 1; }
  done
}

wait_for_dns() {
  # Unset means wait forever, which is what the boot-time run wants: the server
  # should come up whenever the records eventually appear.
  local deadline=""
  [[ -n "${NOCTURNE_DNS_TIMEOUT:-}" ]] && deadline=$((SECONDS + NOCTURNE_DNS_TIMEOUT))
  while ! dns_ready; do
    if [[ -n "$deadline" ]] && (( SECONDS >= deadline )); then
      info "$BASE_DOMAIN and *.$BASE_DOMAIN still do not point at $PUBLIC_IP"
      return 1
    fi
    info "waiting for DNS: $BASE_DOMAIN and *.$BASE_DOMAIN must point at $PUBLIC_IP"
    sleep 30
  done
}

# Brings this server to the state described by install.env. Safe to run at any
# time, as often as you like.
converge() {
  [[ -f "$INSTALL_ENV" ]] || die "$INSTALL_ENV does not exist. Run this script without --converge first."
  # shellcheck disable=SC1090
  . "$INSTALL_ENV"
  cd "$INSTALL_DIR"

  # Serialise against the boot-time run of this same script.
  exec 9>/var/lock/nocturne-up.lock
  flock -w 600 9 || die "another nocturne-up is already running"

  ensure_firewall
  ensure_docker
  ensure_bundle
  ensure_env
  wait_for_dns

  info "starting"
  if [[ -n "${BUNDLE_CHANGED:-}" ]]; then
    docker compose pull --quiet
  fi
  docker compose up -d

  # What another installer adds to this one. Left until last: Nocturne itself
  # matters more than anything that rides along with it.
  local hook
  for hook in "$INSTALL_DIR"/post-up.d/*; do
    if [[ -x "$hook" ]]; then
      "$hook" || warn "$hook failed"
    fi
  done

  info "up to date"
}

# ── Install ──────────────────────────────────────────────────────────────────

require_server() {
  [[ $EUID -eq 0 ]] || die "run this as root: sudo bash install.sh"
  local tool missing=()
  for tool in curl openssl flock getent systemctl; do
    command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
  done
  [[ ${#missing[@]} -eq 0 ]] || die "this server lacks: ${missing[*]}. Install them and run this again."
}

run_install() {
  require_server
  mkdir -p "$INSTALL_DIR"

  local requested_version="${NOCTURNE_VERSION:-}" source
  load_install_env
  source=$(ask_source)

  if [[ -z "${BASE_DOMAIN:-}" ]]; then
    [[ -n "$source" ]] || die "set BASE_DOMAIN to the domain Nocturne should answer on"
    read -rp "Domain Nocturne should answer on (e.g. nocturne.example.com): " BASE_DOMAIN < "$source"
  fi
  normalise_domain

  # A run by hand moves to the latest release; only the boot-time run stays put.
  NOCTURNE_VERSION="$requested_version"
  [[ -n "$NOCTURNE_VERSION" ]] || NOCTURNE_VERSION=$(latest_version)

  if [[ -z "${PUBLIC_IP:-}" ]]; then
    PUBLIC_IP=$(detect_public_ip) || die "could not find this server's public IP address; set PUBLIC_IP"
  fi

  log "Nocturne $NOCTURNE_VERSION on $BASE_DOMAIN, at $PUBLIC_IP"

  log "DNS"
  setup_dns

  log "Setup scripts"
  systemctl stop nocturne.service >/dev/null 2>&1 || true
  save_install_env
  install_self
  info "installed $SELF_PATH and the nocturne systemd unit"

  log "Configuring the server"
  info "Docker, the Nocturne $NOCTURNE_VERSION bundle, secrets, then the stack. Safe to repeat."
  if NOCTURNE_DNS_TIMEOUT=$(( ${DNS_WAIT_MINUTES:-10} * 60 )) "$SELF_PATH" --converge; then
    wait_for_nocturne
  else
    printf '\n'
    info "The server did not finish configuring itself. Nothing is lost: fix what it reported"
    info "and run this script again, which resumes from wherever it stopped."
  fi
  # --no-block, because the unit's own run has no DNS deadline: a blocking start
  # could sit here for ever.
  systemctl start --no-block nocturne.service >/dev/null 2>&1 || true

  summary
}

wait_for_nocturne() {
  log "Waiting for https://$BASE_DOMAIN"
  info "Certificates are issued on the first request, which takes a minute or so."
  local deadline=$((SECONDS + 10 * 60))
  # Asked of this server directly: a home router often cannot reach its own public address.
  until curl -fsS -o /dev/null --max-time 10 --resolve "$BASE_DOMAIN:443:127.0.0.1" "https://$BASE_DOMAIN/api/v1/status.json"; do
    if (( SECONDS >= deadline )); then
      printf '\n'
      info "Not answering yet. That is expected if the DNS records are new, or if ports 80 and 443"
      info "do not reach this server from the internet. Check again with:"
      info "  curl https://$BASE_DOMAIN/api/v1/status.json"
      info "or re-run this script, which is safe and repairs anything that did not finish."
      return 0
    fi
    sleep 15
  done
}

summary() {
  local instance_key=""
  [[ -f "$INSTALL_DIR/.env" ]] && instance_key=$(sed -n 's/^INSTANCE_KEY=//p' "$INSTALL_DIR/.env")

  log "Done"
  info "Open https://$BASE_DOMAIN to create your first site and passkey."
  info "Files: $INSTALL_DIR. Database passwords are in .env; Nocturne generated them and only it needs them."
  info "Logs:  sudo journalctl -u nocturne   and   cd $INSTALL_DIR && sudo docker compose logs"
  info "Fix:   sudo $SELF_PATH   It is this script, kept on the server."
  printf '\n'
  if [[ -n "$instance_key" ]]; then
    info "Your instance key. It is the master credential for this installation, used for administrative"
    info "API access and account recovery. Save it in a password manager now; it is shown again on re-runs."
    printf '\n      %s\n' "$instance_key"
  else
    info "The instance key does not exist yet. Run this script again later to see it."
  fi
}

if [[ -z "${NOCTURNE_INSTALL_LIB:-}" ]]; then
  case "${1:-}" in
    --converge) converge ;;
    "") run_install ;;
    *) die "unknown option $1" ;;
  esac
fi
