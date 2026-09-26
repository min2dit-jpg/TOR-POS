# TOR Cloud 0.14.0 – Produktivsetzung (Checkliste)

Diese Liste ist der **einzige** Ablauf für den Tag, an dem der Server da ist.
Alles, was vorher ohne Server geprüft werden kann, ist in `Cloud/tests` automatisiert
(`npm test`): Update, Zurücksetzen, Caddy-Übernahme, Erstinstallation, Vorabprüfung,
Wiederherstellung, Livemodus-Konfiguration.

Nicht Teil dieser Liste: Server kaufen, DNS ändern, Kundenkonten anlegen – das
geschieht erst nach ausdrücklicher Freigabe („VPS alındı, deploy et“).

## 0. Voraussetzungen

| Punkt | Wert |
|---|---|
| Server | eigener Linux-VPS, **Ubuntu 24.04 LTS**, ≥ 2 GB RAM, ≥ 20 GB SSD |
| Domains | `api.torpos.de` und `bon.torpos.de` (A/AAAA auf die Server-IP) |
| Zugang | SSH mit Schlüssel, eigener Admin-Benutzer mit `sudo` |
| Quelle | GitHub-ZIP von `main` (oder dem freigegebenen Tag), enthält `Cloud/` |

Webhosting-Pakete (nur PHP/FTP) reichen **nicht**: TOR Cloud ist ein dauerhaft
laufender Node-Dienst mit SQLite.

## 1. Grundsystem

```bash
sudo apt update && sudo apt -y full-upgrade
sudo apt -y install curl unzip rsync ufw ca-certificates gnupg
sudo timedatectl set-timezone Europe/Berlin
# Firewall: nur SSH, HTTP (Zertifikat/Weiterleitung) und HTTPS
sudo ufw allow 22/tcp && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
sudo ufw --force enable && sudo ufw status
```

Node **22.13 oder neuer** (Ubuntu 24.04 liefert nur Node 18 – `node:sqlite` fehlt dort):

```bash
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
sudo apt -y install nodejs && node -v     # v22.13+ oder v24
```

## 2. TOR Cloud installieren

```bash
cd /tmp && curl -L -o TOR-POS-main.zip https://github.com/min2dit-jpg/TOR-POS/archive/refs/heads/main.zip
unzip -q TOR-POS-main.zip
sudo bash /tmp/TOR-POS-main/Cloud/deploy/install-cloud.sh /tmp/TOR-POS-main/Cloud
```

Legt Benutzer `torcloud`, `/opt/tor-pos-cloud` (+ `data/`, `updates/`),
`/var/backups/tor-pos-cloud`, `/etc/tor-pos-cloud.env` (chmod 600) und den
systemd-Dienst an. **Erster Lauf endet mit Exit 3** – die Schlüssel sind noch
Platzhalter. Das ist gewollt.

## 3. Umgebungsdatei ausfüllen

```bash
sudo nano /etc/tor-pos-cloud.env
```

| Variable | Wert |
|---|---|
| `TOR_CLOUD_TOTP_KEY` | `openssl rand -base64 36` (nur hier, nie ins Repo) |
| `TOR_CLOUD_PUBLIC_URL` | `https://api.torpos.de` |
| `TOR_CLOUD_RECEIPT_URL` | `https://bon.torpos.de` |
| `TOR_CLOUD_IMPRINT_URL` / `TOR_CLOUD_PRIVACY_URL` | echte Impressum-/Datenschutz-Seiten |
| `TOR_MAIL_*` | TOR-eigener Absender mit SPF/DKIM/DMARC (sonst leer = TOR Mail aus) |
| `TOR_GOOGLE_*`, `TOR_CLOUD_GOOGLE_TOKEN_KEY` | leer lassen, solange Gmail-Versand nicht gebraucht wird |

Bleiben unverändert: `TOR_CLOUD_DEMO=false`, `COOKIE_SECURE=true`,
`TOR_CLOUD_TRUST_PROXY=true`, `HOST=127.0.0.1`, `PORT=8787`.
Der Server verweigert den Start, solange ein Platzhalter (`REPLACE-WITH…`,
`CHANGE-ME`, `PLACEHOLDER`) in einem Schlüssel steht oder eine HTTPS-Adresse
ohne `COOKIE_SECURE=true` konfiguriert ist.

Dann dasselbe Skript noch einmal – jetzt startet es den Dienst und prüft
`/api/health` im Livemodus:

```bash
sudo bash /tmp/TOR-POS-main/Cloud/deploy/install-cloud.sh /tmp/TOR-POS-main/Cloud
curl -s http://127.0.0.1:8787/api/health   # "ok":true, "version":"0.14.0", "demo":false
```

## 4. Caddy (HTTPS)

