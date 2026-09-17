# Digitaler Kassenbon über TOR Cloud (R145)

Der Kunde wählt nach dem Bezahlen **Papierbeleg** oder **Digitalbeleg (QR)**. Beim
Digitalbeleg scannt er den QR-Code und öffnet ohne Anmeldung die Seite
**„Ihr digitaler Kassenbon“**: Der Bon ist direkt am Bildschirm lesbar, darunter stehen
`PDF herunterladen`, `Teilen` und `Drucken`.

## Rechtlicher Rahmen

| Regel | Inhalt | Umsetzung in TOR |
|---|---|---|
| § 146a Abs. 2 AO, § 6 Satz 5 KassenSichV | Beleg in Papierform oder elektronisch | Wahl Papierbeleg / Digitalbeleg |
| AEAO zu § 146a Nr. 2.5.2 | Transaktion vor Bereitstellung des Belegs abschließen | Die Wahl erscheint erst nach dem abgeschlossenen, TSE-gesicherten Verkauf |
| AEAO zu § 146a Nr. 2.5.3 | Elektronischer Beleg nur mit Zustimmung des Kunden, formlos, auch konkludent | Die Wahl „Digitalbeleg“ ist die Zustimmung; sie wird im Audit-Protokoll der Kasse festgehalten (`RECEIPT_CHANNEL`). Enter, Escape und Schließen bedeuten Papier |
| AEAO zu § 146a Nr. 2.5.4 | Anzeige nur am Bildschirm des Unternehmers reicht nicht | Der Kunde erhält den Beleg auf seinem eigenen Gerät |
| AEAO zu § 146a Nr. 2.5.6 | Standardisiertes Datenformat, mit kostenfreier Standardsoftware sichtbar; QR-Code und Download-Link ausdrücklich zulässig | siehe unten |
| AEAO zu § 146a Nr. 2.5.7 | Ausgabe in unmittelbarem zeitlichem Zusammenhang mit dem Vorgangsende | Ist TOR Cloud nicht erreichbar (Zeitlimit 10 s), gibt die Kasse sofort den Papierbeleg aus |
| § 6 KassenSichV, AEAO zu § 146a Nr. 2.4.4 | Pflichtangaben des Belegs | Der Digitalbeleg wird aus denselben Daten erzeugt wie der Papierbeleg; fehlt eine Pflichtangabe, wird er – wie der Druck – nicht ausgegeben |

**Zum Datenformat:** Der elektronische Beleg wird nach AEAO zu § 146a Nr. 2.5.6 in einem
standardisierten Datenformat zur Verfügung gestellt. TOR bietet standardmäßig einen
PDF-Download an.

## Zwei getrennte Bereiche

| | TOR Fiscal Archive | TOR Digital Receipt Cloud |
|---|---|---|
| Was | Kassen-DB, TSE, DSFinV-K-Export und die übrigen aufbewahrungspflichtigen Kassendaten | Die Kundenkopie des Bons, die der Kunde über den QR-Code sieht |
| Wo | auf der Kasse (TOR POS Pro) | `bon.<domain>` in TOR Cloud |
| Wie lange | nach den gesetzlichen Aufbewahrungsfristen | zeitlich begrenzt, Standard 90 Tage (`TOR_CLOUD_RECEIPT_TTL_DAYS`) |
| Danach | – | Token, PDF und Beleginhalt werden automatisch gelöscht |

Der Digital-Receipt-Bereich ist **kein Archiv** und ersetzt das Fiscal Archive nie. Die
Kasse sendet dorthin nichts, was sie selbst aufbewahren müsste, und nichts dort wird
zurückgelesen; das Löschen eines abgelaufenen Links berührt die Daten auf der Kasse nicht.

**Aufbewahrung – was gilt und was nicht:**

- Eine Rechtsregel, nach der Belege in der Cloud nur 30 bis 90 Tage gespeichert werden
  dürfen, gibt es nicht. Die begrenzte Laufzeit ist eine Festlegung von TOR aus der
  Datenminimierung und Speicherbegrenzung (Art. 5 Abs. 1 lit. c und e DSGVO).
