#!/usr/bin/env bash
# C-1: TOR Mail limit on the production Caddy, in one step.
#
#   sudo bash apply-c1-caddy.sh [/etc/caddy/Caddyfile] [api.]
#
# 1. rewrites only the request_body limit of the api.* site (c1-caddy-patch.js)
#    in a copy - the running Caddyfile is not touched yet
# 2. apply-caddyfile.sh: caddy validate on the copy, backup
#    (Caddyfile.vor-<time>), install, systemctl reload caddy; a failed reload
#    puts the backup back
# Running it twice changes nothing.
set -euo pipefail

CADDYFILE="${1:-/etc/caddy/Caddyfile}"
SITE="${2:-api.}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[ -f "$CADDYFILE" ] || { echo "Caddyfile nicht gefunden: $CADDYFILE" >&2; exit 1; }
command -v node >/dev/null || { echo "node fehlt (wird für den Patch gebraucht)." >&2; exit 1; }
command -v caddy >/dev/null || { echo "caddy fehlt im PATH." >&2; exit 1; }

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
cp "$CADDYFILE" "$WORK/Caddyfile"

if ! RESULT="$(node "$HERE/c1-caddy-patch.js" "$WORK/Caddyfile" "$SITE")"; then
  echo "Nichts geändert, Caddy wurde NICHT neu geladen." >&2; exit 1
fi
if [ "$RESULT" = "ALREADY" ]; then
  echo "Bereits angepasst - nichts zu tun."; exit 0
fi

bash "$HERE/apply-caddyfile.sh" "$WORK/Caddyfile" "$CADDYFILE"
echo "Fertig: TOR Mail bis 12 MiB, alle anderen Routen weiter 2 MB."
