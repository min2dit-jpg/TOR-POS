# TOR POS R78 – Sale-to-TSE Lifecycle Wiring (KIOSK / IMBISS SALE)

## Kontext
Bislang existierten die TSE-Transaktionsprimitive
(`StartTransactionAsync`/`FinishTransactionAsync` auf `ITseProvider`) nur
als SDK-Bridge - kein Verkauf rief sie je auf. R78 schließt genau diese
Lücke für den direkten Kassenverkauf (KIOSK und IMBISS SALE-Modus).
Das IMBISS ORDER/Bestellung-Lebenszyklus bleibt bewusst außen vor - das
ist ein eigener TSE-Vorgangstyp und folgt separat.

## Neu
- `TorPos.Core.FiscalProcessData`: baut die TSE-ProcessData für einen
  Verkauf als ein `Beleg^...`-Vorgang (Zeitstempel, Summe, MwSt.-Klassen,
  Belegnummer). **Entwurf** - folgt der allgemeinen BSI TR-03153/DSFinV-K
  Feldform, wurde aber NICHT byte-genau gegen die offizielle BSI-Anlage,
  eine echte Hardware TSE 2 oder das DSFinV-K-Prüftool verifiziert.
- `TorPos.Infrastructure.SaleFiscalSigningService`: läuft NACH dem
  durablen Sale-Commit. Signiert genau einen Beleg als Start+Finish.
  Ein TSE-Ausfall (Start oder Finish schlägt fehl) markiert nur
  TSE-Ausfall über den bestehenden `TseFailSafeService`/
  `ITseOutageRepository` - der bereits abgeschlossene Verkauf wird
  dadurch nie verändert, blockiert oder zurückgerollt.
- `MainWindow.CommitCheckoutAsync` ruft die Signierung direkt nach
  `ISaleRepository.CommitAsync` auf; `BuildReceiptPrintJob` befüllt jetzt
  die längst vorhandenen TSE-Belegfelder (TSE-Transaktionsnummer,
  Signaturzähler, Seriennummer, Prüfwert, TSE-Ausfall-Kennzeichnung)
  aus dem echten Signaturergebnis statt aus Platzhaltern.

## Datenbank
Neue, **append-only** Tabelle `sale_tse_signatures` (Schema V8, siehe
`SchemaMigrationService`) - sale_id, client_id, transaction_number,
signature_counter, serial_number, signature, log_time, outage.

**Wichtiger Architekturpunkt:** die ursprüngliche Umsetzung wollte die
TSE-Felder direkt als neue Spalten auf `sales` per UPDATE nachtragen -
das schlug im Test sofort mit `SQLite Error 19: 'completed sales are
immutable'` fehl, weil `trg_sales_no_update` JEDES Update auf `sales`
verbietet (bewusste fiskale Unveränderlichkeits-Sperre, nicht neu in
R78). Deshalb liegt die Signatur in einer eigenen, ebenfalls
unveränderbaren (`trg_sale_tse_signatures_no_update`/`_no_delete`)
Tabelle - derselbe Aufbau wie `sale_operators` und `category_kitchen_data`
bereits verwenden.

## Sicherheitsschranken - unverändert scharf
- `TorPos.Core.FiscalRelease.Enabled` bleibt `false` (Build-Schalter in
  `CheckoutSafety.cs`): `ISaleRepository.CommitAsync` verweigert JEDEN
  neuen Produktivverkauf, unabhängig von dieser Änderung.
- `FiscalComplianceService.CheckAsync` (`fiscalReleaseBuild=false` u. a.)
  hält `ProductionAllowed` weiterhin fest auf `false`.
- Ergebnis: `MainWindow`s echter (nicht-Simulations-) Checkout-Pfad kann
  `SaleFiscalSigningService` im ausgelieferten Build gar nicht erreichen.
  R78 macht TOR POS **nicht** fiskal produktionsbereit - es baut und
  testet die Verdrahtung, bevor diese Schranken bewusst gelöst werden.

## Was noch fehlt, bevor diese Schranken je gelöst werden dürfen
- ProcessData-Format Feld für Feld gegen BSI TR-03153 und das
  DSFinV-K-Prüftool verifizieren (aktueller Stand: Entwurf).
- Reale Hardware-TSE-2-Abnahme (SDK, Aktivierung, echte Signatur).
- IMBISS ORDER/Bestellung als eigener TSE-Vorgang (Parken → Bestellung).
- Storno/Rückgabe als echter fiskaler Gegen-Vorgang.
- Mali müşavir/TSE-Bayi-Abstimmung zur rechtlichen Freigabe.

## Tests
`R78ReviewTests.cs` - 15 neue Prüfungen: Schema, ProcessData-Determinismus
und -Inhalt, No-Op ohne aktive TSE, No-Op ohne Client-ID, erfolgreiche
Signatur inkl. DB-Rundlauf, TSE-Ausfall bei Start, TSE-Ausfall bei Finish,
und die `sales`-Unveränderlichkeitssperre selbst. Safety-Ziel jetzt
`ALL 393 CHECKS PASSED` (378 + 15).
