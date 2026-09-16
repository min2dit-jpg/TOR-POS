# TOR POS R82 – Teilretoure (einzelne Artikel/Menge zurücknehmen)

## Kontext
R79 deckte nur die volle Bon-Storno ab (der ganze Bon wird rückgängig
gemacht). R82 ergänzt die kleinere, häufigere Variante: einzelne Artikel
in einer beliebigen Menge aus einem abgeschlossenen Bon zurücknehmen,
ohne den Rest des Bons anzufassen. Gleiche Leitplanken wie bei R79 (auf
ausdrücklichen Wunsch beibehalten): nur BAR, admin-gesteuert über die
bereits vorhandene `UserPermissions.ReceiptStorno`-Berechtigung.

## Neu
- `ISaleRepository.RecordReturnAsync(originalSaleId, lines, actor, reason)`:
  bucht einen neuen Bon mit `transaction_type='RETURN'`, der nur die
  angeforderten Positionen/Mengen spiegelt (nicht den ganzen Bon), gibt
  Lagerbestand nur für die zurückgenommene Menge frei, und verweigert eine
  Anfrage, die mehr zurücknehmen würde, als von dieser konkreten
  Originalzeile - nach Abzug bereits früher zurückgenommener Mengen -
  noch übrig ist.
- **Kein Rabatt-Anteil auf Teilretouren:** ein manueller Bon-Rabatt war
  immer auf den GANZEN Bon bezogen; ihn anteilig auf einzelne
  zurückgegebene Zeilen umzurechnen wäre eine willkürliche Annahme. Eine
  Teilretoure bucht deshalb bewusst zum vollen (bereits Angebot-
  bereinigten) Zeilenpreis, ohne Rabattanteil - dokumentierter,
  bewusster Kompromiss, kein Bug.
- `MainWindow.OnPartialReturnClick`: KASSE-Menü → "Teilretoure (einzelne
  Artikel)". Bon auswählen (`ReceiptHistoryWindow` im neuen `returnMode`),
  Mengen je Position im neuen `PartialReturnWindow` eingeben, Pflichtgrund,
  Buchung, TSE-Signatur über die bestehende `SaleFiscalSigningService`
  (genau wie bei R78/R79), als "TEILRETOURE · GEGENBUCHUNG ZU BON …"
  gekennzeichneter Beleg.
- `FiscalProcessData` markiert eine Teilretoure als `AVBelegabbruch`
  (R80 hatte diese Zuordnung für `transaction_type='RETURN'` bereits
  vorgesehen, aber ungetestet) und referenziert weiterhin die
  Original-Beleg-Nr.

## Datenbank
Neue Spalte `sale_items.original_sale_item_id` (Schema V10) - verknüpft
eine Teilretoure-Zeile mit der exakten Originalzeile, die sie
zurücknimmt. Erlaubt die kumulative "wie viel wurde von DIESER Zeile
schon zurückgenommen"-Abfrage über beliebig viele frühere
Teilretouren hinweg, statt nur pro ganzem Bon zu zählen wie bei R79.

## Sicherheitsschranken - unverändert scharf
`RecordReturnAsync` ruft `FiscalRelease.RequireProduction()` an genau
derselben Stelle im Ablauf wie `RecordStornoAsync` und `CommitAsync` -
keine Teilretoure kann real gebucht werden, solange die R78-Schranken
geschlossen bleiben.

## Tests
`R82ReviewTests.cs` - 13 neue Prüfungen: Schema V10, ProcessData-Marker
für RETURN, leere/negative/doppelte/bon-fremde Positionsauswahl
abgelehnt, KARTE abgelehnt, Teilretoure-einer-Teilretoure abgelehnt, die
kumulative Bereits-zurückgenommen-Abfrage selbst, ein berechtigter
Versuch erreicht exakt dieselbe Produktivfreigabe-Sperre, und der
Originalbon bleibt in jedem Fall unverändert. Safety-Ziel jetzt
`ALL 422 CHECKS PASSED` (409 + 13).

## Was noch offen bleibt
- KARTE-Retoure (wie KARTE-Storno: ZVT `ReversalAsync`/`RefundAsync`
  existieren im SDK, bewusst noch nicht angebunden).
- Reale TSE-/DSFinV-K-Abnahme.
