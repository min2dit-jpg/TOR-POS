# TOR POS R93 – Zahlart-Anzeige im Bestellboard immer "siehe Bon"

## Kontext
Gefunden beim Abschluss der R88-R92-Durchsicht: der letzte verbliebene,
noch nicht geprüfte `sales`-Join lag in `OrderWorkflowService.ListAsync`
(IMBISS-Bestellübersicht/"offene Bestellungen"). Die SQL verglich
`s.payment_method` gegen die Literale `'Cash'`/`'Card'` (gemischte
Groß-/Kleinschreibung). Tatsächlich gespeichert wird `payment_method` aber
immer als `'CASH'`/`'CARD'` (Großbuchstaben) -
`CommitAsync` schreibt `paymentMethod.ToString().ToUpperInvariant()`, und
jede andere Stelle im Code (`RecordStornoAsync`, `SearchHistoryAsync`,
`GetExpectedCashCentsAsync`, alle Berichtsabfragen) vergleicht konsequent
gegen Großbuchstaben.

SQLite vergleicht TEXT standardmäßig case-sensitiv (keine `COLLATE NOCASE`
auf dieser Spalte) - der `WHEN`-Fall konnte also nie zutreffen. Jede
bezahlte IMBISS-Bestellung zeigte im Bestellboard immer den generischen
Fallback-Text "Bezahlt · siehe Bon" statt "Bar bezahlt"/"Karte bezahlt".

## Änderung
Die Vergleichsliterale in `OrderWorkflowService.cs` auf `'CASH'`/`'CARD'`
korrigiert - passend zur tatsächlich gespeicherten Schreibweise.

## Ergebnis
- 2 neue Prüfungen in `R93ReviewTests.cs`: eine bar bezahlte und eine per
  Karte bezahlte, abkassierte Bestellung zeigen jetzt jeweils den
  korrekten Zahlart-Text statt des generischen Fallbacks.
  Sicherheits-Testsuite: **466/466 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine Anzeigekorrektur im Bestellboard, keine Auswirkung auf Buchung,
Bestand oder Fiskaldaten - die zugrunde liegende Zahlungsabwicklung war
nie betroffen, nur das angezeigte Label. Beide Fiskal-Sperren unverändert
`false`. Damit ist die auf `Infrastructure.cs`/`OrderWorkflowService.cs`
verbliebene Restunsicherheit aus dem R92-Nachtrag geklärt: kein weiterer
Fund dieser Art in `Infrastructure.cs`, dieser eine Fund in
`OrderWorkflowService.cs`.