- Davon getrennt gelten die Aufbewahrungsfristen des § 147 AO – je nach Unterlage
  10 Jahre, 8 Jahre für Buchungsbelege, 6 Jahre für weitere steuerlich bedeutsame
  Unterlagen. Sie werden mit dem Fiscal Archive erfüllt.
- Wo Kassendaten liegen, ist nicht allein entscheidend; bei Außenprüfung und
  Kassen-Nachschau (§ 146b AO) müssen die erforderlichen Daten kurzfristig verfügbar und
  auswertbar sein (§ 147 Abs. 6 AO). Das leisten die Kasse selbst und ihr
  DSFinV-K-/TSE-Export – nicht der 90-Tage-Link.

## Ablauf

1. TSE `FinishTransaction` – der Verkauf ist abgeschlossen und unveränderlich.
2. Der Kassenbon entsteht (dieselben Daten wie der Papierbeleg).
3. Die Kasse zeigt die Wahl `Papierbeleg` | `Digitalbeleg (QR)`.
4. Digitalbeleg: TOR POS → `POST https://api.<domain>/api/v1/devices/receipts` mit
   Geräte-Authentifizierung; gesendet werden nur die Angaben des Bons.
5. TOR Cloud prüft Form und Summen, legt den Beleg an und erzeugt das PDF, einen
   256-Bit-Token (gespeichert wird nur sein SHA-256-Hash), `created_at`, `expires_at`,
   zugeordnet dem Mandanten (`business_id`) und der Kasse.
6. Die Kasse zeigt den QR-Code (Kassenbildschirm und Kundendisplay):
   `https://bon.<domain>/r/<token>`.
7. Der Kunde öffnet den Link ohne Anmeldung.
8. Nach Ablauf: Token-Hash, PDF und Beleginhalt werden gelöscht; das Fiscal Archive
   bleibt unberührt.

Fordert die Kasse denselben Beleg erneut an (Antwort unterwegs verloren), bekommt er einen
neuen Token – der alte, nie angezeigte hört auf zu gelten, die Laufzeit verlängert sich
nicht. Ein anderer Inhalt unter derselben Referenz wird abgelehnt (409): ein Kassenbon ist
unveränderlich.

## Zwei Sicherheitsbereiche

| Domain | Zweck | Zugang |
|---|---|---|
| `api.<domain>` | Kasse ↔ Cloud, Kundenportal, Updates | Authentifizierung zwingend (Gerätetoken, Portal-Login mit 2FA) |
| `bon.<domain>` | Digitaler Kassenbon für Kunden | kein Login, nur der kryptografische Beleg-Token |

Der Server trennt beide nach dem Host: Auf `bon.<domain>` gibt es nur `/r/<token>`,
`/r/<token>/pdf`, die Seitenelemente und `robots.txt` – keine API, kein Portal, keine
Updates, keine Uploads (405). Auf jeder anderen Domain gibt es keinen Beleg. Beide
Domains müssen verschieden sein, sonst startet der Server nicht.

## HTTPS / TLS

- HTTP → HTTPS: Caddy leitet automatisch um; hinter dem Proxy leitet zusätzlich der Server
  jede als HTTP angekommene Anfrage mit 308 auf die konfigurierte HTTPS-Adresse um.
- TLS 1.2 und TLS 1.3, sonst nichts (`deploy/Caddyfile.example`).
- HSTS: `Strict-Transport-Security: max-age=31536000; includeSubDomains`.
- Sicherheits-Header auf jeder Antwort der Bon-Domain: `Content-Security-Policy`
  (`default-src 'none'`, Skripte und Styles nur von der eigenen Domain),
  `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer` (der Link ist der Schlüssel und darf nirgendwohin
  weitergegeben werden), `Permissions-Policy`, `Cross-Origin-Opener-Policy`,
  `Cross-Origin-Resource-Policy`.
