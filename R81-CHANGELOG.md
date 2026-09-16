# TOR POS R81 – QR-Code statt Bontext für TSE-Angaben

## Neu
Einstellungen -> Bon -> Druckverhalten: neuer Schalter "TSE-Angaben als
QR-Code statt als Text drucken (kürzerer Bon)". Standard: **aus** -
bestehende Bons drucken sich unverändert weiter.

Wenn aktiviert, druckt TOR POS für die fünf TSE-Textzeilen
(eAS/TSE/Transaktion/Signaturzähler/Prüfwert) stattdessen einen QR-Code
mit denselben Angaben. Der Bon wird dadurch kürzer - relevant vor allem
bei viel befahrenen Kassen mit knappem Bonpapierverbrauch.

## Technisch
- `TorPos.Core.TseQrCodePayload.Build(job)`: reine Funktion, baut die
  QR-Nutzlast aus den fünf Feldern (`eAS:..|TSE:..|TXN:..|CTR:..|CHK:..`).
  **Entwurf** - es gibt kein einzelnes vorgeschriebenes QR-Format für
  einen KassenSichV-Beleg; dieses Pipe-Format ist ein vernünftiger,
  menschenlesbarer Ausgangspunkt, nicht gegen eine bestimmte
  Prüfer-/App-Erwartung verifiziert.
- `StarMcPrint3PrinterService.DrawReceipt`: neue lokale `QrCode(...)`-
  Funktion (analog zur bestehenden `Logo(...)`-Funktion), rendert über
  das neu eingebundene `QRCoder`-NuGet-Paket (1.6.0) als Bitmap und
  zeichnet es zentriert über GDI+, genau wie das Bonlogo.
- **Kein ersatzloser Datenverlust bei QR-Fehler:** schlägt die
  QR-Erzeugung aus irgendeinem Grund fehl, fällt der Druck automatisch
  auf die fünf Textzeilen zurück - die fiskalen Angaben fehlen nie
  komplett, nur die Kurzform kann scheitern.
- `ValidateFiscalReceipt` (bereits vorhanden) prüft weiterhin
  unverändert VOR jedem Druck, dass alle fünf zugrunde liegenden Felder
  vorhanden sind - unabhängig vom QR-Schalter.
- `ReceiptPrintJob.TseQrCode` (neues Feld, Default `false`) transportiert
  die Einstellung vom Kassenfenster zum Druckdienst.

## Tests
`R81ReviewTests.cs` - 3 neue Prüfungen: Einstellung ist standardmäßig
aus, QR-Nutzlast enthält alle fünf Felder korrekt, `ReceiptPrintJob`s
neues Feld ist für jeden bestehenden Aufrufer ohne Änderung weiterhin
`false`. Die eigentliche GDI-Druckzeichnung ist ohne echten Windows-
Drucker nicht automatisiert testbar (bestehende Einschränkung dieses
Testrahmens, nicht neu) - die reine Nutzlast-Logik ist getrennt und
vollständig getestet. Safety-Ziel jetzt `ALL 409 CHECKS PASSED`
(406 + 3).

## Unverändert
Betrifft ausschließlich die Bon-Darstellung, keine fiskale Logik -
`FiscalRelease.Enabled`/`FiscalComplianceService` bleiben unberührt.
