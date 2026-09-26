#!/usr/bin/env bash
# TOR POS Cloud - Erstinstallation auf einem frischen Linux-Server (Ubuntu 24.04).
#
#   sudo bash install-cloud.sh /tmp/TOR-POS-main/Cloud
#   sudo bash install-cloud.sh /tmp/TOR-POS-main.zip
#
# Legt an (nur was fehlt): Dienstbenutzer, /opt/tor-pos-cloud mit data/ und
# updates/, Sicherungsordner, /etc/tor-pos-cloud.env aus der Vorlage (chmod 600),
# den systemd-Dienst. Gestartet wird erst, wenn die Umgebungsdatei vollständig
# ist (preflight-cloud.sh ohne FEHLER): beim ersten Lauf fehlen die Schlüssel
# noch - dann eintragen und dasselbe Skript erneut aufrufen. Eine bereits
# laufende Installation wird nie angefasst; Updates macht update-cloud.sh.
#
# Exit-Codes: 0 läuft · 1 Fehler · 3 installiert, wartet auf die Umgebungsdatei.
#
# Anpassbar wie update-cloud.sh: TOR_CLOUD_DIR, TOR_CLOUD_SERVICE, TOR_CLOUD_ENV,
# TOR_CLOUD_USER, TOR_CLOUD_HEALTH_WAIT; dazu TOR_CLOUD_UNIT_DIR (Standard
# /etc/systemd/system) und TOR_CLOUD_BACKUP_DIR (Standard /var/backups/tor-pos-cloud).
set -euo pipefail

INSTALL_DIR="${TOR_CLOUD_DIR:-/opt/tor-pos-cloud}"; INSTALL_DIR="${INSTALL_DIR%/}"
SERVICE="${TOR_CLOUD_SERVICE:-tor-pos-cloud}"
ENV_FILE="${TOR_CLOUD_ENV:-/etc/tor-pos-cloud.env}"
RUN_USER="${TOR_CLOUD_USER:-torcloud}"
UNIT_DIR="${TOR_CLOUD_UNIT_DIR:-/etc/systemd/system}"
BACKUP_DIR="${TOR_CLOUD_BACKUP_DIR:-/var/backups/tor-pos-cloud}"
HEALTH_WAIT="${TOR_CLOUD_HEALTH_WAIT:-30}"
UNIT="$UNIT_DIR/$SERVICE.service"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEST_MODE="${TOR_CLOUD_UPDATE_TEST:-}"

say()  { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFEHLER: %s\n' "$*" >&2; exit 1; }

[ "$#" -eq 1 ] || fail "Aufruf: sudo bash $0 <Cloud-Ordner oder ZIP>"
[ "$(id -u)" -eq 0 ] || [ "$TEST_MODE" = "1" ] || fail "Bitte mit sudo ausführen."
[[ "$SERVICE" =~ ^[A-Za-z0-9@._-]+$ ]] || fail "TOR_CLOUD_SERVICE enthält unzulässige Zeichen: $SERVICE"
[[ "$HEALTH_WAIT" =~ ^[1-9][0-9]*$ ]] || fail "TOR_CLOUD_HEALTH_WAIT muss eine Zahl ab 1 sein."
# systemd splits ReadWritePaths/EnvironmentFile at spaces: such a path would
# make the unit fail to start, so it is refused before anything is created.
for p in "$INSTALL_DIR" "$BACKUP_DIR" "$ENV_FILE" "$UNIT_DIR"; do
  [[ "$p" =~ ^/[A-Za-z0-9/._@-]+$ ]] || fail "Pfad \"$p\" enthält Leerzeichen oder Sonderzeichen - systemd kann ihn nicht verwenden."
done
command -v node >/dev/null || fail "node ist nicht installiert (Node 22.13 oder neuer, z. B. NodeSource)."
node -e 'const [a,b]=process.versions.node.split(".").map(Number);process.exit(a>22||(a===22&&b>=13)?0:1)' \
  || fail "Node $(node -v) ist zu alt - TOR Cloud braucht Node 22.13 oder neuer."
command -v curl >/dev/null || fail "curl ist nicht installiert."

if command -v systemctl >/dev/null && systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
  fail "Der Dienst $SERVICE läuft bereits - für eine neue Version update-cloud.sh verwenden."
fi

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT

# ---------------------------------------------------------------- Quelle
SOURCE="$1"
if [ -f "$SOURCE" ] && [[ "$SOURCE" == *.zip ]]; then
  command -v unzip >/dev/null || fail "unzip ist nicht installiert (apt install unzip)."
  unzip -q "$SOURCE" -d "$WORK/zip" || fail "ZIP $SOURCE lässt sich nicht entpacken."
  SOURCE="$(find "$WORK/zip" -maxdepth 4 -type f -name server.js -path '*/Cloud/server.js' -printf '%h\n' | sort | head -n1)"
  if [ -z "$SOURCE" ]; then
    for candidate in "$WORK/zip" "$WORK"/zip/*; do
      if [ -f "$candidate/server.js" ] && [ -f "$candidate/package.json" ]; then SOURCE="$candidate"; break; fi
    done
  fi
  [ -n "$SOURCE" ] || fail "Im ZIP wurde kein TOR-Cloud-Ordner (Cloud/server.js) gefunden."
fi
SOURCE="${SOURCE%/}"
[ -f "$SOURCE/server.js" ] && [ -f "$SOURCE/package.json" ] || fail "$SOURCE ist kein TOR-Cloud-Ordner (server.js/package.json fehlen)."
while IFS= read -r -d '' f; do
  node --check "$f" 2>/dev/null || fail "Syntaxfehler in ${f#"$SOURCE"/} - nichts installiert."
done < <(find "$SOURCE" \( -path "$SOURCE/node_modules" -o -path "$SOURCE/data" -o -path "$SOURCE/updates" -o -path "$SOURCE/tests" \) -prune -o -type f -name '*.js' -print0)
VERSION="$(sed -n "s/^const CLOUD_VERSION='\([^']*\)';.*/\1/p" "$SOURCE/server.js" | head -n1)"

# ---------------------------------------------------------------- Benutzer und Ordner
if ! id -u "$RUN_USER" >/dev/null 2>&1; then
  say "Dienstbenutzer $RUN_USER anlegen"
  useradd --system --home "$INSTALL_DIR" --shell /usr/sbin/nologin "$RUN_USER"
fi

if [ -f "$INSTALL_DIR/server.js" ]; then
  say "Code in $INSTALL_DIR ist schon da ($(sed -n "s/^const CLOUD_VERSION='\([^']*\)';.*/\1/p" "$INSTALL_DIR/server.js" | head -n1)) - bleibt unverändert."
