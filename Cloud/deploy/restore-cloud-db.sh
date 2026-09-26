#!/usr/bin/env bash
# TOR POS Cloud - Datenbank aus einer Sicherung wiederherstellen.
#
#   sudo bash restore-cloud-db.sh /var/backups/tor-pos-cloud/tor-cloud-20261001T020000Z.db
#
# 1. prüft die Sicherung (SQLite integrity_check, TOR-Cloud-Tabellen) - vorher
#    wird nichts angefasst
# 2. Dienst stoppen, aktuelle Datenbank samt -wal/-shm nach
#    <db>.vor-wiederherstellung-<Zeit> verschieben (nichts wird gelöscht)
# 3. Sicherung als Datenbank einsetzen, Dienst starten, /api/health prüfen
# Startet der Dienst mit der Sicherung nicht, kommt die vorherige Datenbank zurück.
#
# Anpassbar wie update-cloud.sh: TOR_CLOUD_SERVICE, TOR_CLOUD_ENV, TOR_CLOUD_USER,
# TOR_CLOUD_HEALTH_WAIT.
set -euo pipefail

SERVICE="${TOR_CLOUD_SERVICE:-tor-pos-cloud}"
ENV_FILE="${TOR_CLOUD_ENV:-/etc/tor-pos-cloud.env}"
RUN_USER="${TOR_CLOUD_USER:-torcloud}"
HEALTH_WAIT="${TOR_CLOUD_HEALTH_WAIT:-30}"

fail() { printf '\nFEHLER: %s\n' "$*" >&2; exit 1; }
say()  { printf '\n==> %s\n' "$*"; }

[ "$#" -eq 1 ] || fail "Aufruf: sudo bash $0 <Sicherungsdatei .db>"
[ "$(id -u)" -eq 0 ] || [ "${TOR_CLOUD_UPDATE_TEST:-}" = "1" ] || fail "Bitte mit sudo ausführen."
[[ "$HEALTH_WAIT" =~ ^[1-9][0-9]*$ ]] || fail "TOR_CLOUD_HEALTH_WAIT muss eine Zahl ab 1 sein."
BACKUP="$1"
[ -f "$BACKUP" ] || fail "Sicherung $BACKUP nicht gefunden."
[ -f "$ENV_FILE" ] || fail "$ENV_FILE fehlt."
value() { sed -n "s/^[[:space:]]*\(export[[:space:]]\+\)\?$1=\(.*\)$/\2/p" "$ENV_FILE" | tail -n1 | tr -d '"'"'"; }
DB="$(value TOR_CLOUD_DB)"; [ -n "$DB" ] || fail "TOR_CLOUD_DB ist in $ENV_FILE nicht gesetzt."
PORT="$(value PORT)"; PORT="${PORT:-8787}"
[ "$(cd "$(dirname "$BACKUP")" && pwd -P)/$(basename "$BACKUP")" != "$(cd "$(dirname "$DB")" && pwd -P)/$(basename "$DB")" ] \
  || fail "Die Sicherung ist die laufende Datenbank selbst."

say "Sicherung prüfen: $BACKUP"
node -e '
const {DatabaseSync}=require("node:sqlite");
const db=new DatabaseSync(process.argv[1],{readOnly:true});
const ok=db.prepare("PRAGMA integrity_check").get();
if(Object.values(ok)[0]!=="ok"){console.error("integrity_check: "+JSON.stringify(ok));process.exit(1);}
for(const t of ["businesses","registers","users","cloud_sales"])
  if(!db.prepare("SELECT 1 FROM sqlite_master WHERE type=? AND name=?").get("table",t)){console.error("Tabelle "+t+" fehlt");process.exit(1);}
const n=db.prepare("SELECT COUNT(*) n FROM businesses").get().n;
console.log("Sicherung in Ordnung: "+n+" Betrieb(e).");
' "$BACKUP" || fail "Die Sicherung ist keine gültige TOR-Cloud-Datenbank - nichts geändert."

healthy() {
  local i body
  for ((i = 0; i < HEALTH_WAIT; i++)); do
    body="$(curl -fsS --max-time 3 "http://127.0.0.1:${PORT}/api/health" 2>/dev/null || true)"
    if [ -n "$body" ]; then printf '%s' "$body" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.exit(JSON.parse(s).ok===true?0:1))'; return; fi
    sleep 1
  done
  return 1
}

STAMP="$(date +%Y%m%d-%H%M%S)"
KEPT="$DB.vor-wiederherstellung-$STAMP"
say "Dienst $SERVICE stoppen"
systemctl stop "$SERVICE"
say "Aktuelle Datenbank nach $KEPT verschieben"
mkdir -p "$KEPT"
for f in "$DB" "$DB-wal" "$DB-shm"; do [ -e "$f" ] && mv "$f" "$KEPT/"; done
cp "$BACKUP" "$DB"
chown "$RUN_USER": "$DB" 2>/dev/null || [ "${TOR_CLOUD_UPDATE_TEST:-}" = "1" ] || fail "chown auf $RUN_USER fehlgeschlagen."
chmod 640 "$DB"

say "Dienst starten"
systemctl start "$SERVICE" || true
if healthy; then
  say "Fertig: Datenbank aus $BACKUP wiederhergestellt. Vorherige Datenbank: $KEPT"
  exit 0
fi

printf '\nFEHLER: Mit der Sicherung antwortet /api/health nicht - vorherige Datenbank wird zurückgesetzt.\n' >&2
systemctl stop "$SERVICE" || true
rm -f "$DB" "$DB-wal" "$DB-shm"
for f in "$KEPT"/*; do [ -e "$f" ] && mv "$f" "$(dirname "$DB")/"; done
rmdir "$KEPT" 2>/dev/null || true
systemctl start "$SERVICE" || true
exit 1
