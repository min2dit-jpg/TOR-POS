# TOR POS R91 – Drei weitere Berichte zählten Storno/Retoure als Umsatz

## Kontext
Dritte Runde derselben Fehlerfamilie wie R88/R90: nach dem Fund in R90
wurde jede verbleibende `Build*Async`-Berichtsmethode in
`BusinessManagementService.cs`/`BusinessManagementReports.cs` gezielt
darauf durchsucht, ob sie `sales`/`sale_items` ohne Rücksicht auf
`transaction_type` summiert. Gefunden:

1. **`BuildTurnoverSummaryAsync`** (Menüpunkt "UMSATZBERICHTE" -
   Heute/Diese Woche/Dieser Monat): `FROM sales WHERE created_at >= $from`
   ohne jeden `transaction_type`-Filter.
2. **`BuildMonthlyTurnoverAsync`** ("MONATSUMSATZ", 24-Monats-Übersicht):
   dieselbe fehlende Einschränkung.
3. **`BuildSalesStatisticsAsync`** ("VERKAUFSSTATISTIK", Artikel nach
   Umsatz) - sowohl die eigenständige Version als auch die Kopie im
   Monatspaket: summierte direkt `sale_items` ohne Verknüpfung zu `sales`,
   ohne jede Rücksicht auf den Vorgangstyp des zugehörigen Bons.

In allen drei Fällen wurde ein BON STORNO/eine Teilretoure als
**zusätzlicher** Umsatz/zusätzliche Menge gezählt statt ausgeschlossen zu
werden - ein Artikel, der zurückgegeben wurde, erschien in der
Verkaufsstatistik doppelt so oft verkauft, wie tatsächlich beim Kunden
geblieben ist.

Zur Kontrolle wurden alle übrigen `sales`/`sale_items`-Abfragen in beiden
Dateien noch einmal durchgesehen: der X-/Z-Bericht
(`GetPeriodSummaryAsync`) war bereits durchgängig korrekt (`transaction_type`
wird dort seit R79 sauber behandelt), ebenso die Angebots-Auswertung. Das
Kassenjournal (`BuildCashJournalAsync`) und der GDPdU-Exportbericht
(`ExportGdpduAuditPackageAsync`) zeigen Storno/Retoure absichtlich
vollständig mit - das ist für eine lückenlose Journal-/Prüfansicht korrekt
und wurde nicht verändert.

## Änderung
Allen drei betroffenen Abfragen wurde `AND COALESCE(transaction_type,'SALE')
='SALE'` (bzw. für die Artikel-Statistik ein zusätzlicher Join auf
`sales` mit derselben Bedingung) hinzugefügt.

## Ergebnis
- 3 neue Prüfungen in `R91ReviewTests.cs`: für alle drei korrigierten
  Berichte wird ein Verkauf plus seine volle Stornierung eingefügt und
  bestätigt, dass nur der tatsächliche Verkauf gezählt wird - nicht
  Verkauf plus Storno addiert. Sicherheits-Testsuite: **462/462 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine Berichtskorrektur, kein neuer Buchungsweg. Beide Fiskal-Sperren
unverändert `false`. Diese drei Berichte zeigen abweichende Zahlen erst,
sobald echte STORNO/RETURN-Buchungen existieren - das bleibt bis zur
Produktivfreigabe gesperrt. Damit ist die in R88 begonnene Durchsicht
aller Berichtsabfragen in `BusinessManagementService.cs`/
`BusinessManagementReports.cs` auf dieses Fehlermuster abgeschlossen.
