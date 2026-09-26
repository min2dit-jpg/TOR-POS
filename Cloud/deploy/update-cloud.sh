#!/usr/bin/env bash
# TOR POS Cloud - Update auf dem Linux-Server in einem Schritt.
#
#   sudo bash update-cloud.sh /tmp/TOR-POS-main/Cloud
#   sudo bash update-cloud.sh /tmp/TOR-POS-main.zip
#
# Ablauf: neue Version prüfen (Syntax, Version) -> Dienst stoppen -> vollständige
# Sicherung (Code + Datenbank) -> nur den Code ersetzen (data/, updates/ und
# node_modules/ bleiben unberührt) -> Dienst starten -> /api/health muss die neue
# Version melden. Klappt der Start nicht, wird der alte Code automatisch
# wiederhergestellt und der Dienst neu gestartet. Die Datenbank wird nie
# überschrieben; Schemaänderungen der Cloud sind nur additiv (ensureColumn), die
# alte Version läuft daher auch mit der bereits erweiterten Datenbank.
#
# Anpassbar über Umgebungsvariablen:
#   TOR_CLOUD_DIR      Installationsordner       (Standard /opt/tor-pos-cloud)
#   TOR_CLOUD_SERVICE  systemd-Dienst            (Standard tor-pos-cloud)
#   TOR_CLOUD_ENV      Umgebungsdatei            (Standard /etc/tor-pos-cloud.env)
#   TOR_CLOUD_USER     Dienstbenutzer            (Standard torcloud)
#   TOR_CLOUD_KEEP     aufbewahrte Sicherungen   (Standard 5)
set -euo pipefail

INSTALL_DIR="${TOR_CLOUD_DIR:-/opt/tor-pos-cloud}"
SERVICE="${TOR_CLOUD_SERVICE:-tor-pos-cloud}"
ENV_FILE="${TOR_CLOUD_ENV:-/etc/tor-pos-cloud.env}"
RUN_USER="${TOR_CLOUD_USER:-torcloud}"
KEEP="${TOR_CLOUD_KEEP:-5}"
HEALTH_WAIT="${TOR_CLOUD_HEALTH_WAIT:-30}"

say()  { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFEHLER: %s\n' "$*" >&2; exit 1; }

[ "$#" -eq 1 ] || fail "Aufruf: sudo bash $0 <Cloud-Ordner oder ZIP der neuen Version>"
# TOR_CLOUD_UPDATE_TEST=1 only for the automated test with a stand-in systemctl.
[ "$(id -u)" -eq 0 ] || [ "${TOR_CLOUD_UPDATE_TEST:-}" = "1" ] || fail "Bitte mit sudo ausführen."
[ -d "$INSTALL_DIR" ] || fail "Installationsordner $INSTALL_DIR fehlt (TOR_CLOUD_DIR setzen)."
command -v node >/dev/null || fail "node ist nicht installiert."
command -v curl >/dev/null || fail "curl ist nicht installiert."

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ---------------------------------------------------------------- Quelle
SOURCE="$1"
if [ -f "$SOURCE" ] && [[ "$SOURCE" == *.zip ]]; then
  command -v unzip >/dev/null || fail "unzip ist nicht installiert (apt install unzip)."
  unzip -q "$SOURCE" -d "$WORK/zip"
  SOURCE="$(find "$WORK/zip" -maxdepth 3 -type f -name server.js -path '*/Cloud/server.js' -printf '%h\n' | head -n1)"
  [ -n "$SOURCE" ] || fail "Im ZIP wurde kein Ordner Cloud/ mit server.js gefunden."
fi
SOURCE="${SOURCE%/}"
[ -f "$SOURCE/server.js" ] && [ -f "$SOURCE/package.json" ] || fail "$SOURCE ist kein TOR-Cloud-Ordner (server.js/package.json fehlen)."

version_of() { sed -n "s/^const CLOUD_VERSION='\([^']*\)';.*/\1/p" "$1/server.js" | head -n1; }
NEW_VERSION="$(version_of "$SOURCE")"
OLD_VERSION="$(version_of "$INSTALL_DIR" || true)"
[ -n "$NEW_VERSION" ] || fail "CLOUD_VERSION in der neuen server.js nicht gefunden."

say "Neue Version prüfen: ${OLD_VERSION:-unbekannt} -> $NEW_VERSION"
for f in server.js validation.js receipts.js receipt-pdf.js managed-mail.js credentials.js update-store.js public/app.js; do
  if [ -f "$SOURCE/$f" ] && ! node --check "$SOURCE/$f"; then
    fail "Syntaxfehler in $f - Update abgebrochen, nichts geändert."
  fi
done

# ---------------------------------------------------------------- Health
env_value() { [ -f "$ENV_FILE" ] && sed -n "s/^$1=\(.*\)$/\1/p" "$ENV_FILE" | tail -n1 | tr -d '"'"'" || true; }
HOST="$(env_value HOST)"; HOST="${HOST:-127.0.0.1}"
PORT="$(env_value PORT)"; PORT="${PORT:-8787}"
HEALTH_URL="http://${HOST}:${PORT}/api/health"

healthy_version() {
  local i body
  for ((i = 0; i < HEALTH_WAIT; i++)); do
    body="$(curl -fsS --max-time 3 "$HEALTH_URL" 2>/dev/null || true)"
    if [ -n "$body" ]; then
      printf '%s' "$body" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const j=JSON.parse(s);process.stdout.write(j.ok?String(j.version||""):"")}catch{}})'
      return 0
    fi
    sleep 1
  done
  return 1
}

