#!/usr/bin/env bash
# TOR POS Cloud - Produktions-Vorabprüfung. Ändert nichts, prüft nur.
#
#   sudo bash preflight-cloud.sh            # vor dem ersten Start / nach jedem Update
#
# Prüft /etc/tor-pos-cloud.env (Livemodus, sichere Cookies, keine Platzhalter,
# Dateirechte), Ordner und Besitzer, Node, den systemd-Dienst, Caddy und - wenn
# der Dienst läuft - /api/health. Jede Zeile ist OK, WARNUNG oder FEHLER;
# Exit-Code 1 bei mindestens einem FEHLER.
#
# Anpassbar wie update-cloud.sh: TOR_CLOUD_DIR, TOR_CLOUD_SERVICE, TOR_CLOUD_ENV,
# TOR_CLOUD_USER; dazu TOR_CLOUD_UNIT (Standard /etc/systemd/system/<Dienst>.service),
# TOR_CLOUD_CADDYFILE (Standard /etc/caddy/Caddyfile) und TOR_CLOUD_ENV_OWNER
# (erwarteter Besitzer der Umgebungsdatei, Standard root). TOR_CLOUD_SKIP_CADDY=1
# lässt die Caddy-Prüfung aus (install-cloud.sh vor der Caddy-Einrichtung); vor
# dem Livegang immer ohne diese Variable laufen lassen.
set -uo pipefail

INSTALL_DIR="${TOR_CLOUD_DIR:-/opt/tor-pos-cloud}"; INSTALL_DIR="${INSTALL_DIR%/}"
SERVICE="${TOR_CLOUD_SERVICE:-tor-pos-cloud}"
ENV_FILE="${TOR_CLOUD_ENV:-/etc/tor-pos-cloud.env}"
RUN_USER="${TOR_CLOUD_USER:-torcloud}"
UNIT="${TOR_CLOUD_UNIT:-/etc/systemd/system/${SERVICE}.service}"
CADDYFILE="${TOR_CLOUD_CADDYFILE:-/etc/caddy/Caddyfile}"
ENV_OWNER="${TOR_CLOUD_ENV_OWNER:-root}"

FAILS=0
ok()   { printf 'OK       %s\n' "$*"; }
warn() { printf 'WARNUNG  %s\n' "$*"; }
bad()  { printf 'FEHLER   %s\n' "$*"; FAILS=$((FAILS + 1)); }

