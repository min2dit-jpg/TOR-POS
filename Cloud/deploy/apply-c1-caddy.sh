#!/usr/bin/env bash
# C-1: TOR Mail limit on the production Caddy, in one step.
#
#   sudo bash apply-c1-caddy.sh [/etc/caddy/Caddyfile] [api.]
#
# 1. backs up the Caddyfile (Caddyfile.vor-c1-<time>)
# 2. rewrites only the request_body limit of the api.* site (c1-caddy-patch.js)
# 3. caddy validate - on any error the backup is restored and nothing reloads
# 4. systemctl reload caddy (running connections stay open)
# Running it twice changes nothing.
set -euo pipefail

CADDYFILE="${1:-/etc/caddy/Caddyfile}"
SITE="${2:-api.}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[ -f "$CADDYFILE" ] || { echo "Caddyfile nicht gefunden: $CADDYFILE" >&2; exit 1; }
command -v node >/dev/null || { echo "node fehlt (wird für den Patch gebraucht)." >&2; exit 1; }
command -v caddy >/dev/null || { echo "caddy fehlt im PATH." >&2; exit 1; }

BACKUP="$CADDYFILE.vor-c1-$(date +%Y%m%d-%H%M%S)"
cp -p "$CADDYFILE" "$BACKUP"
echo "Sicherung: $BACKUP"

restore() { cp -p "$BACKUP" "$CADDYFILE"; echo "Zurückgesetzt auf die Sicherung. Caddy wurde NICHT neu geladen." >&2; }

if ! RESULT="$(node "$HERE/c1-caddy-patch.js" "$CADDYFILE" "$SITE")"; then
  restore; exit 1
fi
if [ "$RESULT" = "ALREADY" ]; then
  echo "Bereits angepasst - nichts zu tun."; rm -f "$BACKUP"; exit 0
fi

if ! caddy validate --config "$CADDYFILE" --adapter caddyfile; then
  restore; exit 1
fi

systemctl reload caddy
echo "Fertig: TOR Mail bis 12 MiB, alle anderen Routen weiter 2 MB. Caddy neu geladen."
echo "Rückgängig: sudo cp -p '$BACKUP' '$CADDYFILE' && sudo systemctl reload caddy"
