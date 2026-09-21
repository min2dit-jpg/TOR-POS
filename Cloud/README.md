# TOR POS Cloud v0.8.0 R48 · Stand R125

Gemeinsamer Entwicklungsstand mit TOR POS Desktop R48. Lokale Demo, keine Fiskal-Produktivfreigabe.

Start: `START-TOR-CLOUD.bat` (Windows), oder `TOR_CLOUD_DEMO=true node server.js`.
Node 22.13+ mit `node:sqlite`. Standard: 127.0.0.1:8787.
Demo: demo@torpos.local / TorDemo2026!; Gerät DEMO-KASSE-01 / tor-demo-device-token-2026.

## R155 neu: TOR Mail ohne Kunden-Google/SMTP
- Standardweg für automatische Monatsberichte und DATEV-Dateien: Der Kunde trägt an der Kasse nur die Empfänger-E-Mail ein.
- Die Kasse authentifiziert sich mit ihrem vorhandenen Gerätetoken an `POST /api/v1/devices/mail/send`.
- Absender und SMTP-Zugangsdaten liegen ausschließlich als Server-Secrets in `/etc/tor-pos-cloud.env`; sie werden nie an die Kasse ausgeliefert.
- Erlaubt sind nur PDF/CSV, maximal 10 Anhänge, 6 MB je Datei und 8 MB insgesamt.
- Standardlimits pro registrierter Kasse: 20 Sendungen/Stunde und 100/24 Stunden.
- Das Versandjournal speichert Empfänger/Betreff nur als Hash sowie Status/Größe; Mailtext und Anhänge werden nicht als Mailjournal archiviert.
- Google OAuth und kundeneigenes SMTP bleiben als Alternativen vorhanden.
- Für produktive Zustellbarkeit einen TOR-eigenen Mail-Absender mit SPF, DKIM und DMARC konfigurieren. Beispielvariablen stehen in `deploy/tor-pos-cloud.env.example`.
- `GET /api/health` meldet `managed_mail_configured=true`, sobald die zentrale Mailkonfiguration vollständig ist.

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
- R178: Kunden verwenden standardmäßig `https://updates.torpos.de/`; Server-Origin wird mit `TOR_UPDATE_PUBLIC_URL` festgelegt.
- `PUBLISH-UPDATE.ps1` prüft/signiert Setup ve `-Channel PILOT|STABLE` ile ayrı manifestleri yayınlar.
- PILOT saha kabulünden sonra STABLE'a terfi edilir; `DISABLE-UPDATE.ps1 -Channel ...` kanalı anında kapatır.

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
| `Caddyfile.example` | HTTPS (TLS 1.2/1.3) mit automatischem Let's-Encrypt-Zertifikat vor `127.0.0.1:8787`; R178: getrennte `api.<domain>`, `bon.<domain>` und `updates.<domain>` Origins |

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
R178'den itibaren normal müşteriler STABLE, seçili saha testleri PILOT kanalını kullanır.

PILOT:
`powershell -ExecutionPolicy Bypass -File .\PUBLISH-UPDATE.ps1 -SetupPath C:\Build\TOR-POS-Pro-Setup.exe -Version 0.7.33.878 -Revision R178 -SignerThumbprint ABCD... -Channel PILOT -ReleaseNotes "..."`

STABLE:
`powershell -ExecutionPolicy Bypass -File .\PUBLISH-UPDATE.ps1 -SetupPath C:\Build\TOR-POS-Pro-Setup.exe -Version 0.7.33.878 -Revision R178 -SignerThumbprint ABCD... -Channel STABLE -ReleaseNotes "..."`

`updates/manifest.json` (STABLE) ve `updates/pilot-manifest.json` bu pakette bilinçli olarak `enabled=false` başlar. Uzaktan müşteri kurulumu için ayrıca TOR code-signing sertifikasının thumbprint'i Desktop build içine pinlenmiş olmalıdır.

## Prüfung
`npm run check` und `npm test`. Stand R155: 48/48 Cloud-Tests.


> Güvenlik notu: Canlı/uzak otomatik güncelleme, TOR/Demirkaan GmbH code-signing sertifikasının thumbprint değeri `TorRelease.UpdateSignerThumbprint` içine sabitlenmeden bilinçli olarak engellenir. Manifest içindeki thumbprint tek başına güven kaynağı değildir.
>
> R114: Localhost muafiyeti artık **yalnızca Debug derlemelerinde** geçerlidir; müşteriye giden Release derlemesi localhost dahil her yerde imza doğrular.
> R120: Sunucu, `/updates/` üzerinden gönderdiği dosyayı her indirmede manifest hash'iyle yeniden doğrular (uyuşmazsa 409). Ters vekil arkasında hız sınırlaması için `TOR_CLOUD_TRUST_PROXY=true` gerekir.


### R48 Abholnummer
`pickup_number` wird bei IMBISS-Verkäufen zusammen mit der unveränderten Bonnummer synchronisiert. Im Portal erscheint sie in Dashboard, Verkaufshistorie, Bon-Historie und Bon-Details. Sie ist operative Ausgabe-/Bestellnummer und keine Fiskalnummer.

## R62 · Google OAuth QR / Gmail API

R62 adds a device-authenticated QR pairing flow for `MIT GOOGLE ANMELDEN`. Production use requires a public HTTPS `TOR_CLOUD_PUBLIC_URL`, a Google OAuth **Web application** client, and `TOR_CLOUD_GOOGLE_TOKEN_KEY`. See `R62-GOOGLE-OAUTH-SETUP.md`.

The Cloud stores the Google refresh token only in AES-256-GCM protected form. The POS receives short-lived access tokens; monthly report PDFs are sent directly by the POS to the Gmail API and are not uploaded through TOR POS Cloud.


## 7-Tage Desktop-Demo

Die öffentliche Windows-Demo ist vom lokalen Cloud-Demomodus zu unterscheiden.

- Aktivierung: `POST /api/v1/trial/activate`
- Download: `GET /api/v1/trial/download`
- Laufzeit: exakt 7 Tage ab erster Serveraktivierung
- Demo-Identität: zufällige 256-Bit Trial-ID in `%ProgramData%\TOR-POS-Pro\trial-installation.id`
- keine MachineGuid, Laufwerksseriennummer oder andere Hardwarekennung wird verwendet oder übertragen
- der Installer lässt die Trial-ID bei einer normalen Deinstallation bestehen; Neuinstallation verlängert die Demo nicht
- Setup-Publishing: `PUBLISH-DEMO.ps1`
- Abschalten: `DISABLE-DEMO.ps1`
- Manifest: `trial-manifest.json`, getrennt vom normalen `manifest.json`

Das Demo-Setup wird nur ausgeliefert, wenn es vorher mit dem freigegebenen
Code-Signing-Zertifikat geprüft und veröffentlicht wurde. Der Server prüft den
SHA-256-Wert beim Download erneut.

Details: `../Dokumentation/TORPOS-DEMO-7-TAGE.md`.