else
  say "TOR Cloud $VERSION nach $INSTALL_DIR kopieren"
  mkdir -p "$INSTALL_DIR"
  tar -C "$SOURCE" --exclude=./data --exclude=./updates --exclude=./node_modules --exclude=./tests -cf - . | tar -C "$INSTALL_DIR" -xf -
fi
mkdir -p "$INSTALL_DIR/data" "$INSTALL_DIR/updates" "$BACKUP_DIR"
chown -R "$RUN_USER": "$INSTALL_DIR" "$BACKUP_DIR" 2>/dev/null || [ "$TEST_MODE" = "1" ] || fail "chown auf $RUN_USER fehlgeschlagen."
chmod 750 "$INSTALL_DIR/data" "$BACKUP_DIR"

# ---------------------------------------------------------------- Umgebungsdatei
if [ -f "$ENV_FILE" ]; then
  say "$ENV_FILE ist schon da - bleibt unverändert."
else
  say "$ENV_FILE aus der Vorlage anlegen (chmod 600)"
  ( umask 077
    sed -e "s#/opt/tor-pos-cloud#$INSTALL_DIR#g" -e "s#/var/backups/tor-pos-cloud#$BACKUP_DIR#g" \
      "$HERE/tor-pos-cloud.env.example" > "$ENV_FILE" )
fi
chmod 600 "$ENV_FILE"
[ "$TEST_MODE" = "1" ] || chown root:root "$ENV_FILE"

# ---------------------------------------------------------------- systemd
say "Dienst $UNIT einrichten"
mkdir -p "$UNIT_DIR"
sed -e "s#/opt/tor-pos-cloud#$INSTALL_DIR#g" -e "s#/var/backups/tor-pos-cloud#$BACKUP_DIR#g" \
    -e "s#/etc/tor-pos-cloud.env#$ENV_FILE#g" -e "s#^User=torcloud#User=$RUN_USER#" -e "s#^Group=torcloud#Group=$RUN_USER#" \
    -e "s#^ExecStart=/usr/bin/node #ExecStart=$(command -v node) #" \
    "$HERE/tor-pos-cloud.service" > "$UNIT"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null

# ---------------------------------------------------------------- Start nur wenn vollständig
say "Umgebungsdatei prüfen"
if ! TOR_CLOUD_DIR="$INSTALL_DIR" TOR_CLOUD_SERVICE="$SERVICE" TOR_CLOUD_ENV="$ENV_FILE" TOR_CLOUD_USER="$RUN_USER" \
     TOR_CLOUD_UNIT="$UNIT" TOR_CLOUD_SKIP_CADDY=1 TOR_CLOUD_ENV_OWNER="$( [ "$TEST_MODE" = "1" ] && id -un || echo root)" \
     bash "$HERE/preflight-cloud.sh"; then
  printf '\nInstalliert, aber NICHT gestartet: %s vervollständigen (FEHLER oben), dann erneut:\n  sudo bash %s %s\n' "$ENV_FILE" "$0" "$1"
  exit 3
fi

port="$(sed -n 's/^PORT=//p' "$ENV_FILE" | tail -n1)"; port="${port:-8787}"
say "Dienst starten"
systemctl start "$SERVICE"
for ((i = 0; i < HEALTH_WAIT; i++)); do
  body="$(curl -fsS --max-time 3 "http://127.0.0.1:${port}/api/health" 2>/dev/null || true)"
  if [ -n "$body" ]; then
    printf '%s' "$body" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const j=JSON.parse(s);process.exit(j.ok===true&&j.demo===false?0:1)})' \
      || fail "/api/health antwortet nicht im Livemodus: $body"
    say "Fertig: TOR Cloud $VERSION läuft (127.0.0.1:${port}). Weiter mit Caddy (docs/CLOUD-PRODUKTION.md)."
    exit 0
  fi
  sleep 1
done
fail "Keine Antwort von /api/health nach ${HEALTH_WAIT} s - journalctl -u $SERVICE -n 100 prüfen."