Offizielles Caddy-Paket (aktueller als Ubuntu):

```bash
sudo apt -y install debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update && sudo apt -y install caddy
```

Konfiguration übernehmen – geprüft **bevor** die laufende Datei ersetzt wird, bei
Fehlern bleibt die alte aktiv:

```bash
sudo bash /opt/tor-pos-cloud/deploy/apply-caddyfile.sh /opt/tor-pos-cloud/deploy/Caddyfile.example
```

(Andere Domains: `Caddyfile.example` vorher kopieren und `api.torpos.de` /
`bon.torpos.de` ersetzen.) Caddy holt die Zertifikate selbst, sobald DNS zeigt.

Enthalten: TLS 1.2/1.3, HSTS, Weiterleitung auf `127.0.0.1:8787`,
`X-Forwarded-For` wird überschrieben (Anmeldebremse), Upload-Grenzen:
TOR Mail (Berichte/DATEV-Anhänge) 12 MiB, übrige API 2 MB, Bon-Domain 16 KB,
kein Zugriffslog auf der Bon-Domain.

## 5. Vorabprüfung – muss „bereit“ melden

```bash
sudo bash /opt/tor-pos-cloud/deploy/preflight-cloud.sh
```

Prüft Umgebungsdatei (Rechte 600/root, keine Platzhalter, Livemodus, sichere
Cookies, Proxy, lokale Adresse, zwei HTTPS-Domains, Impressum), Ordner und
Besitzer, Node-Version, Dienstdatei, `caddy validate`, `/api/health`.
**Bei einem FEHLER nicht live schalten.**

## 6. Smoke-Test von außen

```bash
curl -sI https://api.torpos.de/ | head -1                 # 200/302 über HTTPS
curl -s  https://api.torpos.de/api/health                 # ok, 0.14.0, demo:false
curl -sI http://api.torpos.de/ | grep -i location         # Weiterleitung auf https
curl -sI https://bon.torpos.de/ | head -1                 # Bon-Domain antwortet
```

Dann im Browser: Portal-Anmeldeseite unter `https://api.torpos.de`, Zertifikat gültig.
Ersten Kunden erst nach Freigabe anlegen (`npm run provision`, siehe README).

## 7. Datensicherung und Wiederherstellung prüfen

```bash
ls -l /var/backups/tor-pos-cloud/          # tor-cloud-<Zeit>Z.db vom Start
```

Wiederherstellung einmal üben (legt die aktuelle Datenbank beiseite, nichts wird gelöscht):

```bash
sudo bash /opt/tor-pos-cloud/deploy/restore-cloud-db.sh /var/backups/tor-pos-cloud/tor-cloud-<Zeit>Z.db
```

Den Sicherungsordner zusätzlich **außerhalb** des Servers ablegen (z. B. täglicher
`rsync`/`rclone` auf einen Speicher eines anderen Anbieters) – eine Sicherung auf
derselben Platte überlebt keinen Plattenausfall.

## 8. Update- und Rollback-Test

Einmal mit derselben Version durchspielen (ändert nichts, beweist den Ablauf):

```bash
sudo bash /opt/tor-pos-cloud/deploy/update-cloud.sh /tmp/TOR-POS-main.zip
ls -d /opt/tor-pos-cloud.sicherung-*                     # Sicherung vorhanden
sudo bash /opt/tor-pos-cloud/deploy/update-cloud.sh /opt/tor-pos-cloud.sicherung-<Zeit>   # Rollback-Weg
curl -s http://127.0.0.1:8787/api/health
```

Das Update-Skript bricht bei Syntaxfehlern ab, bevor der Dienst angefasst wird;
startet die neue Version nicht, meldet sie eine andere Version oder stirbt sie
kurz nach dem Start, wird automatisch der alte Code zurückgesetzt und geprüft,
dass wieder die alte Version antwortet. Die letzten 5 Sicherungen bleiben.

## 9. Abschluss

- [ ] `preflight-cloud.sh` meldet „bereit“
- [ ] `https://api.torpos.de/api/health` → `0.14.0`, `demo:false`
- [ ] Wiederherstellung geübt, externe Sicherung eingerichtet
- [ ] Update/Rollback einmal durchgespielt
- [ ] `/etc/tor-pos-cloud.env` nur auf dem Server (und im Passwortmanager), nie im Repo
- [ ] Erst danach: erster Kunde, Kassen koppeln (`TOR_CLOUD_PUBLIC_URL` in der Kasse)

Grüne Tests und ein grüner Smoke-Test ersetzen keine Prüfung mit echter TSE,
echtem Kartenterminal oder echtem Drucker; die fiskalischen Produktionsschalter
der Kasse bleiben davon unberührt.
