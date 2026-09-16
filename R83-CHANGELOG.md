# TOR POS R83 – IMBISS ORDER Annahme als eigener TSE-Vorgang

## Kontext
R78 verdrahtete TSE-Signierung nur für den direkten Kassenverkauf
(KIOSK / IMBISS SALE) und ließ den IMBISS ORDER/Bestellung-Lebenszyklus
bewusst außen vor. R83 schließt genau diese Lücke: die Annahme einer
Bestellung (Abholnummer vergeben, Küche informiert - lange BEVOR
tatsächlich kassiert wird) wird jetzt als eigener TSE-Vorgang signiert,
getrennt vom späteren Kassenbeleg-V1 bei der eigentlichen Zahlung
(der schon über R78 läuft, sobald die Bestellung abgeholt/kassiert wird).

## Neu
- `TorPos.Core.FiscalProcessData.BuildBestellung(ParkedReceipt)`: baut
  die TSE-ProcessData für eine Bestellannahme als eigenen
  `"Bestellung^..."`-Vorgang (Prozesstyp `Bestellung-V1`), unterscheidbar
  von einem `Beleg`/`AVBelegstorno`. **Entwurf, wie FiscalProcessData
  insgesamt** - nicht gegen BSI TR-03153 verifiziert.
- `TorPos.Infrastructure.OrderFiscalSigningService`: läuft NACH dem
  durablen Park-Commit (`IParkedReceiptRepository.ParkAsync`). Ein
  TSE-Ausfall markiert nur TSE-Ausfall über den bestehenden
  `TseFailSafeService` - die bereits angenommene Bestellung wird dadurch
  nie verändert, blockiert oder zurückgerollt. Läuft NICHT durch
  `FiscalRelease.RequireProduction()` - eine Bestellannahme ist keine
  irreversible Produktivbuchung wie ein Sale/Storno/Retoure-Commit,
  sondern nur ein operativer Zwischenstand.
- `MainWindow.OnParkClick` (Zweig "neue Bestellung", ORDER-Modus, nicht
  im Trainingsmodus): ruft die Signierung direkt nach `ParkAsync` auf.
- `ParkedReceipt` trägt jetzt dieselben TSE-Felder wie `Sale`
  (TseTransactionNumber, TseSignatureCounter, TseSerialNumber,
  TseSignature, TseLogTime, TseOutage, TseClientId).

## Datenbank
Neue TSE-Spalten direkt auf `parked_receipts` (Schema V11) - **kein**
eigenes Append-only-Muster wie `sale_tse_signatures` bei R78/`sales`,
weil `parked_receipts` (anders als `sales`) keinen
Unveränderlichkeits-Trigger trägt: eine Bestellung darf sich vor dem
Kassieren legitim noch ändern (`UpdateAsync`), also ist ein normales
`UPDATE` hier korrekt statt ein separates Append-only-Log zu erzwingen.

## Sicherheitsschranken - unverändert scharf
Genau wie R78: `FiscalComplianceService`s `parkedOrderTseValidated`-
Konstante bleibt `false`, also bleibt `ProductionAllowed` unverändert
`false` und der reale Checkout-Pfad unverändert blockiert. Der
Diagnosetext für "Parken / Bestellung" wurde aktualisiert, um den neuen
Implementierungsstand korrekt zu beschreiben (implementiert, noch nicht
real validiert) - der Blocker selbst bleibt bestehen.

## Tests
`R83ReviewTests.cs` - 12 neue Prüfungen: Schema V11, Bestellung-
ProcessData-Inhalt und -Abgrenzung von Beleg/Storno, No-Op ohne aktive
TSE, No-Op ohne Client-ID, erfolgreiche Signatur inkl. DB-Rundlauf,
TSE-Ausfall bei Start. Da Bestellannahme keinen FiscalRelease-Gate
durchläuft, konnten Erfolgs- UND Fehlerpfade direkt getestet werden
(anders als bei R79/R82, wo nur die Vor-Gate-Validierung testbar war).
Safety-Ziel jetzt `ALL 434 CHECKS PASSED` (422 + 12).

## Was noch offen bleibt
- Update/Storno einer bereits als "Bestellung" signierten Order (aktuell
  wird nur die erste Annahme signiert, nicht jede spätere Änderung/
  Stornierung des Parkbons - bewusst einfach gehalten, keine
  Multi-Phasen-TSE-Lebenszyklus-Verwaltung).
- Reale TSE-/DSFinV-K-Abnahme, wie bei R78/R79/R82.
