#!/usr/bin/env bash
# Deltanium Store node installer for Linux (systemd) and macOS (launchd).
#
#   sudo ./install.sh                                   asks a few questions
#   sudo ./install.sh --network testnet --voucher XXXX  no questions for what you pass
#   sudo ./install.sh --uninstall                       remove the service (keeps your data and node key)
#
# The node runs as a service, reachable by the network over DLTP (TCP) on your public IP: no domain, certificate or
# reverse proxy needed. The node key stays in the data folder (readable by its service account only).
# Forward TCP port 7333 (or --dltp-port) on your router to this computer.
set -euo pipefail

ROLE="Store"; TITLE="Store"; EXE="deltanium-store"; HTTP_PORT=5001
NETWORK=""; VOUCHER=""; TCP_ENDPOINT="auto"; HTTPS_ENDPOINT=""; DLTP_PORT=7333; NONINTERACTIVE=0; UNINSTALL=0
TCP_GIVEN=0
while [ $# -gt 0 ]; do
  case "$1" in
    --network) NETWORK="$2"; shift 2;;
    --voucher) VOUCHER="$2"; shift 2;;
    --tcp-endpoint) TCP_ENDPOINT="$2"; TCP_GIVEN=1; shift 2;;
    --https-endpoint) HTTPS_ENDPOINT="$2"; shift 2;;
    --dltp-port) DLTP_PORT="$2"; shift 2;;
    --http-port) HTTP_PORT="$2"; shift 2;;
    --non-interactive|-y) NONINTERACTIVE=1; shift;;
    --uninstall) UNINSTALL=1; shift;;
    -h|--help) sed -n '2,10p' "$0"; exit 0;;
    *) echo "Unknown option: $1" >&2; exit 2;;
  esac
done

[ "$(id -u)" = 0 ] || { echo "Run with sudo." >&2; exit 1; }
OS="$(uname -s)"
LOWER="$(echo "$ROLE" | tr '[:upper:]' '[:lower:]')"
if [ "$OS" = "Darwin" ]; then
  INSTALL_DIR="/usr/local/deltanium-$LOWER"; DATA_DIR="/Library/Application Support/Deltanium/$ROLE"
  LABEL="com.deltanium.$LOWER"; PLIST="/Library/LaunchDaemons/$LABEL.plist"
else
  INSTALL_DIR="/opt/deltanium-$LOWER"; DATA_DIR="/var/lib/deltanium-$LOWER"
  UNIT="deltanium-$LOWER"; SVC_USER="deltanium"
fi
HERE="$(cd "$(dirname "$0")" && pwd)"

ask() { # ask "question" default
  if [ "$NONINTERACTIVE" = 1 ]; then echo "$2"; return; fi
  local a; read -r -p "$1${2:+ [$2]}: " a </dev/tty || true; echo "${a:-$2}"
}

if [ "$UNINSTALL" = 1 ]; then
  if [ "$OS" = "Darwin" ]; then launchctl bootout system "$PLIST" 2>/dev/null || true; rm -f "$PLIST"
  else systemctl disable --now "$UNIT" 2>/dev/null || true; rm -f "/etc/systemd/system/$UNIT.service"; systemctl daemon-reload; fi
  echo "Service removed. Your data and node key are still in $DATA_DIR."
  exit 0
fi

echo; echo "Deltanium $TITLE node setup"; echo "------------------------------"
[ -n "$NETWORK" ] || NETWORK="$(ask 'Network (mainnet / testnet)' mainnet)"
case "$NETWORK" in mainnet) CENTRAL="https://api.deltanium.com";; testnet) CENTRAL="https://testnet-api.deltanium.com";; *) echo "Network must be mainnet or testnet" >&2; exit 2;; esac

