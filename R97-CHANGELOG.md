# TOR POS R97 – Im Haus/Außer Haus pro Warengruppe abschaltbar

## Kontext
Auf ausdrücklichen Nutzerwunsch: R95s Im-Haus/Außer-Haus-Regel wirkte
bisher nur implizit korrekt für Getränke (`ImHausVat.Effective` hebt
ohnehin nur einen 7 %-Satz an, ein 19 %-Getränk bleibt unberührt) - es
gab aber keinen sichtbaren, direkt einstellbaren Schalter dafür in der
Warengruppen-Verwaltung. Wunsch: beim Anlegen/Bearbeiten einer
Warengruppe soll direkt wählbar sein, ob die Regel für diese Warengruppe
überhaupt gilt.

## Änderung
- `Category.ImHausApplicable` (neu, Default `true`) - "Warengruppe ist
  MwSt.-Master", exakt dasselbe Propagationsmuster wie `VatRate`:
  `category_master_data.im_haus_applicable` ist die Quelle der Wahrheit,
  wird bei jedem Warengruppen-Speichern auf ALLE zugeordneten Artikel
  (`products.im_haus_applicable`) übertragen - auch rückwirkend auf
  bereits bestehende Artikel, nicht nur neue.
- `ImHausVat.Effective` bekommt einen dritten Parameter
  `categoryApplies` (Default `true`) - hebt einen 7 %-Satz nur an, wenn
  sowohl der globale Im-Haus-Umschalter als auch die Warengruppe selbst
  zustimmen.
- Neue Warengruppen-Stammdaten fließen komplett durch: `Product
  .ImHausApplicable` (aus `category_master_data` übernommen, genau wie
  `VatRate`), `CartLine.ImHausApplicable` (aus dem Artikel beim
  Hinzufügen zum Warenkorb), `CheckoutSnapshot.CopyLines` (wendet die
  Regel nur an, wenn die Zeile selbst zustimmt),
  `parked_receipt_items.im_haus_applicable` (Zeilen-Schnappschuss,
  überlebt Parken/Wiedereröffnen wie schon `vat_rate` selbst).
- Neue Checkbox "Im Haus/Außer Haus MwSt.-Regel anwenden" im
  Warengruppen-Editor (Einstellungen/Waren → Artikelverwaltung →
  Warengruppe anlegen), direkt unter dem MwSt.-Feld, mit erklärendem
  Hinweistext. Standard: angehakt (bestehendes R95-Verhalten bleibt für
  jede Warengruppe ohne bewusste Änderung exakt gleich).

## Ergebnis
- 7 neue Prüfungen in `R97ReviewTests.cs`: reine Umschaltfunktion mit
  abgeschalteter Warengruppe, Warengruppen-Rundlauf über
  `GetCategoriesAsync`, ein neu angelegter Artikel übernimmt den
  Warengruppen-Schalter, ein nachträgliches Abschalten wirkt rückwirkend
  auf bereits vorhandene Artikel, der komplette
  Kassenvorgang-Schnappschuss respektiert eine abgeschaltete
  Warengruppe trotz aktivem globalem Umschalter, und eine geparkte Zeile
  behält ihren Schnappschuss nach dem Wiederladen.
  Sicherheits-Testsuite: **492/492 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Verfeinerung von R95, keine neue fiskalische Grundsatzentscheidung - der
Umschalter ändert nur, WELCHE Warengruppen der bereits genehmigten Regel
unterliegen, nicht die Regel selbst. Beide Fiskal-Sperren unverändert
`false`. Bewusst nicht auf Extra-/Zutaten-Artikel (`ExtraItem`)
ausgeweitet - diese tragen weiterhin nur ihren eigenen `VatRate` ohne
eigenes `ImHausApplicable`; verhalten sich also wie vor R97 (nur nach
der impliziten 7 %-Regel).
