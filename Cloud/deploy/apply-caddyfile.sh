#!/usr/bin/env bash
# TOR POS Cloud - eine neue Caddy-Konfiguration sicher übernehmen.
#
#   sudo bash apply-caddyfile.sh <neue Caddyfile> [/etc/caddy/Caddyfile]
#
# 1. caddy validate prüft die NEUE Datei, bevor die laufende angefasst wird
# 2. Sicherung der laufenden Datei (<Caddyfile>.vor-<Zeit>)
# 3. neue Datei an ihren Platz, systemctl reload caddy
# 4. schlägt das Neuladen fehl, kommt die Sicherung zurück und wird neu geladen
# Ist die neue Datei gleich der laufenden, passiert nichts.
# Auch von apply-c1-caddy.sh benutzt.
set -euo pipefail

NEW="${1:-}"
CADDYFILE="${2:-/etc/caddy/Caddyfile}"

fail() { printf 'FEHLER: %s\n' "$*" >&2; exit 1; }
[ -n "$NEW" ] && [ -f "$NEW" ] || fail "Aufruf: sudo bash $0 <neue Caddyfile> [/etc/caddy/Caddyfile]"
command -v caddy >/dev/null || fail "caddy fehlt im PATH."

if [ -f "$CADDYFILE" ] && cmp -s "$NEW" "$CADDYFILE"; then
  echo "Unverändert - nichts zu tun."; exit 0
fi

# Validate a copy with the target's name so relative imports resolve alike.
CHECK="$(mktemp "$(dirname "$CADDYFILE")/.Caddyfile.pruefung.XXXXXX")"
trap 'rm -f "$CHECK"' EXIT
cat "$NEW" > "$CHECK"
if ! caddy validate --config "$CHECK" --adapter caddyfile; then
  fail "Die neue Konfiguration ist ungültig - die laufende Caddyfile wurde NICHT verändert."
fi

BACKUP=""
if [ -f "$CADDYFILE" ]; then
  BACKUP="$CADDYFILE.vor-$(date +%Y%m%d-%H%M%S)"
  cp -p "$CADDYFILE" "$BACKUP"
  echo "Sicherung: $BACKUP"
  cat "$CHECK" > "$CADDYFILE"   # keeps owner and mode of the running file
else
  install -m 644 "$CHECK" "$CADDYFILE"
fi

if ! systemctl reload caddy; then
  if [ -n "$BACKUP" ]; then
    cp -p "$BACKUP" "$CADDYFILE"
    systemctl reload caddy || true
    fail "Caddy ließ sich nicht neu laden - die vorherige Caddyfile ist wiederhergestellt."
  fi
  fail "Caddy ließ sich nicht neu laden (keine vorherige Caddyfile vorhanden). journalctl -u caddy prüfen."
fi
echo "Caddy neu geladen."
[ -z "$BACKUP" ] || echo "Rückgängig: sudo bash $0 '$BACKUP' '$CADDYFILE'"