if [ ! -f "$DATA_DIR/node_info.json" ] && [ -z "$VOUCHER" ]; then
  VOUCHER="$(ask 'One-shot voucher (see https://github.com/dlvstudio/deltanium/blob/main/VOUCHERS.md)' '')"
  [ -n "$VOUCHER" ] || { echo "A voucher is needed the first time a node registers." >&2; exit 2; }
fi
if [ "$NONINTERACTIVE" = 0 ] && [ "$TCP_GIVEN" = 0 ]; then
  TCP_ENDPOINT="$(ask "DLTP address: 'auto' (your public IP), 'tcps://<public IP>:<port>', or 'off'" auto)"
fi

echo "Reading the network keys from $CENTRAL ..."
KEYS="$(curl -fsS --max-time 30 "$CENTRAL/api/explorer/keys")"
json() { printf '%s' "$1" | python3 -c "import json,sys; d=json.load(sys.stdin); print($2)"; }
CENTRAL_KEY="$(json "$KEYS" "d['central']['publicKey']")"
SUPER_KEY="$(json "$KEYS" "d['superAdmin']['publicKey']")"
echo "  Central API key : $CENTRAL_KEY"
echo "  Super Admin key : $SUPER_KEY"
echo "  Compare them with https://deltanium.com/nodes.html and NODE_KEYS.md before you continue."
if [ "$NONINTERACTIVE" = 0 ]; then case "$(ask 'Keys match? (y/n)' y)" in y|Y|yes) ;; *) echo "Stopped."; exit 1;; esac; fi

case "$TCP_ENDPOINT" in off) DLTP="";; auto) DLTP="tcps://auto:$DLTP_PORT";; *) DLTP="$TCP_ENDPOINT";; esac

mkdir -p "$INSTALL_DIR" "$DATA_DIR"
cp -R "$HERE"/. "$INSTALL_DIR"/; rm -f "$INSTALL_DIR/install.ps1"
chmod +x "$INSTALL_DIR/$EXE"
ENV_FILE="$DATA_DIR/node.env"
{
  echo "DELTANIUM_HOME=$DATA_DIR"
  echo "Storage__Provider=Json"
  if [ "$NETWORK" = testnet ]; then echo "NETWORK_ID=deltanium-test"; fi
  if [ -n "$DLTP" ]; then echo "Dltp__Port=$DLTP_PORT"; echo "Dltp__PublicEndpoint=$DLTP"; fi
  echo "NodeRegistration__ApiUrl=$CENTRAL"
  echo "CentralApi__BaseUrl=$CENTRAL"
  echo "CENTRAL_PUBLIC_KEY=$CENTRAL_KEY"
  echo "Storage__BasePath=$DATA_DIR/FileStorage"
  if [ -n "$VOUCHER" ]; then echo "STORE_VOUCHER=$VOUCHER"; fi
  if [ -n "$HTTPS_ENDPOINT" ]; then echo "NodeRegistration__Endpoint=$HTTPS_ENDPOINT"; fi
  # The Store sends post transactions to a Block Creator: take one the network lists.
  BL="$(curl -fsS --max-time 30 "$CENTRAL/api/blockcreatornode/list" || echo '[]')"
  json "$BL" "next(('Blockchain__BlockerApi=%s\\nBlockchain__BlockerPublicKey=%s' % (n['endpoint'], n['publicKey']) for n in d if n.get('endpoint')), '')"
} > "$ENV_FILE"
chmod 600 "$ENV_FILE"

