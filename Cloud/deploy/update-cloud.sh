#!/usr/bin/env bash
# TOR POS Cloud - Update auf dem Linux-Server in einem Schritt.
#
#   sudo bash update-cloud.sh /tmp/TOR-POS-main/Cloud
#   sudo bash update-cloud.sh /tmp/TOR-POS-main.zip
#
# Erstinstallation: deploy/install-cloud.sh (dieses Skript aktualisiert nur eine
# bestehende Installation).
#
# Ablauf: neue Version prüfen (Syntax, Version, Node, Speicherplatz) -> Dienst
# stoppen -> vollständige Sicherung (Code + Datenbank) -> nur den Code ersetzen
# (data/, updates/ und node_modules/ bleiben unberührt) -> Dienst starten ->
# /api/health muss die neue Version melden, auch einige Sekunden später noch.
# Klappt etwas davon nicht, wird der alte Code automatisch wiederhergestellt,
# der Dienst neu gestartet und geprüft, dass wieder die alte Version antwortet.
# Die Datenbank wird nie überschrieben; Schemaänderungen der Cloud sind nur
# additiv (ensureColumn), die alte Version läuft daher auch mit der bereits
# erweiterten Datenbank. Die Sicherung enthält die Datenbank vom Zeitpunkt vor
# dem Update, falls sie doch einmal gebraucht wird.
#
# Exit-Codes: 0 Update erfolgreich · 1 abgebrochen, nichts geändert oder alter
# Stand läuft wieder · 2 alter Stand wiederhergestellt, meldet sich aber nicht.
#
# Anpassbar über Umgebungsvariablen:
#   TOR_CLOUD_DIR          Installationsordner       (Standard /opt/tor-pos-cloud)
#   TOR_CLOUD_SERVICE      systemd-Dienst            (Standard tor-pos-cloud)
#   TOR_CLOUD_ENV          Umgebungsdatei            (Standard /etc/tor-pos-cloud.env)
#   TOR_CLOUD_USER         Dienstbenutzer            (Standard torcloud)
#   TOR_CLOUD_KEEP         aufbewahrte Sicherungen   (Standard 5)
#   TOR_CLOUD_HEALTH_WAIT  Sekunden bis zur Antwort  (Standard 30)
set -euo pipefail

INSTALL_DIR="${TOR_CLOUD_DIR:-/opt/tor-pos-cloud}"
INSTALL_DIR="${INSTALL_DIR%/}"
SERVICE="${TOR_CLOUD_SERVICE:-tor-pos-cloud}"
ENV_FILE="${TOR_CLOUD_ENV:-/etc/tor-pos-cloud.env}"
RUN_USER="${TOR_CLOUD_USER:-torcloud}"
KEEP="${TOR_CLOUD_KEEP:-5}"
HEALTH_WAIT="${TOR_CLOUD_HEALTH_WAIT:-30}"
SETTLE="${TOR_CLOUD_SETTLE:-3}"

