# TOR POS Cloud v0.8.0 R48 · Stand R125

Gemeinsamer Entwicklungsstand mit TOR POS Desktop R48. Lokale Demo, keine Fiskal-Produktivfreigabe.

Start: `START-TOR-CLOUD.bat` (Windows), oder `TOR_CLOUD_DEMO=true node server.js`.
Node 22.13+ mit `node:sqlite`. Standard: 127.0.0.1:8787.
Demo: demo@torpos.local / TorDemo2026!; Gerät DEMO-KASSE-01 / tor-demo-device-token-2026.

## R149 neu: Leergut und Verkaufsereignisse
- `sale.completed` akzeptiert `discount_cents` (ältere Kassen: nur `manual_discount_cents`), `MIXED` und eine
  Pfand-Auszahlung (negativer Gesamtbetrag, nur bar). Vorher lehnte die Cloud jeden Verkauf einer echt buchenden
  Kasse ab. Vertragsdatei: `tests/fixtures/sale-completed-kasse.json` (Desktop R149ReviewTests prüft dieselbe Datei).

## R145 neu: Digitaler Kassenbon
- Nach dem Verkauf wählt der Kunde an der Kasse `Papierbeleg` oder `Digitalbeleg (QR)`.
- Die Kasse sendet nur die Angaben des Bons an `POST /api/v1/devices/receipts` (Geräte-Authentifizierung);
  TOR Cloud erzeugt Seite und PDF und gibt den Link `https://bon.<domain>/r/<Token>` zurück (256 Bit, nur als Hash gespeichert).
- Seite „Ihr digitaler Kassenbon“: Bon direkt lesbar, `PDF herunterladen`, `Teilen`, `Drucken`.
  Der elektronische Beleg wird nach AEAO zu § 146a Nr. 2.5.6 in einem standardisierten Datenformat zur Verfügung gestellt;
  TOR bietet standardmäßig einen PDF-Download an.
- Eigene Domain (`TOR_CLOUD_RECEIPT_URL`), getrennt von API/Portal; TLS, HSTS, `noindex`, `Cache-Control: private, no-store`, keine Cookies.
- Nach `TOR_CLOUD_RECEIPT_TTL_DAYS` (Standard 90) werden Token, PDF und Inhalt gelöscht. Das ist **kein Archiv**:
  die aufbewahrungspflichtigen Kassendaten bleiben auf der Kasse.
- Details, Rechtsrahmen und Betrieb: `docs/DIGITALER-KASSENBON.md`.

## R48 neu
- Bestandssnapshots enthalten Einkaufspreis und individuellen Mindestbestand.
- Portal zeigt VK, EK, Bestand, Mindestbestand und Einkaufs-Warenwert.
- Niedrig-Bestand verwendet den pro Artikel konfigurierten Mindestbestand statt einer festen Grenze.
- R46 bleibt erhalten: `sale.completed` aktualisiert Cloud-Bestand idempotent; Desktop bündelt automatische Bestandssnapshots.

## R45 weiterhin enthalten
- TOTP 2FA für Portal-OWNER, mit 5-Minuten Login-Challenge und maximal 5 Codeversuchen.
- Portal `Sicherheit`: Authenticator manuell per Secret einrichten und 6-stelligen Code bestätigen.
- 8 einmalige Recovery-Codes; serverseitig nur SHA-256-Hashes.
- TOTP-Secret AES-256-GCM verschlüsselt. Live: `TOR_CLOUD_TOTP_KEY` (mind. 24 Zeichen) setzen.
- Live-Betrieb verlangt für OWNER standardmäßig 2FA; steuerbar mit `TOR_CLOUD_REQUIRE_OWNER_2FA`.
- Update-API: `/api/v1/updates/check` und kontrollierter `/updates/<Setup>` Download.
- `PUBLISH-UPDATE.ps1` kopiert das Setup, berechnet SHA-256 und erzeugt das Manifest.
- Produktive Update-URL sollte mit `TOR_CLOUD_PUBLIC_URL=https://...` fest vorgegeben werden.

## Bestehende Sicherheit
- Passwörter mit scrypt + Salt.
- Session-Cookie HttpOnly / SameSite=Lax; produktiv Secure-Cookie erforderlich.
- Herkunftsprüfung für Cookie-POSTs, Login-Rate-Limit, CSP, Tenant-Trennung.
- Gerätetoken werden nur als SHA-256 Hash gespeichert.
- Sale/Stock Sync bleibt idempotent und local-first.

## Produktiver Start
Ohne Demo-Modus: `TOR_CLOUD_DEMO=false`, HTTPS/Reverse Proxy, `COOKIE_SECURE=true`,
`TOR_CLOUD_PUBLIC_URL`, starkes `TOR_CLOUD_TOTP_KEY`.
Demo-Datenbank darf nicht im Live-Modus geöffnet werden.

R125 - fertige Vorlagen in `deploy/`:

| Datei | Zweck |
|---|---|
| `tor-pos-cloud.env.example` | alle Umgebungsvariablen für den Live-Betrieb, kommentiert |
| `tor-pos-cloud.service` | systemd-Dienst (Neustart bei Fehler, sauberes Beenden, gehärtet) |
| `Caddyfile.example` | HTTPS (TLS 1.2/1.3) mit automatischem Let's-Encrypt-Zertifikat vor `127.0.0.1:8787`, seit R145 für `api.<domain>` und `bon.<domain>` |

**Datensicherung:** mit `TOR_CLOUD_BACKUP_DIR` schreibt der Server im laufenden Betrieb
eine konsistente Kopie (`VACUUM INTO`), standardmäßig alle 24 h, die letzten 14 bleiben
(`TOR_CLOUD_BACKUP_INTERVAL_HOURS`, `TOR_CLOUD_BACKUP_KEEP`). Diesen Ordner zusätzlich
außerhalb des Servers ablegen. Wiederherstellen: Dienst stoppen, Sicherungsdatei als
`TOR_CLOUD_DB` zurückkopieren (vorhandene `-wal`/`-shm` daneben entfernen), Dienst starten.

**Aufräumen:** abgelaufene Sitzungen und 2FA-Anfragen werden stündlich gelöscht
(vorher nur, wenn genau diese Zeile wieder benutzt wurde - die Tabelle wuchs unbegrenzt).

## Kunden einrichten (R125)
Auf dem Server, im Cloud-Ordner - kein Web-Endpunkt, damit keine neue Angriffsfläche entsteht:

```
node tools/provision.js create-customer --customer TOR-000123 --name "Imbiss Beispiel" --owner-email inhaber@example.de --owner-name "Vorname Nachname"
node tools/provision.js add-branch --customer TOR-000123 --name "Filiale Mitte" --city Berlin
node tools/provision.js add-register --branch <Filial-ID> --device-code IMBISS-MITTE-01 --name "Kasse 1" --edition IMBISS
node tools/provision.js issue-device-token --device-code IMBISS-MITTE-01 --label "Kasse 1"
node tools/provision.js list --customer TOR-000123
```

Einmal-Passwort und Gerätetoken werden genau **einmal** angezeigt und nur als Hash gespeichert.
Weitere Befehle: `revoke-device-tokens`, `issue-device-token --revoke-existing` (Token tauschen),
`reset-owner-password` (beendet zugleich alle Sitzungen dieses Kontos). Hilfe: `node tools/provision.js help`.

Es gibt bewusst nur die Rolle `OWNER`: das Portal lässt keine andere Rolle zu, ein Mitarbeiterkonto
wäre daher nicht anmeldbar. Ein Rollenmodell (z. B. nur lesender Zugriff) ist eine eigene Produktentscheidung.

## Updates veröffentlichen
Das mit Code-Signing signierte Desktop-Setup erzeugen und dann z. B.:

`powershell -ExecutionPolicy Bypass -File .\PUBLISH-UPDATE.ps1 -SetupPath C:\Build\TOR-POS-Pro-Setup.exe -Version 0.7.33.48 -Revision R48 -SignerThumbprint ABCD... -ReleaseNotes "..."`

`updates/manifest.json` ist in diesem Paket absichtlich `enabled=false`.

## Prüfung
`npm run check` und `npm test`. Stand R149: 41/41 Tests.


> Güvenlik notu: Canlı/uzak otomatik güncelleme, TOR/Demirkaan GmbH code-signing sertifikasının thumbprint değeri `TorRelease.UpdateSignerThumbprint` içine sabitlenmeden bilinçli olarak engellenir. Manifest içindeki thumbprint tek başına güven kaynağı değildir.
>
> R114: Localhost muafiyeti artık **yalnızca Debug derlemelerinde** geçerlidir; müşteriye giden Release derlemesi localhost dahil her yerde imza doğrular.
> R120: Sunucu, `/updates/` üzerinden gönderdiği dosyayı her indirmede manifest hash'iyle yeniden doğrular (uyuşmazsa 409). Ters vekil arkasında hız sınırlaması için `TOR_CLOUD_TRUST_PROXY=true` gerekir.


### R48 Abholnummer
`pickup_number` wird bei IMBISS-Verkäufen zusammen mit der unveränderten Bonnummer synchronisiert. Im Portal erscheint sie in Dashboard, Verkaufshistorie, Bon-Historie und Bon-Details. Sie ist operative Ausgabe-/Bestellnummer und keine Fiskalnummer.

## R62 · Google OAuth QR / Gmail API

R62 adds a device-authenticated QR pairing flow for `MIT GOOGLE ANMELDEN`. Production use requires a public HTTPS `TOR_CLOUD_PUBLIC_URL`, a Google OAuth **Web application** client, and `TOR_CLOUD_GOOGLE_TOKEN_KEY`. See `R62-GOOGLE-OAUTH-SETUP.md`.

The Cloud stores the Google refresh token only in AES-256-GCM protected form. The POS receives short-lived access tokens; monthly report PDFs are sent directly by the POS to the Gmail API and are not uploaded through TOR POS Cloud.