if [ "$OS" = "Darwin" ]; then
  /usr/bin/xattr -dr com.apple.quarantine "$INSTALL_DIR" 2>/dev/null || true
  {
    echo '<?xml version="1.0" encoding="UTF-8"?>'
    echo '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">'
    echo '<plist version="1.0"><dict>'
    echo "<key>Label</key><string>$LABEL</string>"
    echo "<key>ProgramArguments</key><array><string>$INSTALL_DIR/$EXE</string><string>--urls</string><string>http://127.0.0.1:$HTTP_PORT</string></array>"
    echo "<key>WorkingDirectory</key><string>$DATA_DIR</string>"
    echo '<key>EnvironmentVariables</key><dict>'
    while IFS='=' read -r k v; do [ -n "$k" ] && echo "<key>$k</key><string>$v</string>"; done < "$ENV_FILE"
    echo '</dict>'
    echo '<key>RunAtLoad</key><true/><key>KeepAlive</key><true/>'
    echo "<key>StandardOutPath</key><string>$DATA_DIR/service.log</string><key>StandardErrorPath</key><string>$DATA_DIR/service.log</string>"
    echo '</dict></plist>'
  } > "$PLIST"
  chmod 700 "$DATA_DIR"
  launchctl bootout system "$PLIST" 2>/dev/null || true
  launchctl bootstrap system "$PLIST"
else
  id -u "$SVC_USER" >/dev/null 2>&1 || useradd --system --home-dir /var/lib/deltanium --shell /usr/sbin/nologin "$SVC_USER"
  chown -R "$SVC_USER:$SVC_USER" "$DATA_DIR"; chmod 700 "$DATA_DIR"
  cat > "/etc/systemd/system/$UNIT.service" <<UNITEOF
[Unit]
Description=Deltanium $TITLE node
After=network-online.target
Wants=network-online.target

[Service]
User=$SVC_USER
WorkingDirectory=$DATA_DIR
EnvironmentFile=$ENV_FILE
ExecStart=$INSTALL_DIR/$EXE --urls http://127.0.0.1:$HTTP_PORT
Restart=always
RestartSec=5
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
UNITEOF
  systemctl daemon-reload
  systemctl enable --now "$UNIT"
  if command -v ufw >/dev/null 2>&1 && [ -n "$DLTP" ]; then ufw allow "$DLTP_PORT/tcp" >/dev/null || true; fi
fi

echo "Starting ..."
for i in $(seq 1 30); do
  sleep 2
  if V="$(curl -fsS --max-time 5 "http://127.0.0.1:$HTTP_PORT/api/version" 2>/dev/null)"; then
    echo "Node running: $V" | cut -c1-120
    PK=""
    [ -f "$DATA_DIR/node_info.json" ] && PK="$(json "$(cat "$DATA_DIR/node_info.json")" "d.get('publicKey') or d.get('PublicKey') or ''")"
    if [ -n "$PK" ]; then
      echo "Node public key: $PK"
      # New nodes join after the registration fee is paid and the Super Admin approves it.
      if REG="$(curl -fsS --max-time 15 "$CENTRAL/api/noderegistration/$PK" 2>/dev/null)"; then
        printf '%s' "$REG" | python3 -c '
import json,sys
d=json.load(sys.stdin); print(); print("Registration status: " + d.get("status",""))
if d.get("status")=="awaiting_payment":
    print("  Fee            : {:,} VND".format(int(d.get("feeVnd",0))))
    print("  Transfer note  : " + d.get("transferContent",""))
    b=d.get("bank") or {}
    if b: print("  Bank           : {} {} ({})".format(b.get("bankName",""), b.get("accountNo",""), b.get("accountName","")))
    p=d.get("paypal") or {}
    if p: print("  PayPal         : {} (put the transfer note in the message)".format(p.get("url","")))
print("  " + (d.get("instructions") or ""))'
        echo "  Status any time: $CENTRAL/api/noderegistration/$PK"
      fi
    fi
    echo
    echo "Next: forward TCP port $DLTP_PORT on your router to this computer's local IP."
    echo "If your provider uses carrier-grade NAT (several homes share one public IP), ask for a public IP or use a VPS."
    echo "Check that the network sees you: https://deltanium.com/nodes.html$([ "$NETWORK" = testnet ] && echo '?net=testnet')"
    exit 0
  fi
done
echo "The node did not answer on port $HTTP_PORT. Logs: $DATA_DIR/logs" >&2
exit 1