# The update lock (fd 9) must not leak into the service it starts.
svc()  { systemctl "$@" 9>&-; }
say()  { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFEHLER: %s\n' "$*" >&2; exit 1; }

[ "$#" -eq 1 ] || fail "Aufruf: sudo bash $0 <Cloud-Ordner oder ZIP der neuen Version>"
# TOR_CLOUD_UPDATE_TEST=1 only for the automated test with a stand-in systemctl.
[ "$(id -u)" -eq 0 ] || [ "${TOR_CLOUD_UPDATE_TEST:-}" = "1" ] || fail "Bitte mit sudo ausführen."
[[ "$KEEP" =~ ^[1-9][0-9]*$ ]] || fail "TOR_CLOUD_KEEP muss eine Zahl ab 1 sein (ist: $KEEP)."
[[ "$HEALTH_WAIT" =~ ^[1-9][0-9]*$ ]] || fail "TOR_CLOUD_HEALTH_WAIT muss eine Zahl ab 1 sein (ist: $HEALTH_WAIT)."
[[ "$SETTLE" =~ ^[0-9]+$ ]] || fail "TOR_CLOUD_SETTLE muss eine Zahl sein (ist: $SETTLE)."
[[ "$SERVICE" =~ ^[A-Za-z0-9@._-]+$ ]] || fail "TOR_CLOUD_SERVICE enthält unzulässige Zeichen: $SERVICE"
[ -d "$INSTALL_DIR" ] || fail "Installationsordner $INSTALL_DIR fehlt. Erstinstallation mit deploy/install-cloud.sh, sonst TOR_CLOUD_DIR setzen."
[ -f "$INSTALL_DIR/server.js" ] || fail "$INSTALL_DIR enthält keine TOR Cloud (server.js fehlt). Erstinstallation mit deploy/install-cloud.sh."
id -u "$RUN_USER" >/dev/null 2>&1 || fail "Dienstbenutzer $RUN_USER existiert nicht (TOR_CLOUD_USER setzen)."
command -v node >/dev/null || fail "node ist nicht installiert."
command -v curl >/dev/null || fail "curl ist nicht installiert."
# node:sqlite without an experimental flag needs Node 22.13 or newer.
node -e 'const [a,b]=process.versions.node.split(".").map(Number);process.exit(a>22||(a===22&&b>=13)?0:1)' \
  || fail "Node $(node -v) ist zu alt - TOR Cloud braucht Node 22.13 oder neuer."
[ -f "$ENV_FILE" ] || printf 'WARNUNG: %s fehlt - Healthcheck nutzt 127.0.0.1:8787.\n' "$ENV_FILE" >&2

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Only one update at a time.
if command -v flock >/dev/null; then
  exec 9>"${INSTALL_DIR}.update.lock"
  flock -n 9 || fail "Ein anderes Update läuft bereits (${INSTALL_DIR}.update.lock)."
fi

# ---------------------------------------------------------------- Quelle
SOURCE="$1"
if [ -f "$SOURCE" ] && [[ "$SOURCE" == *.zip ]]; then
  command -v unzip >/dev/null || fail "unzip ist nicht installiert (apt install unzip)."
  unzip -q "$SOURCE" -d "$WORK/zip" || fail "ZIP $SOURCE lässt sich nicht entpacken."
  # A GitHub ZIP holds <repo>/Cloud/server.js; a ZIP of the Cloud folder holds
  # server.js at its top. The Cloud/ folder wins when both exist.
  SOURCE="$(find "$WORK/zip" -maxdepth 4 -type f -name server.js -path '*/Cloud/server.js' -printf '%h\n' | sort | head -n1)"
  if [ -z "$SOURCE" ]; then
    for candidate in "$WORK/zip" "$WORK"/zip/*; do
      if [ -f "$candidate/server.js" ] && [ -f "$candidate/package.json" ]; then SOURCE="$candidate"; break; fi
    done
  fi
  [ -n "$SOURCE" ] || fail "Im ZIP wurde kein TOR-Cloud-Ordner (Cloud/server.js) gefunden."
fi
SOURCE="${SOURCE%/}"
[ -d "$SOURCE" ] || fail "$SOURCE ist weder ein Ordner noch eine .zip-Datei."
[ -f "$SOURCE/server.js" ] && [ -f "$SOURCE/package.json" ] || fail "$SOURCE ist kein TOR-Cloud-Ordner (server.js/package.json fehlen)."
REAL_SOURCE="$(cd "$SOURCE" && pwd -P)"
REAL_INSTALL="$(cd "$INSTALL_DIR" && pwd -P)"
case "$REAL_SOURCE/" in
  "$REAL_INSTALL/"*) fail "Die neue Version darf nicht im Installationsordner $INSTALL_DIR liegen." ;;
esac

version_of() { sed -n "s/^const CLOUD_VERSION='\([^']*\)';.*/\1/p" "$1/server.js" | head -n1; }
NEW_VERSION="$(version_of "$SOURCE")"
OLD_VERSION="$(version_of "$INSTALL_DIR" || true)"
[ -n "$NEW_VERSION" ] || fail "CLOUD_VERSION in der neuen server.js nicht gefunden."

say "Neue Version prüfen: ${OLD_VERSION:-unbekannt} -> $NEW_VERSION"
while IFS= read -r -d '' f; do
  node --check "$f" 2>"$WORK/syntax.log" || { cat "$WORK/syntax.log" >&2; fail "Syntaxfehler in ${f#"$SOURCE"/} - Update abgebrochen, nichts geändert."; }
done < <(find "$SOURCE" \( -path "$SOURCE/node_modules" -o -path "$SOURCE/data" -o -path "$SOURCE/updates" -o -path "$SOURCE/tests" \) -prune -o -type f -name '*.js' -print0)

# The full backup is a copy of the installation - it has to fit on the disk.
NEED_KB="$(du -sk "$INSTALL_DIR" | cut -f1)"
FREE_KB="$(df -Pk "$(dirname "$INSTALL_DIR")" | awk 'NR==2 {print $4}')"
[ "$FREE_KB" -gt $((NEED_KB + NEED_KB / 5 + 10240)) ] \
  || fail "Zu wenig Speicherplatz für die Sicherung: frei ${FREE_KB} KB, gebraucht etwa $((NEED_KB + NEED_KB / 5 + 10240)) KB. Nichts geändert."

# ---------------------------------------------------------------- Health
env_value() { [ -f "$ENV_FILE" ] && sed -n "s/^[[:space:]]*\(export[[:space:]]\+\)\?$1=\(.*\)$/\2/p" "$ENV_FILE" | tail -n1 | tr -d '"'"'" || true; }
HOST="$(env_value HOST)"; HOST="${HOST:-127.0.0.1}"
case "$HOST" in 0.0.0.0|::|'[::]') HOST=127.0.0.1 ;; esac
PORT="$(env_value PORT)"; PORT="${PORT:-8787}"
[[ "$PORT" =~ ^[0-9]+$ ]] || fail "PORT in $ENV_FILE ist keine Zahl: $PORT"
HEALTH_URL="http://${HOST}:${PORT}/api/health"

