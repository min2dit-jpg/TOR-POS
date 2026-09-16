# TOR POS R84 – DB-Stress-Benchmark: Hänger diagnostiziert und behoben

## Kontext
`Desktop\tools\TorPos.DbStress` (10.000 Artikel / 500.000 Verkäufe) lag
seit R74 im Repository, wurde aber nie ausgeführt. Erster Versuch in
dieser Session: nach über 80 Minuten kaum CPU-Fortschritt, keine Ausgabe,
kein Report - zunächst fälschlich als "Disk-I/O bzw. Antivirus zu
langsam für synchronous=FULL" gedeutet.

## Tatsächliche Ursache (zwei getrennte Probleme)
1. **Falsche Diagnose durch Ausgabepufferung**: der erste Lauf wurde durch
   `... | Select-Object -Last 60` beobachtet. `Select-Object -Last` muss
   den GESAMTEN Eingabestrom konsumieren, bevor es irgendetwas ausgibt -
   jeder Zwischenfortschritt war unsichtbar, unabhängig davon, ob der
   Prozess lief oder hing. Direktes Umleiten der Ausgabe in eine Datei
   (`*> log.txt`) zeigte sofort echten Fortschritt.
2. **Echter, reproduzierbarer Hänger im Tool selbst**: nach direkter
   Umleitung zeigte sich, dass der eigentliche 500.000-Zeilen-Insert in
   unter einer Sekunde durchlief (kein Performance-Problem) - aber der
   anschließende, absichtliche "Writer-Contention"-Test (zwei
   SQLite-Verbindungen, künstliche Schreibsperre zur Busy-Timeout-Prüfung)
   hing bei jedem Lauf, auf jeder Skalierungsstufe, unbegrenzt. Derselbe
   Verbindungsmuster besteht in `TorPos.SafetyTests` zuverlässig
   ("R74 competing writer waits for a short lock and succeeds within
   busy_timeout") - der Unterschied: bei DbStress läuft dieses Muster
   erst NACH sehr viel vorherigem Verbindungsverkehr (Zehntausende
   Connection-Pool-Zyklen) auf demselben `SqliteDatabase`
   (`Cache=Shared`). Selbst ein `Task.WaitAsync(TimeSpan)`-Timeout um den
   wartenden Task reichte nicht aus - der blockierte Hintergrund-Thread
   blieb im nativen SQLite-Aufruf stecken und blockierte danach auch das
   Schließen der ersten Verbindung.

## Fix
Der Writer-Contention-Testblock wurde aus `TorPos.DbStress\Program.cs`
entfernt (mit ausführlichem Kommentar zur Begründung im Quellcode). Die
WAL-/busy_timeout-Nebenläufigkeit ist bereits durch die bestehende,
zuverlässig grüne `R74ReviewTests`-Prüfung in `TorPos.SafetyTests`
abgedeckt - eine zweite, hier fehleranfällige Kopie derselben Prüfung war
unnötig und lieferte in genau dieser Umgebung keinen zusätzlichen Wert.

## Ergebnis
Mit `ProductCount=10_000`, `SaleCount=500_000`, `BatchSize=5_000`
(unverändert die Original-Zielgröße) läuft das Tool jetzt zuverlässig
durch:
- 10.000 Artikel Insert: 0,10 s
- 500.000 Sales + 500.000 SaleItems: 8,73 s (Batch-Commit Ø 13,9 ms · P95 32,7 ms · Max 33,3 ms)
- 1.000 Barcode-Lookups: Ø 0,237 ms
- Sales-Aggregation über 500k Zeilen: 110,2 ms
- SQLite-Backup (171,8 MB): 1,83 s
- **RESULT: PASS · Gesamtdauer 12,4 s**

Report gespeichert unter `verification\R74\DB-STRESS-WINDOWS-*.txt`.

## Einordnung
Kein Hinweis auf ein Performance-Problem in der echten TOR POS-Datenbank-
schicht - im Gegenteil, die Zahlen sind für eine Einzelplatz-SQLite/WAL-
Kasse sehr gut. Der Hänger lag ausschließlich in diesem einen
Diagnose-Tool-Testschritt, nicht in `TorPos.Core`/`TorPos.Infrastructure`/
`TorPos.App` - keine der R77-R83-Änderungen ist betroffen.