# ---------------------------------------------------------------- Sicherung
STAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP="${INSTALL_DIR}.sicherung-${STAMP}"

say "Dienst $SERVICE stoppen"
systemctl stop "$SERVICE"

say "Vollständige Sicherung nach $BACKUP (Code und Datenbank)"
cp -a "$INSTALL_DIR" "$BACKUP"

copy_code() { # $1 = von, $2 = nach; data/, updates/, node_modules/ bleiben am Ziel
  if command -v rsync >/dev/null; then
    rsync -a --delete --exclude '/data/' --exclude '/updates/' --exclude '/node_modules/' "$1/" "$2/"
  else
    find "$2" -mindepth 1 -maxdepth 1 ! -name data ! -name updates ! -name node_modules -exec rm -rf {} +
    tar -C "$1" --exclude=./data --exclude=./updates --exclude=./node_modules -cf - . | tar -C "$2" -xf -
  fi
  chown -R "$RUN_USER": "$2" 2>/dev/null || true
}

rollback() {
  printf '\nFEHLER: %s\nAlter Stand wird wiederhergestellt ...\n' "$1" >&2
  systemctl stop "$SERVICE" || true
  copy_code "$BACKUP" "$INSTALL_DIR"
  systemctl start "$SERVICE" || true
  local back
  back="$(healthy_version || true)"
  if [ -n "$back" ]; then
    printf 'Zurückgesetzt: TOR Cloud läuft wieder mit %s. Sicherung: %s\n' "$back" "$BACKUP" >&2
  else
    printf 'ACHTUNG: Auch der alte Stand meldet sich nicht. journalctl -u %s -n 100 prüfen. Sicherung: %s\n' "$SERVICE" "$BACKUP" >&2
  fi
  exit 1
}

# ---------------------------------------------------------------- Update
say "Code ersetzen (data/, updates/ und node_modules/ bleiben)"
copy_code "$SOURCE" "$INSTALL_DIR"

say "Dienst starten und Version prüfen ($HEALTH_URL)"
systemctl start "$SERVICE" || rollback "Dienst startet nicht."
RUNNING="$(healthy_version || true)"
[ -n "$RUNNING" ] || rollback "Keine Antwort von $HEALTH_URL innerhalb von ${HEALTH_WAIT} s."
[ "$RUNNING" = "$NEW_VERSION" ] || rollback "Der Dienst meldet $RUNNING statt $NEW_VERSION."

# ---------------------------------------------------------------- Aufräumen
mapfile -t OLD_BACKUPS < <(ls -1d "${INSTALL_DIR}".sicherung-* 2>/dev/null | sort | head -n -"$KEEP")
for b in "${OLD_BACKUPS[@]}"; do rm -rf "$b"; done

say "Fertig: TOR Cloud $NEW_VERSION läuft."
printf 'Sicherung des vorherigen Stands: %s\n' "$BACKUP"
printf 'Zurück zum vorherigen Stand:     sudo bash %s %s\n' "$0" "$BACKUP"