- Cookies: Die Bon-Domain setzt keine. Das Portal auf `api.<domain>` verwendet
  `Secure`, `HttpOnly`, `SameSite=Lax` (`COOKIE_SECURE=true`).
- Kein Caching: `Cache-Control: private, no-store` auf Seite und PDF.
- Keine Suchmaschinen: `X-Robots-Tag: noindex, nofollow, noarchive, nosnippet` auf jeder
  Antwort (auch PDF), dazu `<meta name="robots">`. `robots.txt` sperrt bewusst nichts –
  eine gesperrte Seite zeigt ihr `noindex` keiner Suchmaschine, und ein irgendwo
  veröffentlichter Link könnte sonst als nackte URL im Index landen.
- Protokolle: Der Server schreibt Beleg-Pfade nur als `/r/[token]`. Für `bon.<domain>`
  kein Caddy-Zugriffsprotokoll einschalten (die Vorlage tut es nicht).
- Wer viele unbekannte Links abfragt, erhält nach 60 Fehlversuchen in 10 Minuten
  429; ein gültiger Link funktioniert immer.

## Welche Daten TOR Cloud erhält

Firmenname, Anschrift, Steuernummer/USt-IdNr., Bonnummer, Datum/Uhrzeit, Abholnummer,
Positionen (Bezeichnung, Menge, Einzelpreis, Betrag, Steuersatz, Angebot), Zwischensumme,
Rabatt, Gesamtbetrag, MwSt.-Aufstellung, Zahlarten mit Beträgen, die Pflichtangaben der
TSE (Seriennummer Kasse und TSE, Transaktionsnummer, Signaturzähler, Vorgangsbeginn/-ende,
Bestellbeginn, Prüfwert) und die Fußzeile des Bons. **Nicht:** Bedienername, gegebenes
Geld, Kundendaten, Artikelnummern. Unbekannte Felder verwirft der Server, bevor er
speichert.

**Löschen:** Die stündliche Aufräumroutine löscht abgelaufene Belege vollständig;
abgelaufene Links liefern schon vorher 404. Die Datenbank überschreibt gelöschte Inhalte
(`secure_delete`). In Datensicherungen (`TOR_CLOUD_BACKUP_DIR`) bleibt ein Beleg so lange
enthalten, bis die Sicherung selbst rotiert (`TOR_CLOUD_BACKUP_KEEP`, Standard 14).

## Betrieb

1. DNS: `api.<domain>` und `bon.<domain>` auf den Server.
2. `deploy/Caddyfile.example` anpassen (beide Domains).
3. `/etc/tor-pos-cloud.env`:
   `TOR_CLOUD_PUBLIC_URL=https://api.<domain>`,
   `TOR_CLOUD_RECEIPT_URL=https://bon.<domain>`,
   optional `TOR_CLOUD_RECEIPT_TTL_DAYS` (1–366, Standard 90),
   `TOR_CLOUD_IMPRINT_URL` und `TOR_CLOUD_PRIVACY_URL`.
4. Kasse: TOR Cloud unter Einstellungen › Geräte einrichten und aktivieren
   (`https://api.<domain>`), dann unter Einstellungen › Firma & Bon
   „Digitaler Kassenbon (TOR Cloud)“ einschalten.

## Vom Betreiber zu klären (keine Programmfrage)

- **Impressum und Datenschutzhinweis** der Bon-Domain (§ 5 DDG, Art. 13 DSGVO): Adressen in
  `TOR_CLOUD_IMPRINT_URL` / `TOR_CLOUD_PRIVACY_URL`; der Server warnt im Livebetrieb, wenn
  sie fehlen.
- **Auftragsverarbeitung** (Art. 28 DSGVO): TOR verarbeitet die Belegdaten im Auftrag des
  Händlers; ein AV-Vertrag mit den Händlern gehört vor den Livebetrieb.
- **Verfahrensdokumentation** des Händlers: Belegausgabe (Papier/digital, Ausfallweg Papier).