# ---------------------------------------------------------------- Umgebung
value() { sed -n "s/^[[:space:]]*\(export[[:space:]]\+\)\?$1=\(.*\)$/\2/p" "$ENV_FILE" | tail -n1 | sed 's/^["'"'"']//; s/["'"'"']$//'; }
is_https() { [[ "$1" =~ ^https://[A-Za-z0-9.-]+/?$ ]]; }
host_of() { printf '%s' "$1" | sed -E 's#^https?://([^/:]+).*#\1#' | tr 'A-Z' 'a-z'; }

if [ ! -f "$ENV_FILE" ]; then
  bad "$ENV_FILE fehlt (Vorlage: deploy/tor-pos-cloud.env.example)."
else
  mode="$(stat -c %a "$ENV_FILE")"; owner="$(stat -c %U "$ENV_FILE")"
  [ "$mode" = 600 ] || [ "$mode" = 400 ] && ok "$ENV_FILE Rechte $mode" || bad "$ENV_FILE hat Rechte $mode - nötig: chmod 600 (enthält Schlüssel)."
  [ "$owner" = "$ENV_OWNER" ] && ok "$ENV_FILE gehört $owner" || bad "$ENV_FILE gehört $owner statt $ENV_OWNER."

  placeholders="$(grep -nE '^[^#]*(REPLACE-WITH|CHANGE[-_ ]?ME|PLACEHOLDER)' "$ENV_FILE" | cut -d= -f1 | sed 's/^[0-9]*://' | tr '\n' ' ')"
  [ -z "$placeholders" ] && ok "keine Platzhalter mehr" || bad "Platzhalter noch nicht ersetzt: $placeholders"

  [ "$(value TOR_CLOUD_DEMO)" = false ] && ok "TOR_CLOUD_DEMO=false" || bad "TOR_CLOUD_DEMO muss false sein (ist: $(value TOR_CLOUD_DEMO))."
  [ "$(value COOKIE_SECURE)" = true ] && ok "COOKIE_SECURE=true" || bad "COOKIE_SECURE muss true sein."
  [ "$(value TOR_CLOUD_TRUST_PROXY)" = true ] && ok "TOR_CLOUD_TRUST_PROXY=true (hinter Caddy)" || bad "TOR_CLOUD_TRUST_PROXY muss hinter Caddy true sein (sonst zählt die Anmeldebremse alle Kunden als eine IP)."
  host="$(value HOST)"; [ -z "$host" ] || [ "$host" = 127.0.0.1 ] || [ "$host" = ::1 ] && ok "HOST lokal (${host:-127.0.0.1})" || bad "HOST=$host - Node darf nur lokal lauschen, Caddy ist die öffentliche Seite."
  totp="$(value TOR_CLOUD_TOTP_KEY)"; [ "${#totp}" -ge 24 ] && ok "TOR_CLOUD_TOTP_KEY gesetzt (${#totp} Zeichen)" || bad "TOR_CLOUD_TOTP_KEY fehlt oder ist kürzer als 24 Zeichen (openssl rand -base64 36)."
  gkey="$(value TOR_CLOUD_GOOGLE_TOKEN_KEY)"; [ -z "$gkey" ] || [ "${#gkey}" -ge 32 ] || bad "TOR_CLOUD_GOOGLE_TOKEN_KEY ist gesetzt, aber kürzer als 32 Zeichen."

  api="$(value TOR_CLOUD_PUBLIC_URL)"; bon="$(value TOR_CLOUD_RECEIPT_URL)"
  is_https "$api" && ok "TOR_CLOUD_PUBLIC_URL=$api" || bad "TOR_CLOUD_PUBLIC_URL muss https://<domain> sein (ist: ${api:-leer})."
  if [ -n "$bon" ]; then
    is_https "$bon" && ok "TOR_CLOUD_RECEIPT_URL=$bon" || bad "TOR_CLOUD_RECEIPT_URL muss https://<domain> sein (ist: $bon)."
    [ "$(host_of "$api")" != "$(host_of "$bon")" ] || bad "Kassenbon-Domain und API-Domain müssen verschieden sein."
    [ -n "$(value TOR_CLOUD_IMPRINT_URL)" ] && [ -n "$(value TOR_CLOUD_PRIVACY_URL)" ] && ok "Impressum/Datenschutz verlinkt" || bad "TOR_CLOUD_IMPRINT_URL und TOR_CLOUD_PRIVACY_URL setzen (öffentliche Bon-Domain)."
  else
    warn "TOR_CLOUD_RECEIPT_URL leer - digitaler Kassenbon ist aus."
  fi
  [ -n "$(value TOR_MAIL_SMTP_HOST)" ] && ok "TOR Mail konfiguriert" || warn "TOR Mail nicht konfiguriert - Berichte/DATEV-Versand aus der Kasse ist aus."

  for key in TOR_CLOUD_DB TOR_CLOUD_UPDATES TOR_CLOUD_BACKUP_DIR; do
    [ -n "$(value "$key")" ] || bad "$key ist nicht gesetzt."
  done
fi

# ---------------------------------------------------------------- Ordner
if [ -f "$INSTALL_DIR/server.js" ]; then
  ver="$(sed -n "s/^const CLOUD_VERSION='\([^']*\)';.*/\1/p" "$INSTALL_DIR/server.js" | head -n1)"
  ok "Installation $INSTALL_DIR (Version ${ver:-?})"
else
  bad "$INSTALL_DIR/server.js fehlt (Erstinstallation: deploy/install-cloud.sh)."
fi
id -u "$RUN_USER" >/dev/null 2>&1 && ok "Dienstbenutzer $RUN_USER" || bad "Dienstbenutzer $RUN_USER fehlt."
check_dir() { # $1 Ordner, $2 Zweck
  if [ ! -d "$1" ]; then bad "$2 $1 fehlt (systemd ReadWritePaths - der Dienst startet sonst nicht)."; return; fi
  [ "$(stat -c %U "$1")" = "$RUN_USER" ] && ok "$2 $1 gehört $RUN_USER" || bad "$2 $1 gehört $(stat -c %U "$1") statt $RUN_USER."
}
if [ -f "$ENV_FILE" ]; then
  db="$(value TOR_CLOUD_DB)"; [ -n "$db" ] && check_dir "$(dirname "$db")" "Datenbankordner"
  up="$(value TOR_CLOUD_UPDATES)"; [ -n "$up" ] && check_dir "$up" "Updateordner"
  bk="$(value TOR_CLOUD_BACKUP_DIR)"; [ -n "$bk" ] && check_dir "$bk" "Sicherungsordner"
  if [ -n "$bk" ] && [ -n "$db" ]; then
    [ "$(df -P "$bk" 2>/dev/null | awk 'NR==2{print $1}')" != "$(df -P "$(dirname "$db")" 2>/dev/null | awk 'NR==2{print $1}')" ] \
      || warn "Sicherungen liegen auf derselben Platte wie die Datenbank - zusätzlich außerhalb des Servers kopieren."
  fi
fi

# ---------------------------------------------------------------- Programme
if command -v node >/dev/null; then
  node -e 'const [a,b]=process.versions.node.split(".").map(Number);process.exit(a>22||(a===22&&b>=13)?0:1)' \
    && ok "Node $(node -v)" || bad "Node $(node -v) ist zu alt (mindestens 22.13, node:sqlite)."
else
  bad "node ist nicht installiert."
fi
for tool in curl unzip; do command -v "$tool" >/dev/null && ok "$tool vorhanden" || bad "$tool fehlt (apt install $tool)."; done

if [ -f "$UNIT" ]; then
  ok "Dienstdatei $UNIT"
  for p in $(sed -n 's/^ReadWritePaths=//p' "$UNIT"); do [ -d "$p" ] || bad "ReadWritePaths-Ordner $p fehlt - der Dienst startet sonst nicht."; done
  grep -q "^EnvironmentFile=$ENV_FILE\$" "$UNIT" || bad "$UNIT liest nicht $ENV_FILE."
  grep -q "^User=$RUN_USER\$" "$UNIT" || bad "$UNIT läuft nicht als $RUN_USER."
else
  bad "Dienstdatei $UNIT fehlt."
fi

if [ "${TOR_CLOUD_SKIP_CADDY:-}" = 1 ]; then
  warn "Caddy-Prüfung ausgelassen (TOR_CLOUD_SKIP_CADDY=1) - vor dem Livegang ohne diese Variable wiederholen."
elif command -v caddy >/dev/null; then
  if [ -f "$CADDYFILE" ]; then
    caddy validate --config "$CADDYFILE" --adapter caddyfile >/dev/null 2>&1 && ok "Caddyfile gültig" || bad "caddy validate meldet Fehler in $CADDYFILE."
    grep -q 'reverse_proxy 127.0.0.1:' "$CADDYFILE" && ok "Caddy leitet an 127.0.0.1 weiter" || bad "$CADDYFILE leitet nicht an 127.0.0.1 weiter."
  else
    bad "$CADDYFILE fehlt."
  fi
else
  bad "caddy ist nicht installiert - ohne HTTPS nicht live schalten."
fi

# ---------------------------------------------------------------- Laufender Dienst
if command -v systemctl >/dev/null && systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
  port="$( [ -f "$ENV_FILE" ] && value PORT)"; port="${port:-8787}"
  health="$(curl -fsS --max-time 3 "http://127.0.0.1:${port}/api/health" 2>/dev/null || true)"
  if [ -n "$health" ]; then
    printf '%s' "$health" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const j=JSON.parse(s);process.exit(j.ok===true&&j.demo===false?0:1)})' \
      && ok "/api/health antwortet im Livemodus" || bad "/api/health meldet Demomodus oder Fehler: $health"
  else
    bad "Dienst $SERVICE läuft, aber /api/health antwortet nicht."
  fi
else
  warn "Dienst $SERVICE läuft nicht - /api/health nicht geprüft."
fi

echo
if [ "$FAILS" -gt 0 ]; then printf 'Ergebnis: %s FEHLER - nicht live schalten.\n' "$FAILS"; exit 1; fi
echo "Ergebnis: bereit."
