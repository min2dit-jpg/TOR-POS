# TOR POS R79 – BON STORNO (Gegenbuchung eines abgeschlossenen Bons)

## Kontext
R78 verdrahtete die TSE-Signierung für den normalen Kassenverkauf. R79
nutzt das direkt weiter für den nächsten offenen fiskalen Baustein: die
Gegenbuchung (Storno) eines bereits abgeschlossenen, unveränderbaren Bons.
Das Ziel und die Regeln kamen aus einem expliziten Abstimmungsgespräch mit
dem Nutzer, nicht aus einer einseitigen technischen Entscheidung:

- **Nur BAR-Verkäufe** können vorerst storniert werden. Für KARTE gibt es
  in diesem Projekt noch keine ZVT-Rückbuchung/Reversal - eine automatische
  Kartenrückerstattung wäre nicht abgesichert.
- **Nur volle Bon-Storno** (der ganze Bon). Teil-Retoure (einzelne
  Artikelzeilen) ist bewusst nicht Teil dieser Version.
- **Berechtigung läuft über das bereits vorhandene Rechtesystem**:
  `UserPermissions.ReceiptStorno` existierte im Code bereits (Button,
  Berechtigungs-Flag, Verwaltungsfenster) - Admin steuert pro Kassierer
  über `Personal/Rechte`, ob BON STORNO freigegeben ist. Kein neuer
  Schalter nötig.

## Neu
- `ISaleRepository.RecordStornoAsync` (Infrastructure): lädt den
  Originalbon, validiert (BAR, transaction_type='SALE', noch nicht
  storniert), bucht einen **neuen** Bon mit `transaction_type='STORNO'`
  und `original_sale_id`, spiegelt die Artikelzeilen, macht den
  Lagerabgang der Originalbestellung rückgängig und schreibt einen
  Audit-Log-Eintrag. Der Originalbon wird nie verändert oder gelöscht -
  kann er auch nicht, `trg_sales_no_update`/`trg_sales_no_delete` verbieten
  das ohnehin (siehe R78).
- `SaleFiscalSigningService.SignAsync` signiert den neuen Storno-Bon exakt
  wie einen normalen Verkauf (eigener Beleg, eigene TSE-Transaktionsnummer).
- `MainWindow.OnBonStornoClick`: öffnet die Bon-Suche im Storno-Modus
  (`ReceiptHistoryWindow(..., stornoMode:true)`), verlangt einen
  Pflichtgrund (`function.bon_storno_reasons`, eigene Einstellung -
  getrennt von `function.storno_reasons` für SOFORT STORNO), bucht,
  signiert, druckt einen als "BON STORNO · GEGENBUCHUNG ZU BON …"
  gekennzeichneten Beleg.
- Bestehende Z-Bericht-/Auswertungslogik (`BusinessManagementService`)
  erwartete `transaction_type='STORNO'`/`'RETURN'` bereits seit R71 (eigene
  Summenspalten im Umsatzbericht) - bis jetzt erzeugte aber nie ein
  Code-Pfad einen solchen Bon. R79 füllt diese Lücke erstmals.

## Sicherheitsschranken - unverändert scharf
`RecordStornoAsync` ruft `TorPos.Core.FiscalRelease.RequireProduction()`
genau wie `CommitAsync` - eine Storno-Buchung ist ebenso eine
Produktivbuchung wie der Verkauf, den sie rückgängig macht, und bekommt
keinen separaten, schwächeren Freischalt-Pfad. Solange die R78-Schranken
(`FiscalRelease.Enabled`, `FiscalComplianceService`) auf `false` stehen,
bucht auch BON STORNO nichts real.

## Nachtrag: MwSt.-Aufschlüsselung im Z-/X-Bericht korrigiert
Direkt nach dem ersten Durchgang aufgefallen und noch in R79 behoben:
`BusinessManagementService.GetPeriodSummaryAsync` zog `STORNO`/`RETURN`-
Beträge zwar von der Brutto-Summe ab, aber die MwSt.-Aufschlüsselung
(`taxGroups`) berücksichtigte bisher nur `transaction_type='SALE'`-Zeilen.
Jetzt fließt ein Storno mit umgekehrtem Vorzeichen in dieselbe
Pro-Steuersatz-Summe ein wie sein Originalverkauf - die
MwSt.-Zusammenfassung im X-/Z-Bericht bleibt dadurch auch bei echten
Storno-Bons korrekt statt die abgeführte MwSt. zu überschätzen.

## Tests
`R79ReviewTests.cs` - 7 neue Prüfungen: KARTE-Storno abgelehnt,
nicht-existenter Bon abgelehnt, Storno-eines-Storno abgelehnt,
Doppel-Storno abgelehnt, ein berechtigter Storno-Versuch erreicht exakt
dieselbe Produktivfreigabe-Sperre wie ein normaler Verkauf, der Originalbon
bleibt in jedem Fall unverändert, und die MwSt.-Aufschlüsselungs-Korrektur
selbst (SALE + passender STORNO ergibt 0,00 in der Pro-Satz-Summe).
Safety-Ziel jetzt `ALL 400 CHECKS PASSED` (393 + 7).

## Nachtrag 2: Rabatt-Spiegelung in RecordStornoAsync korrigiert
Bei einer erneuten Durchsicht des eigenen Codes gefunden, bevor es jemals
produktiv relevant werden konnte: `RecordStornoAsync` setzte
`discount_cents` des Storno-Bons fest auf `0`, obwohl die MwSt.-Formel
`subtotal (aus sale_items) - discount_cents` rechnet. Bei einem
Originalverkauf MIT manuellem Rabatt hätte das die abgezogene MwSt. des
Storno zu hoch ausfallen lassen - der Storno-Bon spiegelt jetzt
`discount_cents` (und den daraus berechneten `subtotal_cents`) exakt vom
Original, genau wie bereits `total_cents`.

## Was noch offen bleibt
- KARTE-Storno: ZVT-`ReversalAsync`/`RefundAsync` existieren bereits im
  vendorten `Portalum.Zvt 3.4.0`, wurden aber bewusst noch nicht angebunden
  - auf expliziten Wunsch des Nutzers bleibt BON STORNO vorerst BAR-only.
- Teil-Retoure einzelner Artikelzeilen.
- Reale TSE-/DSFinV-K-Abnahme, wie bei R78.