# Prints the version reported by /api/health (empty for a non-ok answer);
# fails when nothing answers within $1 seconds.
healthy_version() {
  local wait="${1:-$HEALTH_WAIT}" i body
  for ((i = 0; i < wait; i++)); do
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
[ ! -e "$BACKUP" ] || fail "$BACKUP existiert bereits - eine Minute warten und erneut starten."

say "Dienst $SERVICE stoppen"
svc stop "$SERVICE" || fail "Dienst $SERVICE lässt sich nicht stoppen. Nichts geändert."

say "Vollständige Sicherung nach $BACKUP (Code und Datenbank)"
if ! cp -a "$INSTALL_DIR" "$BACKUP"; then
  rm -rf "$BACKUP"
  svc start "$SERVICE" || true
  fail "Sicherung fehlgeschlagen - Update abgebrochen, alter Stand wird wieder gestartet."
fi

copy_code() { # $1 = von, $2 = nach; data/, updates/, node_modules/ bleiben am Ziel
  if command -v rsync >/dev/null; then
    # --checksum: a new server.js of the same size written in the same second
    # (0.13.9 -> 0.14.0) would otherwise be skipped as unchanged.
    rsync -a --checksum --delete --exclude '/data/' --exclude '/updates/' --exclude '/node_modules/' "$1/" "$2/" || return 1
  else
    find "$2" -mindepth 1 -maxdepth 1 ! -name data ! -name updates ! -name node_modules -exec rm -rf {} + || return 1
    tar -C "$1" --exclude=./data --exclude=./updates --exclude=./node_modules -cf - . | tar -C "$2" -xf - || return 1
  fi
  chown -R "$RUN_USER": "$2" 2>/dev/null || [ "${TOR_CLOUD_UPDATE_TEST:-}" = "1" ] || return 1
}

rollback() {
  printf '\nFEHLER: %s\nAlter Stand wird wiederhergestellt ...\n' "$1" >&2
  svc stop "$SERVICE" || true
  if ! copy_code "$BACKUP" "$INSTALL_DIR"; then
    printf 'ACHTUNG: Der alte Code ließ sich nicht zurückkopieren. Von Hand: rsync -a --delete --exclude /data/ --exclude /updates/ %s/ %s/\n' "$BACKUP" "$INSTALL_DIR" >&2
    exit 2
  fi
  svc start "$SERVICE" || true
  local back
  back="$(healthy_version || true)"
  if [ -n "$back" ] && { [ -z "$OLD_VERSION" ] || [ "$back" = "$OLD_VERSION" ]; }; then
    printf 'Zurückgesetzt: TOR Cloud läuft wieder mit %s. Sicherung: %s\n' "$back" "$BACKUP" >&2
    exit 1
  fi
  printf 'ACHTUNG: Der alte Stand %s meldet sich nicht (Antwort: %s). journalctl -u %s -n 100 prüfen. Sicherung: %s\n' \
    "${OLD_VERSION:-?}" "${back:-keine}" "$SERVICE" "$BACKUP" >&2
  exit 2
}

# ---------------------------------------------------------------- Update
say "Code ersetzen (data/, updates/ und node_modules/ bleiben)"
copy_code "$SOURCE" "$INSTALL_DIR" || rollback "Der neue Code ließ sich nicht vollständig kopieren."

say "Dienst starten und Version prüfen ($HEALTH_URL)"
svc start "$SERVICE" || rollback "Dienst startet nicht."
RUNNING="$(healthy_version || true)"
[ -n "$RUNNING" ] || rollback "Keine gültige Antwort von $HEALTH_URL innerhalb von ${HEALTH_WAIT} s."
[ "$RUNNING" = "$NEW_VERSION" ] || rollback "Der Dienst meldet $RUNNING statt $NEW_VERSION."
# A version that answers once and then crashes (Restart=on-failure hides it) is
# caught by asking again a few seconds later.
if [ "$SETTLE" -gt 0 ]; then
  sleep "$SETTLE"
  AGAIN="$(healthy_version 5 || true)"
  [ "$AGAIN" = "$NEW_VERSION" ] || rollback "Die neue Version antwortet nach ${SETTLE} s nicht mehr stabil (${AGAIN:-keine Antwort})."
fi

# ---------------------------------------------------------------- Aufräumen
mapfile -t OLD_BACKUPS < <(ls -1d "${INSTALL_DIR}".sicherung-* 2>/dev/null | sort | head -n -"$KEEP")
for b in "${OLD_BACKUPS[@]}"; do rm -rf "$b"; done

say "Fertig: TOR Cloud $NEW_VERSION läuft."
printf 'Sicherung des vorherigen Stands: %s\n' "$BACKUP"
printf 'Zurück zum vorherigen Stand:     sudo bash %s %s\n' "$0" "$BACKUP"
